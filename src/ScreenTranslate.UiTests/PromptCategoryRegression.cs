using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private sealed class CategoryHandler : System.Net.Http.HttpMessageHandler
    {
        public bool Fail { get; set; }
        public string CategoryName { get; set; } = "视觉还原";
        public int Requests { get; private set; }
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Fail) return new(System.Net.HttpStatusCode.ServiceUnavailable);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var data = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var assignments = data.RootElement.GetProperty("prompts").EnumerateArray().Select(p => new
            {
                id = p.GetProperty("id").GetString(), category = CategoryName, reason = "有指定参考图，目标是还原视觉效果",
                tags = new[] { "截图对比" }, title = "对照参考图检查界面差异", description = "截图对照指定参考图，检查并修正界面差异。"
            }).ToArray();
            var answer = JsonSerializer.Serialize(new { categories = new[] { new { name = CategoryName, description = "对照指定参考图还原界面；开放式设计归界面设计。" } }, assignments });
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } })) };
        }
    }

    private static void VerifyPromptCategories(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "categories", "library.json"));
        store.Update(s => { s.Ai.Model = "fixture"; s.Ai.Endpoint = "http://localhost/v1"; });
        var review = store.SavePrompt(new() { Title = "代码回归审查", Description = "检查实现正确性", Content = "审查代码", Category = "代码审查", CategoryDescription = "检查实现正确性与回归", Tags = ["回归验证"] });
        store.SavePrompt(new() { Title = "截图迭代对比", Description = "对照参考图检查界面差异", Content = "对照参考图", Category = "视觉还原", CategoryDescription = "对照参考图还原视觉效果", Tags = ["截图对比"] });
        for (var i = 0; i < 6; i++) store.SavePrompt(new() { Title = "示例分类 " + i, Content = "fixture", Category = "领域任务" + i, CategoryDescription = "独立任务示例" });
        var copies = 0;
        var handler = new CategoryHandler();
        var window = new LibraryWindow(store, new LibraryHttp(handler), _ => copies++)
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        T Control<T>(string name) => (T)window.FindName(name);
        void Invoke(string method) => typeof(LibraryWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
        try
        {
            window.Show(); window.UpdateLayout();
            var tabs = Control<ListBox>("CategoryTabs");
            Assert.Equal(Visibility.Visible, Control<Button>("MoreCategoriesButton").Visibility);
            Control<Button>("MoreCategoriesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Control<Button>("MoreCategoriesButton").ContextMenu;
            menu.Items.Cast<MenuItem>().Single(c => c.Header.ToString()!.StartsWith("视觉还原")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Assert.Single(Control<ListBox>("PromptList").Items);
            Assert.Contains("截图对比", Control<ListBox>("TagFilters").Items.Cast<string>());
            Assert.DoesNotContain("回归验证", Control<ListBox>("TagFilters").Items.Cast<string>());
            Control<TextBox>("PromptSearch").Text = "代码";
            Assert.Empty(Control<ListBox>("PromptList").Items);
            Assert.Equal(Visibility.Visible, Control<Button>("SearchAllPrompts").Visibility);
            Control<Button>("SearchAllPrompts").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(Control<ListBox>("PromptList").Items);
            Control<TextBox>("PromptSearch").Clear();
            var list = Control<ListBox>("PromptList");
            list.SelectedItem = list.Items.Cast<LibraryPrompt>().Single(p => p.Id == review.Id);
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal(0, copies);
            Render(window, "prompt-categories-desktop", 1120, 760);
            Render(window, "prompt-categories-minimum", 880, 580);

            Invoke("NewPrompt_Click");
            Control<TextBox>("PromptBody").Text = "保存后对照参考截图检查差异";
            Control<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => store.Read().Prompts.Any(p => p.Title == "对照参考图检查界面差异"));
            PumpUntil(() => Control<TextBlock>("SaveNoticeText").Text.Contains("整理完成"));
            Assert.Equal(1, handler.Requests);
            var created = store.Read().Prompts.Single(p => p.Title == "对照参考图检查界面差异");
            Assert.Equal("视觉还原", created.Category);
            Assert.Equal("保存后对照参考截图检查差异", created.Content);
            Assert.Equal(created.Id, ((LibraryPrompt)list.SelectedItem).Id);

            handler.Fail = true;
            Invoke("NewPrompt_Click");
            Control<TextBox>("PromptTitle").Text = "服务失败也要保存";
            Control<TextBox>("PromptBody").Text = "失败正文";
            Control<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<TextBlock>("SaveNoticeText").Text.Contains("AI 整理失败"));
            Assert.Equal("失败正文", store.Read().Prompts.Single(p => p.Title == "服务失败也要保存").Content);
            Assert.Contains("分类未完成", Control<TextBlock>("PromptCategoryStatus").Text);
            handler.Fail = false;

            handler.CategoryName = "图像检查";
            store.Update(s => s.Ai.AutoSummarize = false);
            Invoke("NewPrompt_Click");
            Control<TextBox>("PromptTitle").Text = "保留人工标题";
            Control<TextBox>("PromptBody").Text = "检查参考图";
            Control<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<TextBlock>("SaveNoticeText").Text.Contains("新分类待确认"));
            var suggested = store.Read().Prompts.Single(p => p.Title == "保留人工标题");
            Assert.Empty(suggested.Category);
            Assert.Equal("图像检查", suggested.SuggestedCategory);
            var suggestionButton = Control<Button>("SuggestedCategoryButton");
            Assert.Equal(Visibility.Visible, suggestionButton.Visibility);
            window.UpdateLayout();
            AssertInside(Control<FrameworkElement>("PromptDetail"), suggestionButton);
            Assert.True(Control<TextBox>("PromptPreview").ActualHeight >= 68, "最小窗口正文必须保留两行及内边距。");
            suggestionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>("SuggestedCategoryLayer").Visibility);
            Render(window, "prompt-categories-suggestion", 880, 580);
            AssertInside(Control<FrameworkElement>("SuggestedCategoryLayer"), Control<Button>("ApproveCategoryButton"));
            Control<TextBox>("SuggestedCategoryName").Text = "";
            Control<Button>("ApproveCategoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<TextBlock>("SuggestedCategoryStatus").Text.Contains("具体任务名称"));
            Assert.Equal("图像检查", store.Read().Prompts.Single(p => p.Id == suggested.Id).SuggestedCategory);
            Control<TextBox>("SuggestedCategoryName").Text = "图像检查";
            Control<Button>("ApproveCategoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => store.Read().Prompts.Single(p => p.Id == suggested.Id).Category == "图像检查");
            Assert.True(store.Read().Prompts.Single(p => p.Id == suggested.Id).CategoryManual);
            handler.CategoryName = "视觉还原";

            Invoke("ClassifyAll_Click");
            PumpUntil(() => Control<Button>("ApplyCategoryPlanButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>("CategoryPlanLayer").Visibility);
            Assert.NotEmpty(Control<ListBox>("CategoryPlanList").Items);
            Render(window, "prompt-categories-plan", 880, 580);
            Control<Button>("ApplyCategoryPlanButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<FrameworkElement>("CategoryPlanLayer").Visibility == Visibility.Collapsed);
            Assert.All(store.Read().Prompts.Where(p => p.Id != suggested.Id), p => Assert.Equal("视觉还原", p.Category));
            Assert.Equal("图像检查", store.Read().Prompts.Single(p => p.Id == suggested.Id).Category);
            ApplyTheme(System.Windows.Application.Current, "Dark");
            try { Render(window, "prompt-categories-dark", 880, 580); }
            finally { ApplyTheme(System.Windows.Application.Current, "Light"); }
            Assert.Equal(0, copies);
        }
        finally { window.CloseForTests(); }
    }
}
