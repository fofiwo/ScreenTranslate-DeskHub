using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OverTranslate.Services.ClipboardHistory;

internal sealed record ClipboardRecord
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public string Html { get; init; } = "";
    public string Rtf { get; init; } = "";
    public string[] Files { get; init; } = [];
    public bool HasImage { get; init; }
    public string Source { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CopiedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PinnedAt { get; init; }
    [JsonIgnore] public bool IsPinned => PinnedAt is not null;
    [JsonIgnore] public string PinLabel => IsPinned ? "取消置顶" : "置顶记录";
    [JsonIgnore] public string Category => Files.Length > 0 ? (Files.All(IsImageFile) ? "图片" : "文件") : HasImage ? "图片"
        : Html.Length > 0 || Rtf.Length > 0 ? "富文本"
        : Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" ? "链接"
        : LooksLikeCode(Text) ? "代码" : "文本";
    [JsonIgnore] public string DisplayText => Files.Length > 0 ? string.Join("\n", Files.Select(Path.GetFileName))
        : Text.Length > 0 ? Text : HasImage ? "图片" : Html.Length > 0 ? HtmlPreview(Html)
            : "富文本（打开预览或复制以使用原始格式）";
    [JsonIgnore] public string Header => string.IsNullOrEmpty(Source) ? Category : Source;
    [JsonIgnore] public string SourceLabel => string.IsNullOrEmpty(Source) ? ""
        : IsMouseWithoutBorders(Source) ? "跨设备 · Mouse Without Borders（电脑名未知）"
        : Source.StartsWith("来自 ", StringComparison.Ordinal) ? Source : "来源：" + Source;
    [JsonIgnore] public string ExactTime => $"复制：{CopiedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n首次：{CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
    [JsonIgnore] public string TimeLabel
    {
        get
        {
            var age = DateTimeOffset.UtcNow - CopiedAt;
            return age.TotalMinutes < 1 ? "刚刚" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} 分钟前"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours} 小时前" : age.TotalDays < 7 ? $"{(int)age.TotalDays} 天前"
                : CopiedAt.LocalDateTime.ToString("MM-dd HH:mm");
        }
    }
    public bool Matches(string category, string query) => (category == "全部" || Category == category)
        && (query.Length == 0 || Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Html.Contains(query, StringComparison.OrdinalIgnoreCase) || Source.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Files.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase)));

    internal static bool IsMouseWithoutBorders(string source) =>
        source.Equals("MouseWithoutBorders.exe", StringComparison.OrdinalIgnoreCase)
        || source.Equals("PowerToys.MouseWithoutBorders.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp"
        or ".avif" or ".heic" or ".heif" or ".svg" or ".ico"
        && !Directory.Exists(path);

    private static bool LooksLikeCode(string text)
    {
        try { return Regex.IsMatch(text[..Math.Min(text.Length, 16384)],
            @"(?m)^\s*(?:using\s+[\w.]+;|(?:public|private|internal)\s+(?:class|static|void)|(?:const|let|var)\s+\w+\s*=|(?:def|class|import|from)\s+\w+|(?:function|interface|SELECT|INSERT|CREATE TABLE)\s+|[{}]\s*$)|</?[a-zA-Z][^>]*>",
            RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
    private static string HtmlPreview(string html)
    {
        try { return System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200))).Trim(); }
        catch (RegexMatchTimeoutException) { return html; }
    }
}

internal sealed record ClipboardWindowPlacement(int X, int Y, double Width, double Height, string Monitor);

internal sealed class ClipboardHistoryStore
{
    private readonly object _gate = new();
    private InstalledDataRecovery? _recovery;
    private readonly Dictionary<string, ClipboardRecord> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadableIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ClipboardRecord>? _snapshot;
    public static string DefaultFolder => ClipboardHistoryStorage.ResolveFolder(AppDataPaths.Root);
    public string Folder { get; private set; }
    public int UnreadableCount { get; private set; }
    public event EventHandler? Changed;

