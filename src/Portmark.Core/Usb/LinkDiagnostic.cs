namespace Portmark.Core.Usb;

/// <summary>
/// Compares what a device says it is capable of against the speed it actually negotiated.
///
/// This is the question people actually have when something feels slow. A drive that reports
/// USB 3.2 in its device descriptor but negotiated High Speed is running at roughly a twentieth of
/// its capability, and the usual cause is a USB 2.0 cable or a USB 2.0 hub somewhere in the chain.
/// Nothing else in the system tells you that; Windows simply runs it slowly and says nothing.
///
/// The comparison is only meaningful because both halves come from the hardware: bcdUSB from the
/// device's own descriptor, and the negotiated speed from the hub. Neither is inferred.
/// </summary>
public static class LinkDiagnostic
{
    // Negotiated speed codes as the hub reports them.
    public const byte SpeedLow = 0;
    public const byte SpeedFull = 1;
    public const byte SpeedHigh = 2;
    public const byte SpeedSuper = 3;

    /// <summary>The speed a device's declared USB version entitles it to.</summary>
    public static byte ExpectedSpeed(ushort bcdUsb) => bcdUsb switch
    {
        >= 0x0300 => SpeedSuper,
        >= 0x0200 => SpeedHigh,
        >= 0x0110 => SpeedFull,
        _ => SpeedLow,
    };

    public static string SpeedLabel(byte speed) => speed switch
    {
        SpeedLow => "1.5 Mbps",
        SpeedFull => "12 Mbps",
        SpeedHigh => "480 Mbps",
        SpeedSuper => "5 Gbps or above",
        _ => "unknown",
    };

    /// <summary>
    /// True when the device negotiated slower than its declared version allows.
    ///
    /// Two classes are deliberately excluded. A Billboard device exists only to describe alternate
    /// modes and is specified to attach at low speed, so it is never a fault. Hubs are excluded
    /// because a hub running below its rating is a symptom of the cable feeding it, which is
    /// reported against that cable rather than twice.
    /// </summary>
    public static bool IsUnderperforming(ushort bcdUsb, byte actualSpeed, byte deviceClass)
    {
        if (deviceClass is 0x11 or 0x09) return false;
        return actualSpeed < ExpectedSpeed(bcdUsb);
    }

    /// <summary>
    /// A plain English explanation, or null when the device is running at full capability.
    /// Says what was observed and what usually causes it, without asserting the cause.
    /// </summary>
    public static string? Explain(ushort bcdUsb, byte actualSpeed, byte deviceClass)
    {
        if (!IsUnderperforming(bcdUsb, actualSpeed, deviceClass)) return null;

        byte expected = ExpectedSpeed(bcdUsb);
        string declared = $"{(bcdUsb >> 8) & 0xFF:X}.{(bcdUsb >> 4) & 0x0F:X}";

        string cause = expected == SpeedSuper && actualSpeed == SpeedHigh
            ? "The usual cause is a USB 2.0 cable, or a USB 2.0 hub between this device and the PC. "
            + "The device and the port are probably both fine."
            : "Something in the chain between this device and the PC is limiting it.";

        return $"Running at {SpeedLabel(actualSpeed)}, but this device declares USB {declared}, "
             + $"which allows {SpeedLabel(expected)}. {cause}";
    }
}
