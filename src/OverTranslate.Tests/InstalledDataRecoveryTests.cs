using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed class InstalledDataRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "installed-data-recovery-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "install", "data");
    private string Backup => Path.Combine(_root, "user", "recovery", "data");
    private string Legacy => Path.Combine(_root, "user", "ScreenTranslate-data");
    private static string Write(string folder, string relative, string value)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
        return path;
    }

    [Fact]
    public void LegacyProfileIsCopiedWithoutChangingItsBytesOrOverwritingAnExistingProfile()
    {
        var old = Write(Legacy, "library/library.json", "original library");
        Write(Legacy, "appsettings.json", "original settings");
        var profile = new InstalledDataRecovery(Data, Backup);
        profile.Initialize(Legacy);
        Assert.Equal(File.ReadAllBytes(old), File.ReadAllBytes(Path.Combine(Data, "library/library.json")));
        Assert.True(File.Exists(old));
        Write(Legacy, "library/library.json", "stale legacy");
        profile.Initialize(Legacy);
        Assert.Equal("original library", File.ReadAllText(Path.Combine(Data, "library/library.json")));
    }

    [Fact]
    public void ReinstallRestoresLatestSavedDataAndDoesNotResurrectDeletedHistory()
    {
        Write(Legacy, "library/library.json", "old");
        Write(Legacy, "clipboard-history/record.clip", "encrypted fixture");
        var profile = new InstalledDataRecovery(Data, Backup);
        profile.Initialize(Legacy);
        var library = Write(Data, "library/library.json", "latest");
        profile.BackupFile(library);
        var history = Path.Combine(Data, "clipboard-history/record.clip");
        File.Delete(history);
        profile.BackupFile(history);
        // 对照 Velopack 1.2.0 的覆盖安装行为：整个安装目录被替换，不执行退出回调。
        Directory.Delete(Path.GetDirectoryName(Data)!, recursive: true);
        var reinstalled = new InstalledDataRecovery(Data, Backup);
        reinstalled.Initialize(Legacy);
        Assert.Equal("latest", File.ReadAllText(Path.Combine(Data, "library/library.json")));
        Assert.False(File.Exists(history));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "library/library.json")));
    }

    [Fact]
    public void FailedMigrationKeepsTheSourceAndCannotPublishAPartialProfile()
    {
        var library = Write(Legacy, "library/library.json", "keep");
        Write(Legacy, "appsettings.json", "settings");
        using (new FileStream(library, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => new InstalledDataRecovery(Data, Backup).Initialize(Legacy));
            Assert.False(Directory.Exists(Data));
            Assert.False(Directory.Exists(Backup));
        }
        new InstalledDataRecovery(Data, Backup).Initialize(Legacy);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Data, "library/library.json")));
    }

    [Fact]
    public void RecoveryIsScopedToTheProfileAndKeepsLogsAndModelsOutOfTheBackup()
    {
        Write(Legacy, "library/library.json", "library");
        Write(Legacy, "logs/app.log", "log");
        Write(Legacy, "assets/models/model.bin", "download");
        var profile = new InstalledDataRecovery(Data, Backup);
        profile.Initialize(Legacy);
        var outside = Write(_root, "data-other/library/library.json", "unrelated");
        profile.BackupFile(outside);
        Assert.False(Directory.Exists(Path.Combine(Backup, "logs")));
        Assert.False(Directory.Exists(Path.Combine(Backup, "assets")));
        Assert.True(File.Exists(Path.Combine(Data, "assets/models/model.bin")));
        Assert.Equal("library", File.ReadAllText(Path.Combine(Backup, "library/library.json")));
        Assert.NotEqual(InstalledDataRecovery.RecoveryRoot(Data, _root),
            InstalledDataRecovery.RecoveryRoot(Path.Combine(_root, "other/data"), _root));
    }

    [Fact]
    public void BackupFailureIsReportedAndTheLastCompleteBackupRemainsIntact()
    {
        Write(Legacy, "library/library.json", "old");
        var profile = new InstalledDataRecovery(Data, Backup);
        profile.Initialize(Legacy);
        var path = Write(Data, "library/library.json", "latest");
        var backup = Path.Combine(Backup, "library/library.json");
        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => profile.BackupFile(path));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("old", File.ReadAllText(backup));
        profile.BackupFile(path);
        Assert.Equal("latest", File.ReadAllText(backup));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
