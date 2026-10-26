using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace PhotoFlow.Infrastructure.Wpd;

// Read-only COM surface, in native vtable order. Declarations checked against Windows SDK
// 10.0.26100.0 PortableDeviceApi.h / PortableDeviceTypes.h. No Shell filesystem emulation.
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid format, uint id)
{
    public Guid Format = format;
    public uint Id = id;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public double Date;
}

[SupportedOSPlatform("windows")]
internal static class WpdNative
{
    internal static readonly Guid ManagerClass = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
    internal static readonly Guid DeviceFtmClass = new("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
    internal static readonly Guid ValuesClass = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    internal static readonly Guid KeysClass = new("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
    internal static readonly Guid Image = new("ef2107d5-a52a-4243-a26b-62d4176d7603");
    internal static readonly Guid Video = new("9261b03c-3d78-4519-85e3-02c5e1f50bb9");
    internal static readonly Guid Folder = new("27e2e392-a111-48e0-ab0c-e17705a05f85");
    internal static readonly Guid Functional = new("99ed0160-17ff-4c44-9d98-1d7a6f941921");
    private static readonly Guid ObjectFormat = new("ef6b490d-5cd8-437a-affc-da8b60ee4a3c");
    private static readonly Guid ClientFormat = new("204d9f0c-2292-4080-9f42-40664e70f859");
    internal static PropertyKey Name = new(ObjectFormat, 4);
    internal static PropertyKey PersistentId = new(ObjectFormat, 5);
    internal static PropertyKey ContentType = new(ObjectFormat, 7);
    internal static PropertyKey Size = new(ObjectFormat, 11);
    internal static PropertyKey Hidden = new(ObjectFormat, 9);
    internal static PropertyKey System = new(ObjectFormat, 10);
    internal static PropertyKey FileName = new(ObjectFormat, 12);
    internal static PropertyKey Created = new(ObjectFormat, 18);
    internal static PropertyKey Modified = new(ObjectFormat, 19);
    internal static PropertyKey Width = new(new("2ed8ba05-0ad3-42dc-b0d0-bc95ac396ac8"), 22);
    internal static PropertyKey Height = new(new("2ed8ba05-0ad3-42dc-b0d0-bc95ac396ac8"), 23);
    internal static PropertyKey ClientName = new(ClientFormat, 2);
    internal static PropertyKey ClientMajor = new(ClientFormat, 3);
    internal static PropertyKey ClientMinor = new(ClientFormat, 4);
    internal static PropertyKey ClientRevision = new(ClientFormat, 5);
    internal static PropertyKey DesiredAccess = new(ClientFormat, 9);
    internal static PropertyKey DefaultResource = new(new("e81e79be-34f0-41bf-b53f-f1a06ae87842"), 0);
    internal static PropertyKey ThumbnailResource = new(new("c7c407ba-98fa-46b5-9960-23fec124cfde"), 0);

    internal static T Create<T>(Guid clsid) => (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
    internal static void Release(object? value)
    { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);
}

[ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    void GetDevices(IntPtr devices, ref uint count);
    void RefreshDeviceList();
    void GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder? name, ref uint length);
    void GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string id, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder? name, ref uint length);
    void GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string id, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder? name, ref uint length);
}

[ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    void Open([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceValues client);
    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues result);
    void Content(out IPortableDeviceContent content);
    void Capabilities([MarshalAs(UnmanagedType.Interface)] out object capabilities);
    void Cancel();
    void Close();
}

[ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parent, IPortableDeviceValues? filter, out IEnumPortableDeviceObjectIDs enumerator);
    void Properties(out IPortableDeviceProperties properties);
    void Transfer(out IPortableDeviceResources resources);
}

[ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPortableDeviceObjectIDs
{
    [PreserveSig] int Next(uint count, out IntPtr objectId, out uint fetched);
}

[ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string id, out IPortableDeviceKeyCollection keys);
    void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out IPortableDeviceValues attributes);
    void GetValues([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceKeyCollection keys, out IPortableDeviceValues values);
}

[ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceResources
{
    void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string id, out IPortableDeviceKeyCollection keys);
    void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out IPortableDeviceValues values);
    void GetStream([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, uint mode, ref uint optimalBuffer, out IStream stream);
}

[ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void Add(ref PropertyKey key);
}

[ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    void GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    void SetSignedIntegerValue(ref PropertyKey key, int value);
    void GetSignedIntegerValue(ref PropertyKey key, out int value);
    void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
    void GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
    void SetSignedLargeIntegerValue(ref PropertyKey key, long value);
    void GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
    void SetFloatValue(ref PropertyKey key, float value);
    void GetFloatValue(ref PropertyKey key, out float value);
    void SetErrorValue(ref PropertyKey key, int value);
    void GetErrorValue(ref PropertyKey key, out int value);
    void SetKeyValue(ref PropertyKey key, ref PropertyKey value);
    void GetKeyValue(ref PropertyKey key, out PropertyKey value);
    void SetBoolValue(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] bool value);
    void GetBoolValue(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] out bool value);
    void SetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    void GetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    void SetGuidValue(ref PropertyKey key, ref Guid value);
    void GetGuidValue(ref PropertyKey key, out Guid value);
}
