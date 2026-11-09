using System.Net;
using System.Text;
using OverTranslate.Services;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanTransferTests
{
    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("NotifyIconOverflowWindow")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    public void Opening_tray_does_not_replace_paste_target_with_shell(string windowClass)
    {
        Assert.False(OriginalApplicationPaste.CanTargetWindowClass(windowClass));
        Assert.True(OriginalApplicationPaste.CanTargetWindowClass("Notepad"));
        Assert.True(OriginalApplicationPaste.CanTargetWindowClass("Chrome_WidgetWin_1"));
    }

    [Fact]
    public async Task Receipt_wait_allows_progress_but_times_out_when_stalled()
    {
        var receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressChecks = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = LanNode.WaitForReceiptAsync(receipt.Task, () =>
        {
            // 模拟传输持续有进展，在第三次空闲窗口检查时收到确认，不依赖机器调度速度。
            if (++progressChecks == 3) receipt.TrySetResult(true);
            return Environment.TickCount64;
        }, TimeSpan.FromMilliseconds(10), deadline.Token);
        Assert.True(await wait);
        Assert.Equal(3, progressChecks);
        await Assert.ThrowsAsync<TimeoutException>(() => LanNode.WaitForReceiptAsync(
            new TaskCompletionSource<bool>().Task, () => Environment.TickCount64 - 1000, TimeSpan.FromMilliseconds(10), deadline.Token));
    }

    [Fact]
    public async Task Stop_interrupts_pairing_and_next_pairing_starts_cleanly()
    {
        await using var host = new LanNode("fixture-A", () => Path.GetTempPath());
        await using var client = new LanNode("fixture-B", () => Path.GetTempPath());
        var prompting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ConfirmPairAsync = async (_, token) =>
        {
            prompting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        };
        await host.ListenAsync(IPAddress.Loopback, 0);
        var connecting = client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        await prompting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(host.IsConnected || host.IsListening || client.IsConnected);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        Assert.True(host.IsConnected && client.IsConnected);
    }

    [Theory]
    [InlineData("\\\\server\\share")]
    [InlineData("//server/share")]
    [InlineData("//?/C:/receive")]
    public void Receive_folder_must_not_allow_network_or_device_paths(string folder) =>
        Assert.Throws<InvalidDataException>(() => LanFiles.ValidateFolder(folder));

    [Fact]
    public async Task Paused_receiver_reports_skipped_clipboard_without_disconnect()
    {
        await using var host = new LanNode("fixture-A", () => Path.GetTempPath());
        await using var client = new LanNode("fixture-B", () => Path.GetTempPath());
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var applied = 0;
        host.ApplyClipboardAsync = (_, _) => { applied++; return Task.CompletedTask; };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        host.ClipboardEnabled = false;
        var sending = client.SendClipboardAsync(new("text", [1]));
        await sending;
        Assert.Equal(0, applied);
        Assert.True(client.IsConnected);
        // 暂停不能假报“已同步”：对方必须明确返回未应用。
        Assert.False(await Assert.IsAssignableFrom<Task<bool>>(sending));
        host.ClipboardEnabled = true;
        await client.SendClipboardAsync(new("text", [2]));
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Concurrent_bidirectional_files_and_clipboard_do_not_deadlock()
    {
        var root = Path.Combine(Path.GetTempPath(), "lan-duplex-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "received");
        Directory.CreateDirectory(root);
        await using var host = new LanNode("fixture-A", () => destination);
        await using var client = new LanNode("fixture-B", () => destination);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        host.ApplyClipboardAsync = client.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        try
        {
            var source = Path.Combine(root, "large.bin");
            await File.WriteAllBytesAsync(source, new byte[8 * 1024 * 1024]);
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Task.WhenAll(host.SendFileAsync(source, timeout.Token), host.SendFileAsync(source, timeout.Token),
                client.SendFileAsync(source, timeout.Token), client.SendFileAsync(source, timeout.Token),
                host.SendClipboardAsync(new("text", [1]), timeout.Token),
                client.SendClipboardAsync(new("text", [2]), timeout.Token));
            Assert.Equal(4, Directory.GetFiles(destination).Length);
            Assert.All(Directory.GetFiles(destination), p => Assert.Equal(8 * 1024 * 1024, new FileInfo(p).Length));
            Assert.True(host.IsConnected && client.IsConnected);
        }
        finally
        {
            await host.StopAsync(); await client.StopAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cancelling_queued_clipboard_does_not_abort_active_transfer()
    {
        await using var host = new LanNode("fixture-A", () => Path.GetTempPath());
        await using var client = new LanNode("fixture-B", () => Path.GetTempPath());
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ApplyClipboardAsync = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var active = client.SendClipboardAsync(new("text", [1]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            var queued = client.SendClipboardAsync(new("text", [2]), cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.True(client.IsConnected);
            release.TrySetResult();
            Assert.True(await active.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await client.SendClipboardAsync(new("text", [3])));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Old_queued_files_cannot_follow_a_new_pairing()
    {
        var root = Path.Combine(Path.GetTempPath(), "lan-session-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "received");
        Directory.CreateDirectory(root);
        await using var host = new LanNode("fixture-A", () => destination);
        await using var client = new LanNode("fixture-B", () => destination);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        // 文件有独立通道；必须阻塞文件确认，不能再靠剪贴板接收阻塞制造队列。
        host.Progress += (_, _) => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); };
        try
        {
            var source = Path.Combine(root, "old-batch.bin");
            await File.WriteAllBytesAsync(source, new byte[100000]);
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            var active = client.SendFileAsync(source);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldFile = client.SendFileAsync(source);
            Assert.False(oldFile.IsCompleted);
            await client.StopAsync();
            var stopping = host.StopAsync(); release.Set(); await stopping;
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            await Assert.ThrowsAnyAsync<Exception>(() => active);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldFile);
            Assert.Empty(Directory.GetFiles(destination));
            await client.SendFileAsync(source);
            Assert.Single(Directory.GetFiles(destination));
        }
        finally
        {
            release.Set();
            await host.StopAsync(); await client.StopAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Reconnecting_resets_duplicate_suppression_for_new_copy()
    {
        var loop = new ClipboardLoopGuard();
        var text = new LanClipboardPayload("text", Encoding.UTF8.GetBytes("fixture-same-content"));
        Assert.True(loop.ShouldSend(text, 1));
        Assert.False(loop.ShouldSend(text, 1));
        Assert.True(loop.ShouldSend(text, 2));
        loop.Received(text, 2);
        Assert.False(loop.ShouldSend(text, 2));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("C:\\escape.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("CON.txt")]
    [InlineData("LPT1")]
    [InlineData("trailing.")]
    [InlineData("")]
    public void Remote_names_cannot_choose_paths_or_device_files(string name) =>
        Assert.Throws<InvalidDataException>(() => LanFiles.ValidateName(name));

    [Fact]
    public async Task Tls_pairing_transfers_text_images_and_files_without_overwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lan-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var host = new LanNode("fixture-A", () => directory);
        await using var client = new LanNode("fixture-B", () => directory);
        var received = new List<LanClipboardPayload>();
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        host.ApplyClipboardAsync = (payload, _) => { lock (received) received.Add(payload); return Task.CompletedTask; };
        client.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            try { await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode); }
            catch (Exception ex) { throw new AggregateException(ex, host.LastConnectionError ?? new Exception("No server error")); }
            Assert.True(client.IsConnected);
            await client.SendClipboardAsync(new("text", Encoding.UTF8.GetBytes("fixture-text\n第二行")));
            await client.SendClipboardAsync(new("image", new byte[] { 1, 2, 3, 4 }));
            Assert.Equal(2, received.Count);
            Assert.Equal("fixture-text\n第二行", Encoding.UTF8.GetString(received[0].Bytes));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, received[1].Bytes);

            var source = Path.Combine(directory, "fixture.bin");
            var bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            await client.SendFileAsync(source);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, "fixture (1).bin")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "empty.txt"), []);
            await client.SendFileAsync(Path.Combine(directory, "empty.txt"));
            Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(directory, "empty (1).txt")));
        }
        finally
        {
            await client.StopAsync(); await host.StopAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Wrong_pair_code_and_rejected_devices_never_receive_clipboard()
    {
        await using var host = new LanNode("fixture-A", () => Path.GetTempPath());
        await using var client = new LanNode("fixture-B", () => Path.GetTempPath());
        var prompts = 0;
        host.ConfirmPairAsync = (_, _) => { prompts++; return Task.FromResult(false); };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(IPAddress.Loopback, host.Port, new string('0', 32)));
        Assert.Equal(0, prompts);
        var wrongSecret = host.PairCode[..^1] + (host.PairCode[^1] == '0' ? '1' : '0');
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(IPAddress.Loopback, host.Port, wrongSecret));
        Assert.Equal(0, prompts);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode));
        Assert.Equal(1, prompts);
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendClipboardAsync(new("text", [1])));
    }

    [Fact]
    public async Task Broken_or_corrupted_files_leave_no_completed_or_partial_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "lan-broken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var header = new LanHeader("file", Guid.NewGuid().ToString("N"), "fixture.bin", 4, LanProtocol.Hash([1, 2, 3, 4]));
            await Assert.ThrowsAsync<EndOfStreamException>(() => LanFiles.ReceiveAsync(new MemoryStream([1, 2]), header, root, _ => { }, default));
            Assert.Empty(Directory.GetFiles(root));
            await Assert.ThrowsAsync<InvalidDataException>(() => LanFiles.ReceiveAsync(new MemoryStream([1, 2, 3, 5]), header, root, _ => { }, default));
            Assert.Empty(Directory.GetFiles(root));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LanFiles.ReceiveAsync(new MemoryStream([1, 2, 3, 4]), header, root, _ => { }, cancelled.Token));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Incoming_clipboard_is_not_echoed_and_preferences_round_trip()
    {
        var loop = new ClipboardLoopGuard();
        var first = new LanClipboardPayload("text", Encoding.UTF8.GetBytes("fixture-A"));
        Assert.True(loop.ShouldSend(first));
        Assert.False(loop.ShouldSend(first));
        var incoming = new LanClipboardPayload("image", [1, 2, 3]);
        loop.Received(incoming);
        Assert.False(loop.ShouldSend(incoming));
        Assert.True(loop.ShouldSend(first));
        var root = Path.Combine(Path.GetTempPath(), "lan-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "preferences.json");
        try
        {
            var preferences = new LanPreferences(Path.Combine(root, "接收目录"), false);
            preferences.Save(path);
            Assert.Equal(preferences, LanPreferences.Load(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(root)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Untrusted_headers_cannot_allocate_unbounded_payloads()
    {
        var stream = new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff });
        await Assert.ThrowsAsync<InvalidDataException>(() => LanProtocol.ReadHeaderAsync(stream, default));
        Assert.Throws<InvalidDataException>(() => LanProtocol.ValidatePayload(
            new("image", Guid.NewGuid().ToString("N"), Length: LanProtocol.MaxClipboard + 1L, Hash: new string('0', 64))));
        Assert.Throws<InvalidDataException>(() => LanProtocol.ValidatePayload(
            new("file", Guid.NewGuid().ToString("N"), "../outside", 1, new string('0', 64))));
    }
}
