using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Portmark.Core.Native;

/// <summary>One battery device as it answered, before any decoding.</summary>
internal sealed record BatteryDeviceReading(
    string Path, uint? Tag, byte[]? Information, byte[]? Status,
    string? DeviceName, string? ManufacturerName, string? Error, bool Absent);

/// <summary>
/// The battery class driver's IOCTL interface, following Microsoft's "Enumerating Battery Devices":
/// list GUID_DEVICE_BATTERY interfaces, open each, ask for its tag, then query information and
/// status under that tag.
///
/// Every request here is a query. IOCTL_BATTERY_SET_INFORMATION exists and is not used, and the
/// device is opened for reading only: the three queries need FILE_READ_ACCESS and nothing more,
/// where Microsoft's sample also asks for write access it does not use.
/// </summary>
internal static unsafe class BatteryDevices
{
    /// <summary>GUID_DEVICE_BATTERY in poclass.h, which batclass.h also names GUID_DEVINTERFACE_BATTERY.</summary>
    internal static readonly Guid BatteryInterface = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    private const uint FILE_DEVICE_BATTERY = 0x29;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_READ_ACCESS = 1;

    private static readonly uint IoctlQueryTag = Win32.CtlCode(FILE_DEVICE_BATTERY, 0x10, METHOD_BUFFERED, FILE_READ_ACCESS);
    private static readonly uint IoctlQueryInformation = Win32.CtlCode(FILE_DEVICE_BATTERY, 0x11, METHOD_BUFFERED, FILE_READ_ACCESS);
    private static readonly uint IoctlQueryStatus = Win32.CtlCode(FILE_DEVICE_BATTERY, 0x13, METHOD_BUFFERED, FILE_READ_ACCESS);

    // BATTERY_QUERY_INFORMATION_LEVEL
    private const uint BatteryInformation = 0;
    private const uint BatteryDeviceName = 4;
    private const uint BatteryManufactureName = 6;

    private const uint BatteryTagInvalid = 0;

    internal static IReadOnlyList<BatteryDeviceReading> ReadAll(out int enumerationError)
    {
        IReadOnlyList<string> paths = DeviceInterfaces.FindAll(BatteryInterface);
        enumerationError = DeviceInterfaces.LastError;
        return paths.Select(Read).ToList();
    }

    private static BatteryDeviceReading Read(string path)
    {
        using SafeFileHandle device = Win32.CreateFile(
            path, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
            Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (device.IsInvalid)
            return new(path, null, null, null, null, null,
                       $"The battery device could not be opened: {Win32.Describe(Marshal.GetLastPInvokeError())}", false);

        // A wait of zero: if the slot is empty, say so now rather than waiting for a battery.
        uint wait = 0, tag = BatteryTagInvalid;
        if (!Win32.DeviceIoControl(device, IoctlQueryTag, &wait, sizeof(uint), &tag, sizeof(uint), out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == Win32.ERROR_FILE_NOT_FOUND
                ? new(path, null, null, null, null, null, null, true)
                : new(path, null, null, null, null, null,
                      $"The battery did not report its tag: {Win32.Describe(error)}", false);
        }
        if (tag == BatteryTagInvalid) return new(path, null, null, null, null, null, null, true);

        var errors = new List<string>();

        // Room beyond sizeof(BATTERY_INFORMATION), which is 36 bytes. The first version of this
        // reader miscounted the structure as 32 and sized its buffer to that; the ThinkPad T16 Gen 2
        // (AMD) refused it with ERROR_INSUFFICIENT_BUFFER, so no capacity was read at all. Once given
        // room it returned 36 bytes, whose last four were then wrongly called undocumented: they are
        // CycleCount. Anything a driver returns beyond the 36 is kept in the raw hex and not decoded.
        byte[]? information = Query(device, tag, BatteryInformation, 256, out int infoError);
        if (information is null) errors.Add($"The battery did not answer the information query: {Win32.Describe(infoError)}");

        // Optional levels: a battery that does not keep a name answers ERROR_INVALID_FUNCTION.
        string? name = Text(Query(device, tag, BatteryDeviceName, 512, out _));
        string? maker = Text(Query(device, tag, BatteryManufactureName, 512, out _));

        byte[]? status = QueryStatus(device, tag, out int statusError);
        if (status is null) errors.Add($"The battery did not answer the status query: {Win32.Describe(statusError)}");

        return new(path, tag, information, status, name, maker,
                   errors.Count > 0 ? string.Join(" ", errors) : null, false);
    }

    /// <summary>IOCTL_BATTERY_QUERY_INFORMATION with a BATTERY_QUERY_INFORMATION: tag, level, AtRate 0.</summary>
    private static byte[]? Query(SafeFileHandle device, uint tag, uint level, int size, out int error)
    {
        uint* request = stackalloc uint[3];
        request[0] = tag;
        request[1] = level;
        request[2] = 0;

        byte[] buffer = new byte[size];
        fixed (byte* output = buffer)
        {
            if (!Win32.DeviceIoControl(device, IoctlQueryInformation, request, 3 * sizeof(uint),
                                       output, (uint)size, out uint returned, IntPtr.Zero))
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            error = 0;
            return buffer[..(int)Math.Min(returned, (uint)size)];
        }
    }

    /// <summary>
    /// IOCTL_BATTERY_QUERY_STATUS with a BATTERY_WAIT_STATUS whose timeout is zero, which Microsoft
    /// defines as "return immediately, regardless of the other conditions".
    /// </summary>
    private static byte[]? QueryStatus(SafeFileHandle device, uint tag, out int error)
    {
        uint* request = stackalloc uint[5];
        request[0] = tag;
        for (int i = 1; i < 5; i++) request[i] = 0;

        byte[] buffer = new byte[16];
        fixed (byte* output = buffer)
        {
            if (!Win32.DeviceIoControl(device, IoctlQueryStatus, request, 5 * sizeof(uint),
                                       output, (uint)buffer.Length, out uint returned, IntPtr.Zero))
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            error = 0;
            return buffer[..(int)Math.Min(returned, (uint)buffer.Length)];
        }
    }

    /// <summary>A null-terminated UTF-16 string, or null when absent or empty.</summary>
    private static string? Text(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 2) return null;
        string text = System.Text.Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        int end = text.IndexOf('\0');
        if (end >= 0) text = text[..end];
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }
}