    public ClipboardHistoryStore(string folder, InstalledDataRecovery? recovery = null)
    {
        Folder = Path.GetFullPath(folder);
        _recovery = recovery ?? AppDataPaths.Recovery;
        if (!Directory.Exists(Folder)) return;
        foreach (var path in Directory.EnumerateFiles(Folder, "*.clip"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ClipboardRecord>(LocalSecretStore.Unprotect(File.ReadAllText(path)));
                if (record is null || record.SchemaVersion != 1 || !ValidId(record.Id)
                    || !string.Equals(Path.GetFileNameWithoutExtension(path), record.Id, StringComparison.OrdinalIgnoreCase)
                    || !ValidContent(record)) throw new InvalidDataException();
                _records.Add(record.Id, record);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or ArgumentException or System.ComponentModel.Win32Exception)
            { UnreadableCount++; _unreadableIds.Add(Path.GetFileNameWithoutExtension(path)); }
        }
    }

    public IReadOnlyList<ClipboardRecord> Read()
    {
        lock (_gate) return _snapshot ??= Array.AsReadOnly(_records.Values.OrderByDescending(r => r.IsPinned).ThenByDescending(r => r.CopiedAt)
            .ThenByDescending(r => r.PinnedAt).ToArray());
    }

    internal void Relocate(string folder, Action<string> saveFolder, InstalledDataRecovery? recovery = null)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\"))
            throw new ArgumentException("请选择本机的完整目录。");
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        lock (_gate)
        {
            var source = Path.TrimEndingDirectorySeparator(Folder);
            if (string.Equals(folder, source, StringComparison.OrdinalIgnoreCase)) return;
            if (folder.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || source.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("新旧保存目录不能互相包含。");
            RejectLinks(source);
            RejectLinks(folder);
            if (File.Exists(folder) || (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))
                throw new IOException("目标 clipboard-history 目录已有内容，请选择其他位置，避免覆盖已有资料。");
            foreach (var record in _records.Values.Where(record => record.HasImage))
                if (!File.Exists(ImagePath(record.Id)))
                    throw new IOException("部分历史图片已被外部删除，无法完整迁移。请先在面板中移除失效记录。");

            var parent = Path.GetDirectoryName(folder) ?? throw new IOException("不能使用磁盘根目录。");
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, ".clipboard-history-migrating-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            // 与写入、删除共用锁；完整复制并保存绑定后，所有现有调用方同时切换。
            if (Directory.Exists(source))
                foreach (var file in Directory.EnumerateFiles(source))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                        || !(name.Equals("window.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".clip", StringComparison.OrdinalIgnoreCase)
                            || (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && ValidId(Path.GetFileNameWithoutExtension(name).ToLowerInvariant())))) continue;
                    RejectLinks(file);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output); output.Flush(true);
                }
            foreach (var record in _records.Values)
            {
                var path = Path.Combine(staging, record.Id + ".clip");
                // 外部删除不清空内存；仍在会话中的文字可在切换目录时重新保存。
                if (!File.Exists(path))
                {
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(Encoding.UTF8.GetBytes(LocalSecretStore.Protect(JsonSerializer.Serialize(record))));
                    output.Flush(true);
                }
            }
            if (Directory.Exists(folder)) Directory.Delete(folder); // 仅移除空目录，竞争写入时拒绝切换。
            Directory.Move(staging, folder);
            try
            {
                recovery?.ReplaceFolderBackup(folder);
                saveFolder(folder);
            }
            catch (Exception failure)
            {
                // 保留本次副本供排查，但腾出正式目标，修复权限或占用后可原位重试。
                try { Directory.Move(folder, staging); }
                catch (Exception rollback)
                {
                    throw new IOException($"目录切换失败，且未启用的副本被占用，仍保留在 {folder}。请解除占用或选择其他位置。",
                        new AggregateException(failure, rollback));
                }
                throw;
            }
            Folder = folder;
            _recovery = recovery;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void RejectLinks(string path)
    {
        for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("保存目录不能包含链接或目录联接，请选择普通本地文件夹。");
    }

