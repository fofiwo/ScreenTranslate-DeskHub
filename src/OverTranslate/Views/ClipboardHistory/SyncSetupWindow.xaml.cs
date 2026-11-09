using System.Windows;
using OverTranslate.Services;

namespace OverTranslate.Views.ClipboardHistory;

public partial class SyncSetupWindow : Window
{
    private readonly Action<bool> _complete;
    internal MessageBoxResult Choice { get; private set; } = MessageBoxResult.Cancel;

    internal SyncSetupWindow(Action<bool> complete)
    {
        _complete = complete;
        InitializeComponent();
        Icon = AppIconService.CreateCompactIcon();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Complete(true);
    private void Decline_Click(object sender, RoutedEventArgs e) => Complete(false);
    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Complete(bool enabled)
    {
        StartPairingButton.IsEnabled = DeclineButton.IsEnabled = false;
        try
        {
            _complete(enabled);
            Choice = enabled ? MessageBoxResult.Yes : MessageBoxResult.No;
            Close();
        }
        catch (Exception)
        {
            ErrorText.Visibility = Visibility.Visible;
            StartPairingButton.IsEnabled = DeclineButton.IsEnabled = true;
        }
    }
}
