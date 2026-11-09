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

    /// <summary>
    /// Both 快速工具 rows are switched off for the length of a realtime session, and their tooltips
    /// are then the only place that reason exists — the rail has no room for a note beside them.
    /// WPF withholds tooltips from disabled controls unless asked.
    /// </summary>
    [Theory]
    [InlineData("CaptureBtn")]
    [InlineData("OcrExtractBtn")]
    public void A_quick_tool_still_explains_itself_while_disabled(string name)
    {
        Assert.Equal("True", (string?)Row(name).Attribute("ToolTipService.ShowOnDisabled"));
    }

    /// <summary>
    /// 名称与快捷键分别占一行；名称允许省略，快捷键的完整可见性由实际布局测试验证。
    /// </summary>
    [Theory]
    [InlineData("CaptureBtn", "CaptureHotkeyText")]
    [InlineData("OcrExtractBtn", "OcrHotkeyText")]
    public void A_quick_tool_stacks_its_name_above_its_untrimmed_shortcut(string row, string hotkey)
    {
        var panel = Row(row).Elements().Single();
        Assert.Equal("StackPanel", panel.Name.LocalName);
        Assert.Equal("Vertical", (string?)panel.Attribute("Orientation") ?? "Vertical");
        var children = panel.Elements().ToList();
        var shortcutAt = children.FindIndex(e => e.DescendantsAndSelf()
            .Any(child => (string?)child.Attribute(X + "Name") == hotkey));
        var labelAt = children.FindIndex(e => (string?)e.Attribute("TextTrimming") is not null);
        Assert.True(labelAt >= 0 && shortcutAt > labelAt);
        Assert.Equal("CharacterEllipsis", (string?)children[labelAt].Attribute("TextTrimming"));
        var shortcut = panel.Descendants().Single(e => (string?)e.Attribute(X + "Name") == hotkey);
        Assert.Equal("None", (string?)shortcut.Attribute("TextTrimming") ?? "None");
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
