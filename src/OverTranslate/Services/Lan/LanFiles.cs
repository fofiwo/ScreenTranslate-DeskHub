using System.IO;
using System.Security.Cryptography;

namespace OverTranslate.Services.Lan;

internal static class LanFiles
{
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')
            || name.EndsWith('.') || name.EndsWith(' ') || name.Any(char.IsControl))
            throw new InvalidDataException("文件名无效。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                && "123456789¹²³".Contains(stem[3])))
            throw new InvalidDataException("不接受系统设备文件名。");
    }

    public static string ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new InvalidDataException("请选择本机的完整接收文件夹路径。");
        var normalized = Path.GetFullPath(folder);
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("请选择本机的完整接收文件夹路径。");
        return normalized;
    }

    public static async Task<string> ReceiveAsync(Stream source, LanHeader header, string folder,
        Action<long> progress, CancellationToken token)
    {
        LanProtocol.ValidatePayload(header);
        folder = ValidateFolder(folder);
        Directory.CreateDirectory(folder);
        var drive = new DriveInfo(Path.GetPathRoot(folder)!);
        if (drive.AvailableFreeSpace < header.Length + 16 * 1024 * 1024)
            throw new IOException("接收目录所在磁盘空间不足。");
        var temporary = Path.Combine(folder, ".screentranslate-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65536];
                long done = 0;
                while (done < header.Length)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, header.Length - done)), token)
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("传输提前中断。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    done += count;
                    progress(done);
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(header.Hash)))
                throw new InvalidDataException("文件完整性校验失败。");
            token.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(header.Name);
            var extension = Path.GetExtension(header.Name);
            for (var index = 0; index < 10000; index++)
            {
                var destination = Path.Combine(folder, index == 0 ? header.Name : $"{stem} ({index}){extension}");
                try { File.Move(temporary, destination, false); return destination; }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
            throw new IOException("同名文件过多，无法保存。");
        }
        finally
        {
            // 仅清理由本次接收创建的随机临时文件，不触碰原文件或已完成文件。
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
