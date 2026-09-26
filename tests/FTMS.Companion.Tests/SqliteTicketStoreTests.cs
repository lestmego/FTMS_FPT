using FTMS.Domain;
using FTMS.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class SqliteTicketStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteTicketStore _store;

    public SqliteTicketStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ftms-test-{Guid.NewGuid():N}.db");
        _store = new SqliteTicketStore(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task CleanupAsync_DeletesClosedTicketsFromPreviousDays_AndKeepsActiveAndClosedToday()
    {
        await _store.InitializeAsync(CancellationToken.None);

        var vietnamToday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var today = vietnamToday.Date;
        var nowToday = new DateTimeOffset(today.AddHours(10), TimeSpan.FromHours(7));
        var yesterday = new DateTimeOffset(today.AddDays(-1).AddHours(15), TimeSpan.FromHours(7));

        var activeTicket = new TicketSnapshot
        {
            Code = "RQ-ACTIVE",
            Status = TicketStatus.InProgress,
            UpdatedAt = yesterday,
            IsTerminal = false
        };

        var closedTodayTicket = new TicketSnapshot
        {
            Code = "RQ-CLOSED-TODAY",
            Status = TicketStatus.Closed,
            UpdatedAt = nowToday,
            ClosedAt = nowToday,
            IsTerminal = true
        };

        var closedYesterdayTicket = new TicketSnapshot
        {
            Code = "RQ-CLOSED-YESTERDAY",
            Status = TicketStatus.Closed,
            UpdatedAt = yesterday,
            ClosedAt = yesterday,
            IsTerminal = true
        };

        await _store.SaveSnapshotAsync(activeTicket, CancellationToken.None);
        await _store.SaveSnapshotAsync(closedTodayTicket, CancellationToken.None);
        await _store.SaveSnapshotAsync(closedYesterdayTicket, CancellationToken.None);

        // Mark terminals explicitly with their timestamps
        await _store.MarkTerminalAsync("RQ-CLOSED-TODAY", nowToday, CancellationToken.None);
        await _store.MarkTerminalAsync("RQ-CLOSED-YESTERDAY", yesterday, CancellationToken.None);

        // Add events and outbox for yesterday's closed ticket
        var eventYesterday = new TicketEvent
        {
            EventKey = "EV-1",
            TicketCode = "RQ-CLOSED-YESTERDAY",
            EventType = TicketEventType.Terminal,
            DetectedAt = yesterday,
            Reason = "closed",
            Snapshot = closedYesterdayTicket
        };
        await _store.SaveEventAndEnqueueNotificationAsync(eventYesterday, "Test message", CancellationToken.None);

        // Run daily cleanup (retentionDays = 1, meaning cutoff = start of today)
        await _store.CleanupAsync(1, CancellationToken.None);

        // Check snapshots directly from SQLite
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT code FROM ticket_snapshots ORDER BY code";
        var codes = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                codes.Add(reader.GetString(0));
            }
        }

        // RQ-ACTIVE and RQ-CLOSED-TODAY must be preserved.
        // RQ-CLOSED-YESTERDAY must have been deleted.
        Assert.Contains("RQ-ACTIVE", codes);
        Assert.Contains("RQ-CLOSED-TODAY", codes);
        Assert.DoesNotContain("RQ-CLOSED-YESTERDAY", codes);
    }
}
