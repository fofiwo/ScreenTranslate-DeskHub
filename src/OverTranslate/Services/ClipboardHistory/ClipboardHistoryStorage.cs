using System.IO;
using System.Text;

namespace OverTranslate.Services.ClipboardHistory;

internal static class ClipboardHistoryStorage
{
    private const string BindingName = "clipboard-history-path.txt";
    private static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ScreenTranslate");

    internal static string BindingPath(string dataRoot, string profileDirectory) =>
        Path.Combine(Path.GetDirectoryName(InstalledDataRecovery.RecoveryRoot(dataRoot, profileDirectory))!, BindingName);

    internal static string ResolveFolder(string dataRoot, string? profileDirectory = null)
    {
        var binding = BindingPath(dataRoot, profileDirectory ?? StateDirectory);
        string folder;
        try { folder = File.ReadAllText(binding).Trim(); }
        catch (FileNotFoundException) { return Path.Combine(dataRoot, "clipboard-history"); }
        catch (DirectoryNotFoundException) { return Path.Combine(dataRoot, "clipboard-history"); }
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\")
            || !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), "clipboard-history", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("剪贴板保存目录配置无效，请检查 clipboard-history-path.txt，原记录未更改。");
        return Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))!, "clipboard-history");
    }

    internal static ClipboardHistoryStore Open() => Open(AppDataPaths.Root, StateDirectory, AppDataPaths.Recovery);

    internal static ClipboardHistoryStore Open(string dataRoot, string profileDirectory, InstalledDataRecovery? defaultRecovery = null)
    {
        var folder = ResolveFolder(dataRoot, profileDirectory);
        var recovery = RecoveryFor(folder, dataRoot, profileDirectory, defaultRecovery);
        if (!string.Equals(folder, Path.Combine(dataRoot, "clipboard-history"), StringComparison.OrdinalIgnoreCase))
        {
            recovery?.RestoreFolder(folder);
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException("自定义剪贴板目录及恢复副本不可用，请检查磁盘连接后重新打开，未切换为空历史。");
        }
        return new ClipboardHistoryStore(folder, recovery);
    }

    private static InstalledDataRecovery? RecoveryFor(string folder, string dataRoot, string profileDirectory, InstalledDataRecovery? defaultRecovery)
    {
        if (string.Equals(folder, Path.Combine(dataRoot, "clipboard-history"), StringComparison.OrdinalIgnoreCase))
            return defaultRecovery;
        var parent = Path.GetDirectoryName(folder)!;
        return new InstalledDataRecovery(parent, InstalledDataRecovery.RecoveryRoot(parent, profileDirectory));
    }

    internal static void ChangeDirectory(ClipboardHistoryStore store, string parent)
    {
        if (!Path.IsPathFullyQualified(parent) || parent.StartsWith(@"\\"))
            throw new ArgumentException("请选择本机的完整目录。");
        var folder = Path.Combine(Path.GetFullPath(parent), "clipboard-history");
        // 绑定在用户资料中，原数据目录不可写或被覆盖安装时仍能保留选择。
        store.Relocate(folder, target => SaveFolder(BindingPath(AppDataPaths.Root, StateDirectory), target),
            RecoveryFor(folder, AppDataPaths.Root, StateDirectory, AppDataPaths.Recovery));
    }

    internal static void SaveFolder(string binding, string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(binding)!);
        var temporary = binding + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(Encoding.UTF8.GetBytes(folder));
                output.Flush(true);
            }
            File.Move(temporary, binding, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
