namespace OverTranslate.Services.Lan;

internal sealed class LanReconnectLoop(Func<bool> available, Func<CancellationToken, Task> connect,
    Action<string> status, Func<TimeSpan, CancellationToken, Task>? delay = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _task;
    public void Start() => _task ??= RunAsync();
    internal static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(30, 2 * (1 << Math.Min(attempts, 4))));
    private async Task RunAsync()
    {
        var attempts = 0;
        var wait = delay ?? ((TimeSpan duration, CancellationToken token) => Task.Delay(duration, token));
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (available()) { attempts = 0; await wait(TimeSpan.FromSeconds(2), _stop.Token); continue; }
                status("正在自动重连…");
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(5));
                    await connect(attempt.Token);
                    attempts = 0; await wait(TimeSpan.FromSeconds(2), _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    var backoff = Backoff(attempts++);
                    var reason = exception switch
                    {
                        System.Security.Authentication.AuthenticationException => "设备信任失效，请重新配对",
                        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse } => "连接端口被占用",
                        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused } => "对方尚未启动或未开放连接",
                        TimeoutException or OperationCanceledException => "连接超时",
                        _ => "请检查对方是否启动、地址及网络是否可用"
                    };
                    status($"暂时无法连接 · {reason} · {backoff.TotalSeconds:N0} 秒后重试。");
                    await wait(backoff, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_task is not null) await _task;
        _stop.Dispose();
    }
}
