using System.Text.Json;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Data.Sqlite;

namespace FTMS.Infrastructure;

public sealed class SqliteTicketStore(string databasePath) : ITicketStore
{
    private string ConnectionString => $"Data Source={databasePath};Pooling=True";

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS ticket_snapshots(code TEXT PRIMARY KEY,payload TEXT NOT NULL,is_terminal INTEGER NOT NULL,updated_at TEXT NOT NULL,terminal_at TEXT NULL);
            CREATE TABLE IF NOT EXISTS ticket_events(event_key TEXT PRIMARY KEY,ticket_code TEXT NOT NULL,event_type TEXT NOT NULL,payload TEXT NOT NULL,detected_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS notification_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT,event_key TEXT NOT NULL UNIQUE,message TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,next_attempt_at TEXT NOT NULL,sent_at TEXT NULL,last_error TEXT NULL);
            CREATE INDEX IF NOT EXISTS idx_ticket_events_code ON ticket_events(ticket_code);
            CREATE INDEX IF NOT EXISTS idx_ticket_events_detected ON ticket_events(detected_at);
            CREATE INDEX IF NOT EXISTS idx_outbox_pending ON notification_outbox(sent_at,next_attempt_at,id);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, TicketSnapshot>> LoadActiveSnapshotsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, TicketSnapshot>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM ticket_snapshots WHERE is_terminal=0";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize<TicketSnapshot>(reader.GetString(0));
            if (item is not null) result[item.Code] = item;
        }
        return result;
    }

    public async Task SaveSnapshotsAsync(IReadOnlyList<TicketSnapshot> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ticket_snapshots(code,payload,is_terminal,updated_at,terminal_at)
            VALUES($code,$payload,$terminal,$updated,$terminalAt)
            ON CONFLICT(code) DO UPDATE SET
                payload=excluded.payload,
                is_terminal=excluded.is_terminal,
                updated_at=excluded.updated_at,
                terminal_at=CASE WHEN excluded.is_terminal=1
                    THEN COALESCE(ticket_snapshots.terminal_at, excluded.terminal_at) ELSE NULL END
            """;
        var code = command.Parameters.Add("$code", SqliteType.Text);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);
        var terminal = command.Parameters.Add("$terminal", SqliteType.Integer);
        var updated = command.Parameters.Add("$updated", SqliteType.Text);
        var terminalAt = command.Parameters.Add("$terminalAt", SqliteType.Text);
        var now = DateTimeOffset.Now.ToString("O");

        foreach (var item in snapshots)
        {
            code.Value = item.Code;
            payload.Value = JsonSerializer.Serialize(item);
            terminal.Value = item.IsTerminal ? 1 : 0;
            updated.Value = now;
            terminalAt.Value = item.IsTerminal ? now : DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public Task SaveEventAsync(TicketEvent item, CancellationToken ct) => ExecuteAsync("""
        INSERT OR IGNORE INTO ticket_events(event_key,ticket_code,event_type,payload,detected_at) VALUES($key,$code,$type,$payload,$detected)
        """, ct, ("$key", item.EventKey), ("$code", item.TicketCode), ("$type", item.EventType.ToString()), ("$payload", JsonSerializer.Serialize(item)), ("$detected", item.DetectedAt.ToString("O")));

    public async Task<bool> EventExistsAsync(string eventKey, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(1) FROM ticket_events WHERE event_key=$key"; command.Parameters.AddWithValue("$key", eventKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    public Task EnqueueNotificationAsync(TicketEvent item, string message, CancellationToken ct) => ExecuteAsync(
        "INSERT OR IGNORE INTO notification_outbox(event_key,message,next_attempt_at) VALUES($key,$message,$next)", ct,
        ("$key", item.EventKey), ("$message", message), ("$next", DateTimeOffset.Now.ToString("O")));

    public async Task CleanupAsync(int days, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, days)).ToString("O");
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM ticket_events
                WHERE event_key IN (
                    SELECT event_key FROM notification_outbox
                    WHERE sent_at IS NULL AND (
                        TRIM(message)='' OR message LIKE $missingBody OR attempt_count>=20 OR
                        event_key IN (SELECT event_key FROM ticket_events WHERE detected_at<$cutoff)
                    )
                );
                DELETE FROM notification_outbox
                WHERE sent_at IS NOT NULL OR TRIM(message)='' OR message LIKE $missingBody OR attempt_count>=20 OR
                    event_key NOT IN (SELECT event_key FROM ticket_events);

                DELETE FROM notification_outbox
                WHERE event_key IN (
                    SELECT event_key FROM ticket_events
                    WHERE ticket_code IN (
                        SELECT terminal.ticket_code FROM ticket_events terminal
                        WHERE terminal.event_type='Terminal' AND NOT EXISTS (
                            SELECT 1 FROM notification_outbox pending
                            JOIN ticket_events pending_event ON pending_event.event_key=pending.event_key
                            WHERE pending_event.ticket_code=terminal.ticket_code AND pending.sent_at IS NULL
                        )
                    )
                );
                DELETE FROM ticket_events
                WHERE ticket_code IN (
                    SELECT terminal.ticket_code FROM ticket_events terminal
                    WHERE terminal.event_type='Terminal' AND NOT EXISTS (
                        SELECT 1 FROM notification_outbox pending
                        JOIN ticket_events pending_event ON pending_event.event_key=pending.event_key
                        WHERE pending_event.ticket_code=terminal.ticket_code AND pending.sent_at IS NULL
                    )
                );
                DELETE FROM ticket_snapshots WHERE is_terminal=1;
                DELETE FROM ticket_events
                WHERE detected_at<$cutoff AND event_key NOT IN (
                    SELECT event_key FROM notification_outbox WHERE sent_at IS NULL
                );
                PRAGMA optimize;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.Parameters.AddWithValue("$missingBody", "%Kh\u00f4ng l\u1ea5y \u0111\u01b0\u1ee3c n\u1ed9i dung email.%");
            await command.ExecuteNonQueryAsync(ct);
        }

        await CompactIfNeededAsync(ct);
    }

    private async Task CompactIfNeededAsync(CancellationToken ct)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        var pageCountCommand = connection.CreateCommand();
        pageCountCommand.CommandText = "PRAGMA page_count";
        var pageCount = Convert.ToInt64(await pageCountCommand.ExecuteScalarAsync(ct));
        var freeCountCommand = connection.CreateCommand();
        freeCountCommand.CommandText = "PRAGMA freelist_count";
        var freeCount = Convert.ToInt64(await freeCountCommand.ExecuteScalarAsync(ct));

        var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync(ct);
        if (pageCount == 0 || freeCount * 5 < pageCount) return;

        var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM";
        await vacuum.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
