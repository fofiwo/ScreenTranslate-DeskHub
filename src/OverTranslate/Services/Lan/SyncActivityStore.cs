using System.IO;
using System.Text.Json;

namespace OverTranslate.Services.Lan;

internal enum SyncKind { Clipboard, Library, Files, Connection }
internal enum SyncState { Preparing, Waiting, Running, Completed, Rejected, Failed, Cancelled, Interrupted, Confirming }
internal enum SyncIssue { None, Permission, FileOrDisk, InvalidContent, Timeout, Connection, Paused, Unsupported }
internal sealed record SyncActivity(string Id, SyncKind Kind, bool Incoming, string Peer, SyncState State,
    string Name = "", long Completed = 0, long Total = 0, string Folder = "", string[]? Sources = null,
    DateTimeOffset Updated = default, SyncIssue Issue = SyncIssue.None, string DeviceId = "")
{
    public bool Active => State is SyncState.Preparing or SyncState.Waiting or SyncState.Running or SyncState.Confirming;
    public string KindLabel => Kind switch { SyncKind.Files => "文件", SyncKind.Library => "资料库", SyncKind.Connection => "设备连接", _ => "剪贴板" };
    public string StateLabel => Kind == SyncKind.Connection ? State switch
    {
        SyncState.Completed => "已连接", SyncState.Waiting => "正在自动重连", SyncState.Cancelled => "已断开 · 自动重连已暂停",
        SyncState.Failed => "连接失败 · 将自动重试", _ => "设备未连接"
    } : State switch
    {
        SyncState.Preparing => "正在准备", SyncState.Waiting => Incoming ? "等待你确认接收及保存位置" : "等待对方接收（最多 5 分钟）",
        SyncState.Running => "传输中", SyncState.Completed => "已完成",
        SyncState.Confirming => Incoming ? "正在校验并保存" : "等待对方校验和保存",
        SyncState.Rejected => Incoming ? "未被接收 · 本机拒绝或未开启此项" : "未被接收 · 对方拒绝、暂停或保存失败",
        SyncState.Cancelled => "已取消", SyncState.Interrupted => "已中断 · 请重新配对后重试",
        _ => "失败 · 请检查连接、权限、磁盘及内容格式后重试"
    };
    public string IssueLabel => Issue switch
    {
        SyncIssue.Permission => "目录或文件没有访问权限", SyncIssue.FileOrDisk => "文件不可读写或磁盘空间不足",
        SyncIssue.InvalidContent => "内容格式、名称、完整性或大小不符合传输要求", SyncIssue.Timeout => "等待接收或传输超时",
        SyncIssue.Connection => "设备连接中断", SyncIssue.Paused => "本机未开启对应同步",
        SyncIssue.Unsupported => "对方未使用接收确认，请将双方更新后重新配对", _ => ""
    };
    public string Display => $"{Updated.LocalDateTime:MM-dd HH:mm:ss}  {KindLabel} · {(Incoming ? "接收自" : "发送至")} {Peer}\n{StateLabel}"
        + (Issue == SyncIssue.None ? "" : $"\n原因：{IssueLabel}")
        + (Name.Length == 0 ? "" : $" · {Name}")
        + (Total > 0 ? $" · {Completed:N0} / {Total:N0} 字节" : "")
        + (Folder.Length == 0 ? "" : $"\n保存位置：{Folder}");
}

/// <summary>仅保存任务元数据，绝不接受剪贴板正文、资料库快照或原始异常消息。</summary>
internal sealed class SyncActivityStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<SyncActivity> _items = [];
    private static readonly Lazy<SyncActivityStore> Shared = new(() => new(Path.Combine(AppDataPaths.Root, "sync-activity.json")));
    public static SyncActivityStore Default => Shared.Value;
    public event EventHandler? Changed;
    public string StorageWarning { get; private set; } = "";

    public SyncActivityStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException();
                _items.AddRange((JsonSerializer.Deserialize<SyncActivity[]>(File.ReadAllText(path)) ?? [])
                    .Take(100).Select(x => x.Active ? x with { State = SyncState.Interrupted } : x));
                Save();
            }
        }
        catch (Exception) { StorageWarning = "同步记录读取失败，本次记录仅保存在当前运行中。"; }
    }
    public IReadOnlyList<SyncActivity> Snapshot() { lock (_gate) return _items.ToArray(); }
    public static SyncIssue IssueFor(Exception exception) => exception switch
    {
        UnauthorizedAccessException => SyncIssue.Permission, InvalidDataException => SyncIssue.InvalidContent,
        TimeoutException => SyncIssue.Timeout, EndOfStreamException => SyncIssue.Connection,
        IOException => SyncIssue.FileOrDisk, _ => SyncIssue.Connection
    };
    public void Update(SyncActivity activity)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(x => x.Id == activity.Id && x.Incoming == activity.Incoming && x.DeviceId == activity.DeviceId);
            var old = index < 0 ? null : _items[index];
            if (old is { Active: false } && activity.Active) return;
            if (index >= 0) _items.RemoveAt(index);
            _items.Insert(0, activity with { Updated = DateTimeOffset.Now });
            if (_items.Count > 100) _items.RemoveRange(100, _items.Count - 100);
            // 进度仅更新内存；状态变化才写盘，避免逐块写文件。
            if (old is null || old.State != activity.State) Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void InterruptActive(string peer, string deviceId = "")
    {
        foreach (var item in Snapshot().Where(x => x.Active && (deviceId.Length > 0 ? x.DeviceId == deviceId : x.Peer == peer)))
            Update(item with { State = SyncState.Interrupted });
    }
    private void Save()
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_items));
            File.Move(temporary, _path, true);
            StorageWarning = "";
        }
        catch (Exception) { StorageWarning = "同步记录保存失败，重启后可能丢失；请检查数据目录权限或磁盘空间。"; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { } }
    }
}
