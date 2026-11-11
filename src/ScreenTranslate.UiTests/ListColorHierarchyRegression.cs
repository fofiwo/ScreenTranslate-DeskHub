using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Library;
using OverTranslate.Views.ClipboardHistory;
using OverTranslate.Views.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyListColorHierarchy(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "colors", "library.json"));
        for (var i = 0; i < 6; i++)
        {
            store.SavePrompt(new() { Title = $"日常工作提示词 {i + 1}", Description = "整理资料与翻译", Content = "请整理下面的内容，保留关键事实。" });
            store.SaveConnection(new() { Platform = $"模型连接 {i + 1}", Endpoint = "http://localhost/v1", Model = "example-model", Note = "仅用于界面验证" });
        }
        var history = new ClipboardHistoryStore(System.IO.Path.Combine(directory, "colors", "clipboard"));
        for (var i = 0; i < 6; i++) history.Add(new() { Text = $"剪贴板内容 {i + 1}\n这是一条用于检查条目层级的记录。", Source = "fixture.exe" });
        var library = new LibraryWindow(store, copy: _ => { });
        var clipboard = new ClipboardHistoryWindow(history, _ => Task.CompletedTask,
            systemHistory: new FixtureClipboardHistory(), settings: new AppSettings(), saveSettings: () => true,
            syncActivities: new(System.IO.Path.Combine(directory, "colors", "activity.json")));
        try
        {
            foreach (var window in new Window[] { library, clipboard })
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = window.Top = -10000;
                window.ShowActivated = false;
            }
            library.Show();
            clipboard.SetWindowPinned(true);
            clipboard.ShowPanel();
            PumpUntil(() => !clipboard.IsRefreshing);
            foreach (var theme in new[] { "Light", "Dark" })
            {
                ApplyTheme(System.Windows.Application.Current, theme);
                foreach (var minimum in new[] { false, true })
                {
                    typeof(LibraryWindow).GetMethod("SetQuickAccess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(library, [minimum]);
                    foreach (var tab in new[] { 0, 1 })
                    {
                        ((TabControl)library.FindName("Tabs")).SelectedIndex = tab;
                        library.UpdateLayout();
                        VerifyListSurfaces((ListBox)library.FindName(tab == 0 ? "PromptList" : "ModelList"));
                        Render(library, $"colors-{theme}-{(tab == 0 ? "prompts" : "models")}-{minimum}", minimum ? 880 : 1120, minimum ? 580 : 760);
                    }
                    clipboard.Width = minimum ? 460 : 560;
                    clipboard.Height = minimum ? 400 : 700;
                    clipboard.UpdateLayout();
                    VerifyListSurfaces((ListBox)clipboard.FindName("HistoryList"));
                    Render(clipboard, $"colors-{theme}-clipboard-{minimum}", (int)clipboard.Width, (int)clipboard.Height);
                }
            }
        }
        finally { library.CloseForTests(); clipboard.Close(); ApplyTheme(System.Windows.Application.Current, "Light"); }
    }

    private static void VerifyListSurfaces(ListBox list)
    {
        list.SelectedIndex = 0;
        list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Border Surface(ListBoxItem item) => Visuals<Border>(item).First(b => b.Name is "Root" or "Card" or "RowSurface");
        Color Fill(ListBoxItem item) => ((SolidColorBrush)Surface(item).Background).Color;
        var normal = Fill(row);
        var chosen = Fill(selected);
        Assert.True(normal.A == 255, "Ordinary list rows need an opaque surface distinct from the list tray.");
        Assert.True(Contrast(normal, chosen) >= 1.12, "Selected rows must remain distinguishable from ordinary rows without focus.");
        Assert.True(Contrast(normal, ((SolidColorBrush)list.Background).Color) >= 1.08, "Rows must separate from the list tray.");
        foreach (var item in new[] { row, selected })
        foreach (var text in Visuals<TextBlock>(item).Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)))
            Assert.True(Contrast(((SolidColorBrush)text.Foreground).Color, TextBackground(text)) >= 4.5, $"List text must remain readable: {text.Text}");
        Assert.True(Contrast(((SolidColorBrush)Surface(selected).BorderBrush).Color, chosen) >= 3,
            "The selected outline must provide a clear non-fill cue.");
    }

    private static Color TextBackground(DependencyObject visual)
    {
        for (var parent = visual; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            var brush = parent switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A == 255) return solid.Color;
        }
        throw new InvalidOperationException("List text needs an opaque backing surface.");
    }
}
