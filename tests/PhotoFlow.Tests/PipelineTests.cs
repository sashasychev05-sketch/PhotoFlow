using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using PhotoFlow.Core;
using PhotoFlow.Infrastructure;
using Xunit;

namespace PhotoFlow.Tests;

public sealed class PipelineTests : IDisposable
{
    private static readonly string SuiteRoot = FindSuiteRoot();
    private readonly string root = Path.Combine(SuiteRoot, Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public PipelineTests() => Directory.CreateDirectory(root);
    private string At(string name) => Path.Combine(root, name);

    [Fact]
    public async Task IndexingDoesNotNeedOriginalReadAccess()
    {
        var path = At("IMG_001.JPG");
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);
        using var lockedOriginal = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var source = new LocalFolderSource(root);
        var items = await ListAsync(source, Ct);
        var item = Assert.Single(items);
        Assert.Equal(3, item.SizeBytes);
        Assert.Equal(CaptureSource.FileModified, item.CaptureSource);
        Assert.Equal(Confidence.Inferred, item.CaptureConfidence);
        Assert.Equal(DateTimeKind.Unspecified, item.CaptureLocal!.Value.Kind);
        Assert.Null(await source.TryOpenThumbnailAsync(item.SourceItemKey, Ct));
    }

    [Fact]
    public async Task SameNamesInDifferentFoldersAreDifferentObjects()
    {
        Directory.CreateDirectory(At("one")); Directory.CreateDirectory(At("two"));
        await File.WriteAllBytesAsync(At("one/IMG_001.dng"), [1], Ct);
        await File.WriteAllBytesAsync(At("two/IMG_001.dng"), [1], Ct);
        await File.WriteAllTextAsync(At("notes.txt"), "ignore", Ct);
        await using var source = new LocalFolderSource(root);
        var items = await ListAsync(source, Ct);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.True(item.IsRaw));
        Assert.NotEqual(items[0].StableFingerprint, items[1].StableFingerprint);
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("..\\outside.jpg")]
    [InlineData("C:\\outside.jpg")]
    [InlineData("photo.jpg:stream")]
    [InlineData("/absolute.jpg")]
    [InlineData("")]
    public async Task LocalKeysCannotEscapeTheSource(string key)
    {
        await using var source = new LocalFolderSource(root);
        await Assert.ThrowsAsync<ArgumentException>(() => source.OpenOriginalAsync(key, Ct));
    }

    [Fact]
    public async Task MissingThumbnailNeverOpensOriginal()
    {
        await using var source = new SpySource([1, 2, 3]);
        var result = await new ThumbnailService().ReadAsync(source, "selected", Ct);
        Assert.Equal(ThumbnailStatus.Unavailable, result.Status);
        Assert.Equal(0, source.OriginalOpens);
    }

    [Fact]
    public async Task OversizedThumbnailIsRejectedWithoutOpeningOriginal()
    {
        await using var source = new SpySource([1]) { Thumbnail = new byte[33] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ThumbnailService(32).ReadAsync(source, "selected", Ct));
        Assert.Equal(0, source.OriginalOpens);
    }

    [Fact]
    public async Task CopyReadsOnlySelectedObjectAndVerifiesPersistedHash()
    {
        var data = RandomNumberGenerator.GetBytes(350_000);
        await using var source = new SpySource(data);
        var result = await new SelectedFileTransfer().CopyAsync(source, "selected", At("nested/import.jpg"), data.Length, null, Ct);
        Assert.Equal(1, source.OriginalOpens);
        Assert.Equal("selected", source.LastOpenedKey);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.Path, Ct));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), result.Sha256);
        Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WrongLengthNeverPublishesFinalFile()
    {
        await using var source = new SpySource([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SelectedFileTransfer().CopyAsync(source, "selected", At("out.jpg"), 4, null, Ct));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task ExistingDestinationIsPreservedBeforeOpeningOriginal()
    {
        await File.WriteAllBytesAsync(At("out.jpg"), [99], Ct);
        await using var source = new SpySource([1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() =>
            new SelectedFileTransfer().CopyAsync(source, "selected", At("out.jpg"), 3, null, Ct));
        Assert.Equal(new byte[] { 99 }, await File.ReadAllBytesAsync(At("out.jpg"), Ct));
        Assert.Equal(0, source.OriginalOpens);
    }

    [Fact]
    public async Task CancellationDuringTransferCleansOwnedPart()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var source = new SpySource(new byte[250_000]) { AfterFirstRead = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SelectedFileTransfer().CopyAsync(source, "selected", At("out.jpg"), 250_000, null, cancellation.Token));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task DisconnectCleansPartAndAllowsWholeFileRetry()
    {
        await using var source = new SpySource(new byte[250_000]) { FailSecondRead = true };
        await Assert.ThrowsAsync<IOException>(() =>
            new SelectedFileTransfer().CopyAsync(source, "selected", At("out.jpg"), 250_000, null, Ct));
        Assert.Empty(Directory.GetFiles(root));
        source.FailSecondRead = false;
        var result = await new SelectedFileTransfer().CopyAsync(source, "selected", At("out.jpg"), 250_000, null, Ct);
        Assert.Equal(250_000, result.Bytes);
        Assert.Equal(2, source.OriginalOpens);
    }

    [Fact]
    public async Task DestinationCreatedDuringTransferIsNotOverwritten()
    {
        await using var source = new SpySource([1, 2, 3])
            { AfterFirstRead = () => File.WriteAllBytes(At("out.jpg"), [99]) };
        await Assert.ThrowsAsync<IOException>(() =>
            new SelectedFileTransfer().CopyAsync(source, "selected", At("out.jpg"), 3, null, Ct));
        Assert.Equal(new byte[] { 99 }, await File.ReadAllBytesAsync(At("out.jpg"), Ct));
        Assert.Empty(Directory.GetFiles(root, "*.part"));
    }

    [Fact]
    public async Task LocalTransferLeavesBothSelectedAndUnselectedOriginalsIntact()
    {
        var input = At("source"); Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, "yes.jpg"), [1, 2], Ct);
        await File.WriteAllBytesAsync(Path.Combine(input, "no.jpg"), [3, 4], Ct);
        await using var source = new LocalFolderSource(input);
        await new SelectedFileTransfer().CopyAsync(source, "yes.jpg", At("output/yes.jpg"), 2, null, Ct);
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(input, "yes.jpg"), Ct));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(input, "no.jpg"), Ct));
        Assert.Single(Directory.GetFiles(At("output")));
    }

    [Fact]
    public async Task TenThousandItemsEnumerateWithoutPreviewOrImportFiles()
    {
        for (var i = 0; i < 10_000; i++)
        {
            Ct.ThrowIfCancellationRequested();
            using var file = File.Create(At($"IMG_{i:D5}.jpg"));
        }
        await using var source = new LocalFolderSource(root);
        var count = 0;
        await foreach (var item in source.EnumerateAsync(Ct)) { Assert.Equal(0, item.SizeBytes); count++; }
        Assert.Equal(10_000, count);
        Assert.Equal(10_000, Directory.GetFiles(root).Length);
        Assert.Empty(Directory.GetDirectories(root));
    }

    private static async Task<List<MediaDescriptor>> ListAsync(IMediaSource source, CancellationToken ct)
    {
        var list = new List<MediaDescriptor>();
        await foreach (var item in source.EnumerateAsync(ct)) list.Add(item);
        return list;
    }

    public void Dispose()
    {
        // Delete only this test's resolved GUID directory under the project's work/tests.
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != SuiteRoot) throw new InvalidOperationException("Invalid test directory.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static string FindSuiteRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PhotoFlow.slnx")))
                return Path.GetFullPath(Path.Combine(directory.FullName, "..", "work", "tests"));
        throw new DirectoryNotFoundException("Run tests from the PhotoFlow repository.");
    }

    private sealed class SpySource(byte[] bytes) : IMediaSource
    {
        public string Id => "test";
        public string DisplayName => "test";
        public event Action<SourceWarning>? Warning { add { } remove { } }
        public int OriginalOpens { get; private set; }
        public string? LastOpenedKey { get; private set; }
        public byte[]? Thumbnail { get; init; }
        public Action? AfterFirstRead { get; init; }
        public bool FailSecondRead { get; set; }
        public Task<SourceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new SourceCapabilities(true, true, false, "test"));
        public async IAsyncEnumerable<MediaDescriptor> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public Task<Stream?> TryOpenThumbnailAsync(string key, CancellationToken ct) => Task.FromResult<Stream?>(Thumbnail is null ? null : new MemoryStream(Thumbnail));
        public Task<Stream> OpenOriginalAsync(string key, CancellationToken ct)
        {
            OriginalOpens++; LastOpenedKey = key;
            return Task.FromResult<Stream>(new TestReadStream(bytes, AfterFirstRead, FailSecondRead));
        }
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestReadStream(byte[] bytes, Action? afterFirstRead, bool failSecondRead) : MemoryStream(bytes, writable: false)
    {
        private int reads;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++reads == 2 && failSecondRead) throw new IOException("Simulated USB disconnect");
            var result = base.ReadAsync(buffer, cancellationToken);
            if (reads == 1) afterFirstRead?.Invoke();
            return result;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
}
