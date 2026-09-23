using System.Net.Http.Json;
using System.Net;
using System.Text.RegularExpressions;
using FTMS.Application;
using Microsoft.Data.Sqlite;

namespace FTMS.Infrastructure;

public sealed class TelegramOutboxSender(string databasePath, Func<(string Token, string ChatId)> settings, HttpClient http) : INotificationSender
{
    public async Task SendPendingAsync(CancellationToken ct)
    {
        var telegram = settings();
        if (string.IsNullOrWhiteSpace(telegram.Token) || string.IsNullOrWhiteSpace(telegram.ChatId)) return;
        await using var connection = new SqliteConnection($"Data Source={databasePath}"); await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,message,attempt_count FROM notification_outbox WHERE sent_at IS NULL AND next_attempt_at<=$now ORDER BY id LIMIT 20";
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        var items = new List<(long Id, string Message, int Attempts)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        foreach (var item in items)
        {
            try
            {
                var decodedMessage = WebUtility.HtmlDecode(item.Message);
                var code = Regex.Match(decodedMessage, @"Mã (?:RQ|ticket):</b>\s*([^\s<]+)").Groups[1].Value;
                var canReceive = decodedMessage.Contains("Ticket mới", StringComparison.OrdinalIgnoreCase) ||
                    decodedMessage.Contains("Nhắc ticket chưa được nhận", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(decodedMessage,
                    @"Trạng thái:</b>\s*(?:[^\r\n]*➔\s*)?(?:Tạo mới|Phân công)\s*(?:\r?\n|$)",
                    RegexOptions.IgnoreCase);
                var route = code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || code.StartsWith("AL", StringComparison.OrdinalIgnoreCase)
                    ? "case" : "request";
                var openUrl = $"https://ftms.fpt.net/ihub/{route}/edit/{Uri.EscapeDataString(code)}";
                object[][] buttons = canReceive
                    ? [[new { text = "🔎 Mở ticket", url = openUrl }, new { text = "🙋 Nhận ticket", callback_data = $"receive:{code}" }]]
                    : [[new { text = "🔎 Mở ticket", url = openUrl }]];
                var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{telegram.Token}/sendMessage",
                    new { chat_id = telegram.ChatId, text = item.Message, parse_mode = "HTML", disable_web_page_preview = true,
                        reply_markup = new { inline_keyboard = buttons } }, ct);
                response.EnsureSuccessStatusCode();
                await UpdateAsync(connection, item.Id, true, null, item.Attempts, ct);
            }
            catch (Exception ex) { await UpdateAsync(connection, item.Id, false, ex.Message, item.Attempts + 1, ct); }
        }
    }

    private static async Task UpdateAsync(SqliteConnection connection, long id, bool sent, string? error, int attempts, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.CommandText = sent ? "UPDATE notification_outbox SET sent_at=$now,last_error=NULL WHERE id=$id" :
            "UPDATE notification_outbox SET attempt_count=$attempts,next_attempt_at=$next,last_error=$error WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$attempts", attempts); command.Parameters.AddWithValue("$next", DateTimeOffset.Now.AddSeconds(Math.Min(300, Math.Pow(2, attempts))).ToString("O"));
        command.Parameters.AddWithValue("$error", error ?? string.Empty); await command.ExecuteNonQueryAsync(ct);
    }
}
