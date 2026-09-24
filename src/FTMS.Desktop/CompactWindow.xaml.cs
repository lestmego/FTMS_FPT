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
    private WebViewFtmsClient? _ftmsClient;
    private bool _monitorStarted;
    private bool _targetNavigationPending;
    private bool _manualLoginNotificationShown;
    private long _telegramUpdateOffset;
    private bool _checkingTelegram;
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;

    public CompactWindow()
    {
        InitializeComponent(); _settingsStore.Load(); Loaded += InitializeAsync;
        Closed += (_, _) => { _lifetime.Cancel(); _refreshTimer.Stop(); _trayIcon?.Dispose(); };
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
        FtmsWebView.CoreWebView2.AddWebResourceRequestedFilter(BlockedBotScript, CoreWebView2WebResourceContext.Script);
        FtmsWebView.CoreWebView2.WebResourceRequested += (_, args) =>
        {
            if (!args.Request.Uri.StartsWith(BlockedBotScript, StringComparison.OrdinalIgnoreCase)) return;
            args.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: application/javascript");
        };
        FtmsWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ApiObserverScript);
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BlockBotScript);
        var appSettings = new AppSettings { FtmsUrl = FtmsUrl, PollIntervalSeconds = 2, IdleDelaySeconds = 0 };
        var databasePath = Path.Combine(root, "ftms.db");
        _ftmsClient = new WebViewFtmsClient(FtmsWebView, FtmsUrl);
        _ftmsClient.LoginRecoveryStatusChanged += OnLoginRecoveryStatusChanged;
        var telegram = new TelegramOutboxSender(databasePath, () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId), _http);
        _monitor = new TicketMonitor(_ftmsClient, new SqliteTicketStore(databasePath), telegram, new TicketChangeDetector(), appSettings);
        _monitor.StatusChanged += message => Dispatcher.Invoke(() => MonitorText.Text = message);
        _monitor.SummaryChanged += summary => Dispatcher.Invoke(() => UpdateDashboard(summary));
        await _monitor.InitializeAsync(_lifetime.Token);
        ApplyRefreshSettings();
        _telegramTimer.Start();
        FtmsWebView.Source = new Uri(FtmsUrl);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? message; try { message = JsonSerializer.Deserialize<string>(e.WebMessageAsJson); } catch (JsonException) { return; }
            if (message == "ftms-api-updated" && _monitor is not null) await _monitor.SyncNowAsync(_lifetime.Token);
        }
        catch (Exception ex) { MonitorText.Text = $"L\u1ed7i \u0111\u1ed3ng b\u1ed9 th\u1eddi gian th\u1ef1c: {ex.Message}"; }
    }

    private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested || _ftmsClient is null) return;
        if (!e.IsSuccess)
        {
            SessionText.Text = "L\u1ed7i k\u1ebft n\u1ed1i";
            MonitorText.Text = $"Kh\u00f4ng th\u1ec3 m\u1edf FTMS: {e.WebErrorStatus}";
            return;
        }

        try
        {
            var uri = FtmsWebView.Source;
            if (uri is null) return;

            if (_ftmsClient.IsTargetUri(uri))
            {
                _targetNavigationPending = false;
                _manualLoginNotificationShown = false;
                _ftmsClient.NotifyTargetReached();
                SetSessionStatus("\u0110\u00e3 k\u1ebft n\u1ed1i", "#4AA47B");
                StartMonitorOnce();
                return;
            }

            if (WebViewLoginRecovery.IsFtmsIhubUri(uri))
            {
                SetSessionStatus("\u0110ang m\u1edf danh s\u00e1ch FTMS", "#D9A441");
                if (!_targetNavigationPending)
                {
                    _targetNavigationPending = true;
                    FtmsWebView.Source = new Uri(FtmsUrl);
                }
                return;
            }

            if (WebViewLoginRecovery.IsLoginUri(uri) || WebViewLoginRecovery.IsAdfsUri(uri))
            {
                SetSessionStatus("\u0110ang \u0111\u0103ng nh\u1eadp", "#D9A441");
                await _ftmsClient.BeginLoginRecoveryAsync(_lifetime.Token);
                return;
            }

            SetSessionStatus("\u0110ang k\u1ebft n\u1ed1i", "#D9A441");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { MonitorText.Text = $"Kh\u00f4ng th\u1ec3 t\u1ef1 \u0111\u1ed9ng \u0111\u0103ng nh\u1eadp: {ex.Message}"; }
    }

    private void OnLoginRecoveryStatusChanged(LoginRecoveryStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            MonitorText.Text = status.Message;
            SetSessionStatus(status.RequiresUserAction ? "C\u1ea7n \u0111\u0103ng nh\u1eadp" : "\u0110ang \u0111\u0103ng nh\u1eadp", "#D9A441");
            if (!status.RequiresUserAction || _manualLoginNotificationShown || _trayIcon is null) return;
            _manualLoginNotificationShown = true;
            _trayIcon.BalloonTipTitle = "FTMS c\u1ea7n x\u00e1c th\u1ef1c";
            _trayIcon.BalloonTipText = "Vui l\u00f2ng ho\u00e0n t\u1ea5t \u0111\u0103ng nh\u1eadp ho\u1eb7c MFA trong c\u1eeda s\u1ed5 FTMS Companion.";
            _trayIcon.ShowBalloonTip(4000);
        });
    }

    private void SetSessionStatus(string text, string color)
    {
        SessionText.Text = text;
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void StartMonitorOnce()
    {
        if (_monitorStarted || _monitor is null) return;
        _monitorStarted = true;
        MonitorText.Text = "B\u1eaft \u0111\u1ea7u theo d\u00f5i ticket";
        _ = _monitor.RunAsync(() => DateTimeOffset.Now - _lastUserActivity < TimeSpan.FromSeconds(8), _lifetime.Token);
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
        GlobalNewValue.Text = s.New.ToString("N0");

        if (!s.HasCurrentUser)
        {
            CurrentUserName.Text = "Đang nhận diện người dùng…";
            CurrentUserName.ToolTip = null;
            PersonalTotalValue.Text = "—";
            PersonalAssignedValue.Text = "—";
            PersonalInProgressValue.Text = "—";
            PersonalPausedValue.Text = "—";
            PersonalClosedTodayValue.Text = "—";
            SlaRiskValue.Text = "—";
            SlaViolatedValue.Text = "—";
            System.Windows.Automation.AutomationProperties.SetName(DashboardCard,
                $"Ticket mới tất cả: {s.New}. Đang nhận diện người dùng. Chưa có dữ liệu SLA cá nhân.");
            return;
        }

        var userName = string.IsNullOrWhiteSpace(s.CurrentUser!.UserName) ? "Người dùng hiện tại" : s.CurrentUser.UserName;
        CurrentUserName.Text = userName;
        CurrentUserName.ToolTip = userName;
        PersonalTotalValue.Text = s.PersonalWorkloadTotal.ToString("N0");
        PersonalAssignedValue.Text = s.PersonalAssigned.ToString("N0");
        PersonalInProgressValue.Text = s.PersonalInProgress.ToString("N0");
        PersonalPausedValue.Text = s.PersonalPaused.ToString("N0");
        PersonalClosedTodayValue.Text = s.PersonalClosedToday.ToString("N0");
        SlaRiskValue.Text = s.SlaRisk.ToString("N0");
        SlaViolatedValue.Text = s.SlaViolated.ToString("N0");
        System.Windows.Automation.AutomationProperties.SetName(DashboardCard,
            $"Ticket mới tất cả: {s.New}. Công việc của {userName}: {s.PersonalAssigned} phân công, " +
            $"{s.PersonalInProgress} đang thực hiện, {s.PersonalPaused} tạm ngưng, {s.PersonalClosedToday} đã đóng hôm nay. " +
            $"SLA của {userName}: {s.SlaRisk} sắp hạn, {s.SlaViolated} quá hạn.");
    }

    private const string ApiObserverScript = """
        (() => { if (window.__ftmsCompanionInstalled) return; window.__ftmsCompanionInstalled = true;
          const watched=['GetListRequestV12','GetListCasesV12','GetListAlarm','GetListCasesRequest']; const hit=u=>watched.some(x=>String(u||'').includes(x));
          const f=window.fetch; window.fetch=async(...a)=>{const r=await f(...a);if(hit(a[0]?.url||a[0])){try{window.__ftmsLastTicketResponse=await r.clone().text();}catch{}window.chrome.webview.postMessage('ftms-api-updated');}return r;};
          const o=XMLHttpRequest.prototype.open,s=XMLHttpRequest.prototype.send; XMLHttpRequest.prototype.open=function(m,u,...r){this.__u=u;return o.call(this,m,u,...r);};
          XMLHttpRequest.prototype.send=function(...a){this.addEventListener('load',()=>{if(hit(this.__u)){try{window.__ftmsLastTicketResponse=this.responseText;}catch{}window.chrome.webview.postMessage('ftms-api-updated');}});return s.apply(this,a);}; })();
        """;

    private const string BlockBotScript = """
        (() => {
          const blockedSource = 'https://ftmslite.fpt.vn/agent-ai/js/bot.js';
          const removeBot = () => {
            document.querySelectorAll(`script[src^="${blockedSource}"]`).forEach(element => element.remove());
            document.querySelectorAll('[id*="agent-ai" i], [class*="agent-ai" i], [id*="chatbot" i], [class*="chatbot" i]').forEach(element => element.remove());
          };
          removeBot();
          new MutationObserver(removeBot).observe(document.documentElement, { childList: true, subtree: true });
        })();
        """;

}
