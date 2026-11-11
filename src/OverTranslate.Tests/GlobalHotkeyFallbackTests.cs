using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class GlobalHotkeyFallbackTests
{
    [Theory]
    [InlineData(0x0100, 0x0101)]
    [InlineData(0x0104, 0x0105)]
    public void PressTriggersOnceAndConsumesReleaseEvenAfterModifiersAreReleased(int down, int up)
    {
        var presses = 0;
        using var hook = new GlobalHotkeyFallback(1, 0x41, () => presses++);
        Assert.True(hook.ProcessKey(down, 0x41, 1));
        Assert.True(hook.ProcessKey(down, 0x41, 1));
        Assert.True(hook.ProcessKey(down, 0x41, 0));
        Assert.Equal(1, presses);
        Assert.True(hook.ProcessKey(up, 0x41, 0));
        Assert.True(hook.ProcessKey(down, 0x41, 1));
        Assert.Equal(2, presses);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(9u)]
    public void DifferentModifierCombinationsPassThrough(uint modifiers)
    {
        using var hook = new GlobalHotkeyFallback(1, 0x41, () => Assert.Fail("Wrong combination triggered"));
        Assert.False(hook.ProcessKey(0x0104, 0x41, modifiers));
        Assert.False(hook.ProcessKey(0x0104, 0x41, 1)); // 已按住的普通字母不能因后加 Alt 重复触发。
        Assert.False(hook.ProcessKey(0x0105, 0x41, modifiers));
        Assert.False(hook.ProcessKey(0x0104, 0x44, 1));
    }

    [Fact]
    public void ReleasingUnmatchedKeyAllowsNextMatchingPressAndDisposeStopsConsumption()
    {
        var presses = 0;
        var hook = new GlobalHotkeyFallback(1, 0x41, () => presses++);
        Assert.False(hook.ProcessKey(0x0100, 0x41, 0));
        Assert.False(hook.ProcessKey(0x0101, 0x41, 0));
        Assert.True(hook.ProcessKey(0x0104, 0x41, 1));
        hook.Dispose();
        Assert.False(hook.ProcessKey(0x0105, 0x41, 1));
        Assert.False(hook.ProcessKey(0x0104, 0x41, 1));
        Assert.Equal(1, presses);
    }
}
