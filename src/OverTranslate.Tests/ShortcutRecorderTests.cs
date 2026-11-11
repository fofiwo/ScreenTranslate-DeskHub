using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class ShortcutRecorderTests
{
    [Fact]
    public void RecordsAltCombinationOnReleaseWithOriginalModifiersAndNoRepeats()
    {
        var recorded = new List<(uint Key, uint Modifiers)>();
        using var recorder = new ShortcutRecorder(IntPtr.Zero, (key, modifiers) => recorded.Add((key, modifiers)));
        Assert.False(recorder.ProcessKey(0x0104, 0xA4, 0));
        Assert.True(recorder.ProcessKey(0x0104, 0x41, 1));
        Assert.True(recorder.ProcessKey(0x0104, 0x41, 1));
        Assert.Empty(recorded);
        Assert.False(recorder.ProcessKey(0x0105, 0xA4, 1));
        Assert.True(recorder.ProcessKey(0x0101, 0x41, 0));
        Assert.Equal(new[] { (0x41u, 1u) }, recorded);
        Assert.False(recorder.ProcessKey(0x0101, 0x41, 0));
    }

    [Theory]
    [InlineData(0x09u)]
    [InlineData(0x10u)]
    [InlineData(0x11u)]
    [InlineData(0x12u)]
    [InlineData(0x5Bu)]
    [InlineData(0xA5u)]
    public void ModifiersAndTabRemainAvailable(uint key)
    {
        using var recorder = new ShortcutRecorder(IntPtr.Zero, (_, _) => Assert.Fail("Unexpected recording"));
        Assert.False(recorder.ProcessKey(0x0100, key, 0));
        Assert.False(recorder.ProcessKey(0x0101, key, 0));
    }

    [Fact]
    public void LeavingRecordingDiscardsHeldCombination()
    {
        var recorder = new ShortcutRecorder(IntPtr.Zero, (_, _) => Assert.Fail("Recording survived disposal"));
        Assert.True(recorder.ProcessKey(0x0104, 0x41, 1));
        recorder.Dispose();
        Assert.False(recorder.ProcessKey(0x0105, 0x41, 1));
    }
}
