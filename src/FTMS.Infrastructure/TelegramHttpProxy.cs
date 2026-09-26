using System.Net;

namespace FTMS.Infrastructure;

public sealed class TelegramHttpProxy(Func<string?> proxyUrl) : IWebProxy
{
    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination) => CurrentProxy() ?? destination;

    public bool IsBypassed(Uri host) => CurrentProxy() is null;

    public static bool TryParse(string? value, out Uri? proxy)
    {
        proxy = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttp || string.IsNullOrWhiteSpace(parsed.Host) ||
            parsed.UserInfo.Length > 0 || parsed.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;
        proxy = parsed;
        return true;
    }

    private Uri? CurrentProxy()
    {
        if (TryParse(proxyUrl(), out var proxy)) return proxy;
        throw new InvalidOperationException("Cấu hình HTTP proxy Telegram không hợp lệ.");
    }
}
