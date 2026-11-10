using System.IO;
using System.IO.Compression;

namespace OverTranslate.Services.Lan;

internal sealed class LanBundle(string path) : IDisposable
{
    internal const int MaxEntries = 10000;
    public string Path { get; } = path;
    public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }

    public static Task<LanBundle> CreateAsync(string[] paths, CancellationToken token) => Task.Run(async () =>
    {
        if (paths.Length is < 1 or > 100) throw new InvalidDataException("请选择 1–100 个文件或文件夹。");
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "screentranslate-" + Guid.NewGuid().ToString("N") + ".zip");
        var bundle = new LanBundle(temporary);
        try
        {
            var entries = new List<(string Source, string Name, bool Folder)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            void Add(string source, string name)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries) throw new InvalidDataException("一次最多发送 10000 个文件和文件夹。");
                ValidateEntry(name);
                if (!names.Add(name)) throw new InvalidDataException("所选项目存在同名或重复路径，请分批发送。");
                var attributes = File.GetAttributes(source);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("暂不支持符号链接或目录联接，请选择实际文件。");
                var directory = (attributes & FileAttributes.Directory) != 0;
                entries.Add((source, name, directory));
                if (directory)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(source)) Add(child, name + "/" + System.IO.Path.GetFileName(child));
                }
                else
                {
                    total = checked(total + new FileInfo(source).Length);
                    if (total > LanProtocol.MaxFile) throw new InvalidDataException("本批文件总大小超过 64 GB。");
                }
            }
            foreach (var source in paths)
            {
                var full = System.IO.Path.GetFullPath(source).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                Add(full, System.IO.Path.GetFileName(full));
            }
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    var zipped = archive.CreateEntry(entry.Name + (entry.Folder ? "/" : ""), CompressionLevel.NoCompression);
                    if (entry.Folder) continue;
                    await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    await using var output = zipped.Open();
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
            if (new FileInfo(temporary).Length > LanProtocol.MaxFile) throw new InvalidDataException("传输包超过 64 GB。");
            return bundle;
        }
        catch { bundle.Dispose(); throw; }
    }, token);

    private static string[] ValidateEntry(string name)
    {
        if (name.Length > 2000 || name.Contains('\\')) throw new InvalidDataException("文件路径无效或过长。");
        var parts = name.TrimEnd('/').Split('/');
        if (parts.Length > 64) throw new InvalidDataException("文件夹层级超过 64 层。");
        foreach (var part in parts) LanFiles.ValidateName(part);
        return parts;
    }

    public static Task<string> ExtractAsync(string archivePath, string folder, string name, CancellationToken token) => Task.Run(async () =>
    {
        folder = LanFiles.ValidateFolder(folder);
        LanFiles.ValidateName(name);
        Directory.CreateDirectory(folder);
        var staging = System.IO.Path.Combine(folder, ".screentranslate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count is < 1 or > MaxEntries) throw new InvalidDataException("文件数量无效或超过上限。");
            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var parts = ValidateEntry(entry.FullName);
                if (!names.Add(string.Join('/', parts)) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不接受重复路径或符号链接。");
                total = checked(total + entry.Length);
                if (total > LanProtocol.MaxFile) throw new InvalidDataException("解包大小超过 64 GB。");
            }
            if (new DriveInfo(System.IO.Path.GetPathRoot(folder)!).AvailableFreeSpace < total + 16 * 1024 * 1024)
                throw new IOException("接收磁盘空间不足。");
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var destination = System.IO.Path.Combine(staging, System.IO.Path.Combine(ValidateEntry(entry.FullName)));
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var buffer = new byte[65536];
                long written = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    written += count;
                    if (written > entry.Length) throw new InvalidDataException("文件大小与声明不匹配。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (written != entry.Length) throw new InvalidDataException("文件不完整。");
            }
            for (var index = 0; index < 10000; index++)
            {
                token.ThrowIfCancellationRequested();
                var destination = System.IO.Path.Combine(folder, index == 0 ? name : $"{name} ({index})");
                try { Directory.Move(staging, destination); return destination; }
                catch (IOException) when (Directory.Exists(destination) || File.Exists(destination)) { }
            }
            throw new IOException("同名接收批次过多。");
        }
        finally
        {
            // 仅删除本次创建的暂存目录，成功发布的批次和用户原文件不受影响。
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }, token);
}
