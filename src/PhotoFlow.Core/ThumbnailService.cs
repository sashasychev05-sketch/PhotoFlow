namespace PhotoFlow.Core;

public enum ThumbnailStatus { Available, Unavailable }
public sealed record ThumbnailResult(ThumbnailStatus Status, byte[]? Bytes, string Note);

/// <summary>P0 always uses the strict policy: a missing thumbnail never triggers original reads.</summary>
public sealed class ThumbnailService(int maxBytes = 16 * 1024 * 1024)
{
    public async Task<ThumbnailResult> ReadAsync(IMediaSource source, string key, CancellationToken ct)
    {
        await using var thumbnail = await source.TryOpenThumbnailAsync(key, ct);
        if (thumbnail is null)
            return new(ThumbnailStatus.Unavailable, null, "Устройство не предоставило миниатюру. Оригинал не загружался.");
        using var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await thumbnail.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (result.Length + read > maxBytes)
                throw new InvalidDataException("Миниатюра превышает установленный лимит.");
            result.Write(buffer, 0, read);
        }
        if (result.Length == 0)
            return new(ThumbnailStatus.Unavailable, null, "Пустой ресурс миниатюры. Оригинал не загружался.");
        return new(ThumbnailStatus.Available, result.ToArray(), "Ресурс миниатюры источника; формат байтов не перекодирован.");
    }
}
