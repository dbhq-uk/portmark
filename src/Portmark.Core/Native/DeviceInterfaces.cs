using System.Runtime.InteropServices;

namespace Portmark.Core.Native;

/// <summary>SetupAPI device and device-interface enumeration.</summary>
public static class DeviceInterfaces
{
    /// <summary>Win32 error from the most recent enumeration, 0 when it succeeded.</summary>
    public static int LastError { get; private set; }

    /// <summary>Every present device interface path for an interface class.</summary>
    public static unsafe IReadOnlyList<string> FindAll(Guid interfaceClass)
    {
        LastError = 0;
        var paths = new List<string>();
        IntPtr set = SetupApi.SetupDiGetClassDevs(
            interfaceClass, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_DEVICEINTERFACE);
        if (set == SetupApi.INVALID_HANDLE_VALUE)
        {
            LastError = Marshal.GetLastWin32Error();
            return paths;
        }

        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
                };

                if (!SetupApi.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, interfaceClass, i, ref data))
                    break;

                SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref data, null, 0, out uint needed, null);
                if (needed == 0) continue;

                byte[] buffer = new byte[needed];
                fixed (byte* p = buffer)
                {
                    // cbSize is the size of the fixed part only: 8 on 64-bit, 6 on 32-bit.
                    *(uint*)p = (uint)(IntPtr.Size == 8 ? 8 : 6);
                    if (SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref data, p, needed, out _, null))
                        paths.Add(new string((char*)(p + 4)));
                }
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    public static string? FindFirst(Guid interfaceClass) => FindAll(interfaceClass).FirstOrDefault();

    /// <summary>Instance IDs of present devices in a device setup class.</summary>
    public static unsafe IReadOnlyList<string> FindDevicesInClass(Guid setupClass)
    {
        LastError = 0;
        var ids = new List<string>();
        IntPtr set = SetupApi.SetupDiGetClassDevs(setupClass, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT);
        if (set == SetupApi.INVALID_HANDLE_VALUE)
        {
            LastError = Marshal.GetLastWin32Error();
            return ids;
        }

        char* buffer = stackalloc char[512];

        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupApi.SetupDiEnumDeviceInfo(set, i, ref info))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 259) LastError = err;   // 259 == ERROR_NO_MORE_ITEMS, the normal end
                    break;
                }

                if (SetupApi.SetupDiGetDeviceInstanceId(set, ref info, buffer, 512, out _))
                    ids.Add(new string(buffer));
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        return ids;
    }
}
