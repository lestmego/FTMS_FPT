using System.Text.RegularExpressions;

namespace FTMS.Infrastructure;

public static partial class TelegramErrorSanitizer
{
    private const string Marker = "[REDACTED_TELEGRAM_TOKEN]";

    public static string Sanitize(string? value, string? knownToken = null)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var sanitized = value;
        if (!string.IsNullOrWhiteSpace(knownToken))
            sanitized = sanitized.Replace(knownToken, Marker, StringComparison.Ordinal);
        sanitized = BotUrlRegex().Replace(sanitized, $"/bot{Marker}/");
        return BareTokenRegex().Replace(sanitized, Marker);
    }

    [GeneratedRegex(@"/bot\d{5,}:[A-Za-z0-9_-]{20,}/", RegexOptions.IgnoreCase)]
    private static partial Regex BotUrlRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])\d{5,}:[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])")]
    private static partial Regex BareTokenRegex();
}
