using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LibrarySyncTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ScreenTranslate-sync-tests-" + Guid.NewGuid());
    private LibraryStore Store(string name) => new(Path.Combine(_directory, name, "library.json"));
    private static void Exchange(LibraryStore a, LibraryStore b)
    { b.MergeSync(a.ExportSync()); a.MergeSync(b.ExportSync()); b.MergeSync(a.ExportSync()); }

    [Fact]
    public void FirstPairMergesBothLibrariesAndDoesNotDuplicateIdenticalRecords()
    {
        var a = Store("a"); var b = Store("b");
        a.SavePrompt(new() { Id = "shared", Content = "same" });
        b.SavePrompt(new() { Id = "shared", Content = "same" });
        a.SavePrompt(new() { Content = "only-a" }); b.SavePrompt(new() { Content = "only-b" });
        Exchange(a, b);
        Assert.Equal(3, a.Read().Prompts.Count); Assert.Equal(3, b.Read().Prompts.Count);
        Assert.Equal(JsonSerializer.Serialize(a.ExportSync()), JsonSerializer.Serialize(b.ExportSync()));
    }

    [Fact]
    public void OfflineChangesAndDeletesSurviveRestartWithoutResurrection()
    {
        var a = Store("a"); var b = Store("b");
        var prompt = a.SavePrompt(new() { Content = "initial" }); Exchange(a, b);
        var old = b.ExportSync();
        a.Update(s => s.Prompts.RemoveAll(p => p.Id == prompt.Id));
        b.SavePrompt(new() { Content = "new-while-offline" });
        Exchange(Store("a"), Store("b")); a.MergeSync(old);
        Assert.Equal("new-while-offline", Assert.Single(a.Read().Prompts).Content);
        Assert.DoesNotContain(b.Read().Prompts, p => p.Id == prompt.Id);
        Assert.True(Store("a").ExportSync().Items.Single(i => i.Id == "p:" + prompt.Id).Version.Deleted);
    }

    [Fact]
    public void ConcurrentEditsConvergeWithOneStableConflictCopy()
    {
        var a = Store("a"); var b = Store("b"); var c = Store("c");
        a.SavePrompt(new() { Content = "initial" }); Exchange(a, b); Exchange(a, c);
        var ap = a.Read().Prompts.Single(); ap.Content = "edited-a"; a.SavePrompt(ap);
        var bp = b.Read().Prompts.Single(); bp.Content = "edited-b"; b.SavePrompt(bp);
        var cp = c.Read().Prompts.Single(); cp.Content = "edited-c"; c.SavePrompt(cp);
        for (var i = 0; i < 3; i++) { Exchange(a, b); Exchange(b, c); Exchange(c, a); }
        Assert.Equal(new[] { "edited-a", "edited-b", "edited-c" }, a.Read().Prompts.Select(p => p.Content).Order().ToArray());
        Assert.Equal(JsonSerializer.Serialize(a.ExportSync()), JsonSerializer.Serialize(b.ExportSync()));
        Assert.Equal(JsonSerializer.Serialize(a.ExportSync()), JsonSerializer.Serialize(c.ExportSync()));
        Assert.Equal(2, a.Read().Prompts.Count(p => p.Title.Contains("同步冲突副本")));
    }

    [Fact]
    public void ConcurrentDeleteKeepsEditedContentAsAConflictCopy()
    {
        var a = Store("a"); var b = Store("b");
        var prompt = a.SavePrompt(new() { Content = "initial" }); Exchange(a, b);
        a.Update(s => s.Prompts.Clear());
        var draft = b.Read().Prompts.Single(); draft.Content = "offline edit"; b.SavePrompt(draft);
        Exchange(a, b); Exchange(b, a);
        Assert.Equal("offline edit", Assert.Single(a.Read().Prompts).Content);
        Assert.NotEqual(prompt.Id, a.Read().Prompts.Single().Id);
        Assert.Equal(JsonSerializer.Serialize(a.ExportSync()), JsonSerializer.Serialize(b.ExportSync()));
    }

    [Fact]
    public void DeletedConflictCopiesDoNotReappearAfterReplay()
    {
        var a = Store("a"); var b = Store("b");
        a.SavePrompt(new() { Content = "initial" }); Exchange(a, b);
        var pa = a.Read().Prompts.Single(); pa.Content = "a"; a.SavePrompt(pa);
        var pb = b.Read().Prompts.Single(); pb.Content = "b"; b.SavePrompt(pb);
        var old = b.ExportSync(); Exchange(a, b);
        a.Update(s => s.Prompts.RemoveAll(p => p.Title.Contains("同步冲突副本")));
        Exchange(a, b); a.MergeSync(old); Exchange(a, b);
        Assert.Single(a.Read().Prompts); Assert.Single(b.Read().Prompts);
    }

    [Fact]
    public void CopyCountsAndLocalSettingsStayLocalAndDoNotCreateSyncRevisions()
    {
        var a = Store("a"); var b = Store("b");
        var p = a.SavePrompt(new() { Content = "initial" }); Exchange(a, b);
        var revision = a.ExportSync().Items.Single().Version.Revision;
        a.RememberCopiedPrompt(p.Id); a.Update(s => { s.SelectedTab = 1; s.Ai.Model = "local-ai"; });
        Exchange(a, b);
        Assert.Equal(revision, a.ExportSync().Items.Single().Version.Revision);
        Assert.Equal(1, a.Read().Prompts.Single().CopyCount); Assert.Equal(0, b.Read().Prompts.Single().CopyCount);
        Assert.Equal("", b.Read().Ai.Model); Assert.Equal(0, b.Read().SelectedTab);
    }

    [Fact]
    public void ApiKeysAreReprotectedOnReceiverAndNeverPersistInPlaintext()
    {
        const string fixtureKey = "synthetic-key-for-sync-test-only";
        var a = Store("a"); var b = Store("b");
        a.SaveConnection(new() { Platform = "Fixture", Endpoint = "http://192.168.1.2/v1", AllowInsecureEndpoint = true,
            Model = "fixed", Note = "memo", RemainingBalance = "12" }, fixtureKey, ["one", "two"]);
        Exchange(a, b);
        var c = b.Read().Connections.Single();
        Assert.Equal(fixtureKey, LocalSecretStore.Unprotect(c.ApiKeyProtected));
        Assert.NotEqual(a.Read().Connections.Single().ApiKeyProtected, c.ApiKeyProtected);
        Assert.False(c.AllowInsecureEndpoint); Assert.Equal(["one", "two"], c.ModelIds);
        Assert.DoesNotContain(fixtureKey, File.ReadAllText(Path.Combine(_directory, "b", "library.json")));
        c.Note = "updated from b"; b.SaveConnection(c); Exchange(a, b);
        Assert.Equal("updated from b", a.Read().Connections.Single().Note);
        Assert.Equal(fixtureKey, LocalSecretStore.Unprotect(a.Read().Connections.Single().ApiKeyProtected));
    }

    [Fact]
    public void KeyChangesAndConnectionDeletesPropagate()
    {
        var a = Store("a"); var b = Store("b");
        var c = a.SaveConnection(new() { Platform = "Fixture", Endpoint = "https://fixture.invalid/v1" }, "synthetic-first");
        Exchange(a, b); b.SaveConnection(b.Read().Connections.Single(), "synthetic-second"); Exchange(a, b);
        Assert.Equal("synthetic-second", LocalSecretStore.Unprotect(a.Read().Connections.Single().ApiKeyProtected));
        a.Update(s => s.Connections.RemoveAll(x => x.Id == c.Id)); Exchange(a, b);
        Assert.Empty(b.Read().Connections);
    }

    [Fact]
    public void StaleOpenEditorsCannotOverwriteReceivedChangesOrResurrectDeletedData()
    {
        var a = Store("a"); var b = Store("b");
        var original = a.SavePrompt(new() { Content = "initial" });
        var connection = a.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1" }); Exchange(a, b);
        var edited = b.Read().Prompts.Single(); edited.Content = "from-b"; b.SavePrompt(edited);
        b.Update(s => s.Connections.Clear()); Exchange(a, b);
        Assert.Throws<InvalidOperationException>(() => a.SavePrompt(new() { Id = original.Id, Content = "stale-input" }, original));
        Assert.Throws<InvalidOperationException>(() => a.SaveConnection(connection, expectedRevision: connection.Revision));
        Assert.Equal("from-b", a.Read().Prompts.Single().Content); Assert.Empty(a.Read().Connections);
    }

    [Fact]
    public void InvalidSnapshotCannotPartiallyCommitValidEarlierItems()
    {
        var a = Store("a"); var b = Store("b");
        a.SavePrompt(new() { Content = "valid" }); var snapshot = a.ExportSync();
        snapshot.Items.Add(new() { Id = "bad" });
        Assert.Throws<InvalidDataException>(() => b.MergeSync(snapshot)); Assert.Empty(b.Read().Prompts);
        Assert.False(File.Exists(Path.Combine(_directory, "b", "library.json")));
    }

    [Fact]
    public void RepeatedSyncDoesNotRewriteAnUnchangedLibrary()
    {
        var a = Store("a"); var b = Store("b"); a.SavePrompt(new() { Content = "x" }); Exchange(a, b);
        var path = Path.Combine(_directory, "b", "library.json");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(b.MergeSync(a.ExportSync()));
        Assert.Equal(2020, File.GetLastWriteTimeUtc(path).Year);
    }

    [Fact]
    public void CopiedDeviceMetadataGetsANewActorBeforeLocalEditing()
    {
        var a = Store("a"); var b = Store("b");
        a.SavePrompt(new() { Content = "initial" }); a.ExportSync();
        var state = a.Read(); var oldActor = state.Sync!.DeviceId;
        // 模拟来自另一个 Windows 用户、当前 DPAPI 无法读取的设备标记。
        state.Sync.DeviceBindingProtected = "not-a-local-dpapi-value";
        Directory.CreateDirectory(Path.Combine(_directory, "b"));
        File.WriteAllText(Path.Combine(_directory, "b", "library.json"), JsonSerializer.Serialize(state));
        var draft = b.Read().Prompts.Single(); draft.Content = "from copied device"; b.SavePrompt(draft);
        Assert.NotEqual(oldActor, b.Read().Sync!.DeviceId);
        Assert.Equal(2, b.ExportSync().Items.Single().Version.Clock.Count);
    }

    [Fact]
    public void ConcurrentCredentialEditsPreserveBothVersionsWithoutRepeatedCopies()
    {
        var a = Store("a"); var b = Store("b");
        a.SaveConnection(new() { Platform = "Fixture", Endpoint = "https://fixture.invalid/v1" }, "synthetic-original");
        Exchange(a, b);
        a.SaveConnection(a.Read().Connections.Single(), "synthetic-a");
        b.SaveConnection(b.Read().Connections.Single(), "synthetic-b");
        for (var i = 0; i < 3; i++) Exchange(a, b);
        Assert.Equal(new[] { "synthetic-a", "synthetic-b" }, a.Read().Connections.Select(c => LocalSecretStore.Unprotect(c.ApiKeyProtected)).Order().ToArray());
        Assert.Equal(2, b.Read().Connections.Count);
        Assert.Equal(JsonSerializer.Serialize(a.ExportSync()), JsonSerializer.Serialize(b.ExportSync()));
    }

    [Fact]
    public void OversizedCombinedLibraryCannotPartiallyMerge()
    {
        var a = Store("a"); var b = Store("b");
        a.SavePrompt(new() { Content = new string('a', (LibrarySyncSnapshot.MaxBytes + 28) / 2) });
        b.SavePrompt(new() { Content = new string('b', (LibrarySyncSnapshot.MaxBytes + 28) / 2) });
        var path = Path.Combine(_directory, "b", "library.json"); var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => b.MergeSync(a.ExportSync()));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Single(b.Read().Prompts);
    }

    [Fact]
    public void CorruptSyncMetadataCannotResetOrOverwriteExistingData()
    {
        var a = Store("a"); a.SavePrompt(new() { Content = "keep" }); a.ExportSync();
        var state = a.Read(); state.Sync!.Versions.Values.Single().Clock = null!;
        var path = Path.Combine(_directory, "a", "library.json");
        File.WriteAllText(path, JsonSerializer.Serialize(state)); var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Store("a"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void CausalEditsAndConcurrentMergesConvergeRegardlessOfDeliveryOrder()
    {
        const string actorA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", actorB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        LibrarySyncSnapshot Revision(string content, string revision, params (string Actor, long Count)[] clock) => new()
        {
            Items = [new() { Id = "p:shared", Prompt = new() { Id = "shared", Content = content },
                Version = new() { Revision = revision, Clock = clock.ToDictionary(v => v.Actor, v => v.Count) } }]
        };
        var oldA = Revision("old-a", new string('f', 32), (actorA, 1));
        var newA = Revision("new-a", new string('1', 32), (actorA, 2));
        var concurrentB = Revision("concurrent-b", new string('8', 32), (actorB, 1));
        var left = Store("left"); var right = Store("right");
        foreach (var snapshot in new[] { oldA, concurrentB, newA }) left.MergeSync(snapshot);
        foreach (var snapshot in new[] { newA, concurrentB, oldA }) right.MergeSync(snapshot);
        for (var i = 0; i < 3; i++) Exchange(left, right);
        Assert.Equal(JsonSerializer.Serialize(left.ExportSync()), JsonSerializer.Serialize(right.ExportSync()));
        Assert.Equal(left.Read().Prompts.Single(p => p.Id == "shared").Content, right.Read().Prompts.Single(p => p.Id == "shared").Content);
    }

    [Fact]
    public void RemoteNoteChangeKeepsTheLocalLastCopiedModel()
    {
        var a = Store("a"); var b = Store("b");
        var connection = a.SaveConnection(new() { Platform = "Local", Endpoint = "http://localhost/v1", Model = "fixed" });
        Exchange(a, b); a.RememberCopiedModel(connection.Id, a.Read().Connections.Single().Revision, "local-choice");
        var remote = b.Read().Connections.Single(); remote.Note = "new note"; b.SaveConnection(remote);
        Exchange(a, b);
        Assert.Equal("local-choice", a.Read().Connections.Single().LastCopiedModelId);
        Assert.Equal("", b.Read().Connections.Single().LastCopiedModelId);
    }

    [Fact]
    public void ConcurrentBranchesNeverPersistPlaintextCredentials()
    {
        var a = Store("a"); var b = Store("b");
        a.SaveConnection(new() { Platform = "Fixture", Endpoint = "https://fixture.invalid/v1" }, "synthetic-initial"); Exchange(a, b);
        a.SaveConnection(a.Read().Connections.Single(), "synthetic-branch-a");
        b.SaveConnection(b.Read().Connections.Single(), "synthetic-branch-b"); Exchange(a, b);
        foreach (var name in new[] { "a", "b" })
        {
            var json = File.ReadAllText(Path.Combine(_directory, name, "library.json"));
            Assert.DoesNotContain("synthetic-branch-a", json); Assert.DoesNotContain("synthetic-branch-b", json);
            var snapshot = Store(name).ExportSync();
            Assert.All(snapshot.Items, item => Assert.Empty(item.Version.Branches));
            Assert.All(snapshot.Items.SelectMany(item => item.Alternatives), item =>
            { Assert.Empty(item.Version.Branches); Assert.Empty(item.Connection!.ApiKeyProtected); });
        }
        var updated = a.Read().Connections.Single(c => !c.Note.StartsWith("同步冲突副本"));
        updated.Note = "resolved"; a.SaveConnection(updated);
        Assert.Empty(a.Read().Sync!.Versions["c:" + updated.Id].Branches);
    }

    [Fact]
    public void OldProtocolAndInvalidConcurrentBranchesCannotPartiallyMerge()
    {
        var a = Store("a"); var b = Store("b"); a.SavePrompt(new() { Content = "initial" }); Exchange(a, b);
        var old = a.ExportSync(); old.Protocol = 1;
        Assert.Throws<InvalidDataException>(() => b.MergeSync(old));
        var pa = a.Read().Prompts.Single(); pa.Content = "a"; a.SavePrompt(pa);
        var pb = b.Read().Prompts.Single(); pb.Content = "b"; b.SavePrompt(pb); Exchange(a, b);
        var invalid = a.ExportSync(); invalid.Items.First(item => item.Alternatives.Count > 0).Alternatives[0].Version.Clock.Clear();
        var path = Path.Combine(_directory, "b", "library.json"); var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => b.MergeSync(invalid)); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReorderedDuplicatedAndDelayedPacketsConvergeAcrossFourDevices()
    {
        var devices = Enumerable.Range(0, 4).Select(i => Store("fuzz-" + i)).ToArray();
        var random = new Random(20261108); var delayed = new List<LibrarySyncSnapshot>();
        for (var i = 0; i < 36; i++)
        {
            var device = devices[random.Next(devices.Length)];
            if (i % 7 == 0) device.Update(s => s.Prompts.RemoveAll(p => p.Id == "shared"));
            else device.SavePrompt(new() { Id = "shared", Content = "synthetic-edit-" + i });
            delayed.Add(device.ExportSync());
            for (var delivery = 0; delivery < 2; delivery++)
                devices[random.Next(devices.Length)].MergeSync(delayed[random.Next(delayed.Count)]);
        }
        for (var round = 0; round < 4; round++)
            foreach (var source in devices)
                foreach (var destination in devices) destination.MergeSync(source.ExportSync());
        var expected = JsonSerializer.Serialize(devices[0].ExportSync());
        Assert.All(devices, device => Assert.Equal(expected, JsonSerializer.Serialize(device.ExportSync())));
        var count = devices[0].Read().Prompts.Count;
        foreach (var old in delayed) devices[0].MergeSync(old);
        Assert.Equal(count, devices[0].Read().Prompts.Count);
        Assert.Equal(expected, JsonSerializer.Serialize(devices[0].ExportSync()));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
