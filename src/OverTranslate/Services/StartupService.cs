using System.IO;
using Microsoft.Win32;

namespace OverTranslate.Services;

public static class StartupService
{
    internal const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    internal const string AppName = "ScreenTranslate";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return IsEnabledFor(key, ExecutablePath);
        }
    }

    private static string ExecutablePath => Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("无法确定程序路径，未修改开机启动项。");

    internal static bool IsEnabledFor(RegistryKey? key, string executablePath) =>
        // 同名启动项可能属于旧目录或另一份便携版，不能把它显示为本副本已启用。
        key?.GetValue(AppName) is string command &&
        string.Equals(command.Trim(), $"\"{executablePath}\"", StringComparison.OrdinalIgnoreCase);

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开开机启动注册表项。");

        Set(key, enable, ExecutablePath);
    }

    internal static void Set(RegistryKey key, bool enable, string executablePath)
    {
        if (enable)
        {
            key.SetValue(AppName, $"\"{executablePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Removes the sign-in entry if it starts an executable under <paramref name="directory"/>, for
    /// <see cref="UninstallCleanup"/>. One naming another copy — a portable build set to start with
    /// Windows — is that copy's, and stays.
    /// </summary>
    internal static void RemoveIfPointsInto(RegistryKey runKey, string directory)
    {
        if (runKey.GetValue(AppName) is not string command) return;
        var exe = command.Trim();
        exe = exe.StartsWith('"') ? exe[1..].Split('"')[0] : exe.Split(' ')[0];

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(exe).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            runKey.DeleteValue(AppName, throwOnMissingValue: false);
    }
}
