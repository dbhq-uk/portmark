namespace Portmark.Core.Usb;

/// <summary>The speed diagnostic's answer for one device.</summary>
/// <param name="OperatingSpeed">The negotiated speed, as the hub reports it.</param>
/// <param name="IsUnderperforming">True only when evidence says the device can go faster.</param>
/// <param name="CapableSpeed">What that evidence says it can do. Null unless underperforming.</param>
/// <param name="Explanation">Plain English, null unless underperforming.</param>
public sealed record LinkAssessment(
    string OperatingSpeed, bool IsUnderperforming, string? CapableSpeed, string? Explanation);

/// <summary>
/// Compares the speed a device negotiated against evidence of what it is capable of.
///
/// This is the question people actually have when something feels slow. A drive capable of
/// SuperSpeed that negotiated High Speed is running at roughly a tenth of its capability, and
/// Windows runs it slowly and says nothing.
///
/// The first version of this compared against bcdUSB, and that was wrong. bcdUSB is the
/// specification revision a device conforms to, not a speed: most USB 2.0 mice and keyboards are
/// full-speed-only and entirely correct, and they were reported as limited by "something in the
/// chain". The evidence used now is the hub's own report of the device's SuperSpeed and
/// SuperSpeedPlus capability, and the device's BOS descriptor. With neither, nothing is flagged.
/// </summary>
public static class LinkDiagnostic
{
    // Negotiated speed codes as the hub reports them (USB_DEVICE_SPEED).
    public const byte SpeedLow = 0;
    public const byte SpeedFull = 1;
    public const byte SpeedHigh = 2;
    public const byte SpeedSuper = 3;

    // One rank above SpeedSuper, used only for comparison. No Windows speed code has this value.
    private const int RankSuperSpeedPlus = 4;

    /// <summary>
    /// The SuperSpeedPlus flag is the fastest thing Windows reports. USB 3.2 SuperSpeedPlus covers
    /// 10 and 20 Gbps links, and no documented field says which, so "or above" is the whole claim.
    /// </summary>
    public const string SuperSpeedPlusLabel = "10 Gbps or above";

    public static string SpeedLabel(byte speed) => speed switch
    {
        SpeedLow => "1.5 Mbps",
        SpeedFull => "12 Mbps",
        SpeedHigh => "480 Mbps",
        SpeedSuper => "5 Gbps or above",
        _ => "unknown",
    };

    /// <summary>The negotiated speed, raised to SuperSpeedPlus when the hub says so.</summary>
    public static string OperatingSpeed(byte speed, ConnectionSpeedInfo? connection)
        => connection is { OperatingAtSuperSpeedPlusOrHigher: true }
            ? $"SuperSpeedPlus, {SuperSpeedPlusLabel}"
            : UsbHubIo.SpeedName(speed);

