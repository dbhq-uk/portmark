using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Portmark.Core.Native;

namespace Portmark.Core.Ucsi;

/// <summary>The outcome of one UCSI command.</summary>
public sealed record UcsiResult(bool Ok, uint Cci, byte[] Payload, string? Error)
{
    public int DataLength => (int)((Cci >> 8) & 0xFF);
    public bool Errored => (Cci & UcsiProtocol.CciError) != 0;
    public bool NotSupported => (Cci & UcsiProtocol.CciNotSupported) != 0;

    /// <summary>
    /// The command completed but the PPM returned nothing. That is a real answer meaning "there is
    /// nothing to report", and must never be decoded as though it were field data.
    /// </summary>
    public bool NoPayload => DataLength == 0 || Payload.All(b => b == 0);

    public static UcsiResult Fail(string error) => new(false, 0, [], error);
}

/// <summary>
/// Talks to the UCSI PPM through the in-box test interface.
///
/// Three behaviours of that interface are load-bearing, all established empirically and all
/// non-obvious enough to be worth stating:
///
///   1. Each command needs a freshly opened handle. A handle tolerates only a couple of calls
///      before every further call returns STATUS_INVALID_DEVICE_STATE.
///   2. A command issued while the PPM is still settling from the previous one returns the same
///      status. Retrying with a short delay clears it.
///   3. Control code 0x404 must never be called. It succeeds, and then poisons the handle so that
///      every subsequent data-block call fails.
/// </summary>
public sealed class UcsiConnection
{
    private const int ErrorInvalidDeviceState = 22;   // Win32 view of STATUS_INVALID_DEVICE_STATE
    private const int MaxAttempts = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Whether to send ACK_CC_CI after a completed command. Off, and it should stay off.
    ///
    /// UCSI requires the acknowledgement handshake of the operating system's policy manager, and
    /// on Windows that is UcmUcsiCx, which is driving this same controller. Sending it ourselves
    /// steals a completion Windows was waiting for: measured over fifteen seconds after a full
    /// read, acknowledging made the device drop out of PnP enumeration for about two seconds,
    /// while not acknowledging left it enumerable the whole time. Reads succeed either way,
    /// because Windows performs the handshake for us.
    ///
    /// The switch remains so the measurement can be repeated: 'portmark stress' versus
    /// 'portmark stress --no-ack'.
    /// </summary>
    public static bool SendAcknowledgements { get; set; }

    private readonly string _interfacePath;

    private UcsiConnection(string interfacePath) => _interfacePath = interfacePath;

    /// <summary>The device interface path, useful in diagnostics.</summary>
    public string InterfacePath => _interfacePath;

    /// <summary>
    /// Finds the UCSI test interface, or returns null when it is not published. Not being
    /// published usually means TestInterfaceEnabled is not set.
    /// </summary>
    public static UcsiConnection? TryOpen()
    {
        string? path = DeviceInterfaces.FindFirst(UcsiProtocol.TestInterface);
        return path is null ? null : new UcsiConnection(path);
    }

    /// <summary>Reads the data block without issuing a command, to check the PPM answers at all.</summary>
    public UcsiResult ReadState()
    {
        byte[] block = new byte[UcsiProtocol.BlockSize];
        if (!TryDataBlock(block, out byte[]? result, out string? error) || result is null)
            return UcsiResult.Fail(error ?? "the test interface refused the data block call");

        uint cci = BitConverter.ToUInt32(result, UcsiProtocol.OffsetCci);
        return new UcsiResult(true, cci, result, null);
    }

    /// <summary>The UCSI version the PPM reports, or null when it cannot be read.</summary>
    public ushort? ReadVersion()
    {
        UcsiResult state = ReadState();
        return state.Ok ? BitConverter.ToUInt16(state.Payload, UcsiProtocol.OffsetVersion) : null;
    }

    /// <summary>
    /// Issues one read-only UCSI command and returns its MESSAGE_IN payload, retrying while the
    /// PPM reports an invalid device state.
    /// </summary>
    public UcsiResult Execute(byte command, ulong control)
    {
        UcsiResult last = UcsiResult.Fail("not attempted");

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            last = ExecuteOnce(control);
            if (last.Ok) return last;
            Thread.Sleep(RetryDelay);
        }

