using System.IO;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FTMS.Application;
using FTMS.Domain;
using FTMS.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace FTMS.Desktop;

public partial class CompactWindow : Window
{
    private const string FtmsUrl = "https://ftms.fpt.net/ihub/list?tab=2";
    private const string BlockedBotScript = "https://ftmslite.fpt.vn/agent-ai/js/bot.js";
    private readonly SettingsStore _settingsStore = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _telegramTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly HttpClient _http = new();
    private readonly string _telegramOffsetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion", "telegram.offset");
    private TicketMonitor? _monitor;
    private bool _monitorStarted;
    private int _realtimeSyncScheduled;
    private bool _loginRecoveryInProgress;
    private long _telegramUpdateOffset;
    private bool _checkingTelegram;
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;

    public CompactWindow()
    {
        InitializeComponent(); _settingsStore.Load(); Loaded += InitializeAsync;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _refreshTimer.Stop();
            _telegramTimer.Stop();
            _trayIcon?.Dispose();
            _http.Dispose();
        };
        Closing += OnWindowClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        _refreshTimer.Tick += (_, _) => RunAutoRefresh();
        _telegramTimer.Tick += async (_, _) => await CheckTelegramActionsAsync();
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Mở FTMS Companion", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Thoát", null, (_, _) =>
        {
            _allowClose = true;
            Dispatcher.Invoke(Close);
        });
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "FTMS Companion",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipTitle = "FTMS Companion vẫn đang chạy";
            _trayIcon.BalloonTipText = "Ứng dụng tiếp tục theo dõi ticket và gửi thông báo Telegram.";
            _trayIcon.ShowBalloonTip(2000);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void InitializeAsync(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_telegramOffsetPath) && long.TryParse(File.ReadAllText(_telegramOffsetPath), out var savedOffset))
            _telegramUpdateOffset = savedOffset;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(root, "WebView2"));
        await FtmsWebView.EnsureCoreWebView2Async(environment);
        await MonitorWebView.EnsureCoreWebView2Async(environment);
        foreach (var browser in new[] { FtmsWebView, MonitorWebView })
        {
            browser.CoreWebView2.AddWebResourceRequestedFilter(BlockedBotScript, CoreWebView2WebResourceContext.Script);
            browser.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                if (!args.Request.Uri.StartsWith(BlockedBotScript, StringComparison.OrdinalIgnoreCase)) return;
                args.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: application/javascript");
            };
            await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AutoLoginScript);
            await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BlockBotScript);
        }
        FtmsWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ApiObserverScript);
        var appSettings = new AppSettings { FtmsUrl = FtmsUrl, PollIntervalSeconds = 2, IdleDelaySeconds = 0 };
        var databasePath = Path.Combine(root, "ftms.db");
        var client = new WebViewFtmsClient(MonitorWebView, FtmsUrl);
        var telegram = new TelegramOutboxSender(databasePath, () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId), _http);
        _monitor = new TicketMonitor(client, new SqliteTicketStore(databasePath), telegram, new TicketChangeDetector(), appSettings);
        _monitor.StatusChanged += message => Dispatcher.InvokeAsync(
            () => MonitorText.Text = message, DispatcherPriority.Background);
        _monitor.SummaryChanged += summary => Dispatcher.InvokeAsync(
            () => UpdateDashboard(summary), DispatcherPriority.Background);
        await _monitor.InitializeAsync(_lifetime.Token);
        ApplyRefreshSettings();
        _telegramTimer.Start();
        MonitorWebView.Source = new Uri(FtmsUrl);
        FtmsWebView.Source = new Uri(FtmsUrl);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message;
        try { message = JsonSerializer.Deserialize<string>(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        if (message == "ftms-api-updated") QueueRealtimeSync();
    }

    private void QueueRealtimeSync()
    {
        if (_monitor is null || Interlocked.Exchange(ref _realtimeSyncScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), _lifetime.Token).ConfigureAwait(false);
                await _monitor.SyncNowAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _ = Dispatcher.InvokeAsync(() => MonitorText.Text = $"L\u1ed7i \u0111\u1ed3ng b\u1ed9 th\u1eddi gian th\u1ef1c: {ex.Message}",
                    DispatcherPriority.Background);
            }
            finally
            {
                Interlocked.Exchange(ref _realtimeSyncScheduled, 0);
            }
        });
    }

    private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = FtmsWebView.Source?.AbsoluteUri ?? "";
        var connected = url.Contains("ftms.fpt.net/ihub/", StringComparison.OrdinalIgnoreCase);
        var login = !connected && (url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("adfs", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/id/", StringComparison.OrdinalIgnoreCase));
        if (login) _loginRecoveryInProgress = true;
        SessionText.Text = login ? "\u0110ang \u0111\u0103ng nh\u1eadp" : url.Contains("/ihub/", StringComparison.OrdinalIgnoreCase) ? "\u0110\u00e3 k\u1ebft n\u1ed1i" : "\u0110ang k\u1ebft n\u1ed1i";
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(login ? "#D9A441" : "#4AA47B"));
        if (!connected) return;
        if (_loginRecoveryInProgress && !string.Equals(url, FtmsUrl, StringComparison.OrdinalIgnoreCase))
        {
            _loginRecoveryInProgress = false;
            FtmsWebView.Source = new Uri(FtmsUrl);
            return;
        }
        _loginRecoveryInProgress = false;
        StartMonitorOnce();
    }

    private void StartMonitorOnce()
    {
        if (_monitorStarted || _monitor is null) return;
        _monitorStarted = true;
        MonitorText.Text = "B\u1eaft \u0111\u1ea7u theo d\u00f5i ticket";
        _ = Task.Run(() => _monitor.RunAsync(
            () => DateTimeOffset.Now - _lastUserActivity < TimeSpan.FromSeconds(8), _lifetime.Token));
    }

    private void OpenSettings(object sender, RoutedEventArgs e) { if (new CompactSettingsWindow(_settingsStore) { Owner = this }.ShowDialog() == true) ApplyRefreshSettings(); }
    private void OpenFtms(object sender, RoutedEventArgs e) => FtmsWebView.Source = new Uri(FtmsUrl);
    private async void ReloadFtms(object sender, RoutedEventArgs e) => await RefreshTicketGridAsync("\u0110\u00e3 l\u00e0m m\u1edbi danh s\u00e1ch");
    private void OnUserActivity(object sender, InputEventArgs e) => _lastUserActivity = DateTimeOffset.Now;

    private void ApplyRefreshSettings()
    {
        _refreshTimer.Stop(); if (!_settingsStore.Current.AutoRefreshEnabled) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settingsStore.Current.AutoRefreshSeconds, 5, 3600)); _refreshTimer.Start();
    }

    private async void RunAutoRefresh()
    {
        if (DateTimeOffset.Now - _lastUserActivity < TimeSpan.FromSeconds(8)) { MonitorText.Text = "T\u1ef1 \u0111\u1ed9ng l\u00e0m m\u1edbi \u0111ang t\u1ea1m d\u1eebng khi b\u1ea1n thao t\u00e1c"; return; }
        await RefreshTicketGridAsync($"T\u1ef1 \u0111\u1ed9ng l\u00e0m m\u1edbi danh s\u00e1ch l\u00fac {DateTime.Now:HH:mm:ss}");
    }

    private async Task RefreshTicketGridAsync(string successMessage)
    {
        if (FtmsWebView.CoreWebView2 is null) return;
        const string script = """
            (() => {
              const refreshButton = document.querySelector('a.k-pager-refresh.k-link');
              if (!refreshButton) return false;
              refreshButton.click();
              return true;
            })()
            """;
        try
        {
            var result = await FtmsWebView.ExecuteScriptAsync(script);
            MonitorText.Text = string.Equals(result, "true", StringComparison.OrdinalIgnoreCase)
                ? successMessage
                : "Ch\u01b0a t\u00ecm th\u1ea5y n\u00fat T\u1ea3i l\u1ea1i c\u1ee7a danh s\u00e1ch FTMS";
        }
        catch (Exception ex) { MonitorText.Text = $"Kh\u00f4ng th\u1ec3 l\u00e0m m\u1edbi danh s\u00e1ch: {ex.Message}"; }
    }

    private async Task CheckTelegramActionsAsync()
    {
        var token = _settingsStore.Current.TelegramToken;
        if (_checkingTelegram || string.IsNullOrWhiteSpace(token)) return;
        _checkingTelegram = true;
        try
        {
            var url = $"https://api.telegram.org/bot{token}/getUpdates?offset={_telegramUpdateOffset}&timeout=0&allowed_updates=%5B%22callback_query%22%5D";
            using var response = await _http.GetAsync(url, _lifetime.Token);
            if (!response.IsSuccessStatusCode) return;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_lifetime.Token));
            if (!json.RootElement.TryGetProperty("result", out var results)) return;
            foreach (var update in results.EnumerateArray())
            {
                var updateId = update.GetProperty("update_id").GetInt64();
                _telegramUpdateOffset = Math.Max(_telegramUpdateOffset, updateId + 1);
                Directory.CreateDirectory(Path.GetDirectoryName(_telegramOffsetPath)!);
                File.WriteAllText(_telegramOffsetPath, _telegramUpdateOffset.ToString());
                if (!update.TryGetProperty("callback_query", out var callback)) continue;
                var callbackChatId = callback.TryGetProperty("message", out var callbackMessage) &&
                    callbackMessage.TryGetProperty("chat", out var callbackChat) && callbackChat.TryGetProperty("id", out var callbackChatIdElement)
                    ? callbackChatIdElement.ToString() : string.Empty;
                if (!string.Equals(callbackChatId, _settingsStore.Current.TelegramChatId.Trim(), StringComparison.Ordinal))
                {
                    if (callback.TryGetProperty("id", out var deniedCallbackId))
                        await _http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/answerCallbackQuery", new
                        {
                            callback_query_id = deniedCallbackId.GetString(),
                            text = "Chat này không được phép thao tác ticket.",
                            show_alert = true
                        }, _lifetime.Token);
                    continue;
                }
                var callbackId = callback.GetProperty("id").GetString();
                var data = callback.TryGetProperty("data", out var dataElement) ? dataElement.GetString() : null;
                if (data?.StartsWith("receive:", StringComparison.Ordinal) != true) continue;
                var code = data["receive:".Length..];
                var result = await ReceiveTicketAsync(code);
                if (result && callback.TryGetProperty("message", out var sourceMessage) &&
                    sourceMessage.TryGetProperty("message_id", out var messageIdElement))
                {
                    var route = code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || code.StartsWith("AL", StringComparison.OrdinalIgnoreCase)
                        ? "case" : "request";
                    await _http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/editMessageReplyMarkup", new
                    {
                        chat_id = callbackChatId,
                        message_id = messageIdElement.GetInt64(),
                        reply_markup = new
                        {
                            inline_keyboard = new object[][]
                            {
                                [new { text = "🔎 Mở ticket", url = $"https://ftms.fpt.net/ihub/{route}/edit/{Uri.EscapeDataString(code)}" }]
                            }
                        }
                    }, _lifetime.Token);
                }
                await _http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/answerCallbackQuery", new
                {
                    callback_query_id = callbackId,
                    text = result ? $"Đã gửi yêu cầu nhận {code}" : $"Chưa tìm thấy {code} trong danh sách FTMS",
                    show_alert = !result
                }, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { MonitorText.Text = $"Lỗi thao tác Telegram: {ex.Message}"; }
        finally { _checkingTelegram = false; }
    }

    private async Task<bool> ReceiveTicketAsync(string code)
    {
        if (FtmsWebView.CoreWebView2 is null) return false;
        var serializedCode = JsonSerializer.Serialize(code);
        var script = $$"""
            (() => {
              const code = {{serializedCode}};
              try {
                const grid = window.jQuery?.('#list-grid').data('kendoGrid');
                const item = grid?.dataSource?.data()?.find(x => String(x.code || x.Code) === code);
                let ticketId = item?.id || item?.Id;
                if (!ticketId) {
                  const body = new URLSearchParams({ take: '20', skip: '0', page: '1', pageSize: '20',
                    search: code, isMyTicket: '', isAkabot: '', strStatus: '', strRegionID: '', linkDeptId: '',
                    alarmType: '0', isSortByDate: '' });
                  const findRequest = new XMLHttpRequest();
                  findRequest.__ftmsCompanionInternal = true;
                  findRequest.open('POST', '/ihub/request/GetListRequestV12', false);
                  findRequest.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                  findRequest.send(body.toString());
                  if (findRequest.status < 200 || findRequest.status >= 300) return false;
                  const response = JSON.parse(findRequest.responseText);
                  const findRows = value => {
                    if (Array.isArray(value)) return value;
                    if (!value || typeof value !== 'object') return [];
                    for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                      const rows = findRows(value[key]); if (rows.length) return rows;
                    }
                    return [];
                  };
                  const ticket = findRows(response).find(x => String(x.code || x.Code || x.requestCode || x.RequestCode) === code);
                  ticketId = ticket?.id || ticket?.Id;
                }
                if (!ticketId || typeof userID === 'undefined' || typeof Username === 'undefined') return false;
                const endpoint = /^(CA|AL)/i.test(code)
                  ? '/ihub/Case/TakeAndAssignmentV12'
                  : '/ihub/Request/TakeAndAssignmentV12';
                const payload = { input: { id_Ticket: ticketId, creator: Username, staff_ID: userID,
                  staffName: Username, departmentName: DepartmentName, department_ID: UserDept,
                  createDate: Date.now(), type: 2, code, ticketStatus: 2 }, type: 2 };
                const receiveRequest = new XMLHttpRequest();
                receiveRequest.open('POST', endpoint, false);
                receiveRequest.setRequestHeader('Content-Type', 'application/json; charset=UTF-8');
                receiveRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                receiveRequest.send(JSON.stringify(payload));
                if (receiveRequest.status < 200 || receiveRequest.status >= 300) return false;
                const result = JSON.parse(receiveRequest.responseText || '{}');
                return result.result === true || String(result.status || '').toLowerCase() === 'ok';
              } catch {}
              return false;
            })()
            """;
        var result = await FtmsWebView.ExecuteScriptAsync(script);
        var success = string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
        if (success && _monitor is not null) await _monitor.SyncNowAsync(_lifetime.Token);
        return success;
    }

    private void UpdateDashboard(DashboardSummary s)
    {
        NewValue.Text = (s.New + s.Assigned).ToString("N0"); InProgressValue.Text = s.InProgress.ToString("N0");
        PausedValue.Text = s.Paused.ToString("N0"); ClosedValue.Text = (s.Completed + s.Closed).ToString("N0");
        SlaValue.Text = (s.SlaRisk + s.SlaViolated).ToString("N0");
        var newCount = s.New + s.Assigned;
        var closedCount = s.Completed + s.Closed;
        var total = Math.Max(1, newCount + s.InProgress + s.Paused + closedCount);
        NewChartColumn.Width = new GridLength(Math.Max(0.01, (double)newCount / total), GridUnitType.Star);
        InProgressChartColumn.Width = new GridLength(Math.Max(0.01, (double)s.InProgress / total), GridUnitType.Star);
        PausedChartColumn.Width = new GridLength(Math.Max(0.01, (double)s.Paused / total), GridUnitType.Star);
        ClosedChartColumn.Width = new GridLength(Math.Max(0.01, (double)closedCount / total), GridUnitType.Star);
    }

    private const string ApiObserverScript = """
        (() => { if (window.__ftmsCompanionInstalled) return; window.__ftmsCompanionInstalled = true;
          const watched=['GetListRequestV12','GetListCasesV12','GetListAlarm','GetListCasesRequest']; const hit=u=>watched.some(x=>String(u||'').includes(x));
          let notifyTimer; const notify=()=>{clearTimeout(notifyTimer);notifyTimer=setTimeout(()=>window.chrome.webview.postMessage('ftms-api-updated'),500);};
          const f=window.fetch; window.fetch=async(...a)=>{const r=await f(...a);if(hit(a[0]?.url||a[0]))notify();return r;};
          const o=XMLHttpRequest.prototype.open,s=XMLHttpRequest.prototype.send; XMLHttpRequest.prototype.open=function(m,u,...r){this.__u=u;return o.call(this,m,u,...r);};
          XMLHttpRequest.prototype.send=function(...a){this.addEventListener('load',()=>{if(hit(this.__u)&&!this.__ftmsCompanionInternal)notify();});return s.apply(this,a);}; })();
        """;

    private const string AutoLoginScript = """
        (() => {
          if (window.__ftmsAutoLoginInstalled) return;
          window.__ftmsAutoLoginInstalled = true;
          const normalize = value => String(value || '').replace(/\s+/g, ' ').trim().toLowerCase();
          const clickFptCorporation = () => {
            const clickable = Array.from(document.querySelectorAll(
              'button, a, [role="button"], input[type="button"], input[type="submit"], [tabindex]'));
            let target = clickable.find(element => normalize(
              element.innerText || element.textContent || element.value || element.getAttribute('aria-label'))
              .includes('fpt corporation'));
            if (!target) {
              const label = Array.from(document.querySelectorAll('div, span, p, h1, h2, h3, strong'))
                .find(element => normalize(element.textContent).includes('fpt corporation'));
              target = label?.closest('button, a, [role="button"], [tabindex]') || label;
            }
            if (!target) return false;
            target.click();
            return true;
          };
          if (clickFptCorporation()) return;
          let attempts = 0;
          const timer = setInterval(() => {
            attempts++;
            if (clickFptCorporation() || attempts >= 120) clearInterval(timer);
          }, 500);
        })();
        """;

    private const string BlockBotScript = """
        (() => {
          const blockedSource = 'https://ftmslite.fpt.vn/agent-ai/js/bot.js';
          const blockedSelector = `script[src^="${blockedSource}"], [id*="agent-ai" i], [class*="agent-ai" i], [id*="chatbot" i], [class*="chatbot" i]`;
          const removeBot = root => {
            if (!(root instanceof Element)) return;
            if (root.matches(blockedSelector)) { root.remove(); return; }
            root.querySelectorAll(blockedSelector).forEach(element => element.remove());
          };
          removeBot(document.documentElement);
          new MutationObserver(records => {
            for (const record of records) for (const node of record.addedNodes) removeBot(node);
          }).observe(document.documentElement, { childList: true, subtree: true });
        })();
        """;

}
