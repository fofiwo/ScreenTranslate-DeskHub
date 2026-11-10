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
    private sealed class SummaryFeedbackHandler : System.Net.Http.HttpMessageHandler
    {
        public TaskCompletionSource<System.Net.Http.HttpResponseMessage> Response { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
            => Response.Task.WaitAsync(token);
        public void Complete(bool success) => Response.SetResult(new(success ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.ServiceUnavailable)
        {
            Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "{\"title\":\"整理后的标题\",\"description\":\"整理后的描述\",\"tags\":[\"测试\"]}" } } }
            }))
        });
    }

    private static void VerifyLibraryAiFeedback(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "ai-feedback", "library.json"));
        store.Update(s => { s.Ai.AutoSummarize = true; s.Ai.Model = "fixture"; s.Ai.Endpoint = "http://localhost:1234/v1"; });
        var handler = new SummaryFeedbackHandler();
        var window = new LibraryWindow(store, new LibraryHttp(handler), _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        T Control<T>(string name) => (T)window.FindName(name);
        void Invoke(string method) => typeof(LibraryWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()]);
        void StartSave(string title)
        {
            Invoke("NewPrompt_Click");
            Control<TextBox>("PromptTitle").Text = title;
            Control<TextBox>("PromptBody").Text = "测试正文保持原样";
            Control<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<FrameworkElement>("EditorLayer").Visibility == Visibility.Collapsed);
        }
        bool TimerEnabled() => ((System.Windows.Threading.DispatcherTimer)typeof(LibraryWindow)
            .GetField("_saveNoticeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).IsEnabled;
        try
        {
            window.Show(); window.UpdateLayout();
            StartSave("自动整理验收");
            Assert.Contains("正在", Control<TextBlock>("SaveNoticeText").Text);
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>("SaveNoticeSpinner").Visibility);
            Assert.False(TimerEnabled());
            var rotation = (System.Windows.Media.RotateTransform)Control<FrameworkElement>("SaveNoticeArc").RenderTransform;
            Assert.True(rotation.HasAnimatedProperties);
            var angle = rotation.Angle;
            PumpUntil(() => Math.Abs(rotation.Angle - angle) > 5);
            Render(window, "library-ai-processing", 880, 580);
            handler.Complete(true);
            PumpUntil(() => Control<TextBlock>("SaveNoticeText").Text.Contains("完成"));
            Assert.Equal(Visibility.Collapsed, Control<FrameworkElement>("SaveNoticeSpinner").Visibility);
            Assert.False(rotation.HasAnimatedProperties);
            Assert.True(TimerEnabled());
            Assert.Equal("测试正文保持原样", store.Read().Prompts.Single().Content);
            Render(window, "library-ai-completed", 880, 580);

            handler.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StartSave("失败验收");
            handler.Complete(false);
            PumpUntil(() => Control<TextBlock>("SaveNoticeText").Text.Contains("AI 整理失败"));
            Assert.Contains("已保存", Control<TextBlock>("SaveNoticeText").Text);
            Assert.False(TimerEnabled());
            Assert.Equal(Visibility.Collapsed, Control<FrameworkElement>("SaveNoticeSpinner").Visibility);
            Render(window, "library-ai-failed", 880, 580);

            handler.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StartSave("关闭提示验收");
            Invoke("DismissSaveNotice_Click");
            PumpUntil(() => !rotation.HasAnimatedProperties);
            handler.Complete(true);
            PumpUntil(() => store.Read().Prompts.Count(p => p.Title == "整理后的标题") == 2);
            Assert.Equal(Visibility.Collapsed, Control<FrameworkElement>("SaveNotice").Visibility);

            // 较早的请求失败，不能覆盖较新任务的进度。
            handler.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StartSave("较早任务");
            var previous = handler.Response;
            handler.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StartSave("较新任务");
            previous.SetResult(new(System.Net.HttpStatusCode.ServiceUnavailable));
            PumpUntil(() => ((HashSet<string>)typeof(LibraryWindow).GetField("_summarizing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Count == 1);
            Assert.Contains("较新任务", Control<TextBlock>("SaveNoticeText").Text);
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>("SaveNoticeSpinner").Visibility);
            handler.Complete(true);
            PumpUntil(() => Control<TextBlock>("SaveNoticeText").Text.Contains("整理完成"));

            ApplyTheme(System.Windows.Application.Current, "Dark");
            try
            {
                // 手动整理也使用相同状态提示；取消或超时不能留在旋转状态。
                handler.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var manual = (Task)typeof(LibraryWindow).GetMethod("Summarize", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [store.Read().Prompts.First(), false])!;
                Assert.Equal(Visibility.Visible, Control<FrameworkElement>("SaveNoticeSpinner").Visibility);
                Assert.Equal(((System.Windows.Media.SolidColorBrush)window.FindResource("AppSuccess")).Color,
                    ((System.Windows.Media.SolidColorBrush)Control<System.Windows.Shapes.Path>("SaveNoticeArc").Stroke).Color);
                Render(window, "library-ai-processing-dark", 880, 580);
                handler.Response.SetCanceled();
                PumpUntil(() => manual.IsCompleted);
                manual.GetAwaiter().GetResult();
                Assert.Contains("超时", Control<TextBlock>("SaveNoticeText").Text);
                Assert.Equal(Visibility.Collapsed, Control<FrameworkElement>("SaveNoticeSpinner").Visibility);
                Assert.Equal(((System.Windows.Media.SolidColorBrush)window.FindResource("AppError")).Color,
                    ((System.Windows.Media.SolidColorBrush)Control<TextBlock>("SaveNoticeIcon").Foreground).Color);
                Render(window, "library-ai-failed-dark", 880, 580);
            }
            finally { ApplyTheme(System.Windows.Application.Current, "Light"); }
        }
        finally { window.CloseForTests(); }
    }

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
