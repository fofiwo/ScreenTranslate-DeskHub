using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverTranslate.Services.Library;

internal sealed class LibraryPrompt : INotifyPropertyChanged
{
    private long _copyCount;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    [JsonIgnore] public string ContentPreview => Content.ReplaceLineEndings(" ").Replace('\t', ' ');
    public long CopyCount
    {
        get => _copyCount;
        set { if (_copyCount == value) return; _copyCount = value; PropertyChanged?.Invoke(this, new(nameof(CopyCount))); }
    }
    public List<string> Tags { get; set; } = [];
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

internal sealed class LibraryConnection : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Platform { get; set; } = "";
    public string ModelType { get; set; } = "chat";
    public string Endpoint { get; set; } = "";
    public bool AllowInsecureEndpoint { get; set; }
    public string ApiKeyProtected { get; set; } = "";
    public string Model { get; set; } = "";
    private string _lastCopiedModelId = "";
    public string LastCopiedModelId
    {
        get => _lastCopiedModelId;
        set
        {
            if (_lastCopiedModelId == value) return;
            _lastCopiedModelId = value;
            PropertyChanged?.Invoke(this, new(nameof(LastCopiedModelId)));
            PropertyChanged?.Invoke(this, new(nameof(ModelIdToCopy)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string RemainingBalance { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> ModelIds { get; set; } = [];
    public DateTimeOffset? ModelsUpdatedAt { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    [JsonIgnore] public string TypeLabel => ModelPlatforms.TypeLabel(ModelType);
    [JsonIgnore] public string ModelIdToCopy => string.IsNullOrWhiteSpace(LastCopiedModelId) ? Model : LastCopiedModelId;
    [JsonIgnore] public string KeyLabel => ApiKeyProtected.Length > 0 ? "••••••••" : "未设置";
    [JsonIgnore] public string CatalogLabel => ModelsUpdatedAt is null ? "尚未获取" : $"{ModelIds.Count} 个 · {ModelsUpdatedAt.Value.ToLocalTime():MM-dd HH:mm}";
}

internal sealed class LibraryAiSettings
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    public bool AutoSummarize { get; set; } = true;
    public bool AllowInsecureEndpoint { get; set; }
}

internal sealed class LibraryState
{
    public int SchemaVersion { get; set; } = 1;
    public List<LibraryPrompt> Prompts { get; set; } = [];
    public List<LibraryConnection> Connections { get; set; } = [];
    public LibraryAiSettings Ai { get; set; } = new();
    public int SelectedTab { get; set; }
    public List<string> ImportedSources { get; set; } = [];
    public LibrarySyncMetadata? Sync { get; set; }
}

internal sealed partial class LibraryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly string _mutexName;
    private bool _persisted;
    private LibraryState _state;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "library", "library.json");

    public LibraryStore(string path)
    {
        _path = Path.GetFullPath(path);
        _mutexName = "Local\\ScreenTranslate.Library." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
        _state = Load();
    }

    private LibraryState Load()
    {
        FileStream stream;
        using (var mutex = AcquireFileMutex())
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (_persisted) throw new InvalidDataException("资料文件已被移走，请恢复原文件后重试。未写入旧快照。");
                    return new();
                }
                // 打开快照与替换共用互斥锁；句柄打开后可释放锁，保留旧文件供读取。
                stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            finally { mutex.ReleaseMutex(); }
        }
        using var snapshot = stream;
        var state = JsonSerializer.Deserialize<LibraryState>(stream, Json)
            ?? throw new InvalidDataException("资料文件为空，未覆盖原文件。");
        if (state.SchemaVersion != 1 || state.Prompts is null || state.Connections is null || state.Ai is null || state.ImportedSources is null
            || state.Prompts.Any(p => p is null || p.Id is null || p.Title is null || p.Content is null || p.Description is null || p.Tags is null || p.Tags.Any(t => t is null))
            || state.Connections.Any(c => c is null || c.Id is null || c.Platform is null || c.ModelType is null || c.Endpoint is null || c.Model is null || c.LastCopiedModelId is null || c.ApiKeyProtected is null || c.Note is null || c.RemainingBalance is null || c.ModelIds is null || c.ModelIds.Any(id => id is null))
            || state.Ai.Endpoint is null || state.Ai.Model is null || state.Ai.ApiKeyProtected is null)
            throw new InvalidDataException("资料格式不受支持，未覆盖原文件。请保留文件并检查版本。");
        LibrarySyncMerge.ValidateMetadata(state);
        _persisted = true;
        return state;
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    public LibraryState Read() { lock (_gate) { _state = Load(); return Clone(_state); } }

    private Mutex AcquireFileMutex()
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("资料正在由其他窗口保存，请稍后重试。");
            return mutex;
        }
        catch { mutex.Dispose(); throw; }
    }

