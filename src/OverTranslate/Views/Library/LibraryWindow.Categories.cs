using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace OverTranslate.Views.Library;

public partial class LibraryWindow
{
    internal sealed record CategoryChoice(string? Value, string Label);
    internal sealed class ClassificationRow(LibraryPrompt expected, PromptClassification result)
    {
        public LibraryPrompt Expected { get; } = expected;
        public PromptClassification Result { get; } = result;
        public string SelectedCategory { get; set; } = result.Category;
        public List<CategoryChoice> Choices { get; set; } = [];
    }
    private string? _activeCategory;
    private bool _updatingCategory;
    private bool _categoryPlanRunning;
    private bool _applyingCategoryPlan;
    private LibraryPrompt? _categorySuggestion;
    private CancellationTokenSource? _categoryPlanCancellation;
    private readonly ObservableCollection<ClassificationRow> _classificationRows = [];

    private void RefreshCategoryTabs()
    {
        var catalog = PromptCategories.Catalog(_viewState.Prompts);
        if (_activeCategory is { Length: > 0 } && !catalog.Any(c => c.Name == _activeCategory)) _activeCategory = null;
        var visible = catalog.Take(5).ToList();
        if (_activeCategory is { Length: > 0 } && visible.All(c => c.Name != _activeCategory))
            visible[^1] = catalog.First(c => c.Name == _activeCategory);
        var counts = _viewState.Prompts.GroupBy(p => p.Category).ToDictionary(g => g.Key, g => g.Count());
        var choices = new List<CategoryChoice> { new(null, $"全部 {_viewState.Prompts.Count}") };
        choices.AddRange(visible.Select(c => new CategoryChoice(c.Name, $"{c.Name} {counts.GetValueOrDefault(c.Name)}")));
        choices.Add(new("", $"待分类 {counts.GetValueOrDefault("")}"));
        CategoryTabs.ItemsSource = choices;
        CategoryTabs.SelectedItem = choices.First(c => c.Value == _activeCategory);
        MoreCategoriesButton.Visibility = catalog.Count > 5 ? Visibility.Visible : Visibility.Collapsed;
        CategoryScope.Text = _activeCategory is null ? "全部任务 · 每条提示词一个主要分类，工具和方法作为细分标签。"
            : _activeCategory.Length == 0 ? "待分类 · 新分类待确认、服务失败或用途不明确的提示词。"
            : catalog.First(c => c.Name == _activeCategory).Description;
        PromptSearch.ToolTip = _activeCategory is null ? "搜索全部提示词的标题、描述、正文、标签和分类" : $"仅在“{(_activeCategory.Length == 0 ? "待分类" : _activeCategory)}”中搜索";
    }

