using System.IO;

namespace FTMS.Infrastructure;

public static class DailyLogCleaner
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static void Clean(string directoryPath, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;

        var referenceTime = (now ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);
        var todayStartUtc = new DateTimeOffset(referenceTime.Date, VietnamOffset).UtcDateTime;

        try
        {
            var logFiles = Directory.GetFiles(directoryPath, "*.log", SearchOption.TopDirectoryOnly);
            foreach (var file in logFiles)
            {
                CleanLogFile(file, todayStartUtc);
            }
        }
        catch { }
    }

    private static void CleanLogFile(string filePath, DateTime todayStartUtc)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return;

            // If the file was last modified before today, delete it entirely.
            if (fileInfo.LastWriteTimeUtc < todayStartUtc)
            {
                fileInfo.Delete();
                return;
            }

            // If it is error.log modified today, ensure older days' entries are pruned.
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, "error.log", StringComparison.OrdinalIgnoreCase))
            {
                PruneOldLogEntries(filePath, todayStartUtc);
            }
        }
        catch { }
    }

    private static void PruneOldLogEntries(string filePath, DateTime todayStartUtc)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(content))
            {
                File.Delete(filePath);
                return;
            }

            var entries = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();

            foreach (var entry in entries)
            {
                var trimmed = entry.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                var firstSpace = trimmed.IndexOf(' ');
                if (firstSpace > 0 && DateTimeOffset.TryParse(trimmed[..firstSpace], out var timestamp))
                {
                    if (timestamp.UtcDateTime >= todayStartUtc)
                    {
                        kept.Add(trimmed);
                    }
                }
                else
                {
                    kept.Add(trimmed);
                }
            }

            if (kept.Count == 0)
            {
                File.Delete(filePath);
            }
            else if (kept.Count < entries.Length)
            {
                File.WriteAllText(filePath, string.Join("\n\n", kept) + "\n\n");
            }
        }
        catch { }
    }
}
