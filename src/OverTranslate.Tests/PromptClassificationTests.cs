using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class PromptClassificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prompt-categories-" + Guid.NewGuid());
    private LibraryStore Open() => new(Path.Combine(_directory, "library.json"));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
    private static HttpResponseMessage Answer(object answer) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(answer) } } } }), Encoding.UTF8, "application/json")
    };
    private static PromptClassification Result(LibraryPrompt p, string name = "视觉还原") => new(p.Id, name, "对照指定参考图检查并修正视觉差异", ["截图对比"], "有明确的参考图目标");

    [Fact]
    public void NewCategoryNeedsApprovalAndSurvivesRestartAndSync()
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "对照参考图检查界面差异" });
        store.ApplyClassification(prompt, Result(prompt));
        var pending = Open().Read().Prompts.Single();
        Assert.Empty(pending.Category);
        Assert.Equal("视觉还原", pending.SuggestedCategory);
        store.SetPromptCategory(pending, "视觉还原", approveSuggestion: true);
        var saved = Open().Read().Prompts.Single();
        Assert.Equal("视觉还原", saved.Category);
        Assert.True(saved.CategoryManual);
        Assert.Empty(saved.SuggestedCategory);
        Assert.Equal(prompt.Content, saved.Content);
        var destination = new LibraryStore(Path.Combine(_directory, "other.json"));
        destination.MergeSync(store.ExportSync());
        Assert.Equal("视觉还原", destination.Read().Prompts.Single().Category);
        Assert.True(destination.Read().Prompts.Single().CategoryManual);
        var next = store.SavePrompt(new() { Content = "再对照参考截图" });
        store.ApplyClassification(next, Result(next));
        Assert.Equal("视觉还原", Open().Read().Prompts.Single(p => p.Id == next.Id).Category);
    }

    [Fact]
    public void AiCannotOverwriteManualClassificationOrAnEditedPrompt()
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "original" });
        store.ApplyClassification(prompt, Result(prompt), approveNewCategory: true);
        var snapshot = store.Read().Prompts.Single();
        store.SetPromptCategory(snapshot, "视觉还原");
        Assert.Throws<InvalidOperationException>(() => store.ApplyClassification(snapshot, Result(snapshot)));
        var manual = store.Read().Prompts.Single();
        store.ApplyClassification(manual, Result(manual, "界面设计"));
        Assert.Equal("视觉还原", store.Read().Prompts.Single().Category);
        var edited = store.Read().Prompts.Single();
        edited.Content = "changed";
        store.SavePrompt(edited, manual);
        Assert.Throws<InvalidOperationException>(() => store.ApplyClassification(manual, Result(manual)));
        Assert.Equal("changed", store.Read().Prompts.Single().Content);
    }

    [Fact]
    public void CategoryAndTagsIntersectWithSearchWithoutLosingCopyRank()
    {
        var state = new LibraryState { Prompts = [
            new() { Id = "a", Content = "界面", Category = "视觉还原", Tags = ["截图"], CopyCount = 2 },
            new() { Id = "b", Content = "界面", Category = "界面设计", Tags = ["截图"], CopyCount = 4 },
            new() { Id = "c", Content = "界面", Category = "视觉还原", Tags = ["截图"], CopyCount = 5 },
            new() { Id = "d", Content = "待确认" }] };
        Assert.Equal(["c", "a"], LibraryStore.FilterPrompts(state, "界面", ["截图"], "视觉还原").Select(p => p.Id));
        Assert.Equal(["d"], LibraryStore.FilterPrompts(state, "", [], "").Select(p => p.Id));
        Assert.Equal(2, LibraryStore.FilterPrompts(state, "视觉还原", []).Count());
    }

    [Fact]
    public async Task ClassificationReadsFullContentAndReturnsValidatedMetadataWithoutMutatingInput()
    {
        var prompt = new LibraryPrompt { Content = new string('字', 12500) + "最后的参考图要求" };
        using var http = new LibraryHttp(new Handler(request =>
        {
            var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(payload);
            Assert.Contains("最后的参考图要求", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            return Answer(new { categories = new[] { new { name = "视觉还原", description = "对照参考图修正视觉差异" } }, assignments = new[] { new { id = prompt.Id, category = "视觉还原", tags = new[] { "截图对比" }, reason = "对齐参考图" } } });
        }));
        var result = await http.ClassifyAsync(new() { Endpoint = "http://localhost/v1", Model = "fixture" }, [prompt], []);
        Assert.Equal("视觉还原", result.Single().Category);
        Assert.Empty(prompt.Category);
        Assert.Empty(prompt.Tags);
    }

    [Theory]
    [InlineData("其他")]
    [InlineData("通用")]
    [InlineData("全部")]
    public async Task VagueOrReservedNewCategoriesAreRejected(string name)
    {
        var prompt = new LibraryPrompt { Content = "fixture" };
        using var http = new LibraryHttp(new Handler(_ => Answer(new { categories = new[] { new { name, description = "模糊类别" } }, assignments = new[] { new { id = prompt.Id, category = name, tags = Array.Empty<string>(), reason = "fixture" } } })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.ClassifyAsync(new() { Endpoint = "http://localhost/v1", Model = "fixture" }, [prompt], []));
    }

    [Fact]
    public async Task MissingAssignmentsAndUnregisteredCategoriesCannotBeApplied()
    {
        var prompt = new LibraryPrompt { Content = "fixture" };
        using var http = new LibraryHttp(new Handler(_ => Answer(new { categories = Array.Empty<object>(), assignments = new[] { new { id = prompt.Id, category = "模型自己编的类", tags = Array.Empty<string>(), reason = "fixture" } } })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.ClassifyAsync(new() { Endpoint = "http://localhost/v1", Model = "fixture" }, [prompt], []));
        using var missing = new LibraryHttp(new Handler(_ => Answer(new { categories = Array.Empty<object>(), assignments = Array.Empty<object>() })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ClassifyAsync(new() { Endpoint = "http://localhost/v1", Model = "fixture" }, [prompt], []));
    }

    [Fact]
    public async Task OversizedInputIsRejectedBeforeSending()
    {
        using var http = new LibraryHttp(new Handler(_ => throw new Xunit.Sdk.XunitException("超限正文不应发送")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => http.ClassifyAsync(
            new() { Endpoint = "http://localhost/v1", Model = "fixture" },
            [new() { Content = new string('字', PromptCategories.MaxBatchCharacters) }], []));
        Assert.Contains("60,000", error.Message);
    }

    [Fact]
    public void CopyWhileClassifyingPreservesBothTheResultAndCount()
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "参考图检查" });
        store.RememberCopiedPrompt(prompt.Id);
        store.ApplyClassification(prompt, Result(prompt), approveNewCategory: true);
        var saved = store.Read().Prompts.Single();
        Assert.Equal("视觉还原", saved.Category);
        Assert.Equal(1, saved.CopyCount);
    }

    [Fact]
    public void OldSyncProtocolIsRejectedWithoutChangingLocalData()
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "本机正文" });
        var snapshot = store.ExportSync();
        snapshot.Protocol = 2;
        var error = Assert.Throws<InvalidDataException>(() => store.MergeSync(snapshot));
        Assert.Contains("两台设备", error.Message);
        Assert.Equal(prompt.Content, store.Read().Prompts.Single().Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("其他")]
    public void InvalidSuggestionNameKeepsThePendingSuggestion(string name)
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "参考图检查" });
        store.ApplyClassification(prompt, Result(prompt));
        var pending = store.Read().Prompts.Single();
        Assert.Throws<InvalidOperationException>(() => store.SetPromptCategory(pending, name, approveSuggestion: true));
        Assert.Equal("视觉还原", store.Read().Prompts.Single().SuggestedCategory);
        Assert.Empty(store.Read().Prompts.Single().Category);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
