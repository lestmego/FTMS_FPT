using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FTMS.Domain;

namespace FTMS.Application;

public static partial class NotificationFormatter
{
    public static string Format(TicketEvent item, string ihubBaseUrl)
    {
        var ticket = item.Snapshot;
        var text = new StringBuilder();
        text.AppendLine(item.EventType switch
        {
            TicketEventType.Created => "📨 <b>Ticket mới</b>",
            TicketEventType.EmailReceived => "📧 <b>Email mới của ticket</b>",
            TicketEventType.UnassignedReminder => "🔔 <b>Nhắc ticket chưa được nhận</b>",
            TicketEventType.ResponseReminder => "🔔 <b>Nhắc ticket đã có phản hồi mới</b>",
            TicketEventType.SlaThresholdReached => "⏰ <b>Cảnh báo SLA</b>",
            TicketEventType.Terminal => ticket.Status switch
            {
                TicketStatus.Closed => "🔒 <b>Ticket đã đóng</b>",
                TicketStatus.Cancelled => "🚫 <b>Ticket đã hủy</b>",
                TicketStatus.Unprocessed => "⛔ <b>Ticket không xử lý</b>",
                _ => "✅ <b>Ticket kết thúc</b>"
            },
            TicketEventType.StatusChanged when ticket.Status == TicketStatus.InProgress &&
                item.Reason.Contains("email mới", StringComparison.OrdinalIgnoreCase) => "📧 <b>Ticket đã có phản hồi mới</b>",
            TicketEventType.StatusChanged => "🔄 <b>Thay đổi trạng thái</b>",
            TicketEventType.AssignmentChanged => AssignmentTitle(item),
            _ => "📣 <b>Cập nhật ticket</b>"
        });
        text.AppendLine($"🆔 <b>Mã ticket:</b> {Escape(ticket.Code)}");
        var isStatusTransition = item.EventType is TicketEventType.StatusChanged or TicketEventType.Terminal &&
            item.PreviousStatus is not null && item.PreviousStatus.Value != ticket.Status;
        if (item.EventType is not TicketEventType.Created and not TicketEventType.UnassignedReminder and not TicketEventType.ResponseReminder)
            text.AppendLine(isStatusTransition
                ? $"📌 <b>Trạng thái:</b> {Escape(item.PreviousStatus!.Value.DisplayName())} ➔ {Escape(ticket.Status.DisplayName())}"
                : $"📌 <b>Trạng thái:</b> {Escape(ticket.Status.DisplayName())}");
        if (isStatusTransition && !string.IsNullOrWhiteSpace(item.ChangedBy) && item.ChangedBy.Trim() != "---")
            text.AppendLine($"👤 <b>Người thay đổi:</b> {Escape(item.ChangedBy.Trim())}");
        if (item.EventType is not TicketEventType.AssignmentChanged and not TicketEventType.Created and
            not TicketEventType.UnassignedReminder and not TicketEventType.ResponseReminder)
            text.AppendLine($"👨‍💼 <b>Người xử lý:</b> {Escape(NormalizeEmpty(ticket.AssigneeName, "Chưa nhận"))}");
        var ageMinutes = ticket.CreatedAt is null ? 0 : Math.Max(0, (int)(item.DetectedAt - ticket.CreatedAt.Value).TotalMinutes);
        if (item.EventType is TicketEventType.Created or TicketEventType.UnassignedReminder)
            text.AppendLine($"⏱ <b>Thời gian chưa nhận ticket:</b> {ageMinutes} phút");
        else if (item.EventType == TicketEventType.ResponseReminder)
        {
            var responseMinutes = ticket.ResponseReminderSince is null ? 0 :
                Math.Max(0, (int)(item.DetectedAt - ticket.ResponseReminderSince.Value).TotalMinutes);
            text.AppendLine($"⏱ <b>Thời gian từ lúc có phản hồi mới:</b> {responseMinutes} phút");
        }
        else if (item.EventType != TicketEventType.Terminal || ticket.Status != TicketStatus.Closed)
            text.AppendLine($"🕰 <b>Thời gian tồn tại từ lúc nhận ticket:</b> {ageMinutes} phút");
        if (item.ChangedAt is not null)
            text.AppendLine($"🗓 <b>Thời gian thay đổi:</b> {item.ChangedAt.Value:dd/MM/yyyy HH:mm}");
        if (item.EventType == TicketEventType.AssignmentChanged)
        {
            if (!string.Equals(item.PreviousAssigneeName, ticket.AssigneeName, StringComparison.OrdinalIgnoreCase))
                text.AppendLine($"👥 <b>Người xử lý:</b> {Escape(NormalizeEmpty(item.PreviousAssigneeName, "Chưa nhận"))} ➔ {Escape(NormalizeEmpty(ticket.AssigneeName, "Chưa nhận"))}");
            if (!string.Equals(item.PreviousDepartmentName, ticket.DepartmentName, StringComparison.OrdinalIgnoreCase))
                text.AppendLine($"🏢 <b>Phòng ban:</b> {Escape(NormalizeEmpty(item.PreviousDepartmentName, "Chưa có"))} ➔ {Escape(NormalizeEmpty(ticket.DepartmentName, "Chưa có"))}");
        }
        if (item.EventType == TicketEventType.SlaThresholdReached)
            text.AppendLine($"⚠️ <b>SLA:</b> {Escape(item.Reason)}");
        var email = item.LatestEmail is { } validEmail && !validEmail.IsExcluded()
            ? validEmail
            : ticket.LatestEmail is { } snapshotEmail && !snapshotEmail.IsExcluded() ? snapshotEmail : null;
        var envelope = ExtractEnvelope(email?.Body);
        var emailFrom = string.IsNullOrWhiteSpace(email?.From) ? envelope.From : email.From;
        var emailTime = email?.SentAt ?? envelope.SentAt;
        text.AppendLine();
        text.AppendLine("<blockquote>");
        text.AppendLine($"From: {Escape(NormalizeEmpty(emailFrom, "FTMS không cung cấp"))}");
        text.AppendLine($"Time: {(emailTime is null ? "FTMS không cung cấp" : emailTime.Value.ToString("dd/MM/yyyy HH:mm:ss"))}");
        text.AppendLine($"📝 <b>Tiêu đề: {Escape(NormalizeEmpty(email?.Subject ?? ticket.Title, "Không có thông tin"))}</b>");
        text.AppendLine();
        text.AppendLine(Escape(CleanEmail(email?.Body)));
        text.AppendLine("</blockquote>");
        return text.ToString().Trim();
    }

