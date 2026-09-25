using System.Net;
using System.Net.Http;

namespace FTMS.Desktop;

internal static class TelegramHttpClientFactory
{
    public static HttpClient Create(Func<DesktopSettings> settings) => new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = new SettingsProxy(settings)
    });

    public static bool TryParseProxy(string? value, out Uri? proxy)
    {
        proxy = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttp ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            parsed.UserInfo.Length > 0 ||
            parsed.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment)) return false;
        proxy = parsed;
        return true;
    }

    private sealed class SettingsProxy(Func<DesktopSettings> settings) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => CurrentProxy() ?? HttpClient.DefaultProxy.GetProxy(destination) ?? destination;

        public bool IsBypassed(Uri host) => CurrentProxy() is null && HttpClient.DefaultProxy.IsBypassed(host);

        private Uri? CurrentProxy()
        {
            TryParseProxy(settings().TelegramProxyUrl, out var proxy);
            return proxy;
        }
    }
}
