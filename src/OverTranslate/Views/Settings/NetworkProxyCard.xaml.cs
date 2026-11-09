using System.Windows;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using UserControl = System.Windows.Controls.UserControl;
using CheckBox = System.Windows.Controls.CheckBox;

namespace OverTranslate.Views.Settings;

public partial class NetworkProxyCard : UserControl
{
    private readonly NetworkProxySettings _settings;
    private readonly Func<bool> _save;
    private readonly Action<NetworkProxySettings> _apply;
    private readonly Dictionary<NetworkFeature, CheckBox> _features = new();

    public NetworkProxyCard() : this(SettingsService.Instance.Current.NetworkProxy,
        () => SettingsService.Instance.TrySave(), NetworkProxyService.Apply) { }

    internal NetworkProxyCard(NetworkProxySettings settings, Func<bool> save, Action<NetworkProxySettings> apply)
    {
        _settings = settings; _save = save; _apply = apply;
        InitializeComponent();
        EnabledBox.IsChecked = settings.Enabled;
        AddressBox.Text = settings.Address;
        foreach (var (feature, label) in NetworkProxyService.Features)
        {
            var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");
            var box = new CheckBox { Content = text,
                IsChecked = (settings.Features & feature) != 0, Margin = new Thickness(0, 0, 0, 8) };
            box.SetResourceReference(CheckBox.ForegroundProperty, "AppTextPrimary");
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            _features.Add(feature, box); FeatureRows.Children.Add(box);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledBox.IsChecked == true;
        if (enabled && !NetworkProxyService.TryParseAddress(AddressBox.Text, out _))
        {
            StatusText.Text = "代理地址无效。请输入完整协议、主机与端口，例如 http://127.0.0.1:10808；不要包含账号密码、路径或查询参数。";
            AddressBox.Focus();
            return;
        }
        var old = new NetworkProxySettings { Enabled = _settings.Enabled, Address = _settings.Address, Features = _settings.Features };
        _settings.Enabled = enabled; _settings.Address = AddressBox.Text.Trim();
        _settings.Features = _features.Where(pair => pair.Value.IsChecked == true)
            .Aggregate((NetworkFeature)0, (value, pair) => value | pair.Key);
        if (!_save())
        {
            _settings.Enabled = old.Enabled; _settings.Address = old.Address; _settings.Features = old.Features;
            StatusText.Text = "代理设置保存失败，原网络设置继续生效。请检查目录权限后重试。";
            return;
        }
        _apply(_settings);
        StatusText.Text = enabled && _settings.Features != 0
            ? "已保存，勾选功能的后续请求使用此代理。可点击检查更新验证更新连接。"
            : "已保存，当前使用系统默认网络。";
    }
}