        return last with
        {
            Error = $"{UcsiProtocol.CommandName(command)} was refused after {MaxAttempts} attempts: {last.Error}",
        };
    }

    public UcsiResult Execute(byte command) => Execute(command, UcsiProtocol.Control(command));

    public UcsiResult ExecuteForConnector(byte command, byte connector)
        => Execute(command, UcsiProtocol.ForConnector(command, connector));

    private UcsiResult ExecuteOnce(ulong control)
    {
        byte[] block = new byte[UcsiProtocol.BlockSize];
        BitConverter.TryWriteBytes(block.AsSpan(UcsiProtocol.OffsetControl), control);

        if (!TryDataBlock(block, out byte[]? result, out string? error) || result is null)
            return UcsiResult.Fail(error ?? "the data block call was refused");

        uint cci = BitConverter.ToUInt32(result, UcsiProtocol.OffsetCci);

        // Wait out a busy PPM. Read-only commands settle almost immediately.
        for (int i = 0; i < 50 && (cci & UcsiProtocol.CciBusy) != 0
                              && (cci & UcsiProtocol.CciCommandCompleted) == 0; i++)
        {
            Thread.Sleep(10);
            byte[] poll = new byte[UcsiProtocol.BlockSize];
            if (!TryDataBlock(poll, out byte[]? polled, out _) || polled is null) break;
            result = polled;
            cci = BitConverter.ToUInt32(result, UcsiProtocol.OffsetCci);
        }

        // Trust the declared length. MESSAGE_IN keeps whatever the previous command left there,
        // so returning the full register when the PPM declared zero bytes would hand callers
        // stale data dressed up as an answer.
        int length = (int)((cci >> 8) & 0xFF);
        byte[] payload = length is > 0 and <= UcsiProtocol.MessageInSize
            ? result.AsSpan(UcsiProtocol.OffsetMessageIn, length).ToArray()
            : [];

        if (SendAcknowledgements && (cci & UcsiProtocol.CciCommandCompleted) != 0) Acknowledge();

        return new UcsiResult(true, cci, payload, null);
    }

    /// <summary>Tells the PPM the completed command has been read, so it can accept the next one.</summary>
    private void Acknowledge()
    {
        byte[] ack = new byte[UcsiProtocol.BlockSize];
        BitConverter.TryWriteBytes(ack.AsSpan(UcsiProtocol.OffsetControl),
                                   UcsiProtocol.AcknowledgeCommandCompleted());
        TryDataBlock(ack, out _, out _);
    }

    /// <summary>One data-block IOCTL on a freshly opened handle.</summary>
    private unsafe bool TryDataBlock(byte[] block, out byte[]? output, out string? error)
    {
        output = null;
        error = null;

        SafeFileHandle handle = Win32.CreateFile(
            _interfacePath, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
            Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = $"could not open the UCSI test interface: {Win32.Describe(Marshal.GetLastWin32Error())}";
            handle.Dispose();
            return false;
        }

        using (handle)
        {
            byte[] buffer = (byte[])block.Clone();
            bool ok;
            int err;

            fixed (byte* p = buffer)
            {
                ok = Win32.DeviceIoControl(handle, UcsiProtocol.IoctlDataBlock,
                                           p, (uint)buffer.Length, p, (uint)buffer.Length,
                                           out _, IntPtr.Zero);
                err = Marshal.GetLastWin32Error();
            }

            if (!ok)
            {
                error = err == ErrorInvalidDeviceState
                    ? "the PPM reported an invalid device state (it is busy or still settling)"
                    : Win32.Describe(err);
                return false;
            }

            output = buffer;
            return true;
        }
    }
}

/// <summary>Registry-level facts about the UCM-UCSI device, needed before any IOCTL is possible.</summary>
public static class UcsiDevice
{
    public const string AcpiInstanceId = @"ACPI\USBC000\0";
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>Device setup class GUID for USB Connector Manager devices.</summary>
    public static readonly Guid UcmSetupClass = new("e6f1aa1c-7f3b-4473-b2e8-c97d8ac71d53");

    /// <summary>Instance IDs of the UCM devices present, usually exactly one.</summary>
    public static IReadOnlyList<string> FindDevices() => DeviceInterfaces.FindDevicesInClass(UcmSetupClass);

    public static bool IsTestInterfaceEnabled(string instanceId)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{instanceId}\Device Parameters");
        return key?.GetValue("TestInterfaceEnabled") is int value && value == 1;
    }

    public static string FlagKeyPath(string instanceId)
        => $@"HKEY_LOCAL_MACHINE\{EnumRoot}\{instanceId}\Device Parameters";

    /// <summary>
    /// Restarts the device so a TestInterfaceEnabled change takes effect. pnputil ships in
    /// System32 on every supported Windows, and PnP republishes the interfaces within a couple of
    /// seconds of the restart completing.
    /// </summary>
    public static bool RestartDevice(string instanceId, out string? error)
    {
        error = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "pnputil.exe", $"/restart-device \"{instanceId}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using System.Diagnostics.Process? p = System.Diagnostics.Process.Start(psi);
            if (p is null) { error = "could not start pnputil"; return false; }
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            Thread.Sleep(2000);
            if (p.ExitCode != 0) error = $"pnputil exited with {p.ExitCode}";
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The whole extended-tier switch in one call: flag plus device restart. Requires
    /// administrator rights, which is the caller's problem to arrange; this reports rather than
    /// prompts.
    /// </summary>
    public static bool SetExtendedTier(bool enabled, out string? error)
    {
        IReadOnlyList<string> devices = FindDevices();
        if (devices.Count == 0)
        {
            error = "no USB-C connector manager device on this PC";
            return false;
        }

        if (!SetTestInterfaceEnabled(devices[0], enabled, out error)) return false;
        return RestartDevice(devices[0], out error);
    }

    /// <summary>Sets or clears the flag. Requires administrator rights; returns the failure reason.</summary>
    public static bool SetTestInterfaceEnabled(string instanceId, bool enabled, out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key =
                Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{instanceId}\Device Parameters", writable: true);
            if (key is null)
            {
                error = "could not open the device parameters key";
                return false;
            }

            if (enabled) key.SetValue("TestInterfaceEnabled", 1, RegistryValueKind.DWord);
            else key.DeleteValue("TestInterfaceEnabled", throwOnMissingValue: false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "access denied. This step needs administrator rights.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