    public ClipboardRecord Add(ClipboardRecord value, byte[]? png = null)
    {
        if (value.SchemaVersion != 1 || !ValidContent(value)) throw new InvalidDataException("剪贴板记录字段无效。");
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write(value.Text); writer.Write(value.Html); writer.Write(value.Rtf);
            writer.Write(value.Files.Length); foreach (var file in value.Files) writer.Write(file);
            writer.Write(png?.Length ?? 0); if (png is not null) writer.Write(png);
        }
        var id = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        ClipboardRecord record;
        lock (_gate)
        {
            if (_unreadableIds.Contains(id) || (!_records.ContainsKey(id) && File.Exists(Path.Combine(Folder, id + ".clip"))))
                throw new InvalidDataException("相同内容的历史文件无法读取，已保留原文件，未覆盖。");
            if (_records.TryGetValue(id, out var existing))
                record = existing with { CopiedAt = value.CopiedAt > existing.CopiedAt ? value.CopiedAt : existing.CopiedAt,
                    CreatedAt = value.CopiedAt < existing.CreatedAt ? value.CopiedAt : existing.CreatedAt,
                    Source = SelectSource(existing, value) };
            else record = value with { Id = id, HasImage = png is not null, CreatedAt = value.CopiedAt };
            Directory.CreateDirectory(Folder);
            // 附件先落盘；元数据提交成功前，界面不会把一半写入的记录当作可用记录。
            var addedImage = png is not null && !File.Exists(ImagePath(id));
            if (addedImage) AtomicWrite(ImagePath(id), png!);
            try { Save(record); }
            catch
            {
                // 只撤销本次尚未提交的新附件，不触碰已保存历史。
                if (addedImage) { try { File.Delete(ImagePath(id)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                throw;
            }
            _records[id] = record; _snapshot = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return record;
    }

    private static string SelectSource(ClipboardRecord existing, ClipboardRecord value)
    {
        // shortcut: 无共享事件 ID，仅合并两秒内相同内容的双通道来源；跨事件精确归因需同步协议提供事件 ID。
        if ((value.CopiedAt - existing.CopiedAt).Duration() <= TimeSpan.FromSeconds(2))
        {
            if (existing.Source.StartsWith("来自 ", StringComparison.Ordinal) && ClipboardRecord.IsMouseWithoutBorders(value.Source))
                return existing.Source;
            if (value.Source.StartsWith("来自 ", StringComparison.Ordinal) && ClipboardRecord.IsMouseWithoutBorders(existing.Source))
                return value.Source;
        }
        return value.CopiedAt >= existing.CopiedAt && !string.IsNullOrEmpty(value.Source) ? value.Source : existing.Source;
    }

    public void SetPinned(string id, bool pinned)
    {
        RequireId(id);
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record)) return;
            record = record with { PinnedAt = pinned ? DateTimeOffset.UtcNow : null };
            Save(record); _records[id] = record; _snapshot = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkCopied(string id, DateTimeOffset copiedAt)
    {
        RequireId(id);
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record) || copiedAt <= record.CopiedAt) return;
            record = record with { CopiedAt = copiedAt };
            Save(record); _records[id] = record; _snapshot = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        RequireId(id);
        lock (_gate) { if (_records.ContainsKey(id)) DeleteCore(id); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public (int Deleted, int Failed) DeleteUnpinned()
    {
        var deleted = 0; var failed = 0;
        lock (_gate)
        {
            // 与置顶和写入共用锁；本批之后新复制的内容仍正常保存。
            foreach (var id in _records.Values.Where(r => !r.IsPinned).Select(r => r.Id).ToArray())
            {
                try { DeleteCore(id); deleted++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
            }
            try
            {
                var orphans = new[] { Folder, _recovery?.GetBackupPath(Folder) }
                    .Where(folder => folder is not null && Directory.Exists(folder))
                    .SelectMany(folder => Directory.EnumerateFiles(folder!))
                    .Where(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(path).Equals(".clip", StringComparison.OrdinalIgnoreCase))
                    .Select(path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant())
                    .Where(id => ValidId(id) && !_records.ContainsKey(id) && !_unreadableIds.Contains(id)
                        && !File.Exists(Path.Combine(Folder, id + ".clip"))).Distinct().ToArray();
                foreach (var id in orphans)
                {
                    try
                    {
                        // 文件名不足以证明归属；只有可读取且未置顶的恢复记录才能授权清理残留。
                        var backup = _recovery?.GetBackupPath(Path.Combine(Folder, id + ".clip"));
                        if (backup is null) { failed++; continue; }
                        var record = JsonSerializer.Deserialize<ClipboardRecord>(LocalSecretStore.Unprotect(File.ReadAllText(backup)));
                        if (record is null || record.SchemaVersion != 1 || record.Id != id || !ValidContent(record))
                        { failed++; continue; }
                        if (!record.IsPinned) DeleteCore(id);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                        or JsonException or CryptographicException or FormatException or ArgumentException or System.ComponentModel.Win32Exception)
                    { failed++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        if (deleted > 0) Changed?.Invoke(this, EventArgs.Empty);
        return (deleted, failed);
    }

    private void DeleteCore(string id)
    {
        if (_unreadableIds.Contains(id)) throw new InvalidDataException("无法读取的历史文件已保留，请先检查数据文件。");
        // 只处理自有记录和附件；全部取得删除权限后再动磁盘，主记录最后删除。
        var paths = new[] { ImagePath(id), Path.Combine(Folder, id + ".clip") }
            .SelectMany(path => _recovery?.GetBackupPath(path) is { } backup ? new[] { backup, path } : new[] { path }).ToArray();
        var handles = new List<SafeFileHandle>();
        try
        {
            foreach (var path in paths)
                if (ReserveDelete(path) is { } handle) handles.Add(handle);
            foreach (var path in paths) File.Delete(path);
            _records.Remove(id); _snapshot = null;
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }
    public byte[] ReadImage(string id) { RequireId(id); lock (_gate) return File.ReadAllBytes(ImagePath(id)); }
    private string ImagePath(string id) => Path.Combine(Folder, id + ".png");
    private void Save(ClipboardRecord record) => AtomicWrite(Path.Combine(Folder, record.Id + ".clip"),
        Encoding.UTF8.GetBytes(LocalSecretStore.Protect(JsonSerializer.Serialize(record))));

    public ClipboardWindowPlacement? ReadPlacement()
    {
        lock (_gate)
        {
            var path = Path.Combine(Folder, "window.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ClipboardWindowPlacement>(File.ReadAllText(path));
        }
    }
    public void SavePlacement(ClipboardWindowPlacement placement)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            AtomicWrite(Path.Combine(Folder, "window.json"), JsonSerializer.SerializeToUtf8Bytes(placement));
        }
    }
    public void ClearPlacement()
    {
        lock (_gate)
        {
            var path = Path.Combine(Folder, "window.json");
            File.Delete(path);
            _recovery?.BackupFile(path);
        }
    }

    private static bool ValidId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidContent(ClipboardRecord value) => value.Text is not null && value.Html is not null
        && value.Rtf is not null && value.Source is not null && value.Files is not null
        && value.Files.All(f => !string.IsNullOrWhiteSpace(f));
    private static SafeFileHandle? ReserveDelete(string path)
    {
        var handle = CreateFile(path, 0x00010000, FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException("历史文件为只读，已保留记录，请解除只读后重试。");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error is 2 or 3) return null;
        throw new IOException("无法取得历史文件删除权限，记录已保留。", new System.ComponentModel.Win32Exception(error));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint attributes, IntPtr template);
    private static void RequireId(string id) { if (!ValidId(id)) throw new ArgumentException("无效的剪贴板记录标识。", nameof(id)); }
    private void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
            _recovery?.BackupFile(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
