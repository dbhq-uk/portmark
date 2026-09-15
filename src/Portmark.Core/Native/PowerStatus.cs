using System.Runtime.InteropServices;

namespace Portmark.Core.Native;

/// <summary>
/// The system power status, read through kernel32's <c>GetSystemPowerStatus</c>.
///
/// Used for one purpose: to tell a nearly full battery, which correctly draws very little, apart
/// from a battery that is not full and still is not being charged. Without it portmark can only
/// offer both as possibilities, and on the machine this was written for it would have offered the
/// wrong one. The charge percentage settles it in a single reading, with no sampling over time.
///
/// Deliberately not used to decide whether the battery is charging. Windows reported this machine
/// as charging while its battery fell from 46 percent to 43 percent, and the WMI charge rate read
/// zero throughout, so neither is evidence of anything.
/// </summary>
internal static partial class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private const byte NoBattery = 128;
    private const byte PercentUnknown = 255;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>
    /// The battery charge as a percentage, or null when there is no battery or Windows does not
    /// know. Null means unknown and is never treated as empty or as full.
    /// </summary>
    internal static int? BatteryPercent()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status)) return null;
        if ((status.BatteryFlag & NoBattery) != 0) return null;
        if (status.BatteryLifePercent == PercentUnknown || status.BatteryLifePercent > 100) return null;
        return status.BatteryLifePercent;
    }
}
