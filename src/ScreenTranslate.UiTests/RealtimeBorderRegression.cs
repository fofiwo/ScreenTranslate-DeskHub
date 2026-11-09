using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyRealtimeBorderUpdates(RealtimePage page)
    {
        var controller = RealtimeSessionController.Instance;
        var settings = SettingsService.Instance.Current.Realtime;
        var savedBorder = settings.BorderEnabled;
        var savedColor = settings.BorderColor;
        var toggle = (CheckBox)page.FindName("BorderToggle");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var controllerType = controller.GetType();
        var requestField = controllerType.GetField("_request", flags)!;
        var controlField = controllerType.GetField("_control", flags)!;
        var savedRequest = requestField.GetValue(controller);
        var savedControl = controlField.GetValue(controller);
        var windows = (Dictionary<int, RealtimeBlockWindow>)Field(controller, "_blockWindows")!;
        Assert.Empty(windows);

        var control = new RealtimeControlWindow(new System.Drawing.Rectangle(0, 0, 1920, 1080));
        var overlay = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 600, 300),
            _ => null, "EN", "ZH-HANS", "#FFFFFF", "#000000", 72);
        try
        {
            toggle.IsChecked = false;
            settings.BorderColor = "#FF0000";
            var request = RealtimeQuickStart.Build(SettingsService.Instance.Current,
                new System.Drawing.Rectangle(0, 0, 1920, 1080), "EN", "ZH-HANS",
                settings.Provider, settings.CaptureMode, IntPtr.Zero);
            requestField.SetValue(controller, request);
            controlField.SetValue(controller, control);
            windows.Add(0, overlay);
            // 只驱动真实的设置事件、会话和绘制链路，不启动 OCR 或网络翻译。
            typeof(RealtimeBlockWindow).GetField("_isLoaded", flags)!.SetValue(overlay, true);
            overlay.SetLines([new TranslatedBlock("source", "译文", new Rect(20, 20, 300, 30))]);
            Assert.Equal(0, RenderedBorderPixels(overlay));

            toggle.IsChecked = true;
            Assert.True(RenderedBorderPixels(overlay) > 100,
                "Enabling the page's border switch must immediately draw a visible border on existing translations.");
            Assert.True(((RealtimeStartRequest)requestField.GetValue(controller)!).Border);

            toggle.IsChecked = false;
            Assert.Equal(0, RenderedBorderPixels(overlay));
            toggle.IsChecked = true;
            Assert.True(RenderedBorderPixels(overlay) > 100);
        }
        finally
        {
            windows.Clear();
            requestField.SetValue(controller, savedRequest);
            controlField.SetValue(controller, savedControl);
            settings.BorderColor = savedColor;
            toggle.IsChecked = savedBorder;
            settings.BorderEnabled = savedBorder;
            SettingsService.Instance.Save();
            overlay.Close();
            control.Close();
        }
    }

    private static int RenderedBorderPixels(RealtimeBlockWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(600, 300));
        root.Arrange(new Rect(0, 0, 600, 300));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(600, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[600 * 300 * 4];
        bitmap.CopyPixels(pixels, 600 * 4, 0);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 2] > 240 && pixels[i + 1] < 15 && pixels[i] < 15 && pixels[i + 3] > 240)
                count++;
        return count;
    }
}
