using System.Security.Cryptography;
using System.Text;
using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketChangeDetector
{
    public async Task<IReadOnlyList<TicketEvent>> DetectAsync(IReadOnlyDictionary<string, TicketSnapshot> previous,
        IReadOnlyList<TicketSnapshot> current, IFtmsClient client, AppSettings settings, CancellationToken cancellationToken)
    {
        var events = new List<TicketEvent>();
        foreach (var snapshot in current)
        {
            previous.TryGetValue(snapshot.Code, out var old);
            if (old is null)
            {
                if (snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal)) continue;
                var initialEmail = await client.GetLatestEmailAsync(snapshot.Code, cancellationToken);
                var listEmail = snapshot.LatestEmail.IsExcluded() ? null : snapshot.LatestEmail;
                var email = initialEmail.IsExcluded() ? listEmail : initialEmail is null ? listEmail : initialEmail with
                {
                    From = listEmail?.From ?? initialEmail.From,
                    Subject = initialEmail.Subject ?? listEmail?.Subject,
                    Body = initialEmail.Body ?? listEmail?.Body
                };
                events.Add(Create(snapshot with { LatestEmail = email }, TicketEventType.Created, null, "Phát hiện ticket mới", email));
                continue;
            }

            LatestEmail? fetchedEmail = null;
            var emailFetched = false;
            var emailIncludedInEvent = false;
            async Task<LatestEmail?> LatestEmailAsync()
            {
                if (emailFetched) return fetchedEmail;
                fetchedEmail = SelectEmail(await client.GetLatestEmailAsync(snapshot.Code, cancellationToken), snapshot.LatestEmail, old.LatestEmail);
                emailFetched = true;
                return fetchedEmail;
            }

            if (old.Status != snapshot.Status)
            {
                var email = await LatestEmailAsync();
                var history = await client.GetLatestStatusHistoryAsync(snapshot.Code, snapshot.Status, cancellationToken);
                var reason = IsNewEmail(old.LatestEmail, email) ? "Có email mới gửi tới" : "FTMS không cung cấp nguyên nhân thay đổi";
                var isTerminal = snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal);
                var enriched = snapshot with { LatestEmail = email ?? (old.LatestEmail.IsExcluded() ? null : old.LatestEmail), IsTerminal = isTerminal };
                events.Add(Create(enriched, isTerminal ? TicketEventType.Terminal : TicketEventType.StatusChanged,
                    old.Status, reason, enriched.LatestEmail,
                    discriminator: history?.OccurredAt?.ToString("O") ?? snapshot.UpdatedAt?.ToString("O") ?? string.Empty,
                    changedBy: history?.Actor ?? snapshot.UpdatedBy, changedAt: history?.OccurredAt ?? snapshot.UpdatedAt));
                emailIncludedInEvent = IsNewEmail(old.LatestEmail, email);
            }

            if (!snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal) &&
                (old.AssigneeId != snapshot.AssigneeId || old.DepartmentId != snapshot.DepartmentId))
            {
                var email = await LatestEmailAsync();
                var enriched = snapshot with { LatestEmail = email ?? (old.LatestEmail.IsExcluded() ? null : old.LatestEmail) };
                var discriminator = $"{old.AssigneeId}>{snapshot.AssigneeId}|{old.DepartmentId}>{snapshot.DepartmentId}";
                events.Add(Create(enriched, TicketEventType.AssignmentChanged, old.Status, "Người xử lý hoặc phòng ban đã thay đổi",
                    enriched.LatestEmail, discriminator, previousAssigneeName: old.AssigneeName,
                    previousDepartmentName: old.DepartmentName));
                emailIncludedInEvent |= IsNewEmail(old.LatestEmail, email);
            }

            var isWaitingForReceiver = snapshot.Status is TicketStatus.New or TicketStatus.Assigned &&
                snapshot.AssigneeId is null or 0 &&
                (string.IsNullOrWhiteSpace(snapshot.AssigneeName) || snapshot.AssigneeName.Trim() == "---");
            if (isWaitingForReceiver && snapshot.CreatedAt is not null)
            {
                var unassignedMinutes = Math.Max(0, (int)(DateTimeOffset.Now - snapshot.CreatedAt.Value).TotalMinutes);
                var reminderBucket = unassignedMinutes / 5;
                if (reminderBucket >= 1)
                {
                    var email = await LatestEmailAsync();
                    var enriched = snapshot with { LatestEmail = email ?? (old.LatestEmail.IsExcluded() ? null : old.LatestEmail) };
                    events.Add(Create(enriched, TicketEventType.UnassignedReminder, old.Status,
                        $"Ticket chưa được nhận sau {unassignedMinutes} phút", null,
                        discriminator: $"unassigned-{reminderBucket}"));
                    emailIncludedInEvent |= IsNewEmail(old.LatestEmail, email);
                }
            }

            if (snapshot.Status == TicketStatus.InProgress &&
                !string.IsNullOrWhiteSpace(old.ResponseReminderEmailId) && old.ResponseReminderSince is not null)
            {
                var responseMinutes = Math.Max(0, (int)(DateTimeOffset.Now - old.ResponseReminderSince.Value).TotalMinutes);
                var responseBucket = responseMinutes / 5;
                if (responseBucket >= 1)
                {
                    var email = await LatestEmailAsync();
                    var enriched = snapshot with
                    {
                        LatestEmail = email ?? (old.LatestEmail.IsExcluded() ? null : old.LatestEmail),
                        ResponseReminderEmailId = old.ResponseReminderEmailId,
                        ResponseReminderSince = old.ResponseReminderSince
                    };
                    events.Add(Create(enriched, TicketEventType.ResponseReminder, old.Status,
                        $"Ticket đã có phản hồi mới {responseMinutes} phút", null,
                        discriminator: $"response-{old.ResponseReminderEmailId}-{responseBucket}"));
                    emailIncludedInEvent |= IsNewEmail(old.LatestEmail, email);
                }
            }

            foreach (var threshold in settings.SlaThresholds)
            {
                if (snapshot.SlaType == 2 && old.SlaDeviationMinutes > threshold &&
                    snapshot.SlaDeviationMinutes is > 0 && snapshot.SlaDeviationMinutes <= threshold)
                {
                    var email = await client.GetLatestEmailAsync(snapshot.Code, cancellationToken);
                    var enriched = snapshot with { LatestEmail = email ?? (old.LatestEmail.IsExcluded() ? null : old.LatestEmail) };
                    events.Add(Create(enriched, TicketEventType.SlaThresholdReached, old.Status, $"Còn {threshold} phút đến hạn SLA", enriched.LatestEmail, threshold.ToString()));
                }
            }

            if (snapshot.SlaType == 3 && old.SlaType != 3 &&
                !snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal))
            {
                var email = await client.GetLatestEmailAsync(snapshot.Code, cancellationToken);
                var enriched = snapshot with { LatestEmail = email ?? (old.LatestEmail.IsExcluded() ? null : old.LatestEmail) };
                events.Add(Create(enriched, TicketEventType.SlaThresholdReached, old.Status, "Ticket đã quá hạn SLA",
                    enriched.LatestEmail, "overdue"));
            }

            if (!emailIncludedInEvent && (old.LatestEmail is null || old.UpdatedAt != snapshot.UpdatedAt))
            {
                var email = await LatestEmailAsync();
                if (IsNewEmail(old.LatestEmail, email) && email is not null && !email.IsExcluded())
                {
                    var enriched = snapshot with { LatestEmail = email };
                    events.Add(Create(enriched, TicketEventType.EmailReceived, old.Status,
                        "FTMS có email mới", email, discriminator: email.Id ?? email.SentAt?.ToString("O") ?? string.Empty));
                }
            }

        }
        return events;
    }

    private static bool IsNewEmail(LatestEmail? old, LatestEmail? current) => current is not null &&
        (old is null || !string.IsNullOrWhiteSpace(current.Id) && current.Id != old.Id || current.SentAt > old.SentAt);

    private static LatestEmail? SelectEmail(params LatestEmail?[] candidates) =>
        candidates.FirstOrDefault(email => email is not null && !email.IsExcluded());

    private static TicketEvent Create(TicketSnapshot snapshot, TicketEventType type, TicketStatus? previous,
        string reason, LatestEmail? email, string discriminator = "", string? changedBy = null, DateTimeOffset? changedAt = null,
        string? previousAssigneeName = null, string? previousDepartmentName = null)
    {
        var raw = $"{snapshot.Code}|{type}|{previous}|{snapshot.Status}|{email?.Id}|{discriminator}";
        return new TicketEvent
        {
            EventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
            TicketCode = snapshot.Code, EventType = type, PreviousStatus = previous,
            CurrentStatus = snapshot.Status, DetectedAt = DateTimeOffset.Now, Reason = reason,
            ChangedBy = changedBy, ChangedAt = changedAt, PreviousAssigneeName = previousAssigneeName,
            PreviousDepartmentName = previousDepartmentName, LatestEmail = email, Snapshot = snapshot
        };
    }
}
