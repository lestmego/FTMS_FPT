using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FTMS.Desktop;

public sealed record DesktopSettings(string TelegramToken = "", string TelegramChatId = "", bool StartWithWindows = false,
    bool AutoRefreshEnabled = true, int AutoRefreshSeconds = 30, string TelegramProxyUrl = "");

public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion", "settings.json");
    public DesktopSettings Current { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(_path)) return;
        var persisted = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_path));
        if (persisted is null) return;
        Current = new DesktopSettings(Unprotect(persisted.ProtectedTelegramToken), persisted.TelegramChatId ?? "", persisted.StartWithWindows,
            persisted.AutoRefreshEnabled, persisted.AutoRefreshSeconds <= 0 ? 30 : persisted.AutoRefreshSeconds,
            persisted.TelegramProxyUrl ?? "");
        ApplyStartup(Current.StartWithWindows);
    }

    public void Save(DesktopSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Current = settings;
        var persisted = new PersistedSettings(Protect(settings.TelegramToken), settings.TelegramChatId, settings.StartWithWindows,
            settings.AutoRefreshEnabled, settings.AutoRefreshSeconds, settings.TelegramProxyUrl);
        File.WriteAllText(_path, JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true }));
        ApplyStartup(settings.StartWithWindows);
    }

    private static void ApplyStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        const string name = "FTMS Companion";
        if (enabled)
            key.SetValue(name, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(name, false);
    }

    private static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    private sealed record PersistedSettings(string ProtectedTelegramToken, string? TelegramChatId, bool StartWithWindows,
        bool AutoRefreshEnabled = true, int AutoRefreshSeconds = 30, string? TelegramProxyUrl = null);
}
