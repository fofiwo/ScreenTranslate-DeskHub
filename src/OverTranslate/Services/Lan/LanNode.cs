using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OverTranslate.Services.Lan;

internal sealed class LanNode : IAsyncDisposable
{
    private readonly string _name;
    private readonly Func<string> _receiveFolder;
    private CancellationTokenSource _run = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _clientTask;
    private Session? _session;
    private X509Certificate2? _certificate;
    private byte[]? _pairSecret;
    private long _connectionVersion;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _operationVersion;
    private int _disposed;
    private bool _fullyDisposed;
    internal long ConnectionVersion => Interlocked.Read(ref _connectionVersion);
    internal Exception? LastConnectionError { get; private set; }
    public bool IsConnected => _session is not null;
    public bool IsListening => _listener is not null;
    public int Port { get; private set; }
    public string PairCode { get; private set; } = "";
    public string PeerName { get; private set; } = "";
    public bool LibraryEnabled { get; set; }
    public Func<byte[], CancellationToken, Task>? ApplyLibraryAsync { get; set; }
    public bool ClipboardEnabled { get; set; } = true;
    public Func<LanPairRequest, CancellationToken, Task<bool>>? ConfirmPairAsync { get; set; }
    public Func<LanClipboardPayload, CancellationToken, Task>? ApplyClipboardAsync { get; set; }
    public event EventHandler<string>? Changed;
    public event EventHandler<string>? ClipboardNotice;
    public event EventHandler<LanProgress>? Progress;

    public LanNode(string name, Func<string> receiveFolder)
    {
        _name = ValidateDeviceName(name);
        _receiveFolder = receiveFolder;
    }

