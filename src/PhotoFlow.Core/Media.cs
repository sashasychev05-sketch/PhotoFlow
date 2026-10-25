using System.Security.Cryptography;
using System.Text;

namespace PhotoFlow.Core;

public enum MediaKind { Image, Video }
public enum CaptureSource { Unknown, FileModified, WpdCreated }
public enum Confidence { Unknown, Inferred, Verified }

public sealed record SourceCapabilities(
    bool ThumbnailResourceMayBeAvailable,
    bool CanOpenOriginal,
    bool CanSeekOriginal,
    string Note);

public sealed record MediaDescriptor(
    string SourceItemKey,
    string RelativePath,
    string FileName,
    string Extension,
    MediaKind Kind,
    bool IsRaw,
    long? SizeBytes,
    DateTime? CaptureLocal,
    CaptureSource CaptureSource,
    Confidence CaptureConfidence,
    DateTime? ModifiedUtc,
    string StableFingerprint,
    uint? Width = null,
    uint? Height = null,
    string? PersistentId = null,
    DateTime? SourceModifiedLocal = null);

public sealed record SourceWarning(string Operation, string Message, int? HResult = null);

public interface IMediaSource : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    event Action<SourceWarning>? Warning;
    Task<SourceCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    IAsyncEnumerable<MediaDescriptor> EnumerateAsync(CancellationToken ct);
    Task<Stream?> TryOpenThumbnailAsync(string sourceItemKey, CancellationToken ct);
    Task<Stream> OpenOriginalAsync(string sourceItemKey, CancellationToken ct);
    Task<bool> ExistsAsync(string sourceItemKey, CancellationToken ct);
}

public static class MediaTypes
{
    private static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
        { ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".raf", ".orf", ".rw2", ".pef", ".srw", ".raw" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tif", ".tiff", ".webp", ".bmp", ".gif", ".avif" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".mts", ".m2ts", ".3gp", ".webm" };

    // Recognizing a RAW extension does not claim that its camera variant can be decoded.
    public static (MediaKind Kind, bool IsRaw)? Classify(string extension) =>
        Raw.Contains(extension) ? (MediaKind.Image, true) :
        Images.Contains(extension) ? (MediaKind.Image, false) :
        Videos.Contains(extension) ? (MediaKind.Video, false) : null;

    public static string MetadataFingerprint(string sourceId, string relativePath, long? size, string? date) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\0', sourceId, relativePath.Replace('\\', '/'), size?.ToString(System.Globalization.CultureInfo.InvariantCulture), date))));
}
