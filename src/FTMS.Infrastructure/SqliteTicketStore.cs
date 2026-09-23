using System.Text.Json;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Data.Sqlite;

namespace FTMS.Infrastructure;

public sealed class SqliteTicketStore(string databasePath) : ITicketStore
{
    private string ConnectionString => $"Data Source={databasePath}";

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ticket_snapshots(code TEXT PRIMARY KEY,payload TEXT NOT NULL,is_terminal INTEGER NOT NULL,updated_at TEXT NOT NULL,terminal_at TEXT NULL);
            CREATE TABLE IF NOT EXISTS ticket_events(event_key TEXT PRIMARY KEY,ticket_code TEXT NOT NULL,event_type TEXT NOT NULL,payload TEXT NOT NULL,detected_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS notification_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT,event_key TEXT NOT NULL UNIQUE,message TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,next_attempt_at TEXT NOT NULL,sent_at TEXT NULL,last_error TEXT NULL);
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

    public Task SaveSnapshotAsync(TicketSnapshot item, CancellationToken ct) => ExecuteAsync("""
        INSERT INTO ticket_snapshots(code,payload,is_terminal,updated_at,terminal_at) VALUES($code,$payload,$terminal,$updated,NULL)
        ON CONFLICT(code) DO UPDATE SET payload=$payload,is_terminal=$terminal,updated_at=$updated
        """, ct, ("$code", item.Code), ("$payload", JsonSerializer.Serialize(item)), ("$terminal", item.IsTerminal ? 1 : 0), ("$updated", DateTimeOffset.Now.ToString("O")));

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

    public Task MarkTerminalAsync(string code, DateTimeOffset at, CancellationToken ct) => ExecuteAsync(
        "UPDATE ticket_snapshots SET is_terminal=1,terminal_at=$at WHERE code=$code", ct, ("$at", at.ToString("O")), ("$code", code));

    public Task CleanupAsync(int days, CancellationToken ct) => ExecuteAsync("""
        DELETE FROM ticket_snapshots WHERE is_terminal=1;
        DELETE FROM ticket_events WHERE detected_at<$cutoff;
        DELETE FROM notification_outbox WHERE sent_at IS NOT NULL AND sent_at<$cutoff;
        """, ct, ("$cutoff", DateTimeOffset.Now.AddDays(-Math.Max(1, days)).ToString("O")));

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
