using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PhotoFlow.Core;

namespace PhotoFlow.Infrastructure.Wpd;

public sealed record PortableFolder(string Key, string Name);

/// <summary>Shared read-only P0 transport. Apple and Android acceptance remains device-specific.</summary>
[SupportedOSPlatform("windows")]
public sealed class WpdMediaSource : IMediaSource
{
    private readonly string pnpId;
    private readonly ComWorker worker = new();
    private readonly CancellationTokenSource lifetime = new();
    private IPortableDevice? device;
    private IPortableDeviceContent? content;
    private IPortableDeviceProperties? properties;
    private IPortableDeviceResources? resources;
    private IPortableDeviceKeyCollection? requested;
    private bool disposed;
    private int scanning;
    public string Id { get; }
    public string DisplayName { get; }
    public event Action<SourceWarning>? Warning;

    public WpdMediaSource(string pnpId, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(pnpId)) throw new ArgumentException("Укажите PnP ID устройства.", nameof(pnpId));
        this.pnpId = pnpId;
        DisplayName = displayName ?? "USB / WPD";
        Id = "wpd:" + MediaTypes.MetadataFingerprint("wpd", pnpId.ToUpperInvariant(), null, null);
    }

    public Task<SourceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new SourceCapabilities(true, true, false,
            "Возможности транспорта. Наличие миниатюры проверяется отдельно для каждого объекта; seek не предполагается."));
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (device is not null) return;
        var client = WpdNative.Create<IPortableDeviceValues>(WpdNative.ValuesClass);
        var candidate = WpdNative.Create<IPortableDevice>(WpdNative.DeviceFtmClass);
        try
        {
            client.SetStringValue(ref WpdNative.ClientName, "PhotoFlow P0");
            client.SetUnsignedIntegerValue(ref WpdNative.ClientMajor, 0);
            client.SetUnsignedIntegerValue(ref WpdNative.ClientMinor, 1);
            client.SetUnsignedIntegerValue(ref WpdNative.ClientRevision, 0);
            client.SetUnsignedIntegerValue(ref WpdNative.DesiredAccess, 0x80000000); // GENERIC_READ only
            candidate.Open(pnpId, client);
            candidate.Content(out content);
            content.Properties(out properties);
            content.Transfer(out resources);
            requested = WpdNative.Create<IPortableDeviceKeyCollection>(WpdNative.KeysClass);
            foreach (var property in new[] { WpdNative.Name, WpdNative.PersistentId, WpdNative.ContentType,
                WpdNative.Size, WpdNative.FileName, WpdNative.Created, WpdNative.Modified, WpdNative.Width, WpdNative.Height })
            { var key = property; requested.Add(ref key); }
            device = candidate;
        }
        catch
        {
            try { candidate.Close(); } catch (COMException) { }
            WpdNative.Release(requested); requested = null;
            WpdNative.Release(resources); resources = null;
            WpdNative.Release(properties); properties = null;
            WpdNative.Release(content); content = null;
            WpdNative.Release(candidate);
            throw;
        }
        finally { WpdNative.Release(client); }
    }

    public IAsyncEnumerable<MediaDescriptor> EnumerateAsync(CancellationToken ct) => EnumerateFolderAsync("DEVICE", "", ct);

    public async IAsyncEnumerable<MediaDescriptor> EnumerateFolderAsync(string folderKey, string folderPath, [EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref scanning, 1) != 0)
            throw new InvalidOperationException("Для источника уже выполняется сканирование.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var token = linked.Token;
        var state = new ScanState(folderKey, folderPath);
        try
        {
            while (!state.Done)
            {
                var batch = await worker.InvokeAsync(() => { EnsureOpen(); return ReadBatch(state, token); }, token);
                foreach (var item in batch) { token.ThrowIfCancellationRequested(); yield return item; }
                // Returning to the queue between batches lets visible thumbnail requests run during scanning.
            }
        }
        finally
        {
            linked.Cancel();
            await worker.InvokeAsync(() => { WpdNative.Release(state.Enumerator); state.Enumerator = null; return true; });
            Interlocked.Exchange(ref scanning, 0);
        }
    }

    private sealed class ScanState
    {
        public ScanState(string key, string path) => Pending.Push((key, path));
        public Stack<(string Id, string Path)> Pending { get; } = new();
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
        public IEnumPortableDeviceObjectIDs? Enumerator;
        public (string Id, string Path) Parent;
        public bool Done;
    }

    public Task<IReadOnlyList<PortableFolder>> ListFoldersAsync(string parentKey, CancellationToken ct) => worker.InvokeAsync<IReadOnlyList<PortableFolder>>(() =>
    {
        EnsureOpen();
        var result = new List<PortableFolder>();
        IEnumPortableDeviceObjectIDs? enumerator = null;
        var keys = WpdNative.Create<IPortableDeviceKeyCollection>(WpdNative.KeysClass);
        try
        {
            foreach (var property in new[] { WpdNative.Name, WpdNative.FileName, WpdNative.ContentType })
            { var key = property; keys.Add(ref key); }
            content!.EnumObjects(0, parentKey, null, out enumerator);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var status = enumerator.Next(1, out var pointer, out var fetched);
                string? id;
                try { Marshal.ThrowExceptionForHR(status); id = fetched > 0 ? Marshal.PtrToStringUni(pointer) : null; }
                finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
                if (id is null) break;
                IPortableDeviceValues? values = null;
                try
                {
                    properties!.GetValues(id, keys, out values);
                    var type = ReadGuid(values, WpdNative.ContentType);
                    if (type == WpdNative.Folder || type == WpdNative.Functional)
                        result.Add(new(id, ReadString(values, WpdNative.FileName) ?? ReadString(values, WpdNative.Name) ?? id));
                }
                finally { WpdNative.Release(values); }
                if (status == 1) break;
            }
            return result;
        }
        finally { WpdNative.Release(enumerator); WpdNative.Release(keys); }
    }, ct);

    private List<MediaDescriptor> ReadBatch(ScanState state, CancellationToken ct)
    {
        var batch = new List<MediaDescriptor>(32);
        for (var step = 0; step < 64 && batch.Count < 32; step++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (state.Enumerator is null)
                {
                    if (!state.Pending.TryPop(out state.Parent)) { state.Done = true; break; }
                    if (!state.Visited.Add(state.Parent.Id)) continue;
                    content!.EnumObjects(0, state.Parent.Id, null, out state.Enumerator);
                }
                var status = state.Enumerator.Next(1, out var pointer, out var fetched);
                string? id;
                try { Marshal.ThrowExceptionForHR(status); id = fetched > 0 ? Marshal.PtrToStringUni(pointer) : null; }
                finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
                if (id is not null) ReadObject(id, state.Parent.Path, state.Pending, batch.Add);
                if (id is null || status == 1) { WpdNative.Release(state.Enumerator); state.Enumerator = null; }
            }
            catch (COMException e)
            {
                Warning?.Invoke(new("enumerate subtree", e.Message, e.ErrorCode));
                WpdNative.Release(state.Enumerator); state.Enumerator = null;
                if (state.Parent.Id == "DEVICE") throw;
            }
        }
        return batch;
    }

    private void ReadObject(string id, string parentPath, Stack<(string Id, string Path)> pending, Action<MediaDescriptor> emit)
    {
        IPortableDeviceValues? values = null;
        try
        {
            properties!.GetValues(id, requested!, out values);
            var name = ReadString(values, WpdNative.FileName) ?? ReadString(values, WpdNative.Name) ?? id;
            var relative = parentPath.Length == 0 ? name : parentPath + "/" + name;
            var type = ReadGuid(values, WpdNative.ContentType);
            if (type is null || type == WpdNative.Folder || type == WpdNative.Functional)
            { pending.Push((id, relative)); return; }
            var extension = Path.GetExtension(name).ToLowerInvariant();
            var recognized = MediaTypes.Classify(extension);
            if (recognized is null && type != WpdNative.Image && type != WpdNative.Video) return;
            var kind = recognized?.Kind ?? (type == WpdNative.Video ? MediaKind.Video : MediaKind.Image);
            var raw = recognized?.IsRaw ?? false;
            var size = ReadSize(values);
            var created = ReadDate(values, WpdNative.Created);
            emit(new(id, relative, name, extension, kind, raw, size, created,
                created is null ? CaptureSource.Unknown : CaptureSource.WpdCreated,
                created is null ? Confidence.Unknown : Confidence.Inferred,
                null, // WPD VT_DATE has no reliable timezone; never label it UTC.
                MediaTypes.MetadataFingerprint(Id, relative, size, created?.ToString("O", CultureInfo.InvariantCulture)),
                ReadUInt(values, WpdNative.Width), ReadUInt(values, WpdNative.Height),
                ReadString(values, WpdNative.PersistentId), ReadDate(values, WpdNative.Modified)));
        }
        catch (COMException e) { Warning?.Invoke(new("object properties", e.Message, e.ErrorCode)); }
        finally { WpdNative.Release(values); }
    }

    public async Task<Stream?> TryOpenThumbnailAsync(string sourceItemKey, CancellationToken ct)
    {
        return await worker.InvokeAsync<Stream?>(() =>
        {
            EnsureOpen();
            IPortableDeviceKeyCollection? keys = null;
            try
            {
                resources!.GetSupportedResources(sourceItemKey, out keys);
                keys.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    keys.GetAt(i, out var key);
                    if (key.Format == WpdNative.ThumbnailResource.Format && key.Id == WpdNative.ThumbnailResource.Id)
                        return OpenResource(sourceItemKey, key);
                }
                return null; // Strict mode: NEVER fall back to WPD_RESOURCE_DEFAULT.
            }
            finally { WpdNative.Release(keys); }
        }, ct);
    }

    public Task<Stream> OpenOriginalAsync(string sourceItemKey, CancellationToken ct) => worker.InvokeAsync(() =>
    { EnsureOpen(); return OpenResource(sourceItemKey, WpdNative.DefaultResource); }, ct);

    private Stream OpenResource(string key, PropertyKey resource)
    {
        uint optimal = 0;
        resources!.GetStream(key, ref resource, 0, ref optimal, out var stream); // STGM_READ
        return new WpdReadStream(worker, stream);
    }

    public Task<bool> ExistsAsync(string sourceItemKey, CancellationToken ct) => worker.InvokeAsync(() =>
    {
        EnsureOpen();
        IPortableDeviceValues? values = null;
        try { properties!.GetValues(sourceItemKey, requested!, out values); return true; }
        catch (COMException e) when (e.ErrorCode is unchecked((int)0x80070490) or unchecked((int)0x80070002)) { return false; }
        finally { WpdNative.Release(values); }
    }, ct);

    private static string? ReadString(IPortableDeviceValues values, PropertyKey key)
    { try { values.GetStringValue(ref key, out var value); return value; } catch (COMException) { return null; } }
    private static Guid? ReadGuid(IPortableDeviceValues values, PropertyKey key)
    { try { values.GetGuidValue(ref key, out var value); return value; } catch (COMException) { return null; } }
    private static uint? ReadUInt(IPortableDeviceValues values, PropertyKey key)
    { try { values.GetUnsignedIntegerValue(ref key, out var value); return value; } catch (COMException) { return null; } }
    private static long? ReadSize(IPortableDeviceValues values)
    {
        try { values.GetUnsignedLargeIntegerValue(ref WpdNative.Size, out var value); return value <= long.MaxValue ? (long)value : null; }
        catch (COMException) { return null; }
    }
    private static DateTime? ReadDate(IPortableDeviceValues values, PropertyKey key)
    {
        var value = new PropVariant();
        try
        {
            values.GetValue(ref key, out value);
            return value.Type == 7 ? DateTime.SpecifyKind(DateTime.FromOADate(value.Date), DateTimeKind.Unspecified) : null;
        }
        catch (Exception e) when (e is COMException or ArgumentException) { return null; }
        finally { WpdNative.PropVariantClear(ref value); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try
        {
            await worker.InvokeAsync(() =>
            {
                try { device?.Close(); }
                finally
                {
                    WpdNative.Release(requested); WpdNative.Release(resources);
                    WpdNative.Release(properties); WpdNative.Release(content); WpdNative.Release(device);
                }
                return true;
            });
        }
        finally { await worker.DisposeAsync(); lifetime.Dispose(); }
    }
}
