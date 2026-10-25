using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PhotoFlow.Infrastructure.Wpd;

public sealed record PortableDeviceInfo(string PnpId, string FriendlyName, string Manufacturer);

[SupportedOSPlatform("windows")]
public static class WpdDiscovery
{
    public static async Task<IReadOnlyList<PortableDeviceInfo>> DiscoverAsync(CancellationToken ct)
    {
        await using var worker = new ComWorker();
        return await worker.InvokeAsync<IReadOnlyList<PortableDeviceInfo>>(() =>
        {
            var manager = WpdNative.Create<IPortableDeviceManager>(WpdNative.ManagerClass);
            try
            {
                manager.RefreshDeviceList();
                uint count = 0;
                manager.GetDevices(IntPtr.Zero, ref count);
                if (count == 0) return [];
                if (count > 4096) throw new InvalidDataException("Некорректное количество WPD-устройств.");
                var capacity = count;
                var pointers = Marshal.AllocCoTaskMem(checked((int)capacity * IntPtr.Size));
                try
                {
                    for (var i = 0; i < capacity; i++) Marshal.WriteIntPtr(pointers, i * IntPtr.Size, IntPtr.Zero);
                    manager.GetDevices(pointers, ref count);
                    if (count > capacity) throw new IOException("Список устройств изменился; повторите обнаружение.");
                    var result = new List<PortableDeviceInfo>();
                    for (var i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var id = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointers, i * IntPtr.Size));
                        if (id is null) continue;
                        result.Add(new(id, ReadText(id, manager.GetDeviceFriendlyName), ReadText(id, manager.GetDeviceManufacturer)));
                    }
                    return result;
                }
                finally
                {
                    for (var i = 0; i < capacity; i++)
                    {
                        var pointer = Marshal.ReadIntPtr(pointers, i * IntPtr.Size);
                        if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
                    }
                    Marshal.FreeCoTaskMem(pointers);
                }
            }
            finally { WpdNative.Release(manager); }
        }, ct);
    }

    private delegate void TextGetter(string id, StringBuilder? text, ref uint length);
    private static string ReadText(string id, TextGetter getter)
    {
        try
        {
            uint size = 0;
            getter(id, null, ref size);
            if (size == 0 || size > 65536) return "";
            var buffer = new StringBuilder((int)size);
            getter(id, buffer, ref size);
            return buffer.ToString();
        }
        catch (COMException) { return ""; }
    }
}
