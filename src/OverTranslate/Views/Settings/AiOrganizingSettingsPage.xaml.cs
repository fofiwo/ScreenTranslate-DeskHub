using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Settings;

public partial class AiOrganizingSettingsPage : UserControl
{
    private readonly Func<LibraryStore> _storeFactory;
    private bool _loading, _loaded, _dirty, _saving;
    private string _lastEndpoint = "";

    public AiOrganizingSettingsPage() : this(() => new LibraryStore(LibraryStore.DefaultPath)) { }

    internal AiOrganizingSettingsPage(Func<LibraryStore> storeFactory)
    {
        _storeFactory = storeFactory;
        InitializeComponent();
        Reload();
    }

    public void Reload()
    {
        if (_dirty || _saving) return;
        _loading = true;
        try
        {
            var ai = _storeFactory().Read().Ai;
            AiEndpoint.Text = _lastEndpoint = ai.Endpoint;
            AiModel.Text = ai.Model;
            AiKey.Clear();
            AiAutomatic.IsChecked = ai.AutoSummarize;
            AiInsecure.IsChecked = ai.AllowInsecureEndpoint;
            KeyHint.Text = ai.ApiKeyProtected.Length > 0
                ? "API Key（已加密保存，留空保留同源地址原值）" : "API Key（本地接口可留空）";
            _loaded = true;
            FormPanel.IsEnabled = SaveButton.IsEnabled = true;
            Notice("");
        }
        catch
        {
            _loaded = false;
            FormPanel.IsEnabled = SaveButton.IsEnabled = false;
            Notice("读取 AI 整理设置失败，请检查资料库文件及目录权限，然后点击重新加载。", true);
        }
        finally { _loading = false; }
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !_loaded) return;
        _dirty = true;
        Notice("更改尚未保存。");
    }

    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || !_loaded) return;
        if (!ModelPlatforms.SameOrigin(_lastEndpoint, AiEndpoint.Text))
        {
            AiKey.Clear();
            AiInsecure.IsChecked = false;
        }
        _lastEndpoint = AiEndpoint.Text;
        Input_Changed(sender, e);
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _dirty = false;
        Reload();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || !_loaded) return;
        _saving = true;
        FormPanel.IsEnabled = SaveButton.IsEnabled = ReloadButton.IsEnabled = false;
        try
        {
            var key = AiKey.Password;
            LibraryHttp.ValidateKey(key);
            var insecure = AiInsecure.IsChecked == true;
            var endpoint = LibraryHttp.ValidateEndpoint(AiEndpoint.Text, insecure).AbsoluteUri.TrimEnd('/');
            var model = AiModel.Text.Trim();
            var automatic = AiAutomatic.IsChecked == true;
            Notice("正在保存…");
            await Task.Run(() => _storeFactory().Update(s => s.Ai = new()
            {
                Endpoint = endpoint, Model = model,
                ApiKeyProtected = key.Length > 0 ? LocalSecretStore.Protect(key)
                    : ModelPlatforms.SameOrigin(s.Ai.Endpoint, endpoint) ? s.Ai.ApiKeyProtected : "",
                AutoSummarize = automatic, AllowInsecureEndpoint = insecure
            }));
            AiKey.Clear();
            _dirty = false;
            KeyHint.Text = "API Key（留空保留同源地址原值）";
            Notice("AI 整理设置已保存。");
        }
        catch (Exception ex)
        {
            Notice("保存失败：" + (ex is InvalidOperationException
                ? ex.Message : "请检查资料库文件及目录权限。") + " 输入内容已保留，可修改后重新保存。", true);
        }
        finally
        {
            _saving = false;
            FormPanel.IsEnabled = SaveButton.IsEnabled = ReloadButton.IsEnabled = true;
        }
    }

    private void Notice(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "AppError" : "AppTextSecondary");
    }
}
