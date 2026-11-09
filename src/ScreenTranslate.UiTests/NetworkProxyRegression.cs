using System.Windows;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Settings;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyNetworkProxySettings()
    {
        var settings = new NetworkProxySettings();
        var saves = 0; var applied = 0; var canSave = true;
        var card = new NetworkProxyCard(settings, () => { saves++; return canSave; }, _ => applied++);
        var enabled = Assert.IsType<CheckBox>(card.FindName("EnabledBox"));
        var address = Assert.IsType<TextBox>(card.FindName("AddressBox"));
        var save = Assert.IsType<Button>(card.FindName("SaveButton"));
        var status = Assert.IsType<TextBlock>(card.FindName("StatusText"));
        var rows = Assert.IsType<StackPanel>(card.FindName("FeatureRows")).Children.OfType<CheckBox>().ToArray();
        Assert.Equal(8, rows.Length);
        enabled.IsChecked = true; address.Text = "http://user:fixture@127.0.0.1:10808";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, saves); Assert.Equal(0, applied); Assert.Contains("代理地址无效", status.Text);
        address.Text = "http://127.0.0.1:10808";
        foreach (var row in rows) row.IsChecked = false;
        rows[0].IsChecked = true; rows[4].IsChecked = true;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, saves); Assert.Equal(1, applied); Assert.True(settings.Enabled);
        Assert.Equal(NetworkFeature.Updates | NetworkFeature.OnlineOcr, settings.Features);
        var restored = SettingsService.Parse(SettingsService.Serialize(new AppSettings { NetworkProxy = settings })).NetworkProxy;
        Assert.Equal(settings.Features, restored.Features); Assert.Equal(settings.Address, restored.Address);
        canSave = false; address.Text = "socks5://127.0.0.1:1080";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, applied); Assert.Equal("http://127.0.0.1:10808", settings.Address);
        Assert.Contains("保存失败", status.Text);
        canSave = true; enabled.IsChecked = false;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(settings.Enabled); Assert.Equal(2, applied);
        var window = new Window { Content = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Width = 760, Height = 720,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("AppWindowBg") };
        try
        {
            window.Show();
            Render(window, "network-proxy-settings", 760, 720);
            Assert.True(save.ActualWidth > 0);
            ApplyTheme(Application.Current, "Dark");
            window.Width = 500; window.Height = 620;
            Render(window, "network-proxy-settings-dark", 500, 620);
            foreach (var row in rows)
                Assert.True(((TextBlock)row.Content).ActualWidth < 470, "Every feature label must fit the narrow viewport.");
        }
        finally { window.Close(); ApplyTheme(Application.Current, "Light"); }
    }
}
