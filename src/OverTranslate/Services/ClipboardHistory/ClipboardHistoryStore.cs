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
    [JsonIgnore] public string Category => Files.Length > 0 ? "文件" : HasImage ? "图片"
        : Html.Length > 0 || Rtf.Length > 0 ? "富文本"
        : Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" ? "链接"
        : LooksLikeCode(Text) ? "代码" : "文本";
    [JsonIgnore] public string DisplayText => Files.Length > 0 ? string.Join("\n", Files.Select(Path.GetFileName))
        : Text.Length > 0 ? Text : HasImage ? "图片" : Html.Length > 0 ? HtmlPreview(Html)
            : "富文本（打开预览或复制以使用原始格式）";
    [JsonIgnore] public string Header => string.IsNullOrEmpty(Source) ? Category : Source;
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
    private readonly Dictionary<string, ClipboardRecord> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadableIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ClipboardRecord>? _snapshot;
    public static string DefaultFolder => Path.Combine(AppDataPaths.Root, "clipboard-history");
    public string Folder { get; }
    public int UnreadableCount { get; private set; }
    public event EventHandler? Changed;

    public ClipboardHistoryStore(string folder)
    {
        Folder = Path.GetFullPath(folder);
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
                    Source = value.CopiedAt >= existing.CopiedAt && !string.IsNullOrEmpty(value.Source) ? value.Source : existing.Source };
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
        lock (_gate) DeleteCore(id);
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
        }
        if (deleted > 0) Changed?.Invoke(this, EventArgs.Empty);
        return (deleted, failed);
    }

    private void DeleteCore(string id)
        {
            if (!_records.ContainsKey(id)) return;
            // 两个文件都取得删除权限后才开始删除，锁定/权限失败时保持记录可见。
            using var metadata = ReserveDelete(Path.Combine(Folder, id + ".clip"));
            using var image = File.Exists(ImagePath(id)) ? ReserveDelete(ImagePath(id)) : null;
            File.Delete(Path.Combine(Folder, id + ".clip"));
            // 只删除本条拥有的附件，绝不删除记录中引用的原文件。
            File.Delete(ImagePath(id));
            _records.Remove(id); _snapshot = null;
        }
    public byte[] ReadImage(string id) { RequireId(id); return File.ReadAllBytes(ImagePath(id)); }
    private string ImagePath(string id) => Path.Combine(Folder, id + ".png");
    private void Save(ClipboardRecord record) => AtomicWrite(Path.Combine(Folder, record.Id + ".clip"),
        Encoding.UTF8.GetBytes(LocalSecretStore.Protect(JsonSerializer.Serialize(record))));

    public ClipboardWindowPlacement? ReadPlacement()
    {
        var path = Path.Combine(Folder, "window.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<ClipboardWindowPlacement>(File.ReadAllText(path));
    }
    public void SavePlacement(ClipboardWindowPlacement placement)
    {
        Directory.CreateDirectory(Folder);
        AtomicWrite(Path.Combine(Folder, "window.json"), JsonSerializer.SerializeToUtf8Bytes(placement));
    }
    public void ClearPlacement() => File.Delete(Path.Combine(Folder, "window.json"));

    private static bool ValidId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidContent(ClipboardRecord value) => value.Text is not null && value.Html is not null
        && value.Rtf is not null && value.Source is not null && value.Files is not null
        && value.Files.All(f => !string.IsNullOrWhiteSpace(f));
    private static SafeFileHandle ReserveDelete(string path)
    {
        var handle = CreateFile(path, 0x00010000, FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("无法取得历史文件删除权限，记录已保留。", new System.ComponentModel.Win32Exception(error));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint attributes, IntPtr template);
    private static void RequireId(string id) { if (!ValidId(id)) throw new ArgumentException("无效的剪贴板记录标识。", nameof(id)); }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
