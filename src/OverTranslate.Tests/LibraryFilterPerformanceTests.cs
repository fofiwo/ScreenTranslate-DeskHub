using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LibraryFilterPerformanceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void EmptySearchDoesNotCopyEveryPromptBody(string query)
    {
        var body = new string('x', 20000);
        var state = new LibraryState
        {
            Prompts = Enumerable.Range(0, 2000).Select(i => new LibraryPrompt
                { Id = i.ToString(), Content = body, CopyCount = i, Tags = ["fixture"] }).ToList()
        };
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var result = LibraryStore.FilterPrompts(state, query, ["fixture"]).ToList();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(2000, result.Count);
        Assert.Equal("1999", result[0].Id);
        Assert.True(bytes < 1_000_000, $"空查询复制了无须搜索的正文，分配 {bytes} 字节。");
        Assert.Empty(LibraryStore.FilterPrompts(state, query, ["missing"]));
    }

    [Fact]
    public void SearchKeepsMatchesAcrossFieldsAndInsideTags()
    {
        var prompt = new LibraryPrompt { Title = "alpha", Description = "beta", Content = "gamma", Tags = ["delta"] };
        var state = new LibraryState { Prompts = [prompt] };
        Assert.Same(prompt, Assert.Single(LibraryStore.FilterPrompts(state, " ALPHA beta ", [])));
        Assert.Same(prompt, Assert.Single(LibraryStore.FilterPrompts(state, "gamma delta", ["delta"])));
        Assert.Empty(LibraryStore.FilterPrompts(state, "unknown", []));
    }
}