    public static string CleanEmail(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "Không lấy được nội dung email.";
        html = LatestMessageHtmlRegex().Split(html, 2)[0];
        var value = BreakRegex().Replace(html, "\n");
        value = WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, " "));
        value = LineWhitespaceRegex().Replace(value, " ");
        value = MultiLineRegex().Replace(value, "\n").Trim();
        foreach (var marker in new[]
        {
            "THÔNG BÁO BẢO MẬT", "THONG BAO BAO MAT", "CONFIDENTIALITY NOTICE",
            "__________________________", "*************************"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) value = value[..index].Trim();
        }
        foreach (var marker in new[]
        {
            "Thanks & Best regards", "Thanks and Best regards", "Best regards", "Kind regards", "Regards,",
            "Trân trọng", "Tải Outlook for", "-----Original Message-----", "\nTừ:", "\nTừ:", "\nFrom:"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 20) value = value[..index].Trim();
        }
        foreach (var marker in new[]
        {
            "FPT Telecom International Co., Ltd", "FPT Telecom International",
            "CTY TNHH MTV Viễn Thông Quốc Tế FPT", "CÔNG TY TNHH MTV VIỄN THÔNG QUỐC TẾ FPT",
            "Nhân viên Hỗ trợ kỹ thuật", "Data Center Services Provider", "ITSM - PHÒNG HỖ TRỢ KHÁCH HÀNG"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index <= 20) continue;
            var previousLine = value.LastIndexOf('\n', index - 1);
            var signatureStart = previousLine > 0 ? value.LastIndexOf('\n', previousLine - 1) : previousLine;
            value = value[..Math.Max(0, signatureStart)].Trim();
        }
        value = value.Length > 2500 ? value[..2500] + "..." : value;
        return value;
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string AssignmentTitle(TicketEvent item)
    {
        var hadAssignee = !string.IsNullOrWhiteSpace(item.PreviousAssigneeName) && item.PreviousAssigneeName.Trim() != "---";
        var hasAssignee = !string.IsNullOrWhiteSpace(item.Snapshot.AssigneeName) && item.Snapshot.AssigneeName.Trim() != "---";
        if (!hadAssignee && hasAssignee) return "🙋 <b>Ticket đã có người nhận</b>";
        if (!string.Equals(item.PreviousAssigneeName, item.Snapshot.AssigneeName, StringComparison.OrdinalIgnoreCase))
            return "👥 <b>Ticket đã chuyển người xử lý</b>";
        return "🏢 <b>Ticket đã chuyển phòng ban</b>";
    }

    private static string NormalizeEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "---" ? fallback : value.Trim();

    private static (string? From, DateTimeOffset? SentAt) ExtractEnvelope(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return (null, null);
        var prepared = BreakRegex().Replace(html, "\n");
        var text = WebUtility.HtmlDecode(HtmlTagRegex().Replace(prepared, " "));
        text = LineWhitespaceRegex().Replace(text, " ");
        var from = EnvelopeFromRegex().Match(text).Groups[1].Value.Trim();
        DateTimeOffset? sentAt = null;
        var rawDate = EnvelopeDateRegex().Match(text).Groups[1].Value.Trim();
        if (!string.IsNullOrWhiteSpace(rawDate))
        {
            rawDate = OrdinalSuffixRegex().Replace(rawDate, "$1");
            if (DateTimeOffset.TryParse(rawDate, CultureInfo.GetCultureInfo("en-US"),
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed))
                sentAt = parsed;
        }
        return (string.IsNullOrWhiteSpace(from) ? null : from, sentAt);
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
    [GeneratedRegex(@"<hr\b|id\s*=\s*['""](?:divRplyFwdMsg|x_divRplyFwdMsg|appendonsend)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex LatestMessageHtmlRegex();
    [GeneratedRegex(@"<(?:br\s*/?|/p|/div|/li)>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();
    [GeneratedRegex(@"[^\S\r\n]+")]
    private static partial Regex LineWhitespaceRegex();
    [GeneratedRegex(@"(?:\r?\n\s*){3,}")]
    private static partial Regex MultiLineRegex();
    [GeneratedRegex(@"(?:Từ|Từ|From)\s*:\s*(?:[^\s,;<>]+\s+)?([A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex EnvelopeFromRegex();
    [GeneratedRegex(@"(?:Đã gửi|Đã gửi|Sent)\s*:\s*([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvelopeDateRegex();
    [GeneratedRegex(@"(\d{1,2})(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSuffixRegex();
}
