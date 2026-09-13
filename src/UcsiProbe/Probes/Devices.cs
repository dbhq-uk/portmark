using System.Runtime.InteropServices;
using Microsoft.Win32;
using UcsiProbe.Native;

namespace UcsiProbe.Probes;

/// <summary>SetupAPI-based device and device-interface discovery.</summary>
internal static class Devices
{
    /// <summary>Device setup class GUID for USB Connector Manager devices.</summary>
    internal static readonly Guid UcmSetupClass = new("e6f1aa1c-7f3b-4473-b2e8-c97d8ac71d53");

    private const string DeviceClassesKey = @"SYSTEM\CurrentControlSet\Control\DeviceClasses";
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum";

    internal static List<UcmDevice> EnumerateUcmDevices()
    {
        var results = new List<UcmDevice>();
        IntPtr set = SetupApi.SetupDiGetClassDevs(UcmSetupClass, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT);
        if (set == SetupApi.INVALID_HANDLE_VALUE) return results;

        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupApi.SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                string? id = GetInstanceId(set, ref info);
                if (id is null) continue;

                var dev = new UcmDevice
                {
                    InstanceId = id,
                    Description = GetStringProperty(set, ref info, SetupApi.DEVPKEY_Device_DeviceDesc),
                    Service = GetStringProperty(set, ref info, SetupApi.DEVPKEY_Device_Service),
                    DriverVersion = GetStringProperty(set, ref info, SetupApi.DEVPKEY_Device_DriverVersion),
                    TestInterfaceEnabled = ReadTestInterfaceFlag(id) == 1,
                };
                dev.InterfaceClasses.AddRange(FindInterfaceClasses(id));
                results.Add(dev);
                info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            }
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
        return results;
    }

    /// <summary>Reads the TestInterfaceEnabled DWORD, or null when the value is absent.</summary>
    internal static int? ReadTestInterfaceFlag(string instanceId)
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{instanceId}\Device Parameters");
        return k?.GetValue("TestInterfaceEnabled") as int?;
    }

    internal static string TestInterfaceFlagKeyPath(string instanceId)
        => $@"HKEY_LOCAL_MACHINE\{EnumKey}\{instanceId}\Device Parameters\TestInterfaceEnabled";

    /// <summary>
    /// Lists the device interface class GUIDs the given device instance currently publishes,
    /// by reading the DeviceClasses registry tree. Used to diff before/after the test flag.
    /// </summary>
    internal static List<string> FindInterfaceClasses(string instanceId)
    {
        var found = new List<string>();
        string needle = "##?#" + instanceId.Replace('\\', '#') + "#";
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(DeviceClassesKey);
        if (root is null) return found;

        foreach (string cls in root.GetSubKeyNames())
        {
            using RegistryKey? ck = root.OpenSubKey(cls);
            if (ck is null) continue;
            foreach (string inst in ck.GetSubKeyNames())
            {
                if (inst.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(cls);
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>Enumerates present device interface paths for an interface class GUID.</summary>
    internal static unsafe List<string> EnumerateInterfacePaths(Guid interfaceClass, out int win32Error)
    {
        win32Error = 0;
        var paths = new List<string>();
        IntPtr set = SetupApi.SetupDiGetClassDevs(
            interfaceClass, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_DEVICEINTERFACE);
        if (set == SetupApi.INVALID_HANDLE_VALUE) { win32Error = Marshal.GetLastWin32Error(); return paths; }

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; ; i++)
            {
                ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupApi.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, interfaceClass, i, ref ifData))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Win32.ERROR_NO_MORE_ITEMS) win32Error = err;
                    break;
                }

                SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref ifData, null, 0, out uint needed, null);
                if (needed == 0) continue;

                byte[] buffer = new byte[needed];
                fixed (byte* p = buffer)
                {
                    // cbSize is the size of the fixed part of the struct: 8 on 64-bit, 6 on 32-bit.
                    *(uint*)p = (uint)(IntPtr.Size == 8 ? 8 : 6);
                    if (SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref ifData, p, needed, out _, null))
                        paths.Add(new string((char*)(p + 4)));
                    else
                        win32Error = Marshal.GetLastWin32Error();
                }
            }
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    private static unsafe string? GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        char* buf = stackalloc char[512];
        return SetupApi.SetupDiGetDeviceInstanceId(set, ref info, buf, 512, out _) ? new string(buf) : null;
    }

    private static unsafe string? GetStringProperty(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        byte[] buf = new byte[1024];
        fixed (byte* p = buf)
        {
            if (!SetupApi.SetupDiGetDeviceProperty(set, ref info, key, out _, p, (uint)buf.Length, out uint needed, 0))
                return null;
            return new string((char*)p).TrimEnd('\0');
        }
    }
}
