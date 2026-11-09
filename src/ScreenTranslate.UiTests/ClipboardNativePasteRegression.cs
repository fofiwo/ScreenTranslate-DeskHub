using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    // 独立进程的两个原生输入框，验证真实前台、插入点和 SendInput，不访问用户应用。
    private static void VerifyNativeClipboardPaste()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "fixture.ps1");
        File.WriteAllText(script, """
            param([string]$FixtureFolder)
            Add-Type -AssemblyName System.Windows.Forms
            $form = New-Object System.Windows.Forms.Form
            $form.Text = 'Clipboard paste regression fixture'
            $form.StartPosition = 'Manual'
            $form.Left = 80; $form.Top = 80; $form.Width = 420; $form.Height = 170
            $inputBox = New-Object System.Windows.Forms.TextBox
            $inputBox.SetBounds(20,20,350,25); $inputBox.Text = 'leftright'
            $otherBox = New-Object System.Windows.Forms.TextBox
            $otherBox.SetBounds(20,65,350,25); $otherBox.Text = 'other input'
            $form.Controls.Add($inputBox); $form.Controls.Add($otherBox)
            $inputBox.Add_TextChanged({ [IO.File]::WriteAllText((Join-Path $FixtureFolder 'result.txt'), $inputBox.Text) })
            $form.Add_Shown({
                $form.Activate(); $inputBox.Focus(); $inputBox.Select(4,0)
                [IO.File]::WriteAllText((Join-Path $FixtureFolder 'ready.json'),
                    ('{"Window":' + $form.Handle.ToInt64() + ',"Focus":' + $inputBox.Handle.ToInt64() + '}'))
            })
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            $timer = New-Object System.Windows.Forms.Timer
            $timer.Interval = 100
            $timer.Add_Tick({ if ([IO.File]::Exists((Join-Path $FixtureFolder 'stop')) -or [DateTime]::UtcNow -gt $deadline) { $form.Close() } })
            $timer.Start()
            [System.Windows.Forms.Application]::Run($form)
            $timer.Dispose(); $form.Dispose()
            """, System.Text.Encoding.UTF8);
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-STA", "-File", script, folder }) start.ArgumentList.Add(arg);
        Process? process = null;
        ClipboardHistoryWindow? window = null;
        OriginalApplicationPaste.Start();
        try
        {
            process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            var ready = Path.Combine(folder, "ready.json");
            PumpUntil(() => File.Exists(ready) || process.HasExited);
            Assert.False(process.HasExited, error.IsCompleted ? error.Result : "Native fixture exited before creating its input boxes.");
            using var handles = JsonDocument.Parse(File.ReadAllText(ready));
            var target = new IntPtr(handles.RootElement.GetProperty("Window").GetInt64());
            var focus = new IntPtr(handles.RootElement.GetProperty("Focus").GetInt64());
            NativePasteSetForeground(target);
            PumpUntil(() => NativePasteGetForeground() == target);
            var destination = OriginalApplicationPaste.CaptureTarget();
            Assert.Equal(target, destination.Window); Assert.Equal(focus, destination.Focus);

            var store = new ClipboardHistoryStore(Path.Combine(folder, "history"));
            store.Add(new() { Text = "回填", Source = "fixture.exe" });
            window = new ClipboardHistoryWindow(store, settings: new AppSettings(), saveSettings: () => true);
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing && window.IsActive);
            var action = window.ActivateSelectedAsync();
            PumpUntil(() => action.IsCompleted); action.GetAwaiter().GetResult();
            Assert.False(window.IsVisible);
            var result = Path.Combine(folder, "result.txt");
            PumpUntil(() => File.Exists(result) && File.ReadAllText(result) == "left回填right");
            Assert.Equal(target, NativePasteGetForeground());

            // 原目标已关闭时保留面板和已复制内容，不发送到另一个前台窗口。
            File.WriteAllText(Path.Combine(folder, "stop"), "");
            PumpUntil(() => process.HasExited);
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing && window.IsActive);
            var rejected = OriginalApplicationPaste.PasteAsync(window, destination);
            PumpUntil(() => rejected.IsCompleted);
            Assert.False(rejected.GetAwaiter().GetResult());
            Assert.True(window.IsVisible);
        }
        finally
        {
            window?.Close(); OriginalApplicationPaste.Stop();
            File.WriteAllText(Path.Combine(folder, "stop"), "");
            if (process is not null) { if (!process.WaitForExit(3000)) process.Kill(); process.Dispose(); }
            Directory.Delete(folder, true);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool NativePasteSetForeground(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativePasteGetForeground();
}
