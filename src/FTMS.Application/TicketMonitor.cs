using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketMonitor(IFtmsClient client, ITicketStore store, INotificationSender sender,
    TicketChangeDetector detector, AppSettings settings)
{
    private readonly Dictionary<string, TicketSnapshot> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
    private int _lastCleanupDay = -1;
    public event Action<string>? StatusChanged;
    public event Action<DashboardSummary>? SummaryChanged;
    public event Action? DailyCleanupCompleted;
    public DateTimeOffset LastCleanupTime => _lastCleanupTime;
    public int LastCleanupDay => _lastCleanupDay;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        foreach (var item in await store.LoadActiveSnapshotsAsync(cancellationToken)) _active[item.Key] = item.Value;
        await CheckAndRunDailyCleanupAsync(cancellationToken);
    }

    public async Task RunAsync(Func<bool> userIsActive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!userIsActive() && !await client.IsAuthenticatedAsync(cancellationToken))
                {
                    SummaryChanged?.Invoke(UnavailableSummary());
                    await client.BeginLoginRecoveryAsync(cancellationToken);
                }
                else if (!userIsActive())
                {
                    await SyncNowAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (UnauthorizedAccessException)
            {
                SummaryChanged?.Invoke(UnavailableSummary());
                if (!userIsActive()) await client.BeginLoginRecoveryAsync(cancellationToken);
            }
            catch (Exception ex) { StatusChanged?.Invoke($"Loi: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds)), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            await PollOnceAsync(cancellationToken);
            await sender.SendPendingAsync(cancellationToken);
        }
        finally { _syncLock.Release(); }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var apiTickets = await client.GetTicketsAsync(cancellationToken);
        // Closed history is only relevant when it closes a ticket already being monitored.
        var tickets = apiTickets.Where(item => !item.Status.IsTerminal(settings.UnprocessedIsTerminal) ||
            _active.ContainsKey(item.Code)).ToList();
        var events = await detector.DetectAsync(_active, tickets, client, settings, cancellationToken);
        var ihubBase = new Uri(settings.FtmsUrl).GetLeftPart(UriPartial.Authority) + "/ihub";
        foreach (var item in events)
        {
            if (await store.EventExistsAsync(item.EventKey, cancellationToken)) continue;
            _active.TryGetValue(item.TicketCode, out var previous);
            var message = TicketNotificationFilter.ShouldNotify(item, previous)
                ? NotificationFormatter.Format(item, ihubBase)
                : null;
            await store.SaveEventAndEnqueueNotificationAsync(item, message, cancellationToken);
        }
        foreach (var item in tickets)
        {
            var eventEmail = events.LastOrDefault(x => string.Equals(x.TicketCode, item.Code, StringComparison.OrdinalIgnoreCase))?.LatestEmail;
            _active.TryGetValue(item.Code, out var previous);
            var responseEvent = events.LastOrDefault(x =>
                string.Equals(x.TicketCode, item.Code, StringComparison.OrdinalIgnoreCase) &&
                x.EventType == TicketEventType.StatusChanged && x.CurrentStatus == TicketStatus.InProgress &&
                x.Reason.Contains("email mới", StringComparison.OrdinalIgnoreCase));
            var keepResponseReminder = item.Status == TicketStatus.InProgress;
            var snapshot = item with
            {
                LatestEmail = eventEmail ?? (previous?.LatestEmail.IsExcluded() == true ? null : previous?.LatestEmail),
                ResponseReminderEmailId = keepResponseReminder
                    ? responseEvent?.LatestEmail?.Id ?? previous?.ResponseReminderEmailId
                    : null,
                ResponseReminderSince = keepResponseReminder
                    ? responseEvent?.LatestEmail?.SentAt ?? responseEvent?.DetectedAt ?? previous?.ResponseReminderSince
                    : null,
                IsTerminal = item.Status.IsTerminal(settings.UnprocessedIsTerminal)
            };
            await store.SaveSnapshotAsync(snapshot, cancellationToken);
            if (snapshot.IsTerminal)
            {
                _active.Remove(snapshot.Code);
                await store.MarkTerminalAsync(snapshot.Code, DateTimeOffset.Now, cancellationToken);
            }
            else _active[snapshot.Code] = snapshot;
        }
        var currentUser = await client.GetCurrentUserAsync(cancellationToken);
        IReadOnlyList<TicketSnapshot> personal = currentUser is null
            ? []
            : tickets.Where(x => x.AssigneeId == currentUser.UserId).ToList();
        var vietnamToday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Date;
        var currentUserName = NormalizeUserName(currentUser?.UserName);
        var personalClosedToday = currentUser is null ? 0 : apiTickets
            .Where(x => x.Status == TicketStatus.Closed && x.ClosedAt is not null &&
                x.ClosedAt.Value.ToOffset(TimeSpan.FromHours(7)).Date == vietnamToday &&
                (x.ClosedByUserId == currentUser.UserId ||
                 x.ClosedByUserId is null && currentUserName is not null &&
                 NormalizeUserName(x.ClosedByName) == currentUserName))
            .Select(x => x.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        SummaryChanged?.Invoke(new DashboardSummary(
            tickets.Count,
            tickets.Count(x => (x.Status is TicketStatus.New or TicketStatus.Assigned) &&
                (x.AssigneeId is null or 0) && (string.IsNullOrWhiteSpace(x.AssigneeName) || x.AssigneeName.Trim() == "---")),
            tickets.Count(x => x.Status == TicketStatus.Assigned),
            tickets.Count(x => x.Status == TicketStatus.InProgress),
            tickets.Count(x => x.Status == TicketStatus.Paused),
            tickets.Count(x => x.Status == TicketStatus.Completed),
            tickets.Count(x => x.Status == TicketStatus.Closed),
            personal.Count(x => !x.Status.IsTerminal(settings.UnprocessedIsTerminal) && x.SlaType == 2),
            personal.Count(x => !x.Status.IsTerminal(settings.UnprocessedIsTerminal) && x.SlaType == 3),
            currentUser,
            personal.Count(x => x.Status == TicketStatus.Assigned),
            personal.Count(x => x.Status == TicketStatus.InProgress),
            personal.Count(x => x.Status == TicketStatus.Paused),
            personalClosedToday));
        await CheckAndRunDailyCleanupAsync(cancellationToken);
        StatusChanged?.Invoke($"Đồng bộ {tickets.Count} ticket, đang theo dõi {_active.Count}");
    }

    private async Task CheckAndRunDailyCleanupAsync(CancellationToken cancellationToken)
    {
        var vietnamNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var currentDay = vietnamNow.Year * 1000 + vietnamNow.DayOfYear;
        if (_lastCleanupDay == currentDay) return;

        try
        {
            await store.CleanupAsync(settings.TerminalRetentionDays, cancellationToken);
            _lastCleanupDay = currentDay;
            _lastCleanupTime = vietnamNow;
            DailyCleanupCompleted?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Lỗi dọn dẹp hàng ngày: {ex.Message}");
        }
    }

    private static string? NormalizeUserName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized == "---"
            ? null
            : normalized.ToUpperInvariant();
    }

    private static DashboardSummary UnavailableSummary() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}
