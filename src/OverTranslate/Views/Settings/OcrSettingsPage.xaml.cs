using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime.Capture;

namespace OverTranslate.Views.Settings;

public partial class OcrSettingsPage : System.Windows.Controls.UserControl
{
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Func<bool> _saveSettings;
    private readonly SettingsPage _recognitionOptions;
    public OcrSettingsPage() : this(() => SettingsService.Instance.TrySave()) { }
    internal OcrSettingsPage(Func<bool> saveSettings)
    {
        _saveSettings = saveSettings;
        InitializeComponent();
        _recognitionOptions = new SettingsPage();
        _recognitionOptions.ShowSection("识别选项");
        RecognitionOptionsHost.Content = _recognitionOptions;
        OcrProviderBox.ItemsSource = new[] { "本地 RapidOCR（内置模型）", "HTTP OCR（本地模型或自定义服务）", "百度 OCR（带位置的通用文字识别）" };
        Reload();
    }

    public void Reload()
    {
        var settings = SettingsService.Instance.Current;
        OcrProviderBox.SelectedIndex = (int)settings.Ocr.Provider;
        EndpointBox.Text = settings.Ocr.Endpoint;
        TimeoutBox.Text = settings.Ocr.TimeoutSeconds.ToString();
        IntervalBox.Text = settings.Ocr.MinimumIntervalMs.ToString();
        _recognitionOptions.Reload();
        UpdateCredentialState();
        UpdateProviderFields();
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e) => UpdateProviderFields();
    private void UpdateProviderFields()
    {
        if (LocalHint is null || HttpFields is null || BaiduFields is null || ExternalFields is null) return;
        LocalHint.Visibility = OcrProviderBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        HttpFields.Visibility = OcrProviderBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BaiduFields.Visibility = OcrProviderBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ExternalFields.Visibility = OcrProviderBox.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCredentialState() => CredentialState.Text =
        SettingsService.Instance.Current.Ocr.BaiduApiKeyProtected.Length > 0 ? "已保存加密凭据；输入新值可替换。" : "尚未保存百度凭据。";

    private bool SaveOptions()
    {
        if (Views.Realtime.RealtimeSessionController.Instance.IsActive)
        { StatusText.Text = "请先结束持续翻译，再修改 OCR 设置。"; return false; }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout is < 2 or > 120
            || !int.TryParse(IntervalBox.Text, out var interval) || interval is < 500 or > 60000)
        { StatusText.Text = "请填写范围内的超时与请求间隔。"; return false; }
        try
        {
            if (OcrProviderBox.SelectedIndex == 1) HttpOcrProvider.ValidateEndpoint(EndpointBox.Text.Trim());
            var settings = SettingsService.Instance.Current;
            var old = settings.Ocr;
            var options = new OcrProviderSettings
            {
                Provider = (OcrProviderKind)Math.Clamp(OcrProviderBox.SelectedIndex, 0, 2), Endpoint = EndpointBox.Text.Trim(),
                TimeoutSeconds = timeout, MinimumIntervalMs = interval,
                BaiduApiKeyProtected = ApiKeyBox.Password.Length > 0 ? LocalSecretStore.Protect(ApiKeyBox.Password) : old.BaiduApiKeyProtected,
                BaiduSecretKeyProtected = SecretBox.Password.Length > 0 ? LocalSecretStore.Protect(SecretBox.Password) : old.BaiduSecretKeyProtected,
            };
            if (options.Provider == OcrProviderKind.Baidu && (options.BaiduApiKeyProtected.Length == 0 || options.BaiduSecretKeyProtected.Length == 0))
            { StatusText.Text = "百度 OCR 需要同时保存 API Key 和 Secret Key。"; return false; }
            settings.Ocr = options;
            if (!_saveSettings())
            {
                settings.Ocr = old;
                StatusText.Text = "保存失败，识别设置未应用。请检查目录权限后重试，输入内容已保留。";
                return false;
            }
            AppServices.Ocr.ReleaseModel();
            ApiKeyBox.Clear(); SecretBox.Clear();
            UpdateCredentialState();
            StatusText.Text = "设置已保存。OCR 失败会报告错误，不会自动将截图转发给其他服务。";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { StatusText.Text = ex.Message; return false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { ApiKeyBox.Clear(); SecretBox.Clear(); Reload(); StatusText.Text = "已取消未保存的识别服务更改。"; }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveOptions();

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveOptions()) return;
        TestButton.IsEnabled = false;
        try
        {
            using var image = new Bitmap(640, 160);
            using (var graphics = Graphics.FromImage(image))
            using (var font = new Font("Arial", 30))
            { graphics.Clear(System.Drawing.Color.White); graphics.DrawString("Screen translation test", font, System.Drawing.Brushes.Black, 20, 40); }
            var watch = Stopwatch.StartNew();
            var result = await AppServices.Ocr.RecognizeAsync(image, "EN", _lifetime.Token);
            StatusText.Text = result.Count == 0 ? $"服务已响应，但没有识别到测试文字（{watch.ElapsedMilliseconds}ms）。"
                : $"识别成功 · {watch.ElapsedMilliseconds}ms · {string.Join(" / ", result.Select(block => block.Text))}";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { StatusText.Text = "OCR 检查失败，请核对服务、凭据、配额和网络配置。"; }
        finally { TestButton.IsEnabled = true; }
    }

    private void ClearCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (Views.Realtime.RealtimeSessionController.Instance.IsActive) { StatusText.Text = "请先结束持续翻译。"; return; }
        var options = SettingsService.Instance.Current.Ocr;
        var oldApiKey = options.BaiduApiKeyProtected;
        var oldSecret = options.BaiduSecretKeyProtected;
        options.BaiduApiKeyProtected = options.BaiduSecretKeyProtected = "";
        if (!_saveSettings())
        {
            options.BaiduApiKeyProtected = oldApiKey;
            options.BaiduSecretKeyProtected = oldSecret;
            StatusText.Text = "清除凭据保存失败，原有凭据已保留。请检查目录权限后重试。";
            return;
        }
        ApiKeyBox.Clear(); SecretBox.Clear();
        UpdateCredentialState();
        StatusText.Text = "已清除百度凭据。";
    }

    public void Teardown() => _lifetime.Cancel();
}
