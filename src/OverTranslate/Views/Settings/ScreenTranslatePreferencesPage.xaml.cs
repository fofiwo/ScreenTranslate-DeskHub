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

    public ScreenTranslatePreferencesPage(Func<SettingsPage> advancedFactory)
    {
        _advancedFactory = advancedFactory;
        InitializeComponent();
        EnsureSelectedPage();
    }

    private void EnsureSelectedPage()
    {
        // Tab events also fire while InitializeComponent is building the named hosts.
        if (OcrHost is null || AdvancedHost is null) return;
        if (Tabs.SelectedIndex == 0) OcrHost.Content = _ocr ??= new();
        if (Tabs.SelectedIndex == 2)
        {
            _advanced ??= _advancedFactory();
            _advanced.Reload();
            AdvancedHost.Content = _advanced;
        }
    }

    private void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs)) EnsureSelectedPage();
    }
    public void ShowOcr() { Tabs.SelectedIndex = 0; EnsureSelectedPage(); }
    public void Reload() { _ocr?.Reload(); EnsureSelectedPage(); }
    public void Teardown() => _ocr?.Teardown();
    private void OpenAi_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as ShellWindow)?.OpenServiceSettings(TranslationProvider.OpenAI);
    private void DeepL_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as ShellWindow)?.OpenServiceSettings(TranslationProvider.DeepL);
}
