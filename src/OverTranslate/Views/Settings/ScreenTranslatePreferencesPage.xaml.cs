using System.Windows;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Views.Shell;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Settings;

public partial class ScreenTranslatePreferencesPage : UserControl
{
    private readonly Func<SettingsPage> _advancedFactory;
    private OcrSettingsPage? _ocr;
    private SettingsPage? _advanced;
    private SettingsPage? _quickTools;
    private SettingsPage? _serviceOptions;

    public ScreenTranslatePreferencesPage(Func<SettingsPage> advancedFactory)
    {
        _advancedFactory = advancedFactory;
        InitializeComponent();
        EnsureSelectedPage();
    }

    private void EnsureSelectedPage()
    {
        // Tab events also fire while InitializeComponent is building the named hosts.
        if (OcrHost is null || AdvancedHost is null || QuickToolsHost is null) return;
        if (Tabs.SelectedIndex == 0) OcrHost.Content = _ocr ??= new();
        if (Tabs.SelectedIndex == 1)
        {
            _serviceOptions ??= new SettingsPage();
            _serviceOptions.ShowSection("备用策略");
            _serviceOptions.Reload();
            ServiceOptionsHost.Content = _serviceOptions;
        }
        if (Tabs.SelectedIndex == 3)
        {
            _advanced ??= _advancedFactory();
            _advanced.ShowQuickTools(false);
            _advanced.Reload();
            AdvancedHost.Content = _advanced;
        }
        if (Tabs.SelectedIndex == 2)
        {
            _quickTools ??= new SettingsPage();
            _quickTools.ShowQuickTools(true);
            _quickTools.Reload();
            QuickToolsHost.Content = _quickTools;
        }
    }

    private void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs)) EnsureSelectedPage();
    }
    public void ShowQuickTools() { Tabs.SelectedIndex = 2; EnsureSelectedPage(); }
    public void ShowOcr() { Tabs.SelectedIndex = 0; EnsureSelectedPage(); }
    public void Reload() { _ocr?.Reload(); EnsureSelectedPage(); }
    public void Teardown() => _ocr?.Teardown();
    private void OpenAi_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as ShellWindow)?.OpenServiceSettings(TranslationProvider.OpenAI);
    private void DeepL_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as ShellWindow)?.OpenServiceSettings(TranslationProvider.DeepL);
}
