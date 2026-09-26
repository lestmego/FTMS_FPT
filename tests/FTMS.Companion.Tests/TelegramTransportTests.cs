using FTMS.Infrastructure;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class TelegramTransportTests
{
    private static readonly Uri Destination = new("https://api.telegram.org/bot1/sendMessage");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankProxyUsesDirectConnection(string? value)
    {
        var proxy = new TelegramHttpProxy(() => value);
        Assert.True(proxy.IsBypassed(Destination));
        Assert.Equal(Destination, proxy.GetProxy(Destination));
    }

    [Fact]
    public void ConfiguredProxyIsUsedAndCanChangeAtRuntime()
    {
        string? value = "http://proxy.example:8080";
        var proxy = new TelegramHttpProxy(() => value);
        Assert.False(proxy.IsBypassed(Destination));
        Assert.Equal(new Uri(value), proxy.GetProxy(Destination));

        value = null;
        Assert.True(proxy.IsBypassed(Destination));
        Assert.Equal(Destination, proxy.GetProxy(Destination));
    }

    [Theory]
    [InlineData("https://proxy.example:8080")]
    [InlineData("http://user:pass@proxy.example:8080")]
    [InlineData("http://proxy.example:8080/path")]
    [InlineData("http://proxy.example:8080?x=1")]
    public void InvalidProxyIsRejected(string value)
    {
        Assert.False(TelegramHttpProxy.TryParse(value, out _));
    }

    [Fact]
    public void SanitizerRemovesTokenAndKeepsUsefulContext()
    {
        const string token = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";
        var message = $"The proxy tunnel request to proxy 'https://api.telegram.org/bot{token}/sendMessage' failed with status code '405'.";
        var sanitized = TelegramErrorSanitizer.Sanitize(message, token);
        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("sendMessage", sanitized);
        Assert.Contains("405", sanitized);
        Assert.Contains("[REDACTED_TELEGRAM_TOKEN]", sanitized);
    }

    [Fact]
    public void SanitizerDoesNotHideNormalColonValues()
    {
        const string message = "Proxy localhost:8080 failed at 12:30 for chat 6800804130 and RQ20260001.";
        Assert.Equal(message, TelegramErrorSanitizer.Sanitize(message));
    }
}
