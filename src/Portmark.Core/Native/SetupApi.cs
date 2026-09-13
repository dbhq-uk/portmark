using System.Runtime.InteropServices;

namespace Portmark.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVICE_INTERFACE_DATA
{
    public uint cbSize;
    public Guid InterfaceClassGuid;
    public uint Flags;
    public nuint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVINFO_DATA
{
    public uint cbSize;
    public Guid ClassGuid;
    public uint DevInst;
    public nuint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;
    public DEVPROPKEY(string guid, uint id) { fmtid = new Guid(guid); pid = id; }
}

/// <summary>SetupAPI device and device-interface enumeration.</summary>
internal static partial class SetupApi
{
    internal const uint DIGCF_DEFAULT = 0x01;
    internal const uint DIGCF_PRESENT = 0x02;
    internal const uint DIGCF_ALLCLASSES = 0x04;
    internal const uint DIGCF_DEVICEINTERFACE = 0x10;

    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // DEVPKEY_Device_InstanceId, _DeviceDesc, _FriendlyName, _Service, _DriverVersion, _HardwareIds
    internal static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    internal static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    internal static readonly DEVPROPKEY DEVPKEY_Device_Service = new("a45c254e-df1c-4efd-8020-67d146a850e0", 6);
    internal static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 3);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    internal static partial IntPtr SetupDiGetClassDevs(
        in Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    internal static partial IntPtr SetupDiGetClassDevsAll(
        IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInfo(
        IntPtr devInfo, uint memberIndex, ref SP_DEVINFO_DATA devInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr devInfo, IntPtr devInfoData, in Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetupDiGetDeviceInterfaceDetail(
        IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA interfaceData,
        byte* detailData, uint detailDataSize, out uint required, SP_DEVINFO_DATA* devInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetupDiGetDeviceInstanceId(
        IntPtr devInfo, ref SP_DEVINFO_DATA devInfoData,
        char* buffer, uint bufferSize, out uint required);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetupDiGetDeviceProperty(
        IntPtr devInfo, ref SP_DEVINFO_DATA devInfoData, in DEVPROPKEY propertyKey,
        out uint propertyType, byte* buffer, uint bufferSize, out uint required, uint flags);
}
