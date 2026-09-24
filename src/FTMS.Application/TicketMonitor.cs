using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketMonitor(IFtmsClient client, ITicketStore store, INotificationSender sender,
    TicketChangeDetector detector, AppSettings settings)
{
    private readonly Dictionary<string, TicketSnapshot> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _emailRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;

    public event Action<string>? StatusChanged;
    public event Action<DashboardSummary>? SummaryChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await store.CleanupAsync(settings.TerminalRetentionDays, cancellationToken).ConfigureAwait(false);
        _lastCleanupAt = DateTimeOffset.Now;
        var snapshots = await store.LoadActiveSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in snapshots) _active[item.Key] = item.Value;
    }

    public async Task RunAsync(Func<bool> userIsActive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (settings.IdleDelaySeconds > 0 && userIsActive())
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!await client.IsAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
                {
                    StatusChanged?.Invoke("H\u1ebft phi\u00ean \u0111\u0103ng nh\u1eadp, h\u1ec7 th\u1ed1ng \u0111ang t\u1ef1 \u0111\u1ed9ng \u0111\u0103ng nh\u1eadp l\u1ea1i");
                    await client.BeginLoginRecoveryAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SyncNowAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                StatusChanged?.Invoke("Phi\u00ean FTMS \u0111\u00e3 h\u1ebft h\u1ea1n, \u0111ang k\u1ebft n\u1ed1i \u0111\u0103ng nh\u1eadp l\u1ea1i");
                await client.BeginLoginRecoveryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"L\u1ed7i: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await sender.SendPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var apiTickets = await client.GetTicketsAsync(cancellationToken).ConfigureAwait(false);
        var tickets = apiTickets.Where(item => !item.Status.IsTerminal(settings.UnprocessedIsTerminal) ||
            _active.ContainsKey(item.Code)).ToList();
        var events = await detector.DetectAsync(_active, tickets, client, settings, cancellationToken)
            .ConfigureAwait(false);
        var ihubBase = new Uri(settings.FtmsUrl).GetLeftPart(UriPartial.Authority) + "/ihub";

        var effectiveEvents = new List<TicketEvent>(events.Count);
        var blockedTickets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emailCache = new Dictionary<string, LatestEmail?>(StringComparer.OrdinalIgnoreCase);
        foreach (var detectedEvent in events)
        {
            var item = detectedEvent;
            if (!await store.EventExistsAsync(item.EventKey, cancellationToken).ConfigureAwait(false))
            {
                var enriched = await EnsureLatestEmailAsync(item, emailCache, cancellationToken).ConfigureAwait(false);
                if (enriched is null)
                {
                    blockedTickets.Add(item.TicketCode);
                    continue;
                }
                item = enriched;
                await store.SaveEventAsync(item, cancellationToken).ConfigureAwait(false);
                await store.EnqueueNotificationAsync(item, NotificationFormatter.Format(item, ihubBase), cancellationToken)
                    .ConfigureAwait(false);
            }
            effectiveEvents.Add(item);
        }

        var latestEventByTicket = effectiveEvents
            .GroupBy(item => item.TicketCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var responseEventByTicket = effectiveEvents
            .Where(item => item.EventType == TicketEventType.StatusChanged &&
                item.CurrentStatus == TicketStatus.InProgress &&
                item.Reason.Contains("email m\u1edbi", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.TicketCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<TicketSnapshot>(tickets.Count);

        foreach (var item in tickets)
        {
            if (blockedTickets.Contains(item.Code)) continue;
            latestEventByTicket.TryGetValue(item.Code, out var latestEvent);
            responseEventByTicket.TryGetValue(item.Code, out var responseEvent);
            _active.TryGetValue(item.Code, out var previous);
            var keepResponseReminder = item.Status == TicketStatus.InProgress;
            var snapshot = item with
            {
                LatestEmail = latestEvent?.LatestEmail ??
                    (previous?.LatestEmail.IsExcluded() == true ? null : previous?.LatestEmail),
                ResponseReminderEmailId = keepResponseReminder
                    ? responseEvent?.LatestEmail?.Id ?? previous?.ResponseReminderEmailId
                    : null,
                ResponseReminderSince = keepResponseReminder
                    ? responseEvent?.LatestEmail?.SentAt ?? responseEvent?.DetectedAt ?? previous?.ResponseReminderSince
                    : null,
                IsTerminal = item.Status.IsTerminal(settings.UnprocessedIsTerminal)
            };
            if (!Equals(previous, snapshot)) snapshots.Add(snapshot);

            if (snapshot.IsTerminal) _active.Remove(snapshot.Code);
            else _active[snapshot.Code] = snapshot;
        }

        await store.SaveSnapshotsAsync(snapshots, cancellationToken).ConfigureAwait(false);
        SummaryChanged?.Invoke(BuildSummary(tickets));

        if (DateTimeOffset.Now - _lastCleanupAt >= TimeSpan.FromHours(12))
        {
            await store.CleanupAsync(settings.TerminalRetentionDays, cancellationToken).ConfigureAwait(false);
            _lastCleanupAt = DateTimeOffset.Now;
        }

        var waitingForEmail = blockedTickets.Count == 0
            ? string.Empty
            : $", ch\u1edd n\u1ed9i dung email: {blockedTickets.Count}";
        StatusChanged?.Invoke($"\u0110\u1ed3ng b\u1ed9 {tickets.Count} ticket, \u0111ang theo d\u00f5i {_active.Count}{waitingForEmail}");
    }

    private async Task<TicketEvent?> EnsureLatestEmailAsync(TicketEvent item,
        IDictionary<string, LatestEmail?> emailCache, CancellationToken cancellationToken)
    {
        var current = item.LatestEmail is { } eventEmail && !eventEmail.IsExcluded()
            ? eventEmail
            : item.Snapshot.LatestEmail is { } snapshotEmail && !snapshotEmail.IsExcluded()
                ? snapshotEmail
                : null;
        if (!emailCache.TryGetValue(item.TicketCode, out var detail))
        {
            if (!_emailRetryAfter.TryGetValue(item.TicketCode, out var retryAt) || retryAt <= DateTimeOffset.Now)
            {
                try
                {
                    detail = await client.GetLatestEmailAsync(item.TicketCode, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    detail = null;
                }

                if (HasEmailBody(detail)) _emailRetryAfter.Remove(item.TicketCode);
                else _emailRetryAfter[item.TicketCode] = DateTimeOffset.Now.AddSeconds(2);
            }
            emailCache[item.TicketCode] = detail;
        }

        if (!HasEmailBody(detail)) return null;
        var latest = detail!;
        var merged = latest with
        {
            Id = string.IsNullOrWhiteSpace(latest.Id) ? current?.Id : latest.Id,
            SentAt = latest.SentAt ?? current?.SentAt,
            From = string.IsNullOrWhiteSpace(latest.From) ? current?.From : latest.From,
            Subject = string.IsNullOrWhiteSpace(latest.Subject) ? current?.Subject : latest.Subject
        };
        return item with { LatestEmail = merged, Snapshot = item.Snapshot with { LatestEmail = merged } };
    }

    private static bool HasEmailBody(LatestEmail? email)
    {
        if (email is null || email.IsExcluded() || string.IsNullOrWhiteSpace(email.Body)) return false;
        return !string.IsNullOrWhiteSpace(NotificationFormatter.CleanEmail(email.Body));
    }

    private static DashboardSummary BuildSummary(IReadOnlyList<TicketSnapshot> tickets)
    {
        var newCount = 0;
        var assigned = 0;
        var inProgress = 0;
        var paused = 0;
        var completed = 0;
        var closed = 0;
        var slaRisk = 0;
        var slaViolated = 0;

        foreach (var ticket in tickets)
        {
            switch (ticket.Status)
            {
                case TicketStatus.New: newCount++; break;
                case TicketStatus.Assigned: assigned++; break;
                case TicketStatus.InProgress: inProgress++; break;
                case TicketStatus.Paused: paused++; break;
                case TicketStatus.Completed: completed++; break;
                case TicketStatus.Closed: closed++; break;
            }

            if (ticket.SlaType == 2) slaRisk++;
            else if (ticket.SlaType == 3) slaViolated++;
        }

        return new DashboardSummary(tickets.Count, newCount, assigned, inProgress, paused, completed, closed,
            slaRisk, slaViolated);
    }
}
