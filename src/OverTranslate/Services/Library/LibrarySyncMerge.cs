using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Services.Library;

// 版本向量不依赖两台电脑的时钟；删除标记永久保留，防止离线设备带回已删除资料。
internal sealed class LibrarySyncMetadata
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string Machine { get; set; } = Environment.MachineName;
    public string DeviceBindingProtected { get; set; } = "";
    public Dictionary<string, LibrarySyncVersion> Versions { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class LibrarySyncVersion
{
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public Dictionary<string, long> Clock { get; set; } = new(StringComparer.Ordinal);
    public bool Deleted { get; set; }
    // 持久化的分支只含本机 DPAPI 密文；不可直接作为传输 Version 序列化。
    public List<LibrarySyncItem> Branches { get; set; } = [];
}

internal sealed class LibrarySyncItem
{
    public string Id { get; set; } = "";
    public LibrarySyncVersion Version { get; set; } = new();
    public LibraryPrompt? Prompt { get; set; }
    public LibraryConnection? Connection { get; set; }
    // 仅存在于内存中的传输 DTO，落盘前必须在接收设备上重新用 DPAPI 保护。
    public string ApiKey { get; set; } = "";
    public List<LibrarySyncItem> Alternatives { get; set; } = [];
}

internal sealed class LibrarySyncSnapshot
{
    internal const int MaxBytes = 8 * 1024 * 1024 - 28;

    public int Protocol { get; set; } = 2;
    public List<LibrarySyncItem> Items { get; set; } = [];
}

internal static class LibrarySyncMerge
{
    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    internal static Dictionary<string, string> LocalRecords(LibraryState state)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in state.Prompts)
        {
            var value = Copy(p); value.CopyCount = 0;
            result.Add("p:" + p.Id, JsonSerializer.Serialize(value));
        }
        foreach (var c in state.Connections)
        {
            var value = Copy(c); value.LastCopiedModelId = ""; value.Revision = "";
            value.AllowInsecureEndpoint = false;
            result.Add("c:" + c.Id, JsonSerializer.Serialize(value));
        }
        return result;
    }

    internal static void ValidateMetadata(LibraryState state)
    {
        var meta = state.Sync;
        if (meta is null) return;
        if (!Guid.TryParseExact(meta.DeviceId, "N", out _) || meta.Machine is null || meta.DeviceBindingProtected is null || meta.Versions is null
            || meta.Versions.Count > 10000 || meta.Versions.Any(pair => pair.Key.Length is < 3 or > 180
                || !(pair.Key.StartsWith("p:", StringComparison.Ordinal) || pair.Key.StartsWith("c:", StringComparison.Ordinal))
                || pair.Value is null || !Guid.TryParseExact(pair.Value.Revision, "N", out _)
                || pair.Value.Branches is null || pair.Value.Branches.Count > 128
                || pair.Value.Clock is null || pair.Value.Clock.Count is < 1 or > 128
                || pair.Value.Clock.Any(v => !Guid.TryParseExact(v.Key, "N", out _) || v.Value is < 1 or > long.MaxValue - 1)))
            throw new InvalidDataException("同步版本记录损坏或超出上限，未覆盖资料文件。请保留原文件并检查版本。");
        var ids = state.Prompts.Select(p => "p:" + p.Id).Concat(state.Connections.Select(c => "c:" + c.Id)).ToHashSet(StringComparer.Ordinal);
        if (meta.Versions.Any(v => v.Value.Deleted == ids.Contains(v.Key)))
            throw new InvalidDataException("同步版本记录与资料不一致，未覆盖资料文件。请保留原文件并检查版本。");
        foreach (var pair in meta.Versions)
            foreach (var branch in pair.Value.Branches)
            {
                if (branch is null || branch.Id != pair.Key || branch.ApiKey != "" || branch.Alternatives is null
                    || branch.Alternatives.Count != 0 || branch.Version is null || branch.Version.Branches is null
                    || branch.Version.Branches.Count != 0 || branch.Version.Clock is null
                    || !Guid.TryParseExact(branch.Version.Revision, "N", out _)
                    || branch.Version.Clock.Count is < 1 or > 128
                    || branch.Version.Clock.Any(v => !Guid.TryParseExact(v.Key, "N", out _) || v.Value is < 1 or > long.MaxValue - 1)
                    || branch.Version.Deleted && (branch.Prompt is not null || branch.Connection is not null)
                    || !branch.Version.Deleted && (pair.Key.StartsWith("p:", StringComparison.Ordinal)
                        ? branch.Prompt is null || branch.Prompt.Id != pair.Key[2..] || branch.Connection is not null
                        : branch.Connection is null || branch.Connection.Id != pair.Key[2..] || branch.Prompt is not null || branch.Connection.ApiKeyProtected is null))
                    throw new InvalidDataException("同步并发分支记录损坏，未覆盖资料文件。");
            }
    }

    public static void Track(LibraryState before, LibraryState after)
    {
        var meta = after.Sync ?? throw new InvalidDataException("同步元数据缺失。");
        var moved = meta.Machine != Environment.MachineName;
        if (meta.DeviceBindingProtected.Length > 0)
        {
            try { moved |= LocalSecretStore.Unprotect(meta.DeviceBindingProtected) != "ScreenTranslate.LibrarySync.Device"; }
            catch (Exception) { moved = true; }
        }
        if (moved)
        { meta.DeviceId = Guid.NewGuid().ToString("N"); meta.Machine = Environment.MachineName; }
        if (moved || meta.DeviceBindingProtected.Length == 0)
            meta.DeviceBindingProtected = LocalSecretStore.Protect("ScreenTranslate.LibrarySync.Device");
        var old = LocalRecords(before); var current = LocalRecords(after);
        foreach (var id in old.Keys.Union(current.Keys, StringComparer.Ordinal))
        {
            if (old.GetValueOrDefault(id) == current.GetValueOrDefault(id) && meta.Versions.ContainsKey(id)) continue;
            var version = meta.Versions.GetValueOrDefault(id) ?? new();
            version = Copy(version);
            version.Branches = [];
            version.Clock[meta.DeviceId] = checked(version.Clock.GetValueOrDefault(meta.DeviceId) + 1);
            version.Revision = Guid.NewGuid().ToString("N"); version.Deleted = !current.ContainsKey(id);
            meta.Versions[id] = version;
        }
    }

    internal static LibrarySyncItem ExportItem(LibraryState state, string id, LibrarySyncVersion version,
        IReadOnlyDictionary<string, LibraryPrompt>? prompts = null, IReadOnlyDictionary<string, LibraryConnection>? connections = null)
    {
        var item = new LibrarySyncItem { Id = id, Version = Copy(version), Alternatives = version.Branches.Select(WireBranch).ToList() };
        item.Version.Branches = [];
        item.Version.Clock = item.Version.Clock.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value);
        if (version.Deleted) return item;
        if (id.StartsWith("p:", StringComparison.Ordinal))
        { item.Prompt = Copy(prompts is null ? state.Prompts.Single(p => p.Id == id[2..]) : prompts[id[2..]]); item.Prompt.CopyCount = 0; }
        else
        {
            item.Connection = Copy(connections is null ? state.Connections.Single(c => c.Id == id[2..]) : connections[id[2..]]);
            item.ApiKey = LocalSecretStore.Unprotect(item.Connection.ApiKeyProtected);
            item.Connection.ApiKeyProtected = ""; item.Connection.AllowInsecureEndpoint = false;
            item.Connection.LastCopiedModelId = ""; item.Connection.Revision = "";
        }
        return item;
    }

    private static LibrarySyncItem WireBranch(LibrarySyncItem stored)
    {
        var wire = Copy(stored);
        wire.Version.Clock = wire.Version.Clock.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value);
        if (wire.Connection is { } connection)
        {
            wire.ApiKey = LocalSecretStore.Unprotect(connection.ApiKeyProtected);
            connection.ApiKeyProtected = "";
        }
        return wire;
    }

    private static LibrarySyncItem StoredBranch(LibrarySyncItem wire)
    {
        var stored = Copy(wire);
        if (stored.Connection is { } connection)
            connection.ApiKeyProtected = stored.ApiKey.Length > 0 ? LocalSecretStore.Protect(stored.ApiKey) : "";
        stored.ApiKey = ""; stored.Alternatives = []; stored.Version.Branches = [];
        return stored;
    }

    internal static string VersionStamp(LibrarySyncVersion version) => JsonSerializer.Serialize(new
    {
        version.Revision, version.Deleted,
        Clock = version.Clock.OrderBy(v => v.Key, StringComparer.Ordinal),
        Branches = version.Branches.OrderBy(b => b.Version.Revision, StringComparer.Ordinal).Select(b => VersionStamp(b.Version))
    });

    public static LibrarySyncSnapshot Export(LibraryState state, IEnumerable<string>? ids = null)
    {
        var prompts = state.Prompts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var connections = state.Connections.ToDictionary(c => c.Id, StringComparer.Ordinal);
        return new()
        {
            Items = (ids ?? state.Sync!.Versions.Keys).Order(StringComparer.Ordinal)
                .Select(id => ExportItem(state, id, state.Sync!.Versions[id], prompts, connections)).ToList()
        };
    }

    internal static void Validate(LibrarySyncSnapshot snapshot)
    {
        if (snapshot.Protocol != 2 || snapshot.Items is null || snapshot.Items.Count > 10000)
            throw new InvalidDataException("同步版本或资料数量不受支持。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            if (item is null || item.Id is null || item.Id.Length is < 3 or > 180 || !ids.Add(item.Id)
                || !(item.Id.StartsWith("p:", StringComparison.Ordinal) || item.Id.StartsWith("c:", StringComparison.Ordinal))
                || item.Version is null || !Guid.TryParseExact(item.Version.Revision, "N", out _)
                || item.Version.Branches is null || item.Version.Branches.Count != 0
                || item.Version.Clock is null || item.Version.Clock.Count is < 1 or > 128
                || item.Version.Clock.Any(v => !Guid.TryParseExact(v.Key, "N", out _) || v.Value is < 1 or > long.MaxValue - 1)
                || item.ApiKey is null || item.ApiKey.Length > 16384 || item.Alternatives is null || item.Alternatives.Count > 128)
                throw new InvalidDataException("同步资料格式无效。");
            if (item.Alternatives.Count > 0)
            {
                foreach (var branch in item.Alternatives)
                {
                    if (branch is null || branch.Id != item.Id || branch.Alternatives is null || branch.Alternatives.Count != 0)
                        throw new InvalidDataException("同步并发分支格式无效。");
                    Validate(new() { Items = [branch] });
                    if (!Dominates(item.Version.Clock, branch.Version.Clock))
                        throw new InvalidDataException("同步并发分支版本无效。");
                }
                if (item.Alternatives.Select(b => b.Version.Revision).Distinct().Count() != item.Alternatives.Count
                    || !item.Alternatives.Any(b => b.Version.Revision == item.Version.Revision && SameContent(item, b)))
                    throw new InvalidDataException("同步主版本与分支不一致。");
            }
            if (item.Version.Deleted)
            {
                if (item.Prompt is not null || item.Connection is not null || item.ApiKey.Length != 0)
                    throw new InvalidDataException("删除标记格式无效。");
                continue;
            }
            if (item.Id.StartsWith("p:", StringComparison.Ordinal))
            {
                var p = item.Prompt;
                if (item.Connection is not null || item.ApiKey.Length != 0 || p is null || p.Id != item.Id[2..]
                    || p.Title is null || p.Description is null || string.IsNullOrWhiteSpace(p.Content)
                    || p.Tags is null || p.Tags.Count > 128 || p.Tags.Any(t => t is null) || p.CopyCount != 0)
                    throw new InvalidDataException("同步提示词格式无效。");
            }
            else
            {
                var c = item.Connection;
                if (item.Prompt is not null || c is null || c.Id != item.Id[2..] || string.IsNullOrWhiteSpace(c.Platform)
                    || c.ModelType is null || !ModelPlatforms.Types.ContainsKey(c.ModelType) || c.Model is null
                    || c.Note is null || c.RemainingBalance is null || c.ModelIds is null || c.ModelIds.Any(m => m is null)
                    || c.ApiKeyProtected != "" || c.LastCopiedModelId != "" || c.AllowInsecureEndpoint)
                    throw new InvalidDataException("同步模型资料格式无效。");
                LibraryHttp.ValidateEndpoint(c.Endpoint, allowInsecure: true);
                LibraryHttp.ValidateKey(item.ApiKey);
            }
        }
    }

    private static bool Dominates(Dictionary<string, long> a, Dictionary<string, long> b) =>
        b.All(v => a.GetValueOrDefault(v.Key) >= v.Value);

    private static Dictionary<string, long> Join(Dictionary<string, long> a, Dictionary<string, long> b) =>
        a.Keys.Union(b.Keys).Order(StringComparer.Ordinal).ToDictionary(k => k, k => Math.Max(a.GetValueOrDefault(k), b.GetValueOrDefault(k)), StringComparer.Ordinal);

    // 以每个分支的原始向量判断因果关系。汇总向量只记录已观察历史，不能代替分支来源；
    // 否则 A1/B1/A2 按不同顺序到达会得到相同汇总向量但不同主版本，之后再也无法收敛。
    public static bool Merge(LibraryState state, LibrarySyncSnapshot incoming)
    {
        var changed = false;
        foreach (var remote in incoming.Items)
        {
            var versions = state.Sync!.Versions;
            var local = versions.TryGetValue(remote.Id, out var localVersion) ? ExportItem(state, remote.Id, localVersion) : null;
            var candidates = Heads(remote).Concat(local is null ? [] : Heads(local)).GroupBy(h => h.Version.Revision, StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    if (group.Any(h => !SameContent(first, h) || !EqualClock(first.Version.Clock, h.Version.Clock)))
                        throw new InvalidDataException("同一同步版本携带不同内容或因果记录，未合并。");
                    return Copy(first);
                }).ToList();
            var heads = candidates.Where(head => !candidates.Any(other => Dominates(other.Version.Clock, head.Version.Clock)
                && !Dominates(head.Version.Clock, other.Version.Clock))).OrderBy(h => h.Version.Revision, StringComparer.Ordinal).ToList();
            var winner = Copy(heads.OrderByDescending(h => h.Version.Deleted).ThenByDescending(h => h.Version.Revision, StringComparer.Ordinal).First());
            foreach (var loser in heads.Where(h => !h.Version.Deleted && h.Version.Revision != winner.Version.Revision && !SameContent(h, winner)))
            {
                var conflict = Copy(loser);
                var conflictId = "conflict-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote.Id + ":" + loser.Version.Revision))).ToLowerInvariant();
                conflict.Id = remote.Id[..2] + conflictId;
                if (conflict.Prompt is { } p) { p.Id = conflictId; p.Title += "（同步冲突副本）"; }
                if (conflict.Connection is { } c) { c.Id = conflictId; c.Note = "同步冲突副本；请核对后保留所需版本。\n" + c.Note; }
                if (!versions.ContainsKey(conflict.Id)) { Apply(state, conflict); changed = true; }
            }
            var joined = local is null ? remote.Version.Clock : Join(local.Version.Clock, remote.Version.Clock);
            var retainBranches = heads.Count > 1 || !EqualClock(winner.Version.Clock, joined);
            winner.Alternatives = retainBranches ? heads : [];
            winner.Version.Clock = joined;
            if (local is null || Canonical(local) != Canonical(winner))
            { Apply(state, winner); changed = true; }
        }
        return changed;
    }

    private static IEnumerable<LibrarySyncItem> Heads(LibrarySyncItem item) => item.Alternatives.Count == 0 ? [item] : item.Alternatives;
    private static bool EqualClock(Dictionary<string, long> a, Dictionary<string, long> b) => Dominates(a, b) && Dominates(b, a);
    private static string Canonical(LibrarySyncItem item)
    {
        var copy = Copy(item);
        copy.Version.Clock = copy.Version.Clock.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value);
        foreach (var head in copy.Alternatives)
            head.Version.Clock = head.Version.Clock.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value);
        return JsonSerializer.Serialize(copy);
    }

    private static bool SameContent(LibrarySyncItem a, LibrarySyncItem b)
    {
        if (a.Version.Deleted || b.Version.Deleted) return a.Version.Deleted == b.Version.Deleted;
        var first = Copy(a); var second = Copy(b);
        first.Version = new() { Revision = "" }; second.Version = new() { Revision = "" };
        first.Alternatives = []; second.Alternatives = [];
        if (first.Prompt is { } p) { p.CreatedAt = p.UpdatedAt = 0; second.Prompt!.CreatedAt = second.Prompt.UpdatedAt = 0; }
        if (first.Connection is { } c) { c.CreatedAt = c.UpdatedAt = 0; second.Connection!.CreatedAt = second.Connection.UpdatedAt = 0; }
        return JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    }

    private static void Apply(LibraryState state, LibrarySyncItem item)
    {
        var id = item.Id[2..];
        if (item.Id.StartsWith("p:", StringComparison.Ordinal))
        {
            var index = state.Prompts.FindIndex(p => p.Id == id);
            if (item.Version.Deleted) state.Prompts.RemoveAll(p => p.Id == id);
            else
            {
                var prompt = Copy(item.Prompt!);
                prompt.CopyCount = index >= 0 ? state.Prompts[index].CopyCount : 0;
                if (index < 0) state.Prompts.Insert(0, prompt); else state.Prompts[index] = prompt;
            }
        }
        else
        {
            var index = state.Connections.FindIndex(c => c.Id == id);
            if (item.Version.Deleted) state.Connections.RemoveAll(c => c.Id == id);
            else
            {
                var connection = Copy(item.Connection!);
                var old = index >= 0 ? state.Connections[index] : null;
                var sameEndpoint = old?.Endpoint == connection.Endpoint;
                var sameKey = old is not null && LocalSecretStore.Unprotect(old.ApiKeyProtected) == item.ApiKey;
                connection.ApiKeyProtected = sameKey ? old!.ApiKeyProtected : item.ApiKey.Length > 0 ? LocalSecretStore.Protect(item.ApiKey) : "";
                connection.AllowInsecureEndpoint = sameEndpoint && old!.AllowInsecureEndpoint;
                if (sameEndpoint && sameKey && old!.ModelType == connection.ModelType && old.Model == connection.Model)
                    connection.LastCopiedModelId = old.LastCopiedModelId;
                connection.Revision = Guid.NewGuid().ToString("N");
                if (index < 0) state.Connections.Add(connection); else state.Connections[index] = connection;
            }
        }
        state.Sync!.Versions[item.Id] = Copy(item.Version);
        state.Sync.Versions[item.Id].Branches = item.Alternatives.Select(StoredBranch).ToList();
    }
}

