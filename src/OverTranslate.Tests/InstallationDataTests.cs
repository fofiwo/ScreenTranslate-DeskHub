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
            Assert.Equal(Path.Combine(root, "ScreenTranslate", "data"), data);
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

    [Fact]
    public void LocalPreviewAndDailyExecutableUseTheSameExplicitProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "profile-path-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var daily = Path.Combine(root, "app");
            var preview = Path.Combine(root, "artifacts", "preview");
            var profile = Path.Combine(root, "user", "ScreenTranslate-data");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "appsettings.json"),
                "{\"ClipboardHotkeyDisplay\":\"Alt+D\",\"LibraryHotkeyDisplay\":\"Alt+A\"}");
            foreach (var executable in new[] { daily, preview })
            {
                Directory.CreateDirectory(executable);
                File.WriteAllText(Path.Combine(executable, "data-path.txt"), profile);
                var data = AppDataPaths.ResolveRoot(executable, Path.Combine(root, "user"), installed: false);
                Assert.Equal(profile, data);
                var settings = SettingsService.Parse(File.ReadAllText(Path.Combine(data, "appsettings.json")));
                Assert.Equal("Alt+D", settings.ClipboardHotkeyDisplay);
                Assert.Equal("Alt+A", settings.LibraryHotkeyDisplay);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void InstalledProfileIgnoresPortableBindingAndBlankBindingCannotResetSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "profile-binding-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "data-path.txt"), "   ");
            Assert.Equal(Path.Combine(root, "data"),
                AppDataPaths.ResolveRoot(root, root, installed: true));
            Assert.Throws<InvalidDataException>(() => AppDataPaths.ResolveRoot(root, root, installed: false));
            File.WriteAllText(Path.Combine(root, "data-path.txt"), "../shared-data");
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "../shared-data")),
                AppDataPaths.ResolveRoot(root, root, installed: false));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
