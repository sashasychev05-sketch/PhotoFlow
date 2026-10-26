using System.Globalization;
using System.Runtime.CompilerServices;
using PhotoFlow.Core;

namespace PhotoFlow.Infrastructure;

public sealed class LocalFolderSource : IMediaSource
{
    private readonly string root;
    public string Id { get; }
    public string DisplayName => root;
    public event Action<SourceWarning>? Warning;

    public LocalFolderSource(string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        RejectReparsePoints(root);
        Id = "local:" + MediaTypes.MetadataFingerprint("local", OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root, null, null);
    }

    public Task<SourceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new SourceCapabilities(false, true, true,
            "Индексация файлов без чтения содержимого. Отдельный декодер миниатюр ещё не подключён."));
    }

    public async IAsyncEnumerable<MediaDescriptor> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                RejectReparsePoints(folder);
                entries = Directory.GetFileSystemEntries(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { Warning?.Invoke(new("enumerate", e.Message, e.HResult)); continue; }

            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();
                MediaDescriptor? item = null;
                try
                {
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                        MediaVisibility.IsExcluded(Path.GetRelativePath(root, path))) continue;
                    if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(path); continue; }
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (MediaTypes.Classify(extension) is not { } type) continue;
                    var info = new FileInfo(path);
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    item = new(relative, relative, info.Name, extension, type.Kind, type.IsRaw, info.Length,
                        DateTime.SpecifyKind(info.LastWriteTime, DateTimeKind.Unspecified), CaptureSource.FileModified,
                        Confidence.Inferred, info.LastWriteTimeUtc,
                        MediaTypes.MetadataFingerprint(Id, relative, info.Length, info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { Warning?.Invoke(new("metadata", e.Message, e.HResult)); }
                if (item is not null) yield return item;
            }
            await Task.Yield();
        }
    }

    public Task<Stream?> TryOpenThumbnailAsync(string sourceItemKey, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream?>(null); }

    public Task<Stream> OpenOriginalAsync(string sourceItemKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Stream stream = new FileStream(Resolve(sourceItemKey), FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string sourceItemKey, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(File.Exists(Resolve(sourceItemKey))); }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Path.IsPathRooted(key) || key.Contains(':') ||
            key.Split('/', '\\').Any(segment => segment is ".." or "." or ""))
            throw new ArgumentException("Недопустимый относительный ключ файла.", nameof(key));
        var full = Path.GetFullPath(Path.Combine(root, key));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Файл находится за пределами источника.", nameof(key));
        RejectReparsePoints(full);
        return full;
    }

    private static void RejectReparsePoints(string path)
    {
        for (var entry = path; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Ссылки и reparse points не обрабатываются в прототипе.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
