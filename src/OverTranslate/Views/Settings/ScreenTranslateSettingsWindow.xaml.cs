using System.Windows;
namespace OverTranslate.Views.Settings;
public partial class ScreenTranslateSettingsWindow : Window
{
    public ScreenTranslateSettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => Page.Teardown();
    }
}