    private void CategoryTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_ready || !ReferenceEquals(e.Source, CategoryTabs) || CategoryTabs.SelectedItem is not CategoryChoice choice) return;
        SelectCategory(choice.Value);
    }
    private void SelectCategory(string? category)
    {
        _activeCategory = category; _selectedTags.Clear(); _visiblePrompts = 30; RefreshPromptViews();
    }
    private void SearchAllPrompts_Click(object sender, RoutedEventArgs e) => SelectCategory(null);
    private void MoreCategories_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreCategoriesButton };
        MoreCategoriesButton.ContextMenu = menu;
        foreach (var category in PromptCategories.Catalog(_viewState.Prompts))
        {
            var item = new MenuItem { Header = $"{category.Name} · {_viewState.Prompts.Count(p => p.Category == category.Name)}", ToolTip = category.Description, IsCheckable = true, IsChecked = category.Name == _activeCategory };
            item.Click += (_, _) => SelectCategory(category.Name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void RefreshCategoryDetail()
    {
        if (!_ready) return;
        _updatingCategory = true;
        try
        {
            var prompt = PromptList.SelectedItem as LibraryPrompt;
            var choices = new List<CategoryChoice> { new("", "待分类") };
            choices.AddRange(PromptCategories.Catalog(_viewState.Prompts).Select(c => new CategoryChoice(c.Name, c.Name)));
            PromptCategoryBox.ItemsSource = choices;
            PromptCategoryBox.SelectedItem = choices.FirstOrDefault(c => c.Value == prompt?.Category);
            PromptCategoryBox.IsEnabled = prompt is not null && !_summarizing.Contains(prompt.Id);
            SuggestedCategoryButton.Visibility = prompt?.SuggestedCategory.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SuggestedCategoryButton.Content = $"查看建议：{prompt?.SuggestedCategory}";
            PromptCategoryStatus.Text = prompt is null ? "" : _summarizing.Contains(prompt.Id) ? "已保存，AI 正在整理与分类；仍可查看或复制。"
                : prompt.SuggestedCategory.Length > 0 ? "新分类待确认 · 查看建议并确认，或选择已有分类。"
                : prompt.CategoryNote.Length > 0 ? prompt.CategoryNote
                : prompt.CategoryManual ? "手动分类 · 自动分类将保留你的选择。"
                : prompt.Category.Length > 0 ? "AI 分类 · 可直接调整所属分类。" : "尚未分类。点击下方 AI 整理与分类；用途不明时可先编辑正文。";
        }
        finally { _updatingCategory = false; }
    }

    private async void PromptCategoryBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCategory || _loading || PromptList.SelectedItem is not LibraryPrompt prompt || PromptCategoryBox.SelectedItem is not CategoryChoice choice || choice.Value == prompt.Category) return;
        var name = choice.Value ?? "";
        var saved = await ChangeDataAsync(() => _store.SetPromptCategory(prompt, name));
        if (saved) { KeepPromptVisible(prompt.Id); Notice(name.Length > 0 ? $"已归入“{name}”，自动分类将保留人工选择。" : "已移至待分类。"); }
        RefreshCategoryDetail();
    }
    private void ReviewSuggestedCategory_Click(object sender, RoutedEventArgs e)
    {
        if (PromptList.SelectedItem is not LibraryPrompt { SuggestedCategory.Length: > 0 } prompt) return;
        DismissSaveNotice();
        _categorySuggestion = prompt;
        SuggestedCategoryLayer.DataContext = prompt;
        SuggestedCategoryName.Text = prompt.SuggestedCategory;
        SuggestedCategoryStatus.Text = "";
        SuggestedCategoryLayer.Visibility = Visibility.Visible;
        SuggestedCategoryName.Focus();
    }
    private void CloseSuggestedCategory_Click(object sender, RoutedEventArgs e)
    {
        SuggestedCategoryLayer.Visibility = Visibility.Collapsed;
        _categorySuggestion = null;
        SuggestedCategoryButton.Focus();
    }
    private async void ApproveCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_categorySuggestion is not { } prompt) return;
        var name = SuggestedCategoryName.Text;
        ApproveCategoryButton.IsEnabled = false;
        try
        {
            if (await ChangeDataAsync(() => _store.SetPromptCategory(prompt, name, approveSuggestion: true)))
            { CloseSuggestedCategory_Click(sender, e); KeepPromptVisible(prompt.Id); Notice($"已确认分类“{name.Trim()}”。后续同类提示词会优先复用。"); }
            else SuggestedCategoryStatus.Text = Status.Text;
        }
        finally { if (!_shutdown) ApproveCategoryButton.IsEnabled = true; }
    }
    private void KeepPromptVisible(string id)
    {
        if (!_promptItems.Any(p => p.Id == id))
        {
            _activeCategory = null; _selectedTags.Clear(); PromptSearch.Clear(); TagSearch.Clear();
            _visiblePrompts = Math.Max(30, LibraryStore.FilterPrompts(_viewState, "", []).ToList().FindIndex(p => p.Id == id) + 1);
            RefreshPromptViews();
        }
        PromptList.SelectedItem = _promptItems.FirstOrDefault(p => p.Id == id);
        if (PromptList.SelectedItem is not null) PromptList.ScrollIntoView(PromptList.SelectedItem);
    }
    private async Task ClassificationWriteAsync(Action write)
    {
        var save = Task.Run(write);
        _summaryWrites = Task.WhenAll(_summaryWrites, save);
        await save;
    }
    private async Task ClassifyPromptAsync(LibraryPrompt item, bool afterSave = false, bool summarizeMetadata = true)
    {
        if (!_summarizing.Add(item.Id)) { Notice("这条提示词正在整理与分类，请稍候。"); return; }
        var saved = afterSave ? "提示词已保存，" : "";
        var label = $"《{FeedbackLabel(item.Title)}》";
        var noticeVersion = ShowSaveFeedback($"{saved}AI 正在整理{label}并分类…", busy: true);
        var classificationSaved = false;
        RefreshCategoryDetail();
        try
        {
            var state = await Task.Run(() => _store.Read());
            if (_shutdown) return;
            var results = await _http.ClassifyAsync(state.Ai, [item], PromptCategories.Catalog(state.Prompts), _lifetime.Token, summarizeMetadata);
            await ClassificationWriteAsync(() => _store.ApplyClassification(item, results.Single()));
            classificationSaved = true;
            var keepVisible = (PromptList.SelectedItem as LibraryPrompt)?.Id == item.Id;
            await ReloadViewsAsync();
            if (_shutdown) return;
            if (keepVisible) KeepPromptVisible(item.Id);
            var current = _viewState.Prompts.FirstOrDefault(p => p.Id == item.Id);
            if (noticeVersion == _saveNoticeVersion)
                ShowSaveFeedback($"AI 整理完成：{label}。{(current?.SuggestedCategory.Length > 0 ? "新分类待确认，请在详情中确认或选择已有分类。" : current?.Category.Length > 0 ? $"已归入“{current.Category}”。" : "用途不明确，已保留在待分类。")}正文保持原样。");
        }
        catch (Exception ex)
        {
            if (_shutdown) return;
            var error = Error(ex);
            if (classificationSaved)
            {
                if (noticeVersion == _saveNoticeVersion)
                    ShowSaveFeedback($"AI 整理结果已保存：{label}。界面刷新失败：{error} 请重新打开资料库查看。", error: true);
                return;
            }
            try
            {
                await ClassificationWriteAsync(() => _store.SaveClassificationFailure(item, "分类未完成：" + error));
                await ReloadViewsAsync();
            }
            catch { /* 迟到结果或保存失败不得覆盖用户更新；原错误仍在界面报告。 */ }
            if (!_shutdown && noticeVersion == _saveNoticeVersion)
                ShowSaveFeedback($"{saved}AI 整理失败：{label}。{error} 正文已保留，可在下方重试分类。", error: true);
        }
        finally { _summarizing.Remove(item.Id); if (!_shutdown) RefreshCategoryDetail(); }
    }

    private async void ClassifyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_categoryPlanRunning || _applyingCategoryPlan || _summarizing.Count > 0) { Notice("请等待当前整理完成后再批量分类。"); return; }
        DismissSaveNotice();
        _classificationRows.Clear(); CategoryPlanList.ItemsSource = _classificationRows;
        CategoryPlanLayer.Visibility = Visibility.Visible;
        _categoryPlanRunning = true; ApplyCategoryPlanButton.IsEnabled = false; CancelCategoryPlanButton.IsEnabled = true; CategoryPlanList.IsEnabled = false;
        ClassifyAllButton.IsEnabled = false;
        _categoryPlanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _categoryPlanCancellation.Token;
        CategoryPlanStatus.Text = "正在读取提示词与已有分类…";
        try
        {
            var state = await Task.Run(() => _store.Read(), token);
            var pending = state.Prompts.Where(p => !p.CategoryManual).ToList();
            var catalog = PromptCategories.Catalog(state.Prompts);
            var completed = 0;
            if (pending.Count == 0) { CategoryPlanStatus.Text = "没有需要自动分类的提示词；人工分类保持不变。"; return; }
            while (completed < pending.Count)
            {
                token.ThrowIfCancellationRequested();
                var batch = new List<LibraryPrompt>(); var characters = 0;
                foreach (var prompt in pending.Skip(completed).Take(10))
                {
                    var length = prompt.Content.Length + prompt.Title.Length + prompt.Description.Length + prompt.Tags.Sum(t => t.Length) + 300;
                    if (batch.Count > 0 && characters + length > 45000) break;
                    batch.Add(prompt); characters += length;
                }
                CategoryPlanStatus.Text = $"正在分析 {completed + 1}–{completed + batch.Count} / {pending.Count} 条；手动分类 {state.Prompts.Count - pending.Count} 条保持不变。可取消请求，已完成建议会保留。";
                var results = await _http.ClassifyAsync(state.Ai, batch, catalog, token);
                foreach (var result in results)
                {
                    if (result.Category.Length > 0 && catalog.All(c => !c.Name.Equals(result.Category, StringComparison.OrdinalIgnoreCase)))
                        catalog.Add(new(result.Category, result.CategoryDescription));
                    var row = new ClassificationRow(batch.Single(p => p.Id == result.Id), result)
                    {
                        Choices = new[] { new CategoryChoice("", "待分类 · 需确认") }.Concat(catalog.Select(c => new CategoryChoice(c.Name, c.Name))).ToList()
                    };
                    _classificationRows.Add(row);
                }
                completed += batch.Count;
            }
            CategoryPlanStatus.Text = $"已生成 {_classificationRows.Count} 条建议。检查分类名称、收录边界和每条归属，点击应用后才保存。";
        }
        catch (OperationCanceledException) { if (!_shutdown) CategoryPlanStatus.Text = $"请求已取消或超时，保留 {_classificationRows.Count} 条已完成建议；尚未更改资料。"; }
        catch (Exception ex) { if (!_shutdown) CategoryPlanStatus.Text = $"分类未完成：{Error(ex)} 已保留 {_classificationRows.Count} 条建议，资料尚未更改。"; }
        finally
        {
            _categoryPlanRunning = false;
            _categoryPlanCancellation.Dispose(); _categoryPlanCancellation = null;
            if (!_shutdown)
            {
                var choices = new List<CategoryChoice> { new("", "待分类 · 需确认") };
                choices.AddRange(PromptCategories.Catalog(_viewState.Prompts).Select(c => new CategoryChoice(c.Name, c.Name)));
                foreach (var row in _classificationRows)
                    if (row.Result.Category.Length > 0 && choices.All(c => c.Value != row.Result.Category))
                        choices.Add(new(row.Result.Category, row.Result.Category + " · 新建议"));
                foreach (var row in _classificationRows) row.Choices = choices;
                CategoryPlanList.Items.Refresh();
                ApplyCategoryPlanButton.IsEnabled = _classificationRows.Count > 0;
                CancelCategoryPlanButton.IsEnabled = false;
                ClassifyAllButton.IsEnabled = true;
                CategoryPlanList.IsEnabled = true;
                CategoryPlanList.Focus();
            }
        }
    }
    private void CancelCategoryPlan_Click(object sender, RoutedEventArgs e) => _categoryPlanCancellation?.Cancel();
    private void CategoryPlanClose_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingCategoryPlan) return;
        _categoryPlanCancellation?.Cancel(); CategoryPlanLayer.Visibility = Visibility.Collapsed; ClassifyAllButton.Focus();
    }
    private async void ApplyCategoryPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_categoryPlanRunning || _applyingCategoryPlan || _classificationRows.Count == 0) return;
        _applyingCategoryPlan = true; ApplyCategoryPlanButton.IsEnabled = false; CategoryPlanList.IsEnabled = false;
        var applied = 0; var failed = 0;
        var definitions = _classificationRows.Where(r => r.Result.Category.Length > 0).GroupBy(r => r.Result.Category)
            .ToDictionary(g => g.Key, g => g.First().Result.CategoryDescription);
        try
        {
            foreach (var row in _classificationRows.ToArray())
            {
                if (_shutdown) break;
                try
                {
                    var category = row.SelectedCategory ?? "";
                    var result = row.Result with { Category = category, CategoryDescription = definitions.GetValueOrDefault(category) ?? "",
                        Reason = category.Length == 0 ? "请补充具体任务和预期结果，或手动选择分类。" : row.Result.Reason };
                    await ClassificationWriteAsync(() => _store.ApplyClassification(row.Expected, result, approveNewCategory: true));
                    _classificationRows.Remove(row); applied++;
                }
                catch (Exception) { failed++; }
            }
            await ReloadViewsAsync();
            if (_shutdown) return;
            if (failed == 0) CategoryPlanLayer.Visibility = Visibility.Collapsed;
            CategoryPlanStatus.Text = $"已应用 {applied} 条；{failed} 条保存失败或内容已更改，未覆盖，请返回资料库核对后重新生成。";
            Notice(CategoryPlanStatus.Text, failed > 0);
        }
        catch (Exception ex) { if (!_shutdown) CategoryPlanStatus.Text = $"已应用 {applied} 条；刷新失败：{Error(ex)} 请重新打开资料库。"; }
        finally
        {
            _applyingCategoryPlan = false;
            if (!_shutdown) { CategoryPlanList.IsEnabled = true; ApplyCategoryPlanButton.IsEnabled = _classificationRows.Count > 0; }
        }
    }
}
