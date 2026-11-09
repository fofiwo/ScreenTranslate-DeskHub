using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class PromptCopyConcurrencyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prompt-copy-review-" + Guid.NewGuid());
    private LibraryStore Open() => new(Path.Combine(_directory, "library.json"));

    [Fact]
    public void ExistingReadersKeepTheirSnapshotWhileACopyIsSaved()
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "snapshot fixture" });
        using var snapshot = new FileStream(Path.Combine(_directory, "library.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Assert.Equal(1, store.RememberCopiedPrompt(prompt.Id));
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<LibraryState>(snapshot)!.Prompts.Single().CopyCount);
        Assert.Equal(1, Open().Read().Prompts.Single().CopyCount);
    }

    [Fact]
    public async Task SimultaneousStoresAccumulateEveryCopy()
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "concurrent fixture" });
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var writer = Open();
            for (var i = 0; i < 12; i++) writer.RememberCopiedPrompt(prompt.Id);
        })));
        Assert.Equal(48, Open().Read().Prompts.Single().CopyCount);
    }

    [Fact]
    public async Task ConcurrentReadsDoNotLoseSuccessfulCopyRecords()
    {
        var store = Open();
        var prompts = Enumerable.Range(0, 128).Select(i => new LibraryPrompt
        { Title = "Fixture " + i, Content = new string('x', 8192) }).ToList();
        store.Update(s => s.Prompts = prompts);
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) Assert.Equal(128, Open().Read().Prompts.Count);
        })).ToArray();
        try
        {
            for (var i = 0; i < 32; i++) store.RememberCopiedPrompt(prompts[0].Id);
        }
        finally { stop.Cancel(); await Task.WhenAll(readers); }
        Assert.Equal(32, Open().Read().Prompts[0].CopyCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
