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
    private static void VerifyLibrarySaveFeedback(string directory)
    {
        var path = System.IO.Path.Combine(directory, "save-feedback", "library.json");
        var store = new LibraryStore(path);
        store.SavePrompt(new() { Title = "原有条目", Content = "原有正文", Tags = ["旧标签"] });
        var window = new LibraryWindow(store, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        T Control<T>(string name) => (T)window.FindName(name);
        void Invoke(string method) => typeof(LibraryWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()]);
        void Save() => Control<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            window.Show(); window.UpdateLayout();
            Invoke("NewPrompt_Click");
            Control<TextBox>("PromptTitle").Text = "保存验收";
            Save();
            PumpUntil(() => Control<Button>("SaveButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>("EditorLayer").Visibility);
            Assert.Contains("保存失败", Control<TextBlock>("EditorStatus").Text);
            Assert.Contains("正文", Control<TextBlock>("EditorStatus").Text);
            Assert.Equal("保存验收", Control<TextBox>("PromptTitle").Text);
            Render(window, "library-save-validation", 880, 580);

            Control<TextBox>("PromptBody").Text = "需要原样保存的正文\n中文 😀";
            Control<TextBox>("PromptDescription").Text = "保留我的输入";
            using (var locked = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                Save();
                PumpUntil(() => Control<Button>("SaveButton").IsEnabled);
                Assert.Contains("保存失败", Control<TextBlock>("EditorStatus").Text);
                Assert.Equal(Visibility.Visible, Control<FrameworkElement>("EditorLayer").Visibility);
                Assert.Equal("保留我的输入", Control<TextBox>("PromptDescription").Text);
            }
            // 阻塞真实文件写锁，验证等待期间界面可响应且重复事件不会重复写入。
            using (var gate = (Mutex)typeof(LibraryStore).GetMethod("AcquireFileMutex", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, null)!)
            {
                try
                {
                    Save();
                    Assert.False(Control<Button>("SaveButton").IsEnabled);
                    Assert.Equal("保存中…", Control<Button>("SaveButton").Content);
                    Assert.False(Control<ScrollViewer>("EditorScroll").IsEnabled);
                    for (var i = 0; i < 5; i++) Save();
                    Invoke("CancelEditor_Click");
                    Assert.Equal(Visibility.Visible, Control<FrameworkElement>("EditorLayer").Visibility);
                    window.Close();
                    Assert.True(window.IsVisible);
                    Render(window, "library-saving", 880, 580);
                }
                finally { gate.ReleaseMutex(); }
            }
            PumpUntil(() => Control<FrameworkElement>("EditorLayer").Visibility == Visibility.Collapsed);
            var saved = store.Read().Prompts.Single(p => p.Title == "保存验收");
            Assert.Equal("需要原样保存的正文\n中文 😀", saved.Content);
            Assert.Equal(2, store.Read().Prompts.Count);
            Assert.Equal(saved.Id, ((LibraryPrompt)Control<ListBox>("PromptList").SelectedItem).Id);
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>("SaveNotice").Visibility);
            Assert.Contains("保存验收", Control<TextBlock>("SaveNoticeText").Text);
            Render(window, "library-saved", 880, 580);
            AssertInside((FrameworkElement)window.Content, Control<FrameworkElement>("SaveNotice"));
            Invoke("DismissSaveNotice_Click");
            Assert.Equal(Visibility.Collapsed, Control<FrameworkElement>("SaveNotice").Visibility);

            typeof(LibraryWindow).GetMethod("EditPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [saved]);
            Control<TextBox>("PromptDescription").Text = "修改后重新保存";
            Save();
            PumpUntil(() => Control<FrameworkElement>("EditorLayer").Visibility == Visibility.Collapsed);
            Assert.Equal(2, store.Read().Prompts.Count);
            Assert.Equal("修改后重新保存", store.Read().Prompts.Single(p => p.Id == saved.Id).Description);

            // 保存被当前搜索与标签排除的条目后，仍应能立即找到它。
            Control<TextBox>("PromptSearch").Text = "原有";
            Control<ListBox>("TagFilters").SelectedItem = "旧标签";
            Invoke("NewPrompt_Click");
            Control<TextBox>("PromptBody").Text = "新条目不匹配旧筛选";
            Save();
            PumpUntil(() => Control<FrameworkElement>("EditorLayer").Visibility == Visibility.Collapsed);
            Assert.Equal("新条目不匹配旧筛选", ((LibraryPrompt)Control<ListBox>("PromptList").SelectedItem).Content);
            Assert.Equal("", Control<TextBox>("PromptSearch").Text);
            Assert.Empty(Control<ListBox>("TagFilters").SelectedItems);

            // 模型资料不能漏掉成功/失败状态；AI 配置由独立设置页验收。
            Control<TabControl>("Tabs").SelectedIndex = 1;
            Invoke("NewModel_Click");
            Control<TextBox>("EndpointBox").Text = "http://localhost:1234/v1";
            Control<ComboBox>("PlatformBox").Text = "本地测试";
            Control<TextBox>("ModelBox").Text = "test-model";
            Save();
            PumpUntil(() => Control<FrameworkElement>("EditorLayer").Visibility == Visibility.Collapsed);
            Assert.Single(store.Read().Connections);
            Assert.Contains("连接资料", Control<TextBlock>("SaveNoticeText").Text);

        }
        finally { window.CloseForTests(); }
    }
}
