using PhotoFlow.Core;
using PhotoFlow.Infrastructure.Catalog;
using Xunit;

namespace PhotoFlow.Tests;

public sealed class CatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Database()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PhotoFlow.slnx")))
                return Path.GetFullPath(Path.Combine(directory.FullName, "..", "work", "catalog-tests", Guid.NewGuid().ToString("N"), "catalog.db"));
        throw new DirectoryNotFoundException();
    }
    private static CatalogSource Source => new("source", "Test collection", "local", "test-fixture");

    [Fact]
    public async Task CompleteScanRemovesMissingRowsOnlyFromItsOwnSourceAndKeepsFlags()
    {
        using var store = new CatalogStore(Database());
        await store.UpsertAsync(Source, [Item(1), Item(2)], Ct);
        var other = Source with { Id = "other" };
        await store.UpsertAsync(other, [Item(3)], Ct);
        var kept = (await store.QueryAsync(new(Source.Id), 0, 200, Ct)).Single(i => i.Media == Item(1));
        await store.SetFlagAsync(kept.Id, "favorite", true, Ct);
        await store.SetFlagAsync(kept.Id, "selected", true, Ct);
        await store.CompleteScanAsync(Source.Id, [CatalogStore.Identity(Item(1))], Ct);
        Assert.Equal(2, await store.CountAsync(new(), Ct));
        Assert.True((await store.QueryAsync(new(Source.Id), 0, 200, Ct)).Single().Favorite);
        Assert.Single(await store.SelectedAsync(Source.Id, Ct));
        await store.CompleteScanAsync(Source.Id, [], Ct);
        Assert.Equal(1, await store.CountAsync(new(), Ct));
    }

    [Fact]
    public async Task PartialOrCanceledScanRetainsUnseenRowsAndOldServiceRowsArePurged()
    {
        using var store = new CatalogStore(Database());
        await store.UpsertAsync(Source, [Item(1), Item(2), Item(3) with { RelativePath = "DCIM/.thumbnails/old.jpg" }], Ct);
        await store.UpsertAsync(Source, [Item(1)], Ct); // partial scan: no completion
        Assert.Equal(3, await store.CountAsync(new(), Ct));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompleteScanAsync(Source.Id, [], canceled.Token));
        Assert.Equal(3, await store.CountAsync(new(), Ct));
        await store.RemoveExcludedAsync(Ct);
        Assert.Equal(2, await store.CountAsync(new(), Ct));
    }
    private static MediaDescriptor Item(int index, bool raw = false) => new($"key-{index}", $"frame-{index}.jpg", $"frame-{index}.jpg", raw ? ".dng" : ".jpg",
        MediaKind.Image, raw, index + 1, null, CaptureSource.Unknown, Confidence.Unknown, null, $"fingerprint-{index}");

    [Fact]
    public async Task SelectionUsesEntireFilteredCatalogBeyondTheFirstPage()
    {
        using var store = new CatalogStore(Database());
        await store.UpsertAsync(Source, Enumerable.Range(0, 10_000).Select(i => Item(i, i % 2 == 0)).ToArray(), Ct);
        var query = new CatalogQuery(Source.Id, Type: "raw");
        Assert.Equal(200, (await store.QueryAsync(query, 0, 200, Ct)).Count);
        await store.SetAllSelectedAsync(query, true, Ct);
        Assert.Equal(5000, (await store.SelectionAsync(Source.Id, Ct)).Count);
        Assert.Equal(5000, (await store.SelectedAsync(Source.Id, Ct)).Count);
    }

    [Fact]
    public async Task FavoritesAndSelectionsSurviveDatabaseReopenAndObjectIdRefresh()
    {
        var path = Database(); long id;
        using (var store = new CatalogStore(path))
        {
            await store.UpsertAsync(Source, [Item(1)], Ct);
            id = (await store.QueryAsync(new(), 0, 200, Ct)).Single().Id;
            await store.SetFlagAsync(id, "favorite", true, Ct); await store.SetFlagAsync(id, "selected", true, Ct);
        }
        using var reopened = new CatalogStore(path);
        await reopened.UpsertAsync(Source, [Item(1) with { SourceItemKey = "new-session-object-id" }], Ct);
        var item = (await reopened.QueryAsync(new(FavoritesOnly: true), 0, 200, Ct)).Single();
        Assert.Equal(id, item.Id); Assert.True(item.Favorite); Assert.True(item.Selected);
        Assert.Equal("new-session-object-id", item.Media.SourceItemKey);
    }

    [Fact]
    public async Task AReplacedFileDoesNotInheritFavoritesOrImportState()
    {
        using var store = new CatalogStore(Database());
        await store.UpsertAsync(Source, [Item(1)], Ct);
        var item = (await store.QueryAsync(new(), 0, 200, Ct)).Single();
        await store.SetFlagAsync(item.Id, "favorite", true, Ct);
        await store.MarkImportedAsync(item.Id, new("test-import.jpg", 2, "TEST"), Ct);
        await store.UpsertAsync(Source, [Item(1) with { StableFingerprint = "different-content-metadata" }], Ct);
        var replacement = (await store.QueryAsync(new(), 0, 200, Ct)).Single();
        Assert.False(replacement.Favorite); Assert.Null(replacement.ImportedPath);
    }

    [Fact]
    public async Task SearchTreatsWildcardsLiterallyAndHandlesCyrillicCase()
    {
        using var store = new CatalogStore(Database());
        await store.UpsertAsync(Source, [Item(1) with { FileName = "Кадр_100%.jpg" }, Item(2)], Ct);
        Assert.Single(await store.QueryAsync(new(Search: "кадр_100%"), 0, 200, Ct));
        Assert.Single(await store.QueryAsync(new(Search: "%"), 0, 200, Ct));
        Assert.Empty(await store.QueryAsync(new(Search: "' OR 1=1 --"), 0, 200, Ct));
    }

    [Fact]
    public async Task ImportCompletionUnselectsOnlyTheCompletedObject()
    {
        using var store = new CatalogStore(Database());
        await store.UpsertAsync(Source, [Item(1), Item(2)], Ct);
        await store.SetAllSelectedAsync(new(), true, Ct);
        var completed = (await store.SelectedAsync(null, Ct))[0];
        await store.MarkImportedAsync(completed.Id, new("test-import.jpg", 2, "TEST"), Ct);
        Assert.Equal(1, (await store.SelectionAsync(null, Ct)).Count);
        Assert.Equal(1, await store.CountAsync(new(OnlyNew: true), Ct));
    }
}
