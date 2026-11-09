using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

public static class AppIconService
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = GetStream("icons.app-compact.ico");
        return new System.Drawing.Icon(stream, 32, 32);
    }

    public static ImageSource CreateMainIcon()
        => CreateImageSource();

    public static ImageSource CreateCompactIcon()
        => CreateImageSource();

    private static ImageSource CreateImageSource()
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri("pack://application:,,,/ScreenTranslate;component/icons/screentranslate-logo.png", UriKind.Absolute);
        src.EndInit();
        src.Freeze();
        return src;
    }

    private static System.IO.Stream GetStream(string relativeName)
    {
        var name = $"OverTranslate.{relativeName}";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
    }
}
