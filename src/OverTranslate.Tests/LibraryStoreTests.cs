using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ScreenTranslate-library-" + Guid.NewGuid());
    private LibraryStore Open() => new(Path.Combine(_directory, "library.json"));

    [Fact]
    public void OldHttpConnectionsKeepTheirDataButDoNotGrantInsecurePermission()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "library.json"),
            """{"SchemaVersion":1,"Connections":[{"Id":"legacy","Platform":"Fixture","Endpoint":"http://gateway.invalid/v1","ApiKeyProtected":"opaque-fixture","Model":"fixed","ModelIds":["cached-model"]}]}""");
        var store = Open();
        var connection = store.Read().Connections.Single();
        Assert.False(connection.AllowInsecureEndpoint);
        Assert.Equal("http://gateway.invalid/v1", connection.Endpoint);
        Assert.Equal("opaque-fixture", connection.ApiKeyProtected);
        Assert.Equal(["cached-model"], connection.ModelIds);
        connection.Note = "edited without sending a request";
        store.SaveConnection(connection);
        connection = Open().Read().Connections.Single();
        Assert.False(connection.AllowInsecureEndpoint);
        Assert.Equal("opaque-fixture", connection.ApiKeyProtected);
        Assert.Equal(["cached-model"], connection.ModelIds);
    }

    [Fact]
    public void OldPromptsStartWithZeroCopies()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "library.json"),
            """{"SchemaVersion":1,"Prompts":[{"Id":"legacy","Content":"original"}]}""");
        Assert.Equal(0, Open().Read().Prompts.Single().CopyCount);
    }

    [Fact]
    public void PromptCopyCountsAccumulateAcrossStoresAndSurviveEditingAndRestart()
    {
        var first = Open();
        var draft = first.SavePrompt(new() { Content = "original", UpdatedAt = 123 });
        var second = Open();
        Assert.Equal(1, first.RememberCopiedPrompt(draft.Id));
        Assert.Equal(2, second.RememberCopiedPrompt(draft.Id));
        Assert.Equal(draft.UpdatedAt, Open().Read().Prompts.Single().UpdatedAt);
        draft.Content = "edited from an older snapshot";
        first.SavePrompt(draft);
        var saved = Open().Read().Prompts.Single();
        Assert.Equal(2, saved.CopyCount);
        Assert.Equal(draft.Content, saved.Content);
    }

    [Fact]
    public void PromptFilteringRanksByCopyCountAndPreservesStoredOrderForTies()
    {
        var store = Open();
        var first = store.SavePrompt(new() { Content = "common first", Tags = ["selected"] });
        var second = store.SavePrompt(new() { Content = "common second", Tags = ["selected"] });
        var third = store.SavePrompt(new() { Content = "common third", Tags = ["other"] });
        store.RememberCopiedPrompt(first.Id);
        store.RememberCopiedPrompt(second.Id);
        store.RememberCopiedPrompt(first.Id);
        Assert.Equal(new[] { first.Id, second.Id, third.Id },
            LibraryStore.FilterPrompts(Open().Read(), "", []).Select(p => p.Id));
        store.RememberCopiedPrompt(second.Id);
        Assert.Equal(new[] { second.Id, first.Id },
            LibraryStore.FilterPrompts(Open().Read(), "common", ["selected"]).Select(p => p.Id));
    }

    [Fact]
    public void CopyingADeletedPromptCannotRecreateIt()
    {
        var store = Open();
        var prompt = store.SavePrompt(new() { Content = "original" });
        store.Update(s => s.Prompts.Clear());
        Assert.Throws<InvalidOperationException>(() => store.RememberCopiedPrompt(prompt.Id));
        Assert.Empty(Open().Read().Prompts);
    }

    [Fact]
    public void OldConnectionsWithoutCopyHistoryUseTheFixedModel()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "library.json"),
            """{"SchemaVersion":1,"Connections":[{"Id":"legacy","Model":"fixed-id"}]}""");
        var connection = Open().Read().Connections.Single();
        Assert.Empty(connection.LastCopiedModelId);
        Assert.Equal("fixed-id", connection.ModelIdToCopy);
    }

    [Fact]
    public void CopyHistoryIsPerConnectionAndSurvivesRestartWithoutChangingTheFixedModel()
    {
        var store = Open();
        var first = store.SaveConnection(new() { Platform = "First", Endpoint = "http://localhost/v1", Model = "fixed-first" });
        var second = store.SaveConnection(new() { Platform = "Second", Endpoint = "http://localhost/v1", Model = "fixed-second" });
        store.RememberCopiedModel(first.Id, first.Revision, "last-first");
        var connections = Open().Read().Connections;
        Assert.Equal("last-first", connections.Single(c => c.Id == first.Id).ModelIdToCopy);
        Assert.Equal("fixed-first", connections.Single(c => c.Id == first.Id).Model);
        Assert.Equal("fixed-second", connections.Single(c => c.Id == second.Id).ModelIdToCopy);
    }

    [Fact]
    public async Task CopyHistorySurvivesRefreshAndAnEditorOpenedBeforeTheCopy()
    {
        var store = Open();
        var draft = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1", Model = "fixed" });
        var pending = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = store.RefreshModelsAsync(draft.Id, (_, _) => pending.Task);
        store.RememberCopiedModel(draft.Id, draft.Revision, "copied");
        pending.SetResult(["new-catalog-id"]);
        await refresh;
        draft.Note = "edited note";
        store.SaveConnection(draft);
        var saved = Open().Read().Connections.Single();
        Assert.Equal("copied", saved.ModelIdToCopy);
        Assert.Equal("fixed", saved.Model);
        Assert.Equal(["new-catalog-id"], saved.ModelIds);
        Assert.Equal("edited note", saved.Note);
    }

    [Theory]
    [InlineData("Model")]
    [InlineData("Endpoint")]
    [InlineData("ModelType")]
    public void ChangingTheFixedModelOrConnectionResetsTheCopyDefault(string field)
    {
        var store = Open();
        var draft = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1", Model = "fixed" });
        store.RememberCopiedModel(draft.Id, draft.Revision, "copied");
        if (field == "Model") draft.Model = "new-fixed";
        if (field == "Endpoint") draft.Endpoint = "http://localhost:11435/v1";
        if (field == "ModelType") draft.ModelType = "image";
        store.SaveConnection(draft);
        var saved = Open().Read().Connections.Single();
        Assert.Empty(saved.LastCopiedModelId);
        Assert.Equal(draft.Model, saved.ModelIdToCopy);
    }

    [Fact]
    public void AStaleCopyCannotOverrideAnEditedOrDeletedConnection()
    {
        var store = Open();
        var original = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1", Model = "fixed" });
        var draft = store.Read().Connections.Single();
        draft.Model = "edited";
        store.SaveConnection(draft);
        Assert.Throws<InvalidOperationException>(() => store.RememberCopiedModel(original.Id, original.Revision, "stale"));
        Assert.Equal("edited", Open().Read().Connections.Single().ModelIdToCopy);
        store.Update(s => s.Connections.Clear());
        Assert.Throws<InvalidOperationException>(() => store.RememberCopiedModel(original.Id, original.Revision, "stale"));
        Assert.Empty(Open().Read().Connections);
    }

    [Fact]
    public async Task CatalogSurvivesRestartAndFailedRefreshKeepsThePreviousList()
    {
        var store = Open();
        var connection = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost:11434/v1", Model = "manual" });
        await store.RefreshModelsAsync(connection.Id, (_, _) => Task.FromResult<IReadOnlyList<string>>(["model-b", "model-a", "model-a"]));
        store = Open();
        Assert.Equal(["model-a", "model-b"], store.Read().Connections.Single().ModelIds);
        await Assert.ThrowsAsync<IOException>(() => store.RefreshModelsAsync(connection.Id, (_, _) => throw new IOException("offline")));
        Assert.Equal(["model-a", "model-b"], Open().Read().Connections.Single().ModelIds);
        Assert.Equal("manual", Open().Read().Connections.Single().Model);
        await store.RefreshModelsAsync(connection.Id, (_, _) => Task.FromResult<IReadOnlyList<string>>(["new-model"]));
        Assert.Equal(["new-model"], Open().Read().Connections.Single().ModelIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task AnOldRequestCannotReplaceTheListAfterEditingTheConnection()
    {
        var store = Open();
        var c = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost:11434/v1", Model = "chosen" });
        var pending = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = store.RefreshModelsAsync(c.Id, (_, _) => pending.Task);
        c.Endpoint = "http://localhost:11435/v1";
        store.SaveConnection(c);
        pending.SetResult(["stale-id"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.Empty(Open().Read().Connections.Single().ModelIds);
    }

    [Fact]
    public void PromptSearchIncludesBodyAndMatchesAnySelectedTag()
    {
        var store = Open();
        store.SavePrompt(new() { Title = "文案", Content = "Amazon product", Tags = ["电商", "英文"] });
        store.SavePrompt(new() { Title = "代码", Content = "WPF", Tags = ["开发"] });
        var hits = LibraryStore.FilterPrompts(store.Read(), "amazon", ["电商", "未命中"]).ToList();
        Assert.Single(hits);
        Assert.Equal("文案", hits[0].Title);
    }

    [Fact]
    public void KeyIsEncryptedAndNeverCarriedToAnotherOrigin()
    {
        var store = Open();
        var c = store.SaveConnection(new() { Platform = "Fixture", Endpoint = "https://example.invalid/v1", Model = "fixture-model" }, "fixture-only-secret");
        Assert.DoesNotContain("fixture-only-secret", File.ReadAllText(Path.Combine(_directory, "library.json")));
        c.Endpoint = "https://other.invalid/v1";
        Assert.Throws<InvalidOperationException>(() => store.SaveConnection(c));
        Assert.Equal("https://example.invalid/v1", store.Read().Connections.Single().Endpoint);
    }

    [Fact]
    public void BrokenFilesAreReportedWithoutOverwritingThem()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "library.json");
        File.WriteAllText(path, "broken-data");
        Assert.Throws<System.Text.Json.JsonException>(() => Open());
        Assert.Equal("broken-data", File.ReadAllText(path));
    }

    [Fact]
    public void IndependentStoresCannotSilentlyLoseEachOthersChanges()
    {
        var first = Open();
        var second = Open();
        first.SavePrompt(new() { Content = "first prompt" });
        second.SavePrompt(new() { Content = "second prompt" });
        Assert.Equal(2, Open().Read().Prompts.Count);
        Assert.Equal(2, first.Read().Prompts.Count);
    }

    [Fact]
    public async Task DeletingAConnectionDuringRefreshCannotRecreateIt()
    {
        var store = Open();
        var c = store.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1", Model = "chosen" });
        var pending = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = store.RefreshModelsAsync(c.Id, (_, _) => pending.Task);
        store.Update(s => s.Connections.Clear());
        pending.SetResult(["stale-id"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.Empty(Open().Read().Connections);
    }

    [Fact]
    public void AFileDamagedAfterOpeningCannotBeOverwrittenByAStaleSnapshot()
    {
        var store = Open();
        store.SavePrompt(new() { Content = "original" });
        var path = Path.Combine(_directory, "library.json");
        File.WriteAllText(path, "damaged-after-opening");
        Assert.Throws<System.Text.Json.JsonException>(() => store.SavePrompt(new() { Content = "later" }));
        Assert.Equal("damaged-after-opening", File.ReadAllText(path));
    }
}
