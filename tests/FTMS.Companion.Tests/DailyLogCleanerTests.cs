using FTMS.Infrastructure;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class DailyLogCleanerTests : IDisposable
{
    private readonly string _tempDir;

    public DailyLogCleanerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ftms-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Clean_DeletesLogFilesOlderThanToday()
    {
        var oldFile = Path.Combine(_tempDir, "old-errors.log");
        File.WriteAllText(oldFile, "Old log content");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-2));

        var todayFile = Path.Combine(_tempDir, "today.log");
        File.WriteAllText(todayFile, "Today log content");
        File.SetLastWriteTimeUtc(todayFile, DateTime.UtcNow);

        DailyLogCleaner.Clean(_tempDir);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(todayFile));
    }

    [Fact]
    public void Clean_PrunesOldEntriesInErrorLog()
    {
        var errorLog = Path.Combine(_tempDir, "error.log");
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var yesterday = now.AddDays(-1);

        var oldEntry = $"{yesterday:O} Old error occurred";
        var todayEntry = $"{now:O} Today error occurred";

        File.WriteAllText(errorLog, $"{oldEntry}\n\n{todayEntry}\n\n");
        File.SetLastWriteTimeUtc(errorLog, DateTime.UtcNow);

        DailyLogCleaner.Clean(_tempDir, now);

        Assert.True(File.Exists(errorLog));
        var content = File.ReadAllText(errorLog);
        Assert.DoesNotContain("Old error occurred", content);
        Assert.Contains("Today error occurred", content);
    }
}
