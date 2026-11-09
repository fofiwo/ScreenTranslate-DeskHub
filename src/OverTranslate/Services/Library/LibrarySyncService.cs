using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace OverTranslate.Services.Library;

internal sealed class LibrarySyncOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 48539;
    public List<string> Peers { get; set; } = [];
    public string CodeProtected { get; set; } = "";

    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "library", "sync.json");
    public static LibrarySyncOptions Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<LibrarySyncOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException("同步设置为空。") : new();

    public static IPEndPoint ParsePeer(string text)
    {
        if (!IPEndPoint.TryParse(text.Trim(), out var endpoint) || endpoint.Port is < 1 or > 65535
            || endpoint.Address.AddressFamily != AddressFamily.InterNetwork || !LibrarySyncTransport.IsLan(endpoint.Address))
            throw new InvalidOperationException("对方地址须为局域网 IPv4:端口，例如 192.168.1.20:48539。");
        return endpoint;
    }

    public void Validate()
    {
        if (Port is < 1024 or > 65535 || Peers is null || Peers.Count > 16 || CodeProtected is null)
            throw new InvalidOperationException("端口须为 1024–65535，配对地址最多 16 个。");
        foreach (var peer in Peers) ParsePeer(peer);
        if (Enabled) CryptographicOperations.ZeroMemory(LibrarySyncTransport.ParseCode(LocalSecretStore.Unprotect(CodeProtected)));
    }

    public void Save(string path)
    {
        Validate(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, this); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class LibrarySyncService : IAsyncDisposable
{
    private readonly LibraryStore _store;
    private readonly LibrarySyncOptions _options;
    private readonly byte[] _key;
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task _server = Task.CompletedTask, _client = Task.CompletedTask;
    private string _lastReceived = "";
    public event Action? Changed;
    public event Action<string>? StatusChanged;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public LibrarySyncService(LibraryStore store, LibrarySyncOptions options, byte[] key)
    { _store = store; _options = options; _key = key.ToArray(); _listener = new(IPAddress.Any, options.Port); }

    public void Start()
    {
        Listen(); Run();
    }

    internal void Listen() => _listener.Start(8);
    internal void Run()
    {
        _server = Task.Run(ServeLoop); _client = Task.Run(SyncLoop);
    }

    public void SyncNow()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // 串行处理并限制 backlog、帧长和超时，避免未配对连接无限消耗工作线程。
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (await LibrarySyncTransport.ServeAsync(_store, client, _key, _stop.Token)) Changed?.Invoke();
                _lastReceived = $"已同步 · {DateTime.Now:HH:mm:ss} · 等待后续变更。";
                await Task.Delay(100, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception)
            {
                if (_stop.IsCancellationRequested) break;
                StatusChanged?.Invoke("收到的同步未完成：请核对配对码、软件版本和资料文件；已有资料保留。");
                try { await Task.Delay(500, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SyncLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var failed = 0;
            await Task.WhenAll(_options.Peers.Select(async peer =>
            {
                if (_stop.IsCancellationRequested) return;
                try
                {
                    if (await LibrarySyncTransport.ExchangeAsync(_store, LibrarySyncOptions.ParsePeer(peer), _key, _stop.Token)) Changed?.Invoke();
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception) { Interlocked.Increment(ref failed); }
            }));
            if (!_stop.IsCancellationRequested)
            {
                StatusChanged?.Invoke(_options.Peers.Count == 0 ? (_lastReceived.Length > 0 ? _lastReceived : "已开启，等待已配对设备连接。")
                    : failed > 0 ? $"{failed} 台设备暂未同步。请检查对方在线、配对码、IP、端口、防火墙及资料大小／格式；恢复后自动补同步。"
                    : $"已同步 · {DateTime.Now:HH:mm:ss} · {_options.Peers.Count} 个配对地址");
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(3), _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        try { await Task.WhenAll(_server, _client).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(_key); _stop.Dispose(); _wake.Dispose(); }
    }
}

internal static class LibrarySyncRuntime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static LibrarySyncService? _service;
    private static LibrarySyncOptions? _activeOptions;
    private static LibraryStore? _activeStore;
    public static string Status { get; private set; } = "局域网同步未开启。";
    public static event Action? Changed;
    internal static void NotifyUnifiedChange() => Changed?.Invoke();
    public static event Action? StatusChanged;

    public static async Task StartSavedAsync()
    {
        try { await ConfigureAsync(LibrarySyncOptions.Load(LibrarySyncOptions.DefaultPath), false).ConfigureAwait(false); }
        catch (Exception) { SetStatus("同步未启动，请打开局域网同步检查设置、端口或文件权限。"); }
    }

    private static void SetStatus(string status) { Status = status; StatusChanged?.Invoke(); }

    public static async Task ConfigureAsync(LibrarySyncOptions options, bool save = true, LibraryStore? store = null, string? optionsPath = null)
    {
        options.Validate();
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            options = LibrarySyncMerge.Copy(options);
            optionsPath ??= LibrarySyncOptions.DefaultPath;
            var previous = _service;
            if (!options.Enabled)
            {
                if (save) options.Save(optionsPath);
                if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
                _service = null; _activeOptions = null; _activeStore = null;
                SetStatus("局域网同步已关闭。"); return;
            }
            var key = LibrarySyncTransport.ParseCode(LocalSecretStore.Unprotect(options.CodeProtected));
            LibrarySyncService service;
            try { store ??= new LibraryStore(LibraryStore.DefaultPath); service = new LibrarySyncService(store, options, key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            var stoppedPrevious = false;
            try
            {
                // 同端口必须先停旧监听；新端口先绑定，避免输入被占用端口时中断原本正常的同步。
                if (previous is not null && previous.Port == options.Port)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                    _service = null; stoppedPrevious = true;
                }
                service.Listen();
                if (save) options.Save(optionsPath);
            }
            catch
            {
                await service.DisposeAsync().ConfigureAwait(false);
                if (stoppedPrevious && _activeOptions is not null && _activeStore is not null)
                {
                    var oldKey = LibrarySyncTransport.ParseCode(LocalSecretStore.Unprotect(_activeOptions.CodeProtected));
                    LibrarySyncService restored;
                    try { restored = new LibrarySyncService(_activeStore, _activeOptions, oldKey); }
                    finally { CryptographicOperations.ZeroMemory(oldKey); }
                    try { Attach(restored); restored.Start(); _service = restored; }
                    catch { await restored.DisposeAsync().ConfigureAwait(false); SetStatus("新设置未应用，旧监听也未能恢复，请检查端口及文件权限。"); throw; }
                }
                SetStatus(_service is null ? "同步未启动，请检查端口占用及设置文件权限。" : "新设置未应用，原同步服务继续运行。请检查端口占用及文件权限。");
                throw;
            }
            if (previous is not null && !stoppedPrevious) await previous.DisposeAsync().ConfigureAwait(false);
            Attach(service);
            _service = service; _activeOptions = options; _activeStore = store;
            service.Run();
            SetStatus("已开启，正在连接已配对设备…");
        }
        finally { Gate.Release(); }
    }

    private static void Attach(LibrarySyncService service)
    {
            service.Changed += () => Changed?.Invoke();
            service.StatusChanged += SetStatus;
    }

    public static bool SyncNow()
    {
        var service = _service;
        if (service is null) return false;
        service.SyncNow(); return true;
    }
    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_service is not null) { await _service.DisposeAsync().ConfigureAwait(false); _service = null; }
            _activeOptions = null; _activeStore = null;
        }
        finally { Gate.Release(); }
    }

    public static string LocalAddresses() => string.Join("、", NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && LibrarySyncTransport.IsLan(a) && !IPAddress.IsLoopback(a))
        .Select(a => a.ToString()).Distinct());
}
