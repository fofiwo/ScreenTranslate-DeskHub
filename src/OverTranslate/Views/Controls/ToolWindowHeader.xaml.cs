using System.Windows;
using OverTranslate.Services;
using OverTranslate.Views.Shell;

namespace OverTranslate.Views.Controls;

public partial class ToolWindowHeader : System.Windows.Controls.UserControl
{
    private Window? _window;

    public ToolWindowHeader() => InitializeComponent();

    private void Header_Loaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null || window == _window) return;
        _window = window;
        window.Icon ??= AppIconService.CreateCompactIcon();
        WindowFrame.Attach(window);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is { } window) SystemCommands.MinimizeWindow(window);
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not { } window) return;
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
