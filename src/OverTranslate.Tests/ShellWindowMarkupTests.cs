using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The facts about the shell's nav rail markup that read like decoration and are not.
/// </summary>
public class ShellWindowMarkupTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Window() => XDocument.Load(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "Shell", "ShellWindow.xaml"));

    private static XElement Row(string name) => Window()
        .Descendants()
        .Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void App_prompts_do_not_open_native_message_boxes()
    {
        var calls = Directory.EnumerateFiles(StringsParityTests.ProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), @"\bMessageBox\.Show\s*\("))
            .Select(Path.GetFileName).ToArray();
        Assert.Empty(calls);
    }

    [Fact]
    public void App_windows_do_not_fall_back_to_system_title_bars()
    {
        var project = StringsParityTests.ProjectDirectory();
        var style = XDocument.Load(Path.Combine(project, "Themes", "ProductStyles.xaml"))
            .Descendants().Single(e => (string?)e.Attribute(X + "Key") == "ProductToolWindow");
        Assert.Contains(style.Elements(), e => (string?)e.Attribute("Property") == "WindowStyle"
            && (string?)e.Attribute("Value") == "None");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(project, "Views"), "*.xaml", SearchOption.AllDirectories))
        {
            var root = XDocument.Load(path).Root!;
            if (root.Name.LocalName != "Window") continue;
            var explicitStyle = (string?)root.Attribute("WindowStyle");
            Assert.True(explicitStyle is not null ? explicitStyle == "None"
                : (string?)root.Attribute("Style") == "{StaticResource ProductToolWindow}",
                $"{Path.GetFileName(path)} must use the application's window chrome.");
        }
    }

    /// <summary>
    /// Both 快速工具 rows are switched off for the length of a realtime session, and their tooltips
    /// are then the only place that reason exists — the rail has no room for a note beside them.
    /// WPF withholds tooltips from disabled controls unless asked.
    /// </summary>
    [Theory]
    [InlineData("ScreenshotBtn")]
    [InlineData("OcrExtractBtn")]
    public void A_quick_tool_still_explains_itself_while_disabled(string name)
    {
        Assert.Equal("True", (string?)Row(name).Attribute("ToolTipService.ShowOnDisabled"));
    }

    /// <summary>快捷操作单行排列；长快捷键允许省略，但完整内容必须可通过提示查看。</summary>
    [Theory]
    [InlineData("ScreenshotBtn", "ScreenshotHotkeyText")]
    [InlineData("OcrExtractBtn", "OcrHotkeyText")]
    [InlineData("PinImageBtn", "PinImageHotkeyText")]
    public void A_quick_tool_keeps_its_name_beside_its_shortcut(string row, string hotkey)
    {
        var panel = Row(row).Elements().Single();
        Assert.Equal("Grid", panel.Name.LocalName);
        var shortcut = panel.Descendants().Single(e => (string?)e.Attribute(X + "Name") == hotkey);
        Assert.Equal("1", (string?)shortcut.Attribute("Grid.Column"));
        var label = panel.Elements().Single(e => (string?)e.Attribute("TextTrimming") is not null);
        Assert.Equal("CharacterEllipsis", (string?)label.Attribute("TextTrimming"));
        var style = Window().Descendants().Single(e => (string?)e.Attribute(X + "Key") == "QuickToolHotkeyText");
        Assert.Contains(style.Elements(), e => (string?)e.Attribute("Property") == "ToolTip"
            && ((string?)e.Attribute("Value"))?.Contains("Binding Text") == true);
    }

    /// <summary>
    /// The drag area and the strip the user sees have to be the same strip: WindowChrome's caption
    /// height is what makes the title bar draggable, and it knows nothing about the row the markup
    /// draws there.
    /// </summary>
    [Fact]
    public void The_draggable_caption_covers_exactly_the_title_row()
    {
        var doc = Window();

        var chrome = doc.Descendants().Single(e => e.Name.LocalName == "WindowChrome");
        var titleRow = doc.Descendants()
            .First(e => e.Name.LocalName == "RowDefinition");

        Assert.Equal((string?)titleRow.Attribute("Height"), (string?)chrome.Attribute("CaptionHeight"));
    }

    /// <summary>
    /// Everything inside the caption strip is drag surface unless it says otherwise, so without
    /// this the window's own buttons would move the window instead of pressing.
    /// </summary>
    [Fact]
    public void The_caption_buttons_are_clickable_rather_than_drag_surface()
    {
        var buttons = Row("MinimizeBtn").Parent!;

        Assert.Equal("True", (string?)buttons.Attribute(
            XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation") + "IsHitTestVisibleInChrome")
            ?? (string?)buttons.Attribute("WindowChrome.IsHitTestVisibleInChrome"));
    }

    /// <summary>
    /// The rail can be put away entirely, and this button is the only way back — so it has to live
    /// outside the rail. Anywhere inside it would go away with it and strand a collapsed rail.
    /// </summary>
    [Fact]
    public void The_rail_toggle_lives_outside_the_rail()
    {
        var ancestors = Row("SidebarToggleBtn").Ancestors()
            .Select(e => (string?)e.Attribute(X + "Name"));

        Assert.DoesNotContain("RailPanel", ancestors);
    }

    /// <summary>
    /// The toggle sits in the caption strip, which is drag surface unless a control says otherwise
    /// — without this it would move the window instead of putting the rail away.
    /// </summary>
    [Fact]
    public void The_rail_toggle_is_clickable_rather_than_drag_surface()
    {
        var toggle = Row("SidebarToggleBtn");

        Assert.Equal("True", (string?)toggle.Attribute(
            XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation") + "IsHitTestVisibleInChrome")
            ?? (string?)toggle.Attribute("WindowChrome.IsHitTestVisibleInChrome"));
    }

    /// <summary>
    /// The rail is put away by animating its column to 0, and a column with a MinWidth can never be
    /// taken there — not even from code.
    /// </summary>
    [Fact]
    public void The_rail_column_can_be_taken_to_nothing()
    {
        Assert.Equal("0", (string?)Row("SidebarColumn").Attribute("MinWidth"));
    }
}