internal sealed partial class LibraryStore
{
    private LibraryState? _syncState;
    private (long Length, DateTime Modified) _syncFileStamp;
    private long _syncReadAt;
    private IReadOnlyDictionary<string, string>? _syncVersions;

    private static bool SamePromptContent(LibraryPrompt a, LibraryPrompt b) =>
        a.Title == b.Title && a.Content == b.Content && a.Description == b.Description && a.Tags.SequenceEqual(b.Tags);

    public LibrarySyncSnapshot ExportSync()
    {
        lock (_gate)
        {
            return LibrarySyncMerge.Export(LoadSyncState());
        }
    }

    private LibraryState LoadSyncState()
    {
        var current = Load();
        if (current.Sync is not null) return current;
        UpdateCore(s => { if (s.Sync is null) { s.Sync = new(); LibrarySyncMerge.Track(new LibraryState(), s); } }, false);
        return Load();
    }

    internal LibrarySyncSnapshot ExportSyncChanges(IReadOnlyDictionary<string, string>? knownVersions,
        out IReadOnlyDictionary<string, string> versions)
    {
        lock (_gate)
        {
            var file = new FileInfo(_path);
            var stamp = (file.Exists ? file.Length : -1, file.LastWriteTimeUtc);
            // 缓存仅保留本机加密状态；定期重读兜底发现保留了时间戳和长度的外部替换。
            if (_syncState is null || stamp != _syncFileStamp
                || System.Diagnostics.Stopwatch.GetElapsedTime(_syncReadAt) >= TimeSpan.FromSeconds(30))
            {
                _syncState = null;
                var current = LoadSyncState();
                _syncVersions = current.Sync!.Versions.ToDictionary(v => v.Key, v => LibrarySyncMerge.VersionStamp(v.Value), StringComparer.Ordinal);
                _syncState = current; _syncFileStamp = stamp;
                _syncReadAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            var currentVersions = _syncVersions!;
            versions = currentVersions;
            if (knownVersions is null) return LibrarySyncMerge.Export(_syncState);
            if (ReferenceEquals(knownVersions, currentVersions)) return new();
            var changed = currentVersions.Keys.Where(id => knownVersions.GetValueOrDefault(id) != currentVersions[id]).ToArray();
            return changed.Length == 0 ? new() : LibrarySyncMerge.Export(_syncState, changed);
        }
    }

    public bool MergeSync(LibrarySyncSnapshot snapshot)
    {
        LibrarySyncMerge.Validate(snapshot);
        var changed = false;
        UpdateCore(s =>
        {
            if (s.Sync is null) { s.Sync = new(); LibrarySyncMerge.Track(new LibraryState(), s); }
            changed = LibrarySyncMerge.Merge(s, snapshot);
            if (changed)
            {
                var merged = LibrarySyncMerge.Export(s);
                LibrarySyncMerge.Validate(merged);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(merged);
                try
                {
                    if (bytes.Length > LibrarySyncSnapshot.MaxBytes)
                        throw new InvalidDataException("合并后的资料超过单次同步 8 MB 上限，未写入本次同步。");
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }, false, () => changed);
        return changed;
    }
}
