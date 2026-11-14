namespace OverTranslate.Services.Lan;

internal sealed record LanClipboardMessage(string Id, LanClipboardPayload Payload);

/// <summary>消息编号跨设备转发保持不变；本机只应用一次，避免三角连接循环覆盖剪贴板。</summary>
internal sealed class LanClipboardRelay : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly Dictionary<LanNode, Action<LanClipboardMessage>> _nodes = [];
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _order = new();
    private readonly Dictionary<long, string> _localIds = [];
    private readonly Queue<long> _localOrder = new();
    private string MessageId(LanClipboardPayload payload)
    {
        if (payload.Sequence == 0) return Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            if (_localIds.TryGetValue(payload.Sequence, out var id)) return id;
            id = Guid.NewGuid().ToString("N");
            _localIds[payload.Sequence] = id; _localOrder.Enqueue(payload.Sequence);
            while (_localOrder.Count > 128) _localIds.Remove(_localOrder.Dequeue());
            return id;
        }
    }
    private bool Remember(string id)
    {
        lock (_gate)
        {
            if (!_seen.Add(id)) return false;
            _order.Enqueue(id);
            while (_order.Count > 512) _seen.Remove(_order.Dequeue());
            return true;
        }
    }
    private void Sent(string id) => Remember(id);
    private async Task<bool> ApplyAsync(string id, Func<Task> apply, CancellationToken token)
    {
        // 多路收到同一消息时，必须等首次写入成功才能确认重复消息。
        await _applyGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate) { if (_seen.Contains(id)) return false; }
            await apply().ConfigureAwait(false);
            Remember(id);
            return true;
        }
        finally { _applyGate.Release(); }
    }
    public void Add(LanNode node)
    {
        lock (_gate)
        {
            if (_nodes.ContainsKey(node)) return;
            Action<LanClipboardMessage> received = message => Forward(node, message);
            _nodes[node] = received;
            node.ApplyClipboardMessageAsync = ApplyAsync;
            node.ClipboardMessageId = MessageId;
            node.ClipboardSending += Sent;
            node.ClipboardReceived += received;
        }
    }
    private void Forward(LanNode source, LanClipboardMessage message)
    {
        LanNode[] targets;
        lock (_gate) targets = _nodes.Keys.Where(n => n != source && n.IsConnected && n.ClipboardEnabled).ToArray();
        foreach (var target in targets) _ = SendAsync(target, message);
    }
    private static async Task SendAsync(LanNode target, LanClipboardMessage message)
    {
        try { await target.SendClipboardAsync(message.Payload, message.Id).ConfigureAwait(false); }
        catch (Exception) { /* 传输节点记录失败；中继不重放旧剪贴板覆盖用户的新复制。 */ }
    }
    public void Remove(LanNode node)
    {
        lock (_gate)
        {
            if (!_nodes.Remove(node, out var received)) return;
            node.ClipboardReceived -= received; node.ClipboardSending -= Sent; node.ApplyClipboardMessageAsync = null; node.ClipboardMessageId = null;
        }
    }
    public void Dispose() { lock (_gate) { foreach (var node in _nodes.Keys.ToArray()) Remove(node); } }
}
