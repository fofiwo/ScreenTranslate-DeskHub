using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyLibraryConnectionEditor(string directory)
    {
        var path = System.IO.Path.Combine(directory, "connection-editor", "library.json");
        var store = new LibraryStore(path);
        using var http = new LibraryHttp(new CatalogHandler());
        var window = new LibraryWindow(store, http, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        T Control<T>(string name) => (T)window.FindName(name);
        void Invoke(string method, params object[] args) => typeof(LibraryWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        void NewConnection()
        {
            Invoke("NewModel_Click", window, new RoutedEventArgs());
            Control<ComboBox>("PlatformBox").Text = "本地测试";
            Control<TextBox>("EndpointBox").Text = "http://localhost:1234/v1";
        }
        void Save()
        {
            Control<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<Button>("SaveButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Control<FrameworkElement>("EditorLayer").Visibility);
        }
        var alignmentErrors = new List<string>();
        void AssertCentered(TextBox box, string label)
        {
            foreach (var sample in new[] { "", "中文 Abc 123" })
            {
                box.Text = sample;
                window.UpdateLayout();
                foreach (var index in new[] { 0, box.Text.Length })
                {
                    var caret = box.GetRectFromCharacterIndex(index);
                    var offset = caret.Top + caret.Height / 2 - box.ActualHeight / 2;
                    if (caret.IsEmpty || Math.Abs(offset) > 1.5)
                        alignmentErrors.Add($"{label}: caret {index}, offset {offset:F2}, height {box.ActualHeight:F2}");
                }
            }
        }
        try
        {
            window.Show();
            Control<TabControl>("Tabs").SelectedIndex = 1;
            NewConnection();
            foreach (var (width, height) in new[] { (1120, 760), (880, 580) })
            {
                Render(window, $"connection-editor-layout-{width}", width, height);
                foreach (var name in new[] { "EndpointBox", "ModelBox", "BalanceBox" })
                    AssertCentered(Control<TextBox>(name), name);
                var platform = Control<ComboBox>("PlatformBox");
                AssertCentered((TextBox)platform.Template.FindName("PART_EditableTextBox", platform), "平台");
                var key = Control<PasswordBox>("ModelKey");
                key.Password = "layout-sample";
                window.UpdateLayout();
                var view = Visuals<FrameworkElement>(key).Single(v => v.GetType().Name == "TextBoxView");
                var midpoint = view.TransformToAncestor(key).Transform(new Point(0, view.ActualHeight / 2));
                if (Math.Abs(midpoint.Y - key.ActualHeight / 2) > 1.5)
                    alignmentErrors.Add($"API Key: content offset {midpoint.Y - key.ActualHeight / 2:F2}");
                key.Clear();
            }
            Assert.True(alignmentErrors.Count == 0, string.Join(Environment.NewLine, alignmentErrors));

            // 未获取列表、未填固定 ID 也必须能保存，并从磁盘重新打开。
            NewConnection();
            Control<TextBox>("ModelBox").Text = "  ";
            Save();
            var saved = new LibraryStore(path).Read().Connections.Single();
            Assert.Empty(saved.Model);
            Assert.Empty(saved.ModelIds);
            Assert.Contains("连接资料已保存", Control<TextBlock>("SaveNoticeText").Text);

            // 获取目录但不选择固定 ID 时，目录仍须随连接保存。
            Invoke("EditModel", saved);
            Control<Button>("EditorFetchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<Button>("EditorFetchButton").IsEnabled);
            Assert.Equal(2, Control<ComboBox>("EditorModelIds").Items.Count);
            Assert.Empty(Control<TextBox>("ModelBox").Text);
            Save();
            saved = new LibraryStore(path).Read().Connections.Single();
            Assert.Empty(saved.Model);
            Assert.Equal(new[] { "fresh-a", "fresh-b" }, saved.ModelIds);

            // 保持已有选择与手填行为，编辑时也能清空固定 ID。
            Invoke("EditModel", saved);
            Control<ComboBox>("EditorModelIds").SelectedIndex = 1;
            Assert.Equal("fresh-b", Control<TextBox>("ModelBox").Text);
            Save();
            saved = new LibraryStore(path).Read().Connections.Single();
            Assert.Equal("fresh-b", saved.Model);
            Invoke("EditModel", saved);
            Control<TextBox>("ModelBox").Clear();
            Save();
            Assert.Empty(new LibraryStore(path).Read().Connections.Single().Model);
        }
        finally { window.CloseForTests(); }
    }
}
