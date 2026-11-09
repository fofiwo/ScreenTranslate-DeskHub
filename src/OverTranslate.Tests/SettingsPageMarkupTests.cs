using System.Xml.Linq;
using Xunit;
namespace OverTranslate.Tests;
public class SettingsPageMarkupTests
{
    [Fact]
    public void OptionsKeepOnlyConfirmedSettingsAndAdvancedDiagnostics()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Settings", "SettingsPage.xaml"));
        var names = page.Descendants().Select(e => (string?)e.Attribute(x + "Name")).Where(n => n is not null).ToArray();
        foreach (var required in new[] { "PreserveFormattingCheckBox", "AllowFallbackCheckBox", "AutoTranslateCheckBox", "StartupCheckBox", "ExportDiagnosticsBtn" }) Assert.Contains(required, names);
        foreach (var removed in new[] { "DarkThemeRadio", "UiLanguageBox", "QuickLookupHotkeyBox", "QuickTranslateHotkeyBox", "SaveScreenshotCheckBox", "OcrLineBoxesCheckBox" }) Assert.DoesNotContain(removed, names);
    }
}
