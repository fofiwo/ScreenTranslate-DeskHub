using System.Windows;
using System.Windows.Controls;
using OverTranslate.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Realtime;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Shell;

public partial class HomePage : UserControl
{
    private bool _loading = true;
    private readonly Settings.SettingsPage _captureOptions;
    private readonly OcrSourcePicker _sourcePicker;

    public HomePage()
    {
        InitializeComponent();
        _captureOptions = new Settings.SettingsPage();
        _captureOptions.ShowSection("截图选项");
        _captureOptions.OptionsChanged += (_, _) => RefreshAvailability();
        CaptureOptionsHost.Content = _captureOptions;
        _sourcePicker = new(SourceBox, () => LanguageData.DefaultOcrSourceLanguage);
        Reload();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            _captureOptions.Reload();
            var settings = SettingsService.Instance.Current;
            LocalizationService.BindLocalizedItems(SourceBox, LanguageData.OcrSourceLanguages);
            LocalizationService.BindLocalizedItems(TargetBox, LanguageData.TargetLanguages);
            ProviderPicker.Bind(ProviderBox, ProviderVisibility.MenuItems(settings, settings.Provider));
            SourceBox.SelectedValue = LanguageData.GetValidOcrSourceCode(settings.SourceLanguage);
            TargetBox.SelectedValue = LanguageData.GetValidTargetCode(settings.TargetLanguage);
            ProviderBox.SelectedValue = settings.Provider;
            ShortcutText.Text = settings.HotkeyDisplay;
            OcrSummary.Text = settings.Ocr.Provider switch
            {
                OcrProviderKind.Http => "OCR · 自定义 HTTP 模型服务",
                OcrProviderKind.Baidu => "OCR · 百度云端识别",
                _ => "OCR · 内置 RapidOCR，本地识别"
            };
            RefreshAvailability();
        }
        finally { _loading = false; }
    }

    public void RefreshAvailability()
    {
        var session = RealtimeSessionController.Instance;
        StartCaptureButton.IsEnabled = !session.IsActive || session.IsTranslating;
        StatusText.Text = !StartCaptureButton.IsEnabled ? "持续翻译正在框选区域，请先完成或取消框选。"
            : session.IsActive ? "持续翻译运行中；截图期间会暂时让出画面。"
            : SettingsService.Instance.Current.LiveCaptureOverlay
                ? "当前显示：原位覆盖 · 快捷键随时可用"
                : "当前显示：冻结截图 · 可在下方截图选项开启原位覆盖";
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sourcePicker is not null && ReferenceEquals(sender, SourceBox)
            && SettingsService.Instance.Current.Ocr.Provider == OcrProviderKind.Local && _sourcePicker.Intercept(e)) return;
        if (_loading || SourceBox.SelectedValue is not string source || TargetBox.SelectedValue is not string target
            || ProviderBox.SelectedValue is not TranslationProvider provider) return;
        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = source;
        settings.TargetLanguage = target;
        settings.Provider = provider;
        SettingsService.Instance.Save();
    }

    private ShellWindow? Shell => Window.GetWindow(this) as ShellWindow;
    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (Shell is { } shell && System.Windows.Application.Current.MainWindow is MainWindow main)
            main.StartCaptureFromShell(shell);
    }
    private void Shortcuts_Click(object sender, RoutedEventArgs e) => Shell?.ShowQuickToolSettings();
    private void Settings_Click(object sender, RoutedEventArgs e) => Shell?.ShowOcrSettings();
    private void Realtime_Click(object sender, RoutedEventArgs e) => Shell?.Navigate(ShellPage.Realtime);
    public void Teardown() => _sourcePicker.Dispose();
}
