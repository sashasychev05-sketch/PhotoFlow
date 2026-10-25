using System.Buffers;
using System.Security.Cryptography;

namespace PhotoFlow.Core;

public sealed record TransferResult(string Path, long Bytes, string Sha256);

/// <summary>One explicitly chosen file. A persistent, reconnectable job queue is a later stage.</summary>
public sealed class SelectedFileTransfer
{
    public async Task<TransferResult> CopyAsync(IMediaSource source, string sourceKey, string destination,
        long? expectedBytes, IProgress<long>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("Файл назначения уже существует. Укажите другое имя.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var part = target + "." + Guid.NewGuid().ToString("N") + ".part";
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            await using var original = await source.OpenOriginalAsync(sourceKey, ct);
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await original.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    progress?.Report(total);
                }
                if (expectedBytes is { } expected && total != expected)
                    throw new InvalidDataException($"Размер не совпал: ожидалось {expected}, получено {total} байт.");
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            // Verify persisted destination bytes, not only the bytes received from the source.
            await using (var verify = File.OpenRead(part))
            {
                var persisted = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct));
                if (persisted != digest) throw new InvalidDataException("Проверка SHA-256 записанного файла не пройдена.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(part, target, overwrite: false);
            return new(target, total, digest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            // Clean only the temporary file owned by this transfer.
            if (File.Exists(part)) File.Delete(part);
        }
    }
}