    /// <summary>
    /// Decides whether a device is running slower than the evidence says it can.
    ///
    /// Two classes are deliberately excluded. A Billboard device exists only to describe alternate
    /// modes and is specified to attach below its capability, so it is never a fault. Hubs are
    /// excluded because a hub running below its rating is a symptom of the cable feeding it, which
    /// is reported against that cable rather than twice.
    /// </summary>
    public static LinkAssessment Assess(byte speed, byte deviceClass, bool isHub,
                                        ConnectionSpeedInfo? connection, BosSpeedCapability? bos)
    {
        string operating = OperatingSpeed(speed, connection);
        var fine = new LinkAssessment(operating, false, null, null);

        if (deviceClass is 0x11 or 0x09 || isHub) return fine;
        if (speed > SpeedSuper) return fine;   // not a documented speed, so nothing to compare

        int operatingRank = connection is { OperatingAtSuperSpeedPlusOrHigher: true } ? RankSuperSpeedPlus : speed;

        // The hub's flags come first because they are Windows' own reading. The BOS is the
        // device's word, used when the hub says nothing. SuperSpeedPlus is only ever taken from the
        // hub: without its operating flag there is no way to tell 5 Gbps from 10 Gbps in use.
        (int rank, string source)? capable =
            connection is { SuperSpeedPlusCapableOrHigher: true }
                ? (RankSuperSpeedPlus, "the hub reports this device is SuperSpeedPlus capable")
            : connection is { SuperSpeedCapableOrHigher: true }
                ? (SpeedSuper, "the hub reports this device is SuperSpeed capable")
            : bos is { DeclaresSuperSpeed: true }
                ? (SpeedSuper, "the device's BOS descriptor declares SuperSpeed support")
            : bos is { DeclaresHighSpeed: true }
                ? (SpeedHigh, "the device's BOS descriptor declares High Speed support")
            : null;

        if (capable is not { } c || c.rank <= operatingRank) return fine;

        string could = c.rank switch
        {
            RankSuperSpeedPlus => SuperSpeedPlusLabel,
            SpeedSuper => SpeedLabel(SpeedSuper),
            _ => SpeedLabel(SpeedHigh),
        };
        string running = c.rank == RankSuperSpeedPlus && speed == SpeedSuper
            ? "at SuperSpeed but not SuperSpeedPlus"
            : $"at {SpeedLabel(speed)}";

        return new LinkAssessment(operating, true, could,
            $"Running {running}, but {c.source}, which is {could}. {PortClause(c.rank, connection)}");
    }

    /// <summary>
    /// What the port's reported protocols do and do not settle. Never names a cause: a port that
    /// supports the faster protocol rules the port's protocol out, and nothing more.
    /// </summary>
    private static string PortClause(int capableRank, ConnectionSpeedInfo? connection)
    {
        if (connection is not { ProtocolsReported: true })
            return "Windows did not report which USB versions this port supports, so whether the "
                 + "port or something between it and the device is the limit is not known.";

        string list = connection.ProtocolList()!;

        // A port number without USB 3 is not yet a socket without it: on an xHCI root hub the
        // USB 3 half of the same connector is a companion port under another number.
        if (capableRank >= SpeedSuper && !connection.PortSupportsUsb300)
        {
            return connection.CompanionPortNumber switch
            {
                null => $"The hub reports this port number supports {list} and not USB 3. Whether "
                      + "another port shares its connector could not be read, so whether the "
                      + "connector supports USB 3 is not known.",
                0 => $"The hub reports this port supports {list} and does not support USB 3, with "
                   + "no companion port on its connector, so this port cannot run it any faster.",
                ushort n when connection.CompanionSupportsUsb300 == true =>
                    $"The hub reports this port number supports {list}, and that its connector is "
                  + $"shared with companion port {n}, which supports USB 3. Something between the "
                  + "device and the connector, such as a cable or adapter without USB 3 support, "
                  + "would produce this, but nothing read here identifies what is limiting it.",
                ushort n when connection.CompanionSupportsUsb300 == false =>
                    $"The hub reports this port supports {list} and does not support USB 3, and "
                  + $"that companion port {n} on the same connector does not either, so this "
                  + "connector cannot run it any faster.",
                ushort n =>
                    $"The hub reports this port number supports {list} and not USB 3, and that its "
                  + $"connector is shared with companion port {n}, whose protocols could not be "
                  + "read, so whether the connector supports USB 3 is not known.",
            };
        }

        if (capableRank == SpeedHigh && !connection.PortSupportsUsb200)
            return $"The hub reports this port supports {list} and does not support USB 2.0 High "
                 + "Speed, so this port cannot run it any faster.";

        if (capableRank == RankSuperSpeedPlus)
            return "The hub reports this port supports USB 3, but that report does not distinguish "
                 + "SuperSpeed from SuperSpeedPlus ports, so whether the port, the cable or "
                 + "something between them is the limit is not known.";

        string protocol = capableRank >= SpeedSuper ? "USB 3" : "USB 2.0";
        return $"The hub reports this port supports {protocol}. Something between the device and "
             + $"this port, such as a cable or adapter without {protocol} support, would produce "
             + "this, but nothing read here identifies what is limiting it.";
    }
}
