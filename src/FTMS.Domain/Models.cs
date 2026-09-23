namespace FTMS.Domain;

public enum TicketStatus { New = 0, Assigned = 1, InProgress = 2, Completed = 3, Paused = 4, Closed = 5, Cancelled = 7, Unprocessed = 8 }

public static class TicketStatusExtensions
{
    public static bool IsTerminal(this TicketStatus value, bool unprocessedIsTerminal = true) =>
        value is TicketStatus.Closed or TicketStatus.Cancelled || unprocessedIsTerminal && value is TicketStatus.Unprocessed;

    public static string DisplayName(this TicketStatus value) => value switch
    {
        TicketStatus.New => "T\u1ea1o m\u1edbi", TicketStatus.Assigned => "Ph\u00e2n c\u00f4ng",
        TicketStatus.InProgress => "Đang thực hiện", TicketStatus.Completed => "Ho\u00e0n th\u00e0nh",
        TicketStatus.Paused => "T\u1ea1m ng\u01b0ng", TicketStatus.Closed => "\u0110\u00e3 \u0111\u00f3ng",
        TicketStatus.Cancelled => "\u0110\u00e3 h\u1ee7y", TicketStatus.Unprocessed => "Kh\u00f4ng x\u1eed l\u00fd", _ => value.ToString()
    };
}

public sealed record LatestEmail(string? Id, DateTimeOffset? SentAt, string? From, string? Subject, string? Body);

public static class LatestEmailExtensions
{
    public static bool IsAkabot(this LatestEmail? email) => email is not null &&
        (string.Equals(email.From?.Trim(), "ihub.akabot2@fpt.com", StringComparison.OrdinalIgnoreCase) ||
         email.Body?.Contains("ihub.akabot2@fpt.com", StringComparison.OrdinalIgnoreCase) == true);

    public static bool IsAutomatedAcknowledgement(this LatestEmail? email)
    {
        if (email?.Body is null) return false;
        var body = email.Body;
        var standardReceipt = body.Contains("Thông tin yêu cầu hỗ trợ", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("đã được tiếp nhận", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("chuyển đến bộ phận", StringComparison.OrdinalIgnoreCase);
        var technicalReceipt = body.Contains("Thông tin hỗ trợ", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("đã được tiếp nhận", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("Kỹ thuật sẽ kiểm tra và phản hồi", StringComparison.OrdinalIgnoreCase);
        var assignmentReceipt = body.Contains("Kỹ thuật FPT nhận thông tin YCHT", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("phân công nhân sự xử lý theo RQ", StringComparison.OrdinalIgnoreCase);
        return standardReceipt || technicalReceipt || assignmentReceipt;
    }

    public static bool IsExcluded(this LatestEmail? email) => email.IsAkabot() || email.IsAutomatedAcknowledgement();
}

public sealed record StatusHistoryEntry(TicketStatus Status, DateTimeOffset? OccurredAt, string? Actor);

public sealed record TicketSnapshot
{
    public required string Code { get; init; }
    public TicketStatus Status { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public long? AssigneeId { get; init; }
    public string? AssigneeName { get; init; }
    public long? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public int? SlaDeviationMinutes { get; init; }
    public int? SlaType { get; init; }
    public LatestEmail? LatestEmail { get; init; }
    public string? ResponseReminderEmailId { get; init; }
    public DateTimeOffset? ResponseReminderSince { get; init; }
    public bool IsTerminal { get; init; }
}

public enum TicketEventType { Created, StatusChanged, AssignmentChanged, SlaThresholdReached, Terminal, EmailReceived, UnassignedReminder, ResponseReminder }

public sealed record TicketEvent
{
    public required string EventKey { get; init; }
    public required string TicketCode { get; init; }
    public required TicketEventType EventType { get; init; }
    public TicketStatus? PreviousStatus { get; init; }
    public TicketStatus CurrentStatus { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required string Reason { get; init; }
    public string? ChangedBy { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }
    public string? PreviousAssigneeName { get; init; }
    public string? PreviousDepartmentName { get; init; }
    public LatestEmail? LatestEmail { get; init; }
    public required TicketSnapshot Snapshot { get; init; }
}

public sealed record AppSettings
{
    public string FtmsUrl { get; init; } = "https://ftms.fpt.net/ihub/list?tab=2";
    public int PollIntervalSeconds { get; init; } = 30;
    public int IdleDelaySeconds { get; init; } = 10;
    public bool AutoRefreshEnabled { get; init; } = true;
    public string TelegramBotToken { get; init; } = string.Empty;
    public string TelegramChatId { get; init; } = string.Empty;
    public int[] SlaThresholds { get; init; } = [30, 25, 20, 10, 5];
    public int TerminalRetentionDays { get; init; } = 30;
    public bool UnprocessedIsTerminal { get; init; } = true;
}

public sealed record DashboardSummary(int Total, int New, int Assigned, int InProgress, int Paused, int Completed, int Closed, int SlaRisk, int SlaViolated);