    public void Update(Action<LibraryState> change) => UpdateCore(change, true);

    private void UpdateCore(Action<LibraryState> change, bool track, Func<bool>? shouldPersist = null)
    {
        lock (_gate)
        {
            // 与迁移工具等独立进程串行保存，写入前重读，避免旧快照吞掉其他资料。
            using var mutex = AcquireFileMutex();
            try
            {
                _state = Load();
                var next = Clone(_state);
                change(next);
                if (track && _state.Sync is not null) LibrarySyncMerge.Track(_state, next);
                if (shouldPersist is not null && !shouldPersist())
                {
                    // 上次已提交主文件但恢复副本失败时，同步重试仍需补齐副本。
                    AppDataPaths.BackupFile(_path);
                    return;
                }
                LibrarySyncMerge.ValidateMetadata(next);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, next, Json);
                        stream.Flush(true);
                    }
                    // Windows 的覆盖移动仍会被打开的读句柄阻止；Replace 支持保留读者的旧快照。
                    if (_persisted) File.Replace(temporary, _path, null);
                    else File.Move(temporary, _path);
                    _state = next;
                    _persisted = true;
                    AppDataPaths.BackupFile(_path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    public LibraryPrompt SavePrompt(LibraryPrompt draft, LibraryPrompt? expected = null)
    {
        if (string.IsNullOrWhiteSpace(draft.Content)) throw new InvalidOperationException("请填写提示词正文。");
        var item = Clone(draft);
        item.Title = string.IsNullOrWhiteSpace(item.Title) ? new string(item.Content.ReplaceLineEndings(" ").Take(36).ToArray()) : item.Title.Trim();
        item.Tags = item.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(12).ToList();
        item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Update(s =>
        {
            var index = s.Prompts.FindIndex(p => p.Id == item.Id);
            if (expected is not null && (index < 0 || !SamePromptContent(s.Prompts[index], expected)))
                throw new InvalidOperationException("这条提示词已被其他设备或窗口修改／删除。输入已保留，请复制输入内容，取消后核对最新资料。");
            if (index < 0) { item.CopyCount = 0; s.Prompts.Insert(0, item); }
            else { item.CopyCount = s.Prompts[index].CopyCount; s.Prompts[index] = item; }
        });
        return Clone(item);
    }

    public long RememberCopiedPrompt(string id)
    {
        long count = 0;
        Update(s =>
        {
            var item = s.Prompts.FirstOrDefault(p => p.Id == id)
                ?? throw new InvalidOperationException("提示词已删除，未保存本次复制次数。");
            count = item.CopyCount = checked(item.CopyCount + 1);
        });
        return count;
    }

    public LibraryConnection SaveConnection(LibraryConnection draft, string? newKey = null, IReadOnlyList<string>? fetchedModelIds = null, string? expectedRevision = null)
    {
        LibraryHttp.ValidateKey(newKey ?? "");
        var item = Clone(draft);
        // 保存资料不发请求；允许保留 HTTP 地址并撤销授权，实际联网由 LibraryHttp 检查此连接的开关。
        var uri = LibraryHttp.ValidateEndpoint(item.Endpoint, allowInsecure: true);
        if (string.IsNullOrWhiteSpace(item.Platform)) throw new InvalidOperationException("请填写平台名称。");
        if (!ModelPlatforms.Types.ContainsKey(item.ModelType)) throw new InvalidOperationException("请选择模型类型。");
        item.Endpoint = uri.AbsoluteUri.TrimEnd('/');
        Update(s =>
        {
            var index = s.Connections.FindIndex(c => c.Id == item.Id);
            var previous = index < 0 ? null : s.Connections[index];
            if (expectedRevision is not null && previous?.Revision != expectedRevision)
                throw new InvalidOperationException("这条模型资料已被其他设备或窗口修改／删除。输入已保留，请取消后核对最新资料。");
            var sameOrigin = previous is not null && ModelPlatforms.SameOrigin(previous.Endpoint, item.Endpoint);
            item.ApiKeyProtected = !string.IsNullOrWhiteSpace(newKey) ? LocalSecretStore.Protect(newKey.Trim()) : sameOrigin ? previous!.ApiKeyProtected : "";
            if (!uri.IsLoopback && item.ApiKeyProtected.Length == 0) throw new InvalidOperationException("请填写此连接的 API Key。");
            if (previous is null || previous.Endpoint != item.Endpoint || previous.ModelType != item.ModelType || previous.ApiKeyProtected != item.ApiKeyProtected)
            { item.ModelIds = []; item.ModelsUpdatedAt = null; }
            else { item.ModelIds = previous.ModelIds; item.ModelsUpdatedAt = previous.ModelsUpdatedAt; }
            item.LastCopiedModelId = previous is not null && previous.Endpoint == item.Endpoint && previous.ModelType == item.ModelType
                && previous.ApiKeyProtected == item.ApiKeyProtected && previous.Model == item.Model ? previous.LastCopiedModelId : "";
            if (fetchedModelIds is not null)
            {
                item.ModelIds = fetchedModelIds.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
                item.ModelsUpdatedAt = DateTimeOffset.UtcNow;
            }
            item.Revision = Guid.NewGuid().ToString("N");
            item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (index < 0) s.Connections.Add(item); else s.Connections[index] = item;
        });
        return Clone(item);
    }

    public void RememberCopiedModel(string connectionId, string revision, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new InvalidOperationException("模型 ID 不能为空。");
        Update(s =>
        {
            var current = s.Connections.FirstOrDefault(c => c.Id == connectionId);
            if (current is null || current.Revision != revision)
                throw new InvalidOperationException("连接资料已更改，未保存本次复制记录。");
            // 复制记录不改变连接配置，也不使正在获取的模型目录失效。
            current.LastCopiedModelId = modelId;
        });
    }

    public void MoveConnection(string id, string targetId, bool after) => Update(s =>
    {
        var item = s.Connections.FirstOrDefault(c => c.Id == id);
        var target = s.Connections.FirstOrDefault(c => c.Id == targetId);
        if (item is null || target is null)
            throw new InvalidOperationException("连接资料已删除，请刷新列表后重新排序。");
        if (id == targetId) return;
        // 只移动当前资料，不回写拖拽开始时的快照，保留其他窗口的编辑和新增项。
        s.Connections.Remove(item);
        s.Connections.Insert(s.Connections.IndexOf(target) + (after ? 1 : 0), item);
    });

    public void DuplicateConnection(string id) => Update(s =>
    {
        var copy = Clone(s.Connections.Single(c => c.Id == id));
        copy.Id = Guid.NewGuid().ToString("N"); copy.Revision = Guid.NewGuid().ToString("N");
        copy.CreatedAt = copy.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        s.Connections.Add(copy);
    });

    public async Task RefreshModelsAsync(string id, Func<LibraryConnection, CancellationToken, Task<IReadOnlyList<string>>> fetch, CancellationToken token = default)
    {
        var original = Read().Connections.Single(c => c.Id == id);
        var ids = await fetch(original, token);
        token.ThrowIfCancellationRequested();
        Update(s =>
        {
            var current = s.Connections.FirstOrDefault(c => c.Id == id);
            if (current is null || current.Revision != original.Revision)
                throw new InvalidOperationException("连接资料已更改，本次列表未写入，请重新获取。");
            current.ModelIds = ids.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
            current.ModelsUpdatedAt = DateTimeOffset.UtcNow;
            current.Revision = Guid.NewGuid().ToString("N");
        });
    }

    public static IEnumerable<LibraryPrompt> FilterPrompts(LibraryState state, string query, IEnumerable<string> tags)
    {
        var selected = tags.ToHashSet();
        return state.Prompts.Where(p => (selected.Count == 0 || p.Tags.Any(selected.Contains)) &&
            string.Join(' ', new[] { p.Title, p.Description, p.Content }.Concat(p.Tags)).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CopyCount);
    }
}
