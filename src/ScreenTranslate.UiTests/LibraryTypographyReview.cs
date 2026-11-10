using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyLibraryTypography(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "typography", "library.json"));
        const string title = "跨语言字体与阅读层级 · WPF UI";
        const string finalLine = "最后一行：中文 English 日本語 한국어 😀 gypÁÉ";
        var body = "第一行中文与 English gypÁÉ。\n第二行用于检查实际行距。\n"
            + string.Concat(Enumerable.Repeat("多行正文需要完整保留，不能因字体或行距调整失去内容。\n", 35)) + finalLine;
        store.SavePrompt(new() { Title = title, Description = "摘要应该比正文安静，标题应当一眼可辨。", Content = body, Tags = ["阅读", "样式检查"] });
        var note = string.Concat(Enumerable.Repeat("长备注需要能够滚动查看，正文与按钮不应超出窗口。\n", 20)) + finalLine;
        store.SaveConnection(new() { Platform = "字体与模型资料", Endpoint = "http://localhost/" + new string('a', 260),
            Model = new string('m', 230), Note = note, RemainingBalance = "手动记录" }, fetchedModelIds: [new string('m', 230)]);
        var copied = new List<string>();
        var globalFont = System.Windows.Application.Current.Resources["AppFont"];
        var window = new LibraryWindow(store, copy: copied.Add)
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        var samples = new List<object>();
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            var tabs = (TabControl)window.FindName("Tabs");
            foreach (var width in new[] { 1120, 880 })
            {
                var height = width == 880 ? 580 : 760;
                tabs.SelectedIndex = 0;
                Render(window, $"type-review-prompts-{width}", width, height);
                var preview = (TextBox)window.FindName("PromptPreview");
                preview.ScrollToHome();
                CaptureTypographyViewport(window, $"type-review-prompts-{width}");
                var detail = (FrameworkElement)window.FindName("PromptDetail");
                var heading = Visuals<TextBlock>(detail).Single(t => t.Text == title);
                var row = (ListBoxItem)((ListBox)window.FindName("PromptList")).ItemContainerGenerator.ContainerFromIndex(0);
                var summary = Visuals<TextBlock>(row).Single(t => t.Text.StartsWith("第一行中文"));
                Assert.True(heading.FontSize > preview.FontSize && preview.FontSize > summary.FontSize,
                    "The rendered title, body and summary must have distinct reading roles.");
                var first = preview.GetRectFromCharacterIndex(0);
                var second = preview.GetRectFromCharacterIndex(body.IndexOf('\n') + 1);
                Assert.True(second.Y - first.Y >= 24, "The actual body line spacing must remain comfortable for Chinese text.");
                var fonts = RenderedFonts(preview);
                Assert.Contains(fonts, name => name.Contains("YaHei", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(fonts, name => name.Contains("SimSun", StringComparison.OrdinalIgnoreCase));
                Assert.True(Contrast(((SolidColorBrush)preview.Foreground).Color, ((SolidColorBrush)preview.Background).Color) >= 4.5);
                AssertInside(root, preview);
                Assert.True(preview.ActualHeight >= 70, "The smallest window must keep several body lines visible.");
                preview.ScrollToEnd();
                window.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.InRange(preview.GetRectFromCharacterIndex(body.Length - 1).Bottom, 0, preview.ActualHeight);
                CaptureTypographyViewport(window, $"type-review-prompts-end-{width}");
                var endFonts = RenderedFonts(preview);
                preview.SelectAll();
                Assert.Equal(body, preview.SelectedText);
                var copy = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
                copy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => !window.IsCopyPending);
                Assert.Equal(body, copied.Last());
                AssertInside(root, copy);
                samples.Add(new { width, height, heading = heading.FontSize, body = preview.FontSize,
                    summary = summary.FontSize, lineSpacing = second.Y - first.Y, fonts, endFonts, previewHeight = preview.ActualHeight });

                tabs.SelectedIndex = 1;
                Render(window, $"type-review-models-{width}", width, height);
                var scroll = (ScrollViewer)window.FindName("ModelDetailScroll");
                scroll.ScrollToHome();
                CaptureTypographyViewport(window, $"type-review-models-{width}");
                var noteView = Visuals<TextBlock>(scroll).Single(t => t.Text == note);
                scroll.ScrollToEnd();
                window.UpdateLayout();
                Assert.True(scroll.ViewportHeight >= 60, "The model detail must retain a usable scroll viewport.");
                Assert.True(scroll.VerticalOffset >= scroll.ScrollableHeight - 1);
                var bottom = noteView.TransformToAncestor(scroll).Transform(new Point(0, noteView.ActualHeight));
                Assert.InRange(bottom.Y, 0, scroll.ActualHeight + 1);
                AssertInside(root, (FrameworkElement)window.FindName("ModelCatalog"));
                var modelRoot = (FrameworkElement)window.FindName("ModelDetail");
                foreach (var button in Visuals<System.Windows.Controls.Button>(modelRoot).Where(b => Equals(b.Content, "编辑") || Equals(b.Content, "删除")))
                    AssertInside(root, button);
                CaptureTypographyViewport(window, $"type-review-models-end-{width}");
            }
            var crowded = store.SavePrompt(new()
            {
                Title = string.Concat(Enumerable.Repeat("两行长标题需要完整保留并能查看", 5)),
                Description = string.Concat(Enumerable.Repeat("较长摘要用于检查窄窗口中的阅读区域。", 12)),
                Content = body,
                Tags = Enumerable.Range(1, 12).Select(i => $"第{i}个用于检查换行的长标签").ToList()
            });
            ReloadLibrary(window);
            tabs.SelectedIndex = 0;
            ((ListBox)window.FindName("PromptList")).SelectedItem = ((ListBox)window.FindName("PromptList")).Items.Cast<LibraryPrompt>().Single(p => p.Id == crowded.Id);
            Render(window, "type-review-crowded-prompts-minimum", 880, 580);
            CaptureTypographyViewport(window, "type-review-crowded-prompts-minimum");
            var crowdedPreview = (TextBox)window.FindName("PromptPreview");
            Assert.True(crowdedPreview.ActualHeight >= 70,
                $"Long title, summary and twelve tags must leave a readable body viewport; actual height: {crowdedPreview.ActualHeight}.");
            crowdedPreview.ScrollToEnd(); window.UpdateLayout();
            Assert.InRange(crowdedPreview.GetRectFromCharacterIndex(body.Length - 1).Bottom, 0, crowdedPreview.ActualHeight);
            var another = store.SavePrompt(new() { Title = crowded.Title + "另一条", Description = crowded.Description, Content = body, Tags = crowded.Tags });
            ReloadLibrary(window);
            var prompts = (ListBox)window.FindName("PromptList");
            prompts.SelectedItem = prompts.Items.Cast<LibraryPrompt>().Single(p => p.Id == crowded.Id);
            window.UpdateLayout();
            var header = (ScrollViewer)window.FindName("PromptHeader");
            header.ScrollToEnd(); window.UpdateLayout();
            Assert.True(header.VerticalOffset > 0);
            var lastTag = Visuals<TextBlock>(header).Single(t => t.Text == crowded.Tags.Last());
            Assert.InRange(lastTag.TransformToAncestor(header).Transform(new Point(0, lastTag.ActualHeight)).Y, 0, header.ActualHeight + 1);
            prompts.SelectedItem = prompts.Items.Cast<LibraryPrompt>().Single(p => p.Id == another.Id);
            window.UpdateLayout();
            Assert.Equal(0, header.VerticalOffset);
            Assert.Same(globalFont, System.Windows.Application.Current.Resources["AppFont"]);
            samples.Add(new { crowdedPreviewHeight = crowdedPreview.ActualHeight, headerHeight = header.ActualHeight });
            ((System.Windows.Controls.Button)Visuals<System.Windows.Controls.Button>(root).Single(b => Equals(b.Content, "快捷键")))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Render(window, "type-review-crowded-shortcut-minimum", 880, 580);
            CaptureTypographyViewport(window, "type-review-crowded-shortcut-minimum");
            Assert.True(crowdedPreview.ActualHeight >= 70, "Opening shortcut settings must not erase the body viewport.");
            Assert.True(header.ActualHeight >= 30, $"The compact header must show at least one complete title line; actual height: {header.ActualHeight}.");
            AssertInside(root, (FrameworkElement)window.FindName("PromptCopyButton"));
            samples.Add(new { shortcutPanelPreviewHeight = crowdedPreview.ActualHeight, shortcutPanelHeaderHeight = header.ActualHeight });
            var evidence = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-evidence", "typography-measurements.json");
            System.IO.File.WriteAllText(evidence, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.CloseForTests(); }
    }

    private static string[] RenderedFonts(Visual root)
    {
        static IEnumerable<string> Families(Drawing drawing)
        {
            if (drawing is GlyphRunDrawing glyph) return glyph.GlyphRun.GlyphTypeface.FamilyNames.Values;
            return drawing is DrawingGroup group ? group.Children.SelectMany(Families) : [];
        }
        return Visuals<Visual>(root).Prepend(root).Select(VisualTreeHelper.GetDrawing)
            .Where(d => d is not null).SelectMany(d => Families(d!)).Distinct().ToArray();
    }

    private static void CaptureTypographyViewport(Window window, string name)
    {
        // 保存滚动后的当前视口；重新测量窗口会再次改变 TextBox 的滚动布局。
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "ui-evidence", name + ".png"));
        encoder.Save(output);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { var c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
