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
    private readonly SettingsStore _settingsStore = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _telegramTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly HttpClient _http;
    private readonly string _telegramOffsetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion", "telegram.offset");
    private TicketMonitor? _monitor;
    private WebViewFtmsClient? _ftmsClient;
    private WebViewLoginRecovery? _displayLoginRecovery;
    private WebViewFtmsClient? _visibleIdentityClient;
    private CancellationTokenSource? _accountLifetime;
    private long? _expectedAccountId;
    private long? _activeAccountId;
    private int _visibleNavigationGeneration;
    private int _hiddenReloadAttempts;
    private bool _monitorStarting;
    private bool _monitorStarted;
    private bool _isListPage;
    private bool _settingsOpen;
    private bool _refreshInProgress;
    private bool _manualLoginNotificationShown;
    private long _telegramUpdateOffset;
    private bool _checkingTelegram;
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;

    public CompactWindow()
    {
        InitializeComponent(); _settingsStore.Load(); _http = TelegramHttpClientFactory.Create(() => _settingsStore.Current); Loaded += InitializeAsync;
        Closed += (_, _) => { _lifetime.Cancel(); _accountLifetime?.Cancel(); _refreshTimer.Stop(); _telegramTimer.Stop(); _http.Dispose(); _trayIcon?.Dispose(); };
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
        FtmsWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        FtmsWebView.CoreWebView2.WebResourceResponseReceived += OnVisibleFtmsResponseReceived;
        FtmsWebView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            _isListPage = false;
            MarkUserActivity();
            _visibleNavigationGeneration++;
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var next) ||
                !WebViewLoginRecovery.IsFtmsIhubUri(next)) DeactivateAccount();
        };
        FtmsWebView.CoreWebView2.SourceChanged += (_, _) => UpdateCurrentPage();
        MonitorWebView.NavigationCompleted += OnMonitorNavigationCompleted;
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FtmsUserActivityScript.Value);
        _displayLoginRecovery = new WebViewLoginRecovery(FtmsWebView, new Uri(FtmsUrl));
        _displayLoginRecovery.StatusChanged += OnLoginRecoveryStatusChanged;
        _ftmsClient = new WebViewFtmsClient(MonitorWebView, FtmsUrl);
        _visibleIdentityClient = new WebViewFtmsClient(FtmsWebView, FtmsUrl);
        _ftmsClient.LoginRecoveryStatusChanged += OnMonitorLoginRecoveryStatusChanged;
        ApplyRefreshSettings();
        _telegramTimer.Start();
        FtmsWebView.Source = new Uri(FtmsUrl);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? message; try { message = JsonSerializer.Deserialize<string>(e.WebMessageAsJson); } catch (JsonException) { return; }
            if (message == "ftms-user-activity") { MarkUserActivity(); return; }
        }
        catch (Exception ex) { MonitorText.Text = $"L\u1ed7i \u0111\u1ed3ng b\u1ed9 th\u1eddi gian th\u1ef1c: {ex.Message}"; }
    }

    private async void OnVisibleFtmsResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        var monitor = _monitor;
        var accountLifetime = _accountLifetime;
        var accountId = _activeAccountId;
        if (!_monitorStarted || monitor is null || accountLifetime is null ||
            accountId is null || accountLifetime.IsCancellationRequested) return;
        if (e.Response.StatusCode is < 200 or >= 300) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "ftms.fpt.net", StringComparison.OrdinalIgnoreCase)) return;
        var path = uri.AbsolutePath;
        if (!path.Contains("GetListRequestV12", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("GetListCasesV12", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("GetListAlarm", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("GetListCasesRequest", StringComparison.OrdinalIgnoreCase)) return;

        if (_activeAccountId != accountId) return;
        try { await Task.Run(() => monitor.SyncNowAsync(accountLifetime.Token)); }
        catch (OperationCanceledException) when (accountLifetime.IsCancellationRequested) { }
        catch (Exception ex) { MonitorText.Text = $"Lỗi đồng bộ FTMS: {ex.Message}"; }
    }

    private async void OnMonitorNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested || _ftmsClient is null || _expectedAccountId is null) return;
        if (!e.IsSuccess)
        {
            MonitorText.Text = $"WebView giám sát không mở được FTMS: {e.WebErrorStatus}";
            return;
        }

        var uri = MonitorWebView.Source;
        if (uri is null) return;
        if (WebViewLoginRecovery.IsFtmsIhubUri(uri))
        {
            _ftmsClient.NotifyTargetReached();
            var expectedAccountId = _expectedAccountId.Value;
            CurrentUserIdentity? hiddenIdentity;
            try { hiddenIdentity = await _ftmsClient.GetCurrentUserAsync(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            if (_expectedAccountId != expectedAccountId) return;
            if (hiddenIdentity?.UserId != expectedAccountId)
            {
                if (_hiddenReloadAttempts++ < 2)
                    MonitorWebView.CoreWebView2?.Reload();
                else
                    MonitorText.Text = "Tài khoản WebView giám sát không khớp; đã tạm dừng để tránh lẫn dữ liệu.";
                return;
            }
            _hiddenReloadAttempts = 0;
            await StartMonitorOnceAsync(expectedAccountId);
            return;
        }
        if (WebViewLoginRecovery.IsLoginUri(uri) || WebViewLoginRecovery.IsAdfsUri(uri))
        {
            try { await _ftmsClient.BeginLoginRecoveryAsync(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { MonitorText.Text = $"Giám sát FTMS cần đăng nhập: {ex.Message}"; }
        }
    }

    private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested || _displayLoginRecovery is null) return;
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

            if (WebViewLoginRecovery.IsFtmsIhubUri(uri))
            {
                UpdateCurrentPage();
                _manualLoginNotificationShown = false;
                _displayLoginRecovery.NotifyTargetReached();
                _visibleIdentityClient!.NotifyTargetReached();
                var navigationGeneration = _visibleNavigationGeneration;
                CurrentUserIdentity? visibleIdentity = null;
                for (var attempt = 0; attempt < 3 && visibleIdentity is null; attempt++)
                {
                    visibleIdentity = await _visibleIdentityClient.GetCurrentUserAsync(_lifetime.Token);
                    if (visibleIdentity is null) await Task.Delay(1000, _lifetime.Token);
                }
                if (navigationGeneration != _visibleNavigationGeneration) return;
                if (visibleIdentity is null)
                {
                    // A ticket detail can omit the list page's identity globals. Keep an already
                    // verified session; logout/navigation away from iHUB cancels it separately.
                    if (_expectedAccountId is null)
                    {
                        SetSessionStatus("Chưa xác định tài khoản", "#D9A441");
                        MonitorText.Text = "Chưa xác định được tài khoản FTMS; giám sát đang tạm dừng.";
                    }
                    return;
                }
                SetSessionStatus("Đã kết nối", "#4AA47B");
                if (_expectedAccountId != visibleIdentity.UserId)
                {
                    DeactivateAccount();
                    _expectedAccountId = visibleIdentity.UserId;
                    _hiddenReloadAttempts = 0;
                    if (MonitorWebView.Source is null ||
                        !WebViewLoginRecovery.IsFtmsIhubUri(MonitorWebView.Source))
                        MonitorWebView.Source = new Uri(FtmsUrl);
                    else
                        MonitorWebView.CoreWebView2?.Reload();
                }
                return;
            }

            if (WebViewLoginRecovery.IsLoginUri(uri) || WebViewLoginRecovery.IsAdfsUri(uri))
            {
                SetSessionStatus("\u0110ang \u0111\u0103ng nh\u1eadp", "#D9A441");
                await _displayLoginRecovery.BeginAsync(_lifetime.Token);
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

    private void OnMonitorLoginRecoveryStatusChanged(LoginRecoveryStatus status)
    {
        Dispatcher.Invoke(() => MonitorText.Text = status.RequiresUserAction
            ? "Giám sát cần xác thực FTMS; hãy đăng nhập ở trang FTMS đang hiển thị."
            : status.Message);
    }

    private void SetSessionStatus(string text, string color)
    {
        SessionText.Text = text;
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void DeactivateAccount()
    {
        _accountLifetime?.Cancel();
        _accountLifetime = null;
        _expectedAccountId = null;
        _activeAccountId = null;
        _monitor = null;
        _monitorStarted = false;
        _monitorStarting = false;
        UpdateDashboard(new DashboardSummary(0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    private async Task StartMonitorOnceAsync(long accountId)
    {
        if (_monitorStarted || _monitorStarting || _expectedAccountId != accountId || _ftmsClient is null) return;
        _monitorStarting = true;
        var accountLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _accountLifetime = accountLifetime;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
            var databasePath = Path.Combine(root, $"ftms-user-{accountId}.db");
            var settings = new AppSettings { FtmsUrl = FtmsUrl, PollIntervalSeconds = 2, IdleDelaySeconds = 0 };
            var telegram = new TelegramOutboxSender(databasePath,
                () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId), _http);
            var monitor = new TicketMonitor(_ftmsClient, new SqliteTicketStore(databasePath),
                telegram, new TicketChangeDetector(), settings);
            monitor.StatusChanged += message => Dispatcher.BeginInvoke(() =>
            {
                if (_activeAccountId == accountId) MonitorText.Text = message;
            });
            monitor.SummaryChanged += summary => Dispatcher.BeginInvoke(() =>
            {
                if (_activeAccountId == accountId) UpdateDashboard(summary);
            });
            await Task.Run(() => monitor.InitializeAsync(accountLifetime.Token));
            if (accountLifetime.IsCancellationRequested || _expectedAccountId != accountId) return;
            _monitor = monitor;
            _activeAccountId = accountId;
            _monitorStarted = true;
            MonitorText.Text = "Bắt đầu theo dõi ticket";
            _ = Task.Run(() => monitor.RunAsync(() => false, accountLifetime.Token));
        }
        catch (OperationCanceledException) when (accountLifetime.IsCancellationRequested) { }
        catch (Exception ex) { MonitorText.Text = $"Không thể khởi tạo giám sát: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_accountLifetime, accountLifetime)) _monitorStarting = false;
        }
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        _settingsOpen = true;
        try
        {
            if (new CompactSettingsWindow(_settingsStore) { Owner = this }.ShowDialog() == true)
                ApplyRefreshSettings();
        }
        finally { _settingsOpen = false; MarkUserActivity(); }
    }
    private void OpenFtms(object sender, RoutedEventArgs e) => FtmsWebView.Source = new Uri(FtmsUrl);
    private async void ReloadFtms(object sender, RoutedEventArgs e) => await RefreshTicketGridAsync(automatic: false);
    private void OnUserActivity(object sender, InputEventArgs e) => MarkUserActivity();
    private void MarkUserActivity() => _lastUserActivity = DateTimeOffset.UtcNow;
    private bool IsUserBusy() => _settingsOpen || !_isListPage ||
        DateTimeOffset.UtcNow - _lastUserActivity < TimeSpan.FromSeconds(15);

    private void UpdateCurrentPage()
    {
        _isListPage = Uri.TryCreate(FtmsWebView.CoreWebView2?.Source, UriKind.Absolute, out var uri) &&
            WebViewLoginRecovery.IsFtmsIhubUri(uri) &&
            string.Equals(uri.AbsolutePath.TrimEnd('/'), "/ihub/list", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRefreshSettings()
    {
        _refreshTimer.Stop(); if (!_settingsStore.Current.AutoRefreshEnabled) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settingsStore.Current.AutoRefreshSeconds, 5, 3600)); _refreshTimer.Start();
    }

    private async void RunAutoRefresh()
    {
        if (!IsVisible || IsUserBusy()) return;
        await RefreshTicketGridAsync(automatic: true);
    }

    private async Task RefreshTicketGridAsync(bool automatic)
    {
        if (FtmsWebView.CoreWebView2 is null || !_isListPage || _refreshInProgress) return;
        _refreshInProgress = true;
        var automaticValue = automatic ? "true" : "false";
        var script = $$"""
            (() => {
              if (location.hostname.toLowerCase() !== 'ftms.fpt.net' ||
                  !/^\/ihub\/list\/?$/i.test(location.pathname)) return;
              if ({{automaticValue}} &&
                  Date.now() - (window.__ftmsCompanionLastInputAt || 0) < 15000) return;
              document.querySelector('a.k-pager-refresh.k-link')?.click();
            })()
            """;
        try
        {
            await FtmsWebView.ExecuteScriptAsync(script);
        }
        catch (Exception ex) { MonitorText.Text = $"Không thể làm mới danh sách: {ex.Message}"; }
        finally { _refreshInProgress = false; }
    }

    private async Task CheckTelegramActionsAsync()
    {
        var token = _settingsStore.Current.TelegramToken;
        if (_checkingTelegram || string.IsNullOrWhiteSpace(token) || _activeAccountId is null) return;
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
        var accountId = _activeAccountId;
        if (MonitorWebView.CoreWebView2 is null || accountId is null ||
            _accountLifetime?.IsCancellationRequested != false) return false;
        var serializedCode = JsonSerializer.Serialize(code);
        var script = $$"""
            (() => {
              const code = {{serializedCode}};
              const expectedUserId = {{accountId.Value}};
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
                if (!ticketId || Number(globalThis.userID) !== expectedUserId ||
                    typeof Username === 'undefined') return false;
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
        var result = await MonitorWebView.ExecuteScriptAsync(script);
        if (_activeAccountId != accountId || _accountLifetime?.IsCancellationRequested != false) return false;
        var success = string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
        if (success && _monitor is not null && _accountLifetime is not null)
        {
            var monitor = _monitor;
            var accountLifetime = _accountLifetime;
            await Task.Run(() => monitor.SyncNowAsync(accountLifetime.Token));
        }
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

}
