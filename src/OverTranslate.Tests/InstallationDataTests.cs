using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed class InstallationDataTests
{
    [Fact]
    public void InstalledDataSurvivesReplacingTheApplicationVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "screen-translate-install-test-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "ScreenTranslate", "current");
        try
        {
            Directory.CreateDirectory(app);
            var data = AppDataPaths.ResolveRoot(app, root, installed: true);
            Directory.CreateDirectory(data);
            var marker = Path.Combine(data, "marker.txt");
            File.WriteAllText(marker, "preserved");
            Directory.Delete(app, recursive: true);
            Directory.CreateDirectory(app);
            Assert.Equal(data, AppDataPaths.ResolveRoot(app, root, installed: true));
            Assert.Equal("preserved", File.ReadAllText(marker));
            Assert.False(data.StartsWith(Path.Combine(root, "ScreenTranslate") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PortableDataStaysBesideItsExecutable()
    {
        var executableRoot = Path.Combine(Path.GetTempPath(), "ScreenTranslate-portable");
        Assert.Equal(Path.Combine(executableRoot, "data"),
            AppDataPaths.ResolveRoot(executableRoot, Path.GetTempPath(), installed: false));
    }
}
