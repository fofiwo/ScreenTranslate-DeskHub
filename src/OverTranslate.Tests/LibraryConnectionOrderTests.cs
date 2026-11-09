using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LibraryConnectionOrderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ScreenTranslate-order-" + Guid.NewGuid());
    private LibraryStore Open() => new(Path.Combine(_directory, "library.json"));

    [Fact]
    public void MovesInBothDirectionsSurviveRestartAndEditing()
    {
        var store = Open();
        store.Update(s => s.Connections = [new() { Id = "a", Platform = "same", Endpoint = "http://localhost/v1" },
            new() { Id = "b", Platform = "other" }, new() { Id = "c", Platform = "same" }]);
        store.MoveConnection("c", "a", false);
        Assert.Equal(["c", "a", "b"], Open().Read().Connections.Select(c => c.Id));
        store.MoveConnection("c", "b", true);
        Assert.Equal(["a", "b", "c"], Open().Read().Connections.Select(c => c.Id));
        var draft = store.Read().Connections[0];
        draft.Note = "edited";
        store.MoveConnection("a", "c", true);
        store.SaveConnection(draft);
        Assert.Equal(["b", "c", "a"], Open().Read().Connections.Select(c => c.Id));
    }

    [Fact]
    public void FilteredMovePreservesHiddenRowsAndOtherStoresChanges()
    {
        var store = Open();
        store.Update(s => s.Connections = [new() { Id = "a" }, new() { Id = "hidden" }, new() { Id = "b" }]);
        var other = Open();
        other.Update(s => { s.Connections[1].Note = "new note"; s.Connections.Add(new() { Id = "new" }); });
        store.MoveConnection("b", "a", false);
        var saved = Open().Read().Connections;
        Assert.Equal(["b", "a", "hidden", "new"], saved.Select(c => c.Id));
        Assert.Equal("new note", saved.Single(c => c.Id == "hidden").Note);
    }

    [Theory]
    [InlineData("missing", "a")]
    [InlineData("a", "missing")]
    public void DeletedSourceOrTargetCannotDropOrRecreateData(string source, string target)
    {
        var store = Open();
        store.Update(s => s.Connections = [new() { Id = "a" }, new() { Id = "b" }]);
        Assert.Throws<InvalidOperationException>(() => store.MoveConnection(source, target, false));
        Assert.Equal(["a", "b"], Open().Read().Connections.Select(c => c.Id));
    }

    [Fact]
    public async Task ReorderDoesNotInvalidateAnInFlightModelCatalogOrCopyHistory()
    {
        var store = Open();
        var first = store.SaveConnection(new() { Platform = "first", Endpoint = "http://localhost/v1", Model = "fixed" });
        var last = store.SaveConnection(new() { Platform = "last", Endpoint = "http://localhost/v1" });
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var refresh = store.RefreshModelsAsync(first.Id, (_, _) => pending.Task);
        store.RememberCopiedModel(first.Id, first.Revision, "copied");
        store.MoveConnection(first.Id, last.Id, true);
        var moved = Open().Read().Connections.Last();
        Assert.Equal(first.Revision, moved.Revision);
        Assert.Equal(first.UpdatedAt, moved.UpdatedAt);
        Assert.Equal("copied", moved.ModelIdToCopy);
        pending.SetResult(["catalog"]);
        await refresh;
        Assert.Equal(first.Id, Open().Read().Connections.Last().Id);
        Assert.Equal(["catalog"], Open().Read().Connections.Last().ModelIds);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
