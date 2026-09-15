namespace Portmark.Core.Usb4;

/// <summary>
/// The USB4 register fields the rundown events copy, decoded only as far as a published
/// definition goes.
///
/// Microsoft's "TraceLogging USB4 rundown events" page says which register each event field is
/// copied from. The values are taken from the USB4 specification's register layout as Linux
/// records it in drivers/thunderbolt/tb_regs.h, which is used here as a spec reference only. A
/// value neither source defines returns null, and the caller keeps it raw.
/// </summary>
public static class Usb4Registers
{
    /// <summary>
    /// CurrentLinkSpeed, LANE_ADP_CS_1[19:16]. tb_regs.h: LANE_ADP_CS_1_CURRENT_SPEED_GEN2 0x8,
    /// _GEN3 0x4, _GEN4 0x2. USB4 Gen 2 and Gen 3 signal at 10 and 20 Gbps per lane, and Gen 4 at
    /// 40. Zero is not "slow": no speed was reported, which is what a link that is not up reads.
    /// </summary>
    public static string? LinkSpeed(int value) => value switch
    {
        0x8 => "Gen 2 (10 Gbps per lane)",
        0x4 => "Gen 3 (20 Gbps per lane)",
        0x2 => "Gen 4 (40 Gbps per lane)",
        _ => null,
    };

    /// <summary>
    /// SupportedLinkSpeeds, LANE_ADP_CS_0[19:16], a set of the same bits as the current speed.
    /// This machine reads 0xC, Gen 2 and Gen 3, which is a 40 Gbps USB4 port. Bits with no
    /// definition are listed as unrecognised rather than dropped.
    /// </summary>
    public static List<string> SupportedLinkSpeeds(int value)
    {
        var speeds = new List<string>();
        foreach (int bit in (int[])[0x8, 0x4, 0x2])
            if ((value & bit) != 0) speeds.Add(LinkSpeed(bit)!);
        if ((value & ~0xE) != 0) speeds.Add($"unrecognised (0x{value & ~0xE:X})");
        return speeds;
    }

    /// <summary>
    /// NegotiatedLinkWidth, LANE_ADP_CS_1[25:20]: 0x1 one lane, 0x2 both lanes bonded. Other
    /// values, including the asymmetric widths of USB4 version 2, are left unknown.
    /// </summary>
    public static string? LinkWidth(int value) => value switch
    {
        0x1 => "single lane",
        0x2 => "dual lane",
        _ => null,
    };

    /// <summary>
    /// SupportedLinkWidths, LANE_ADP_CS_0[25:20]. tb_regs.h: LANE_ADP_CS_0_SUPPORTED_WIDTH_DUAL
    /// 0x2, so 0x1 is single lane. This machine reads 0x3.
    /// </summary>
    public static List<string> SupportedLinkWidths(int value)
    {
        var widths = new List<string>();
        if ((value & 0x1) != 0) widths.Add("single lane");
        if ((value & 0x2) != 0) widths.Add("dual lane");
        if ((value & ~0x3) != 0) widths.Add($"unrecognised (0x{value & ~0x3:X})");
        return widths;
    }

    /// <summary>
    /// AdapterState, LANE_ADP_CS_1[29:26], named as tb_regs.h's enum tb_port_state names them.
    /// </summary>
    public static string? AdapterStateName(int value) => value switch
    {
        0 => "disabled",
        1 => "connecting",
        2 => "up",
        3 => "up, transmit in CL0s",
        4 => "up, receive in CL0s",
        5 => "up, in CL1",
        6 => "up, in CL2",
        7 => "unplugged",
        _ => null,
    };

    /// <summary>States 2 to 6 are a link that is up, whether at full power or in a low-power state.</summary>
    public static bool IsLinkUp(int adapterState) => adapterState is >= 2 and <= 6;

    /// <summary>
    /// RouterUSB4Version is ROUTER_CS_6's USB4 version. tb_regs.h: USB4_VERSION_MAJOR_MASK
    /// GENMASK(7, 5), so 0x20 is version 1 and 0x40 version 2. Zero is no version reported.
    /// </summary>
    public static int? RouterMajorVersion(int value)
    {
        int major = (value >> 5) & 0x7;
        return major == 0 ? null : major;
    }

    /// <summary>
    /// AdapterType, named from ADP_CS_2[23:0], which Microsoft says the field holds, using
    /// tb_regs.h's enum tb_port_type. Only the low 24 bits are compared: every adapter on this
    /// machine also has bit 24 set (0x1200101 for USB 3 downstream), which neither source
    /// explains, so it is kept in the raw value and not interpreted.
    /// </summary>
    public static string? AdapterKind(long value) => (value & 0xFFFFFF) switch
    {
        0x0E0101 => "DisplayPort in",
        0x0E0102 => "DisplayPort out",
        0x100101 => "PCIe downstream",
        0x100102 => "PCIe upstream",
        0x200101 => "USB 3 downstream",
        0x200102 => "USB 3 upstream",
        _ => null,
    };
}
