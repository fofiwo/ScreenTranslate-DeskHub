using Microsoft.Win32;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed class StartupServiceTests : IDisposable
{
    private readonly string _keyPath = @"Software\ScreenTranslateTests-" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    [Theory]
    [InlineData("\"D:\\Software\\ScreenTranslate\\current\\ScreenTranslate.exe\"", true)]
    [InlineData("\"d:\\software\\screentranslate\\CURRENT\\screentranslate.exe\"", true)]
    [InlineData("\"D:\\Software\\OCR翻译\\app\\ScreenTranslate.exe\"", false)]
    [InlineData("", false)]
    public void EnabledMeansThisExecutableIsRegistered(string command, bool expected)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(StartupService.AppName, command);

        Assert.Equal(expected, StartupService.IsEnabledFor(key, @"D:\Software\ScreenTranslate\current\ScreenTranslate.exe"));
    }

    [Fact]
    public void MissingEntryIsDisabled()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        Assert.False(StartupService.IsEnabledFor(key, @"D:\Software\ScreenTranslate\current\ScreenTranslate.exe"));
        Assert.False(StartupService.IsEnabledFor(null, @"D:\Software\ScreenTranslate\current\ScreenTranslate.exe"));
    }

    [Fact]
    public void EnablingReplacesTheOldCopyAndDisablingKeepsOtherStartupEntries()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        const string executable = @"D:\软件 with spaces\ScreenTranslate.exe";
        key.SetValue(StartupService.AppName, "\"D:\\old\\ScreenTranslate.exe\"");
        key.SetValue("OtherApp", "other.exe");

        StartupService.Set(key, true, executable);
        Assert.Equal($"\"{executable}\"", key.GetValue(StartupService.AppName));
        Assert.True(StartupService.IsEnabledFor(key, executable));

        StartupService.Set(key, false, executable);
        Assert.Null(key.GetValue(StartupService.AppName));
        Assert.Equal("other.exe", key.GetValue("OtherApp"));
    }
}