    public static IReadOnlyList<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => IsLocalAddress(a) && !IPAddress.IsLoopback(a)).Distinct().ToList();

    internal static bool IsLocalAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168)
            || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 169 && b[1] == 254);
    }

    private static string ValidateDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.Any(char.IsControl))
            throw new InvalidDataException("设备名称无效。");
        return value;
    }

    public async Task ListenAsync(IPAddress address, int port)
    {
        if (!IsLocalAddress(address)) throw new InvalidDataException("请选择本机局域网 IPv4 地址。");
        await StartAsync(token => ListenCoreAsync(address, port, token)).ConfigureAwait(false);
    }

    private async Task StartAsync(Func<CancellationToken, Task> start)
    {
        var operation = Interlocked.Increment(ref _operationVersion);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (operation != Volatile.Read(ref _operationVersion)) throw new OperationCanceledException();
            await StopCoreAsync().ConfigureAwait(false);
            if (operation != Volatile.Read(ref _operationVersion)) throw new OperationCanceledException();
            await start(_run.Token).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task ListenCoreAsync(IPAddress address, int port, CancellationToken token)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ScreenTranslate LAN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        // Windows Schannel 不支持 CreateSelfSigned 返回的临时密钥句柄。
        // 使用系统默认导入方式，密钥容器随证书 Dispose 清理，不安装证书或保存 PFX 文件。
        var pfx = generated.Export(X509ContentType.Pfx);
        try { _certificate = new X509Certificate2(pfx); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        _pairSecret = RandomNumberGenerator.GetBytes(8);
        PairCode = Convert.ToHexString(_certificate.GetCertHash(HashAlgorithmName.SHA256).AsSpan(0, 8))
            + "-" + Convert.ToHexString(_pairSecret);
        try
        {
            token.ThrowIfCancellationRequested();
            _listener = new TcpListener(address, port);
            _listener.Start(4);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(_listener, _certificate, token);
            Changed?.Invoke(this, "正在等待另一台 Windows 电脑配对。");
        }
        catch { await StopCoreAsync().ConfigureAwait(false); throw; }
    }

    private async Task AcceptLoopAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token).ConfigureAwait(false);
                var challenge = Guid.NewGuid().ToString("N");
                await LanProtocol.WriteHeaderAsync(ssl, new("challenge", Id: challenge), timeout.Token).ConfigureAwait(false);
                var hello = await LanProtocol.ReadHeaderAsync(ssl, timeout.Token).ConfigureAwait(false);
                if (hello.Kind != "hello" || hello.Length != 0) throw new InvalidDataException("配对请求无效。");
                if (_pairSecret is null || hello.Hash.Length != 64 || !hello.Hash.All(Uri.IsHexDigit)
                    || !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_pairSecret, Encoding.UTF8.GetBytes(challenge)),
                        Convert.FromHexString(hello.Hash)))
                    throw new AuthenticationException("配对凭据无效。");
                var name = ValidateDeviceName(hello.Name);
                var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (!IsLocalAddress(remote)) throw new InvalidDataException("仅接受局域网设备。");
                var accepted = ConfirmPairAsync is { } confirm
                    && await confirm(new(name, remote.ToString()), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                await LanProtocol.WriteHeaderAsync(ssl, new("paired", Name: _name, Accepted: accepted), timeout.Token).ConfigureAwait(false);
                if (!accepted) { ssl.Dispose(); client.Dispose(); client = null; await Task.Delay(500, token).ConfigureAwait(false); continue; }
                var session = new Session(client, ssl);
                client = null;
                await RunSessionAsync(session, name, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastConnectionError = ex;
                if (!token.IsCancellationRequested)
                {
                    Changed?.Invoke(this, "配对或连接已中断，仍在等待连接。请核对配对码后重试。");
                    try { await Task.Delay(500, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }
            finally { client?.Dispose(); }
        }
    }

    public async Task ConnectAsync(IPAddress address, int port, string pairCode)
    {
        if (!IsLocalAddress(address) || port is < 1 or > 65535)
            throw new InvalidDataException("请输入局域网 IPv4 地址及有效端口。");
        var normalized = pairCode.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalized.Length != 32 || !normalized.All(Uri.IsHexDigit))
            throw new InvalidDataException("请输入对方显示的完整配对码。");
        await StartAsync(token => ConnectCoreAsync(address, port, normalized, token)).ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync(IPAddress address, int port, string normalized, CancellationToken runToken)
    {
        var client = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            var expected = Convert.FromHexString(normalized[..16]);
            ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    certificate.GetCertHash(HashAlgorithmName.SHA256).AsSpan(0, 8), expected));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "ScreenTranslate LAN",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token).ConfigureAwait(false);
            var challenge = await LanProtocol.ReadHeaderAsync(ssl, timeout.Token).ConfigureAwait(false);
            if (challenge.Kind != "challenge" || !Guid.TryParseExact(challenge.Id, "N", out _))
                throw new InvalidDataException("配对挑战无效。");
            var secret = Convert.FromHexString(normalized[16..]);
            string proof;
            try { proof = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(challenge.Id))); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            await LanProtocol.WriteHeaderAsync(ssl, new("hello", Name: _name, Hash: proof), timeout.Token).ConfigureAwait(false);
            var response = await LanProtocol.ReadHeaderAsync(ssl, timeout.Token).ConfigureAwait(false);
            if (response.Kind != "paired" || !response.Accepted) throw new IOException("对方未接受配对。");
            var name = ValidateDeviceName(response.Name);
            runToken.ThrowIfCancellationRequested();
            var session = new Session(client, ssl);
            _clientTask = RunSessionAsync(session, name, runToken);
        }
        catch { ssl?.Dispose(); client.Dispose(); throw; }
    }

    private async Task RunSessionAsync(Session session, string name, CancellationToken token)
    {
        _session = session;
        Interlocked.Increment(ref _connectionVersion);
        PeerName = name;
        Changed?.Invoke(this, $"已连接 {name}。");
        async Task ReceiveAsync(Stream stream, int channel)
        {
            while (!token.IsCancellationRequested)
            {
                var header = await LanProtocol.ReadHeaderAsync(stream, token).ConfigureAwait(false);
                session.Touch();
                if (header.Kind == "ack")
                {
                    if (session.Pending.TryRemove(header.Id, out var pending)) pending.TrySetResult(header.Accepted);
                    continue;
                }
                LanProtocol.ValidatePayload(header);
                if ((header.Kind == "file" ? 1 : header.Kind == "library" ? 2 : 0) != channel) throw new InvalidDataException("传输通道与内容不匹配。");
                var applied = true;
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                receiveTimeout.CancelAfter(header.Kind == "file" ? TimeSpan.FromHours(24) : TimeSpan.FromSeconds(30));
                if (header.Kind == "file")
                {
                    var last = Environment.TickCount64;
                    var path = await LanFiles.ReceiveAsync(stream, header, _receiveFolder(), done =>
                    {
                        session.Touch();
                        if (Environment.TickCount64 - last < 150 && done != header.Length) return;
                        last = Environment.TickCount64;
                        Progress?.Invoke(this, new(header.Name, done, header.Length, "接收中"));
                    }, receiveTimeout.Token).ConfigureAwait(false);
                    Progress?.Invoke(this, new(Path.GetFileName(path), header.Length, header.Length, "已接收"));
                }
                else
                {
                    var bytes = new byte[(int)header.Length];
                    await stream.ReadExactlyAsync(bytes, receiveTimeout.Token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(header.Hash)))
                        throw new InvalidDataException("剪贴板内容校验失败。");
                    if (header.Kind == "library")
                    {
                        try
                        {
                            applied = LibraryEnabled && ApplyLibraryAsync is not null;
                            if (applied) await ApplyLibraryAsync!(bytes, receiveTimeout.Token).ConfigureAwait(false);
                        }
                        // 完整消息已消费，资料格式或落盘失败只拒绝本条，保留其他通道及重试能力。
                        catch (Exception) when (!receiveTimeout.IsCancellationRequested) { applied = false; }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    else if (ClipboardEnabled)
                    {
                        if (ApplyClipboardAsync is not { } apply) throw new InvalidOperationException("剪贴板接收尚未启用。");
                        await apply(new(header.Kind, bytes), receiveTimeout.Token).ConfigureAwait(false);
                        ClipboardNotice?.Invoke(this, header.Kind == "text" ? "已同步对方新复制的文本。" : "已同步对方新复制的图片。");
                    }
                    else
                    {
                        applied = false;
                        ClipboardNotice?.Invoke(this, "自动同步已暂停，未修改本机剪贴板。");
                    }
                }
                await session.WriteAsync(new("ack", header.Id, Accepted: applied), null, null, token, channel).ConfigureAwait(false);
            }
        }
        var clipboard = ReceiveAsync(session.Stream, 0);
        var files = ReceiveAsync(session.FileStream, 1);
        var library = ReceiveAsync(session.LibraryStream, 2);
        try
        {
            await Task.WhenAny(clipboard, files, library).ConfigureAwait(false);
            session.Dispose();
            await Task.WhenAll(clipboard, files, library).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) Changed?.Invoke(this, "连接已中断；未完成的传输已取消，请重新配对。");
        }
        finally
        {
            session.Dispose();
            if (ReferenceEquals(_session, session)) { _session = null; PeerName = ""; }
            Changed?.Invoke(this, "设备已断开连接。");
        }
    }

    public async Task<bool> SendClipboardAsync(LanClipboardPayload payload, CancellationToken token = default)
    {
        if (payload.Kind is not ("text" or "image") || payload.Bytes.Length > LanProtocol.MaxClipboard)
            throw new InvalidDataException("剪贴板内容类型或大小无效。");
        using var stream = new MemoryStream(payload.Bytes, false);
        var session = _session ?? throw new InvalidOperationException("请先配对另一台电脑。");
        return await SendAsync(session, payload.Kind, "", stream, LanProtocol.Hash(payload.Bytes), token).ConfigureAwait(false);
    }

    internal async Task<bool> SendLibraryAsync(byte[] bytes, CancellationToken token = default)
    {
        if (!LibraryEnabled) throw new InvalidOperationException("资料库同步尚未开启。");
        if (bytes.Length > OverTranslate.Services.Library.LibrarySyncSnapshot.MaxBytes) throw new InvalidDataException("资料库超过同步大小上限。");
        var session = _session ?? throw new InvalidOperationException("请先配对另一台电脑。");
        using var body = new MemoryStream(bytes, false);
        return await SendAsync(session, "library", "", body, LanProtocol.Hash(bytes), token).ConfigureAwait(false);
    }

    public async Task SendFileAsync(string path, CancellationToken token = default)
    {
        // 将哈希准备与发送绑定到同一次配对，断线重连后不能把旧批次发给新设备。
        var session = _session ?? throw new InvalidOperationException("请先配对另一台电脑。");
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token, session.Closed);
        var name = Path.GetFileName(path);
        LanFiles.ValidateName(name);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > LanProtocol.MaxFile) throw new InvalidDataException("文件超过 64 GB 上限。");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, preparation.Token).ConfigureAwait(false));
        file.Position = 0;
        if (!await SendAsync(session, "file", name, file, hash, token).ConfigureAwait(false))
            throw new IOException("对方未能保存文件。");
    }

    private async Task<bool> SendAsync(Session session, string kind, string name, Stream body, string hash, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Closed, token);
        // 每个方向的每条逻辑通道仅允许一个待确认请求；文件和剪贴板分别排队。
        // 同通道确认仍走独立写锁，避免双向传输的确认排在后续请求之后。
        var gate = kind == "file" ? session.FileSendGate : kind == "library" ? session.LibrarySendGate : session.SendGate;
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        if (kind == "library" && !LibraryEnabled)
        {
            gate.Release();
            throw new InvalidOperationException("资料库同步已暂停，未发送排队内容。");
        }
        var header = new LanHeader(kind, Guid.NewGuid().ToString("N"), name, body.Length, hash);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[header.Id] = completed;
        try
        {
            var last = Environment.TickCount64;
            await session.WriteAsync(header, body, done =>
            {
                if (kind != "file" || Environment.TickCount64 - last < 150 && done != body.Length) return;
                last = Environment.TickCount64;
                Progress?.Invoke(this, new(name, done, body.Length, "发送中"));
            }, linked.Token).ConfigureAwait(false);
            var accepted = await WaitForReceiptAsync(completed.Task, () => session.LastActivity,
                TimeSpan.FromSeconds(60), linked.Token).ConfigureAwait(false);
            if (accepted && kind == "file") Progress?.Invoke(this, new(name, body.Length, body.Length, "已送达"));
            return accepted;
        }
        catch
        {
            // 帧中途取消后无法继续复用同一个字节流，关闭连接防止下一条消息被错当作文件内容。
            session.Dispose();
            throw;
        }
        finally { session.Pending.TryRemove(header.Id, out _); gate.Release(); }
    }

    internal static async Task<bool> WaitForReceiptAsync(Task<bool> receipt, Func<long> lastActivity,
        TimeSpan idleTimeout, CancellationToken token)
    {
        while (true)
        {
            try { return await receipt.WaitAsync(idleTimeout, token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // 超时计时器与确认可能同时完成；优先消费已经到达的真实结果。
                if (receipt.IsCompleted) return await receipt.ConfigureAwait(false);
                if (Environment.TickCount64 - lastActivity() >= idleTimeout.TotalMilliseconds) throw;
                // 对方的确认可能排在它正在发送的大文件后面；有传输进展就继续等。
            }
        }
    }

    public Task StopAsync() => StopAsync(false);

    private async Task StopAsync(bool disposing)
    {
        Interlocked.Increment(ref _operationVersion);
        // 先取消正在握手的操作，再等待生命周期锁，避免停止被 60 秒配对等待挡住。
        try { _run.Cancel(); } catch (ObjectDisposedException) { }
        _session?.Dispose();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_fullyDisposed) return;
            await StopCoreAsync().ConfigureAwait(false);
            if (disposing) { _run.Dispose(); _fullyDisposed = true; }
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var old = _run;
        old.Cancel();
        _listener?.Stop(); _listener = null;
        _session?.Dispose();
        foreach (var task in new[] { _acceptTask, _clientTask })
            if (task is not null) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
        _acceptTask = _clientTask = null;
        _session = null;
        _certificate?.Dispose(); _certificate = null;
        if (_pairSecret is not null) CryptographicOperations.ZeroMemory(_pairSecret);
        _pairSecret = null;
        PairCode = ""; PeerName = ""; Port = 0;
        _run = new();
        old.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(true).ConfigureAwait(false);
    }

    private sealed class Session(TcpClient client, SslStream stream) : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly SemaphoreSlim _fileWriteGate = new(1, 1);
        private readonly SemaphoreSlim _libraryWriteGate = new(1, 1);
        private readonly LanMultiplexStream _multiplex = new(stream);
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public SemaphoreSlim FileSendGate { get; } = new(1, 1);
        public SemaphoreSlim LibrarySendGate { get; } = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        public CancellationToken Closed => _closed.Token;
        private long _lastActivity = Environment.TickCount64;
        public long LastActivity => Interlocked.Read(ref _lastActivity);
        public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        private int _disposed;
        public Stream Stream => _multiplex.Clipboard;
        public Stream FileStream => _multiplex.Files;
        public Stream LibraryStream => _multiplex.Library;
        public ConcurrentDictionary<string, TaskCompletionSource<bool>> Pending { get; } = new();

        public async Task WriteAsync(LanHeader header, Stream? body, Action<long>? progress, CancellationToken token, int channel = 0)
        {
            if (header.Kind == "file") channel = 1;
            if (header.Kind == "library") channel = 2;
            var target = channel == 1 ? FileStream : channel == 2 ? LibraryStream : Stream;
            var gate = channel == 1 ? _fileWriteGate : channel == 2 ? _libraryWriteGate : _writeGate;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await LanProtocol.WriteHeaderAsync(target, header, token).ConfigureAwait(false);
                Touch();
                if (body is not null)
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    while (true)
                    {
                        var count = await body.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (count == 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, count), token).AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                        Touch(); total += count; progress?.Invoke(total);
                    }
                }
                await target.FlushAsync(token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _closed.Cancel(); _multiplex.Dispose();
            client.Dispose(); stream.Dispose();
            foreach (var pending in Pending.Values) pending.TrySetException(new IOException("连接已断开。"));
            Pending.Clear();
        }
    }
}
