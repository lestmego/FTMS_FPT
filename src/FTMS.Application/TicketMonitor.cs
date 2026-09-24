using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketMonitor(IFtmsClient client, ITicketStore store, INotificationSender sender,
    TicketChangeDetector detector, AppSettings settings)
{
    private readonly Dictionary<string, TicketSnapshot> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    public event Action<string>? StatusChanged;
    public event Action<DashboardSummary>? SummaryChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        foreach (var item in await store.LoadActiveSnapshotsAsync(cancellationToken)) _active[item.Key] = item.Value;
    }

    public async Task RunAsync(Func<bool> userIsActive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await client.IsAuthenticatedAsync(cancellationToken))
                {
                    SummaryChanged?.Invoke(UnavailableSummary());
                    await client.BeginLoginRecoveryAsync(cancellationToken);
                }
                else
                {
                    await SyncNowAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (UnauthorizedAccessException)
            {
                SummaryChanged?.Invoke(UnavailableSummary());
                await client.BeginLoginRecoveryAsync(cancellationToken);
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
            await store.SaveEventAsync(item, cancellationToken);
            _active.TryGetValue(item.TicketCode, out var previous);
            if (TicketNotificationFilter.ShouldNotify(item, previous))
                await store.EnqueueNotificationAsync(item, NotificationFormatter.Format(item, ihubBase), cancellationToken);
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
        var personalClosedToday = currentUser is null ? 0 : apiTickets.Count(x =>
            x.Status == TicketStatus.Closed && x.AssigneeId == currentUser.UserId && x.UpdatedAt is not null &&
            x.UpdatedAt.Value.ToOffset(TimeSpan.FromHours(7)).Date == vietnamToday);
        SummaryChanged?.Invoke(new DashboardSummary(
            tickets.Count,
            tickets.Count(x => (x.Status is TicketStatus.New or TicketStatus.Assigned) &&
                (x.AssigneeId is null or 0) && (string.IsNullOrWhiteSpace(x.AssigneeName) || x.AssigneeName.Trim() == "---")),
            tickets.Count(x => x.Status == TicketStatus.Assigned),
            tickets.Count(x => x.Status == TicketStatus.InProgress),
            tickets.Count(x => x.Status == TicketStatus.Paused),
            tickets.Count(x => x.Status == TicketStatus.Completed),
            tickets.Count(x => x.Status == TicketStatus.Closed),
            personal.Count(x => x.SlaType == 2),
            personal.Count(x => x.SlaType == 3),
            currentUser,
            personal.Count(x => x.Status == TicketStatus.Assigned),
            personal.Count(x => x.Status == TicketStatus.InProgress),
            personal.Count(x => x.Status == TicketStatus.Paused),
            personalClosedToday));
        await store.CleanupAsync(settings.TerminalRetentionDays, cancellationToken);
        StatusChanged?.Invoke($"Đồng bộ {tickets.Count} ticket, đang theo dõi {_active.Count}");
    }

    private static DashboardSummary UnavailableSummary() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}
