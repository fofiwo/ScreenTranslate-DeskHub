using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OverTranslate.Services;

/// <summary>恢复副本在安装器不会删除的用户目录；文件始终原样复制。</summary>
internal sealed class InstalledDataRecovery(string dataRoot, string backupRoot)
{
    internal string DataRoot { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
    internal string BackupRoot { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupRoot));

    internal static string RecoveryRoot(string dataRoot, string profileDirectory) => Path.Combine(
        profileDirectory, "recovery",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant()))), "data");

    internal void Initialize(string legacyRoot)
    {
        if (IsWithin(DataRoot, BackupRoot) || IsWithin(BackupRoot, DataRoot))
            throw new InvalidDataException("主数据与恢复副本不能互相包含。");
        // 先完整复制到临时目录再发布；复制失败不能留下会被当作新资料的空目录。
        if (!Directory.Exists(DataRoot))
            CopyDirectory(Directory.Exists(BackupRoot) ? BackupRoot : legacyRoot, DataRoot, personalOnly: false);
        if (!Directory.Exists(BackupRoot)) CopyDirectory(DataRoot, BackupRoot, personalOnly: true);
    }

    internal void BackupFile(string path)
    {
        path = Path.GetFullPath(path);
        var destination = GetBackupPath(path);
        if (destination is null) return;
        if (!File.Exists(path)) { File.Delete(destination); return; }
        RejectLinks(path, DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RejectLinks(Path.GetDirectoryName(destination)!, BackupRoot);
        AtomicCopy(path, destination);
    }

    internal void RestoreFolder(string folder)
    {
        var backup = GetBackupPath(folder);
        // 自定义剪贴板位置只恢复自己的子目录，不复制用户所选父目录中的其他资料。
        if (!Directory.Exists(folder) && backup is not null && Directory.Exists(backup))
            CopyDirectory(backup, folder, personalOnly: false);
    }

    internal void ReplaceFolderBackup(string folder)
    {
        // 完整替换快照；旧副本隔离保留，恢复时不合并旧位置遗留的记录。
        var destination = GetBackupPath(folder);
        if (destination is null) return;
        var staged = destination + ".snapshot-" + Guid.NewGuid().ToString("N");
        CopyDirectory(folder, staged, personalOnly: false);
        var previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(destination)) Directory.Move(destination, previous);
        try { Directory.Move(staged, destination); }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(previous)) Directory.Move(previous, destination);
            throw;
        }
    }

    internal string? GetBackupPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!IsWithin(path, DataRoot) || string.Equals(path, DataRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = Path.GetRelativePath(DataRoot, path);
        if (!IsPersonalFile(relative)) return null;
        var destination = Path.Combine(BackupRoot, relative);
        if (Directory.Exists(Path.GetDirectoryName(path)!)) RejectLinks(Path.GetDirectoryName(path)!, DataRoot);
        if (Directory.Exists(Path.GetDirectoryName(destination)!)) RejectLinks(Path.GetDirectoryName(destination)!, BackupRoot);
        return destination;
    }

    private static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsPersonalFile(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            || relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        return parts[0] is "library" or "clipboard-history"
            || (parts.Length == 1 && (parts[0].StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                || parts[0] is "lan-preferences.json" or "sync-onboarding.json"));
    }

    private static void CopyDirectory(string source, string destination, bool personalOnly)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + ".migrating-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        // 出错时保留未发布的临时副本供排查，不改动来源、不发布半份资料。
        if (Directory.Exists(source)) CopyContents(source, source, staging, personalOnly);
        Directory.Move(staging, destination);
    }

    private static void CopyContents(string sourceRoot, string source, string destination, bool personalOnly)
    {
        RejectLinks(source, sourceRoot);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (Path.GetFileName(file).StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || (personalOnly && !IsPersonalFile(relative))) continue;
            RejectLinks(file, sourceRoot);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (personalOnly && source == sourceRoot && name is not ("library" or "clipboard-history")) continue;
            var target = Path.Combine(destination, name);
            Directory.CreateDirectory(target);
            CopyContents(sourceRoot, directory, target, personalOnly);
        }
    }

    private static void RejectLinks(string path, string root)
    {
        for (var entry = path; IsWithin(entry, root); entry = Path.GetDirectoryName(entry)!)
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("数据目录包含链接，已停止迁移或备份，请保留原文件并检查目录。");
            if (string.Equals(entry, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static void AtomicCopy(string source, string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
