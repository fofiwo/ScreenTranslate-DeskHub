using System.Runtime.InteropServices;
using System.Text;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanClipboardWriterTests
{
    [Fact]
    public async Task Busy_readback_retries_read_without_republishing()
    {
        var writes = 0; var reads = 0;
        await LanClipboardWriter.WriteVerifiedAsync(() => writes++, () =>
        {
            if (++reads < 3) throw new COMException("fixture", unchecked((int)0x800401D0));
        }, default);
        Assert.Equal(1, writes); Assert.Equal(3, reads);
    }

    [Fact]
    public async Task Replaced_content_is_failure_not_success_or_another_write()
    {
        var writes = 0;
        await Assert.ThrowsAsync<COMException>(() => LanClipboardWriter.WriteVerifiedAsync(() => writes++,
            () => throw new COMException("fixture", unchecked((int)0x800401D3)), default));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task Busy_write_is_bounded_and_cancellation_prevents_late_write()
    {
        var writes = 0;
        await Assert.ThrowsAsync<COMException>(() => LanClipboardWriter.WriteVerifiedAsync(() =>
        {
            writes++; throw new COMException("fixture", unchecked((int)0x800401D0));
        }, () => Assert.Fail("failed write must not be verified"), default));
        Assert.Equal(6, writes);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LanClipboardWriter.WriteVerifiedAsync(
            () => writes++, () => Assert.Fail("cancelled write must not be verified"), cancelled.Token));
        Assert.Equal(6, writes);
    }

    [Fact]
    public void Native_origin_stream_remains_compatible_and_rejects_bad_data()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fixture-电脑\0\0"));
        Assert.Equal("来自 fixture-电脑", LanClipboardSync.DescribeOrigin(stream));
        Assert.Equal("局域网", LanClipboardSync.DescribeOrigin(new byte[] { 0xff }));
        Assert.Equal("局域网", LanClipboardSync.DescribeOrigin(new byte[321]));
    }
}
