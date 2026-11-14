using System.Collections.Concurrent;
using System.IO;
using System.Buffers.Binary;
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
    internal string ClipboardPeerKey => _session?.PeerKey ?? "";
    internal Exception? LastConnectionError { get; private set; }
    public bool IsConnected => _session is { } session && !session.Closed.IsCancellationRequested;
    public bool IsListening => _listener is not null;
    public int Port { get; private set; }
    public string PairCode { get; private set; } = "";
    public string PeerName { get; private set; } = "";
    public string PeerAddress => _session?.PeerAddress ?? "";
    public bool LibraryEnabled { get; set; }
    public Func<byte[], CancellationToken, Task>? ApplyLibraryAsync { get; set; }
    public bool ClipboardEnabled { get; set; } = true;
    public Func<LanPairRequest, CancellationToken, Task<bool>>? ConfirmPairAsync { get; set; }
    public Func<LanClipboardPayload, CancellationToken, Task>? ApplyClipboardAsync { get; set; }
    public event EventHandler<string>? Changed;
    public event EventHandler<string>? ClipboardNotice;
    public event EventHandler<LanProgress>? Progress;
    internal Func<string, Func<Task>, CancellationToken, Task<bool>>? ApplyClipboardMessageAsync { get; set; }
    internal Func<LanClipboardPayload, string>? ClipboardMessageId { get; set; }
    internal event Action<string>? ClipboardSending;
    internal event Action<LanClipboardMessage>? ClipboardReceived;
    internal SyncActivityStore? Activities { get; set; }
    internal string ActivityDeviceId { get; set; } = "";
    private void RecordActivity(SyncActivity activity) => Activities?.Update(activity with { DeviceId = ActivityDeviceId });
    public bool SupportsFileOffers => _session?.FileOffers == true;
    public bool SupportsReconnect => _session?.Reconnect == true;
    public Func<LanHeader, CancellationToken, Task<string?>>? ConfirmFilesAsync { get; set; }
    internal void ReportPreparationFailure(SyncKind kind, Exception exception)
    {
        if (Activities is not { } store) return;
        var last = store.Snapshot().FirstOrDefault(x => x.Kind == kind && !x.Incoming && x.DeviceId == ActivityDeviceId && x.Peer == PeerName);
        if (last is { State: SyncState.Failed } && DateTimeOffset.Now - last.Updated < TimeSpan.FromSeconds(2)) return;
        RecordActivity(new($"prepare-{ConnectionVersion}-{kind}", kind, false, PeerName, SyncState.Failed,
            Issue: SyncActivityStore.IssueFor(exception)));
    }

    public LanNode(string name, Func<string> receiveFolder)
    {
        _name = ValidateDeviceName(name);
        _receiveFolder = receiveFolder;
    }

    internal sealed record LocalAddress(IPAddress Address, NetworkInterfaceType InterfaceType, bool HasGateway, bool IsVirtual);

    public static IReadOnlyList<IPAddress> LocalAddresses() => OrderLocalAddresses(NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n =>
        {
            var properties = n.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any) && !IPAddress.IsLoopback(g.Address));
            var isVirtual = IsVirtualAdapter(n.Name + " " + n.Description);
            return properties.UnicastAddresses.Select(a => new LocalAddress(a.Address, n.NetworkInterfaceType, hasGateway, isVirtual));
        }));

    internal static bool IsVirtualAdapter(string name) => new[] { "virtual", "vethernet", "hyper-v", "wsl", "vmware", "docker", "vpn", "tunnel", "tap-", "tun-" }
        .Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<IPAddress> OrderLocalAddresses(IEnumerable<LocalAddress> addresses) => addresses
        .Where(a => IsLocalAddress(a.Address) && !IPAddress.IsLoopback(a.Address))
        // 保留虚拟网卡供手选，但默认优先真实局域网；169.254 自分配地址仅作最后备选。
        .OrderBy(a => a.Address.GetAddressBytes()[0] == 169 && a.Address.GetAddressBytes()[1] == 254)
        .ThenBy(a => a.IsVirtual)
        .ThenByDescending(a => a.HasGateway)
        .ThenByDescending(a => a.InterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .Select(a => a.Address).Distinct().ToList();

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

    internal static void ConfigureSocket(TcpClient client)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
    }

    internal sealed record ListenIdentity(byte[] Certificate, byte[] Secret);
    internal ListenIdentity ExportListenIdentity() => new(
        (_certificate ?? throw new InvalidOperationException()).Export(X509ContentType.Pfx),
        (_pairSecret ?? throw new InvalidOperationException()).ToArray());

    public async Task ListenAsync(IPAddress address, int port, ListenIdentity? identity = null)
    {
        if (!IsLocalAddress(address)) throw new InvalidDataException("请选择本机局域网 IPv4 地址。");
        await StartAsync(token => ListenCoreAsync(address, port, token, identity)).ConfigureAwait(false);
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

    private async Task ListenCoreAsync(IPAddress address, int port, CancellationToken token, ListenIdentity? identity)
    {
        if (identity is not null)
        {
            if (identity.Secret.Length != 8) throw new InvalidDataException("设备信任信息无效，请重新配对。");
            _certificate = new X509Certificate2(identity.Certificate, (string?)null, X509KeyStorageFlags.Exportable);
            _pairSecret = identity.Secret.ToArray();
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=ScreenTranslate LAN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            // Windows Schannel 不支持 CreateSelfSigned 返回的临时密钥句柄。
            // 使用系统默认导入方式，密钥容器随证书 Dispose 清理，不安装证书或保存 PFX 文件。
            var pfx = generated.Export(X509ContentType.Pfx);
            try { _certificate = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            _pairSecret = RandomNumberGenerator.GetBytes(8);
        }
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
                ConfigureSocket(client);
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
                await LanProtocol.WriteHeaderAsync(ssl, new("paired", Name: _name, Accepted: accepted, FileOffers: true, Reconnect: true, FileCancellation: true), timeout.Token).ConfigureAwait(false);
                if (!accepted) { ssl.Dispose(); client.Dispose(); client = null; await Task.Delay(500, token).ConfigureAwait(false); continue; }
                var session = new Session(client, ssl, hello.FileOffers, hello.Reconnect, hello.FileCancellation)
                { PeerKey = LanProtocol.Hash(Encoding.UTF8.GetBytes(PairCode.Replace("-", "") + "\0" + name)) };
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

    public async Task ConnectAsync(IPAddress address, int port, string pairCode, CancellationToken connectToken = default)
    {
        if (!IsLocalAddress(address) || port is < 1 or > 65535)
            throw new InvalidDataException("请输入局域网 IPv4 地址及有效端口。");
        var normalized = pairCode.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalized.Length != 32 || !normalized.All(Uri.IsHexDigit))
            throw new InvalidDataException("请输入对方显示的完整配对码。");
        await StartAsync(token => ConnectCoreAsync(address, port, normalized, token, connectToken)).ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync(IPAddress address, int port, string normalized, CancellationToken runToken, CancellationToken connectToken)
    {
        var client = new TcpClient();
        SslStream? ssl = null;
        try
        {
            ConfigureSocket(client);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken, connectToken);
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
            await LanProtocol.WriteHeaderAsync(ssl, new("hello", Name: _name, Hash: proof, FileOffers: true, Reconnect: true, FileCancellation: true), timeout.Token).ConfigureAwait(false);
            var response = await LanProtocol.ReadHeaderAsync(ssl, timeout.Token).ConfigureAwait(false);
            if (response.Kind != "paired" || !response.Accepted) throw new IOException("对方未接受配对。");
            var name = ValidateDeviceName(response.Name);
            runToken.ThrowIfCancellationRequested();
            var session = new Session(client, ssl, response.FileOffers, response.Reconnect, response.FileCancellation)
            { PeerKey = LanProtocol.Hash(Encoding.UTF8.GetBytes(normalized + "\0" + name)) };
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
            (LanHeader Header, string Folder)? offer = null;
            while (!token.IsCancellationRequested)
            {
                var header = await LanProtocol.ReadHeaderAsync(stream, token).ConfigureAwait(false);
                session.Touch();
                if (header.Kind == "file-cancel")
                {
                    if (!session.FileCancellation || channel != 0 || header.Length != 0 || header.Name.Length != 0
                        || header.Hash.Length != 0 || !Guid.TryParseExact(header.Id, "N", out _))
                        throw new InvalidDataException("文件取消请求无效。");
                    session.CancelConfirmation(header.Id);
                    continue;
                }
                if (header.Kind == "ack")
                {
                    if (session.Pending.TryRemove(header.Id, out var pending)) pending.TrySetResult(header.Accepted);
                    continue;
                }
                LanProtocol.ValidatePayload(header);
                var isFile = header.Kind is "file" or "bundle" or "file-offer";
                if ((isFile ? 1 : header.Kind == "library" ? 2 : 0) != channel) throw new InvalidDataException("传输通道与内容不匹配。");
                var activity = new SyncActivity(header.Id, isFile ? SyncKind.Files : header.Kind == "library" ? SyncKind.Library : SyncKind.Clipboard,
                    true, name, header.Kind == "file-offer" ? SyncState.Waiting : SyncState.Running,
                    isFile ? header.Name : "", Total: header.Length);
                RecordActivity(activity);
                try
                {
                    if (header.Kind == "file-offer")
                    {
                        if (!session.FileOffers || offer is not null) throw new InvalidDataException("文件请求无效。");
                        using var confirmation = session.BeginConfirmation(header.Id, token);
                        confirmation.CancelAfter(TimeSpan.FromMinutes(5));
                        string? folder = null;
                        try
                        {
                            if (ConfirmFilesAsync is { } confirm)
                                folder = await confirm(header, confirmation.Token).WaitAsync(confirmation.Token).ConfigureAwait(false);
                            if (folder is not null) folder = LanFiles.ValidateFolder(folder);
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested && !session.Closed.IsCancellationRequested)
                        { activity = activity with { Issue = session.ConfirmationCancelled(header.Id) ? SyncIssue.None : ex is OperationCanceledException ? SyncIssue.Timeout : SyncActivityStore.IssueFor(ex) }; }
                        finally { session.EndConfirmation(); }
                        if (session.ConfirmationCancelled(header.Id)) folder = null;
                        offer = folder is null ? null : (header, folder);
                        RecordActivity(activity with { State = folder is not null ? SyncState.Running : session.ConfirmationCancelled(header.Id) ? SyncState.Cancelled : SyncState.Rejected });
                        await session.WriteAsync(new("ack", header.Id, Accepted: folder is not null), null, null, token, session.FileCancellation ? 0 : 1).ConfigureAwait(false);
                        continue;
                    }
                    var applied = true;
                    using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token, session.Closed);
                    receiveTimeout.CancelAfter(isFile ? TimeSpan.FromHours(24) : TimeSpan.FromSeconds(30));
                    if (isFile)
                    {
                        Stream payload = session.FileCancellation ? new LanFileChunks(stream, header.Length) : stream;
                        if (header.Kind == "bundle" && (offer is null || offer.Value.Header.Id != header.Id
                            || offer.Value.Header.Name != header.Name || offer.Value.Header.Length != header.Length || offer.Value.Header.Hash != header.Hash))
                            throw new InvalidDataException("文件尚未获得接收确认。");
                        var folder = header.Kind == "bundle" ? offer!.Value.Folder : _receiveFolder();
                        offer = null;
                        if (header.Kind == "file" && ConfirmFilesAsync is not null)
                        {
                            // 旧版直接推送没有接收确认；消费完整帧后拒绝，避免绕过新版界面的逐批授权。
                            var buffer = new byte[65536];
                            long remaining = header.Length;
                            while (remaining > 0)
                            {
                                var count = await payload.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), receiveTimeout.Token)
                                    .AsTask().WaitAsync(TimeSpan.FromSeconds(30), receiveTimeout.Token).ConfigureAwait(false);
                                if (count == 0) throw new EndOfStreamException();
                                remaining -= count; session.Touch();
                            }
                            RecordActivity(activity with { State = SyncState.Rejected, Issue = SyncIssue.Unsupported });
                            await session.WriteAsync(new("ack", header.Id, Accepted: false), null, null, token, session.FileCancellation ? 0 : channel).ConfigureAwait(false);
                            continue;
                        }
                        var last = Environment.TickCount64;
                        var path = await LanFiles.ReceiveAsync(payload, header, folder, done =>
                        {
                            session.Touch();
                            if (Environment.TickCount64 - last < 150 && done != header.Length) return;
                            last = Environment.TickCount64;
                            RecordActivity(activity with { Completed = done });
                            Progress?.Invoke(this, new(header.Name, done, header.Length, "接收中"));
                        }, receiveTimeout.Token).ConfigureAwait(false);
                        if (header.Kind == "bundle")
                        {
                            RecordActivity(activity with { State = SyncState.Confirming, Completed = header.Length });
                            var archive = path;
                            try { path = await LanBundle.ExtractAsync(archive, folder, Path.GetFileNameWithoutExtension(header.Name), receiveTimeout.Token).ConfigureAwait(false); }
                            catch (Exception ex) when (!token.IsCancellationRequested) { applied = false; activity = activity with { Issue = SyncActivityStore.IssueFor(ex) }; }
                            finally { File.Delete(archive); }
                        }
                        activity = activity with { Folder = applied ? (header.Kind == "bundle" ? path : folder) : "" };
                        if (applied) Progress?.Invoke(this, new(Path.GetFileName(path), header.Length, header.Length, "已接收"));
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
                                if (!applied) activity = activity with { Issue = SyncIssue.Paused };
                                if (applied) await ApplyLibraryAsync!(bytes, receiveTimeout.Token).ConfigureAwait(false);
                            }
                            // 完整消息已消费，资料格式或落盘失败只拒绝本条，保留其他通道及重试能力。
                            catch (Exception ex) when (!receiveTimeout.IsCancellationRequested) { applied = false; activity = activity with { Issue = SyncActivityStore.IssueFor(ex) }; }
                            finally { CryptographicOperations.ZeroMemory(bytes); }
                        }
                        else if (ClipboardEnabled)
                        {
                            try
                            {
                                if (ApplyClipboardAsync is not { } apply) throw new InvalidOperationException("剪贴板接收尚未启用。");
                                var payload = new LanClipboardPayload(header.Kind, bytes, Origin: header.Origin.Length == 0 ? name : header.Origin);
                                var fresh = true;
                                if (ApplyClipboardMessageAsync is { } applyMessage)
                                    fresh = await applyMessage(header.Id, () => apply(payload, receiveTimeout.Token), receiveTimeout.Token).ConfigureAwait(false);
                                else await apply(payload, receiveTimeout.Token).ConfigureAwait(false);
                                if (fresh)
                                {
                                    ClipboardReceived?.Invoke(new(header.Id, payload));
                                    ClipboardNotice?.Invoke(this, $"已接收来自 {payload.Origin} 的{(header.Kind == "text" ? "文本" : "图片")}。");
                                }
                            }
                            // 消息已完整校验；本机剪贴板暂时不可写只拒绝本条，不破坏共享连接。
                            catch (Exception ex) when (!receiveTimeout.IsCancellationRequested)
                            {
                                applied = false; activity = activity with { Issue = SyncActivityStore.IssueFor(ex) };
                                ClipboardNotice?.Invoke(this, "本次剪贴板内容未写入，连接保持可用。");
                            }
                        }
                        else if (!ClipboardEnabled)
                        {
                            applied = false;
                            activity = activity with { Issue = SyncIssue.Paused };
                            ClipboardNotice?.Invoke(this, "自动同步已暂停，未修改本机剪贴板。");
                        }
                    }
                    RecordActivity(activity with { State = applied ? SyncState.Completed : activity.Issue is not (SyncIssue.None or SyncIssue.Paused) ? SyncState.Failed : SyncState.Rejected, Completed = header.Length });
                    await session.WriteAsync(new("ack", header.Id, Accepted: applied), null, null, token, session.FileCancellation && isFile ? 0 : channel).ConfigureAwait(false);
                }
                catch (LanFileCancelledException) when (session.FileCancellation && isFile)
                {
                    RecordActivity(activity with { State = SyncState.Cancelled });
                    await session.WriteAsync(new("ack", header.Id, Accepted: false), null, null, token, 0).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RecordActivity(activity with { State = token.IsCancellationRequested || session.Closed.IsCancellationRequested ? SyncState.Interrupted : SyncState.Failed,
                        Issue = SyncActivityStore.IssueFor(ex) });
                    throw;
                }
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
            Activities?.InterruptActive(name, ActivityDeviceId);
            session.Dispose();
            if (ReferenceEquals(_session, session)) { _session = null; PeerName = ""; }
            Changed?.Invoke(this, "设备已断开连接。");
        }
    }

    public async Task<bool> SendClipboardAsync(LanClipboardPayload payload, CancellationToken token = default, Func<bool>? canSend = null)
        => await SendClipboardAsync(payload, ClipboardMessageId?.Invoke(payload) ?? Guid.NewGuid().ToString("N"), token, canSend).ConfigureAwait(false);

    internal async Task<bool> SendClipboardAsync(LanClipboardPayload payload, string id, CancellationToken token = default, Func<bool>? canSend = null)
    {
        if (payload.Kind is not ("text" or "image") || payload.Bytes.Length > LanProtocol.MaxClipboard)
            throw new InvalidDataException("剪贴板内容类型或大小无效。");
        using var stream = new MemoryStream(payload.Bytes, false);
        var session = _session ?? throw new InvalidOperationException("请先配对另一台电脑。");
        ClipboardSending?.Invoke(id);
        return await SendAsync(session, payload.Kind, "", stream, LanProtocol.Hash(payload.Bytes), token,
            new(id, SyncKind.Clipboard, false, PeerName, SyncState.Running),
            payload.Origin.Length == 0 ? _name : ValidateDeviceName(payload.Origin), canSend).ConfigureAwait(false);
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

    public async Task<bool> SendBundleAsync(string[] paths, CancellationToken token = default)
    {
        var session = _session ?? throw new InvalidOperationException("请先配对另一台电脑。");
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token, session.Closed);
        var activity = new SyncActivity(Guid.NewGuid().ToString("N"), SyncKind.Files, false, PeerName,
            SyncState.Preparing, $"接收文件-{paths.Length}项-{DateTime.Now:yyyyMMdd-HHmmss}.zip", Sources: paths.ToArray());
        RecordActivity(activity);
        try
        {
            if (!session.FileOffers)
            {
                RecordActivity(activity with { State = SyncState.Failed, Issue = SyncIssue.Unsupported });
                return false;
            }
            using var bundle = await LanBundle.CreateAsync(paths, preparation.Token).ConfigureAwait(false);
            await using var file = new FileStream(bundle.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, preparation.Token).ConfigureAwait(false));
            file.Position = 0;
            return await SendAsync(session, "bundle", activity.Name, file, hash, token, activity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordActivity(activity with { State = token.IsCancellationRequested ? SyncState.Cancelled : SyncState.Failed, Issue = token.IsCancellationRequested ? SyncIssue.None : SyncActivityStore.IssueFor(ex) });
            throw;
        }
    }

    private async Task<bool> SendAsync(Session session, string kind, string name, Stream body, string hash, CancellationToken token,
        SyncActivity? existing = null, string origin = "", Func<bool>? canSend = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Closed, token);
        // 每个方向的每条逻辑通道仅允许一个待确认请求；文件和剪贴板分别排队。
        // 同通道确认仍走独立写锁，避免双向传输的确认排在后续请求之后。
        var isFile = kind is "file" or "bundle";
        var gate = isFile ? session.FileSendGate : kind == "library" ? session.LibrarySendGate : session.SendGate;
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        if (canSend is not null && !canSend())
        {
            gate.Release();
            throw new OperationCanceledException("复制内容或接收设备已变化，未发送排队内容。", token);
        }
        if (kind == "library" && !LibraryEnabled || (kind is "text" or "image") && !ClipboardEnabled)
        {
            gate.Release();
            throw new InvalidOperationException("资料库同步已暂停，未发送排队内容。");
        }
        var header = new LanHeader(kind, existing?.Id ?? Guid.NewGuid().ToString("N"), name, body.Length, hash, Origin: origin);
        var activity = existing ?? new SyncActivity(header.Id, isFile ? SyncKind.Files : kind == "library" ? SyncKind.Library : SyncKind.Clipboard,
            false, PeerName, SyncState.Running, isFile ? name : "");
        activity = activity with { Total = body.Length };
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[header.Id] = completed;
        var safeCancellation = false;
        try
        {
            if (kind == "bundle")
            {
                RecordActivity(activity with { State = SyncState.Waiting });
                await session.WriteAsync(header with { Kind = "file-offer" }, null, null, session.FileCancellation ? session.Closed : linked.Token, 1).ConfigureAwait(false);
                bool offerAccepted;
                try { offerAccepted = await completed.Task.WaitAsync(TimeSpan.FromMinutes(5), linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (session.FileCancellation && token.IsCancellationRequested && !session.Closed.IsCancellationRequested)
                {
                    await session.WriteAsync(new("file-cancel", header.Id), null, null, session.Closed).WaitAsync(TimeSpan.FromSeconds(30), session.Closed).ConfigureAwait(false);
                    offerAccepted = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), session.Closed).ConfigureAwait(false);
                }
                if (!offerAccepted)
                { safeCancellation = true; token.ThrowIfCancellationRequested(); RecordActivity(activity with { State = SyncState.Rejected }); return false; }
                completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Pending[header.Id] = completed;
            }
            RecordActivity(activity with { State = SyncState.Running });
            var last = Environment.TickCount64;
            void ReportProgress(long done)
            {
                if (!isFile || Environment.TickCount64 - last < 150 && done != body.Length) return;
                last = Environment.TickCount64;
                RecordActivity(activity with { State = SyncState.Running, Completed = done });
                Progress?.Invoke(this, new(name, done, body.Length, "发送中"));
            }
            var sentAll = true;
            if (isFile && session.FileCancellation)
                sentAll = await session.WriteFileAsync(header, body, ReportProgress, linked.Token).ConfigureAwait(false);
            else await session.WriteAsync(header, body, ReportProgress, linked.Token).ConfigureAwait(false);
            if (isFile && sentAll) RecordActivity(activity with { State = SyncState.Confirming, Completed = body.Length });
            // 剪贴板确认只等本条结果，文件或资料库的进展不能延长它的等待。
            var accepted = kind is "text" or "image"
                ? await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false)
                : await WaitForReceiptAsync(completed.Task, () => session.LastActivity,
                    isFile ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(60), isFile && session.FileCancellation ? session.Closed : linked.Token).ConfigureAwait(false);
            safeCancellation = true;
            if (!sentAll) throw new OperationCanceledException(token);
            RecordActivity(activity with { State = accepted ? SyncState.Completed : SyncState.Rejected, Completed = body.Length });
            if (accepted && kind == "file") Progress?.Invoke(this, new(name, body.Length, body.Length, "已送达"));
            return accepted;
        }
        catch (Exception ex)
        {
            RecordActivity(activity with { State = token.IsCancellationRequested ? SyncState.Cancelled : SyncState.Failed, Issue = token.IsCancellationRequested ? SyncIssue.None : SyncActivityStore.IssueFor(ex) });
            // 新版已确认取消边界才能保留会话；旧版或真实传输失败仍关闭，防止半帧污染。
            if (!(isFile && session.FileCancellation && safeCancellation && token.IsCancellationRequested)) session.Dispose();
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

    private sealed class Session(TcpClient client, SslStream stream, bool fileOffers, bool reconnect, bool fileCancellation) : IDisposable
    {
        public string PeerKey { get; init; } = "";
        public bool FileCancellation { get; } = fileCancellation;
        private readonly object _confirmationGate = new();
        private string _cancelledConfirmation = "";
        private string _confirmationId = "";
        private CancellationTokenSource? _confirmation;
        public CancellationTokenSource BeginConfirmation(string id, CancellationToken token)
        {
            lock (_confirmationGate)
            {
                _confirmationId = id;
                _confirmation = CancellationTokenSource.CreateLinkedTokenSource(token, Closed);
                if (_cancelledConfirmation == id) _confirmation.Cancel();
                return _confirmation;
            }
        }
        public void EndConfirmation() { lock (_confirmationGate) { _confirmation = null; _confirmationId = ""; } }
        public bool ConfirmationCancelled(string id) { lock (_confirmationGate) return _cancelledConfirmation == id; }
        public void CancelConfirmation(string id)
        {
            lock (_confirmationGate)
            {
                _cancelledConfirmation = id;
                if (_confirmationId == id) _confirmation?.Cancel();
            }
        }
        public bool Reconnect { get; } = reconnect;
        public string PeerAddress { get; } = client.Client.RemoteEndPoint?.ToString() ?? "";
        public bool FileOffers { get; } = fileOffers;
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

        public async Task<bool> WriteFileAsync(LanHeader header, Stream body, Action<long> progress, CancellationToken token)
        {
            await _fileWriteGate.WaitAsync(Closed).ConfigureAwait(false);
            try
            {
                await LanProtocol.WriteHeaderAsync(FileStream, header, Closed).ConfigureAwait(false);
                var buffer = new byte[LanFileChunks.MaxChunk + 4];
                long total = 0;
                while (total < header.Length)
                {
                    if (token.IsCancellationRequested)
                    {
                        BinaryPrimitives.WriteInt32BigEndian(buffer, -1);
                        await FileStream.WriteAsync(buffer.AsMemory(0, 4), Closed).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Closed).ConfigureAwait(false);
                        return false;
                    }
                    var count = await body.ReadAsync(buffer.AsMemory(4, (int)Math.Min(LanFileChunks.MaxChunk, header.Length - total)), Closed).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    BinaryPrimitives.WriteInt32BigEndian(buffer, count);
                    // 当前分块完整写出后再检查取消，断网仍按原有超时关闭连接。
                    await FileStream.WriteAsync(buffer.AsMemory(0, count + 4), Closed).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Closed).ConfigureAwait(false);
                    total += count; Touch(); progress(total);
                }
                return true;
            }
            finally { _fileWriteGate.Release(); }
        }

        public async Task WriteAsync(LanHeader header, Stream? body, Action<long>? progress, CancellationToken token, int channel = 0)
        {
            if (header.Kind is "file" or "bundle" or "file-offer") channel = 1;
            if (header.Kind == "library") channel = 2;
            var target = channel == 1 ? FileStream : channel == 2 ? LibraryStream : Stream;
            var gate = channel == 1 ? _fileWriteGate : channel == 2 ? _libraryWriteGate : _writeGate;
            await gate.WaitAsync(token).ConfigureAwait(false);
            using var idle = header.Kind is "text" or "image" ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
            var writeToken = idle?.Token ?? token;
            idle?.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await LanProtocol.WriteHeaderAsync(target, header, writeToken).ConfigureAwait(false);
                idle?.CancelAfter(TimeSpan.FromSeconds(3));
                Touch();
                if (body is not null)
                {
                    var buffer = new byte[idle is null ? 65536 : LanMultiplexStream.MaxChunk];
                    long total = 0;
                    while (true)
                    {
                        var count = await body.ReadAsync(buffer, writeToken).ConfigureAwait(false);
                        if (count == 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, count), writeToken).AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(30), writeToken).ConfigureAwait(false);
                        idle?.CancelAfter(TimeSpan.FromSeconds(3));
                        Touch(); total += count; progress?.Invoke(total);
                    }
                }
                await target.FlushAsync(writeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (idle?.IsCancellationRequested == true && !token.IsCancellationRequested)
            { throw new TimeoutException("剪贴板传输连续 3 秒没有读取进展。", ex); }
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
