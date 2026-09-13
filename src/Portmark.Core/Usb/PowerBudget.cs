using Microsoft.Win32.SafeHandles;
using Portmark.Core.Model;

namespace Portmark.Core.Usb;

/// <summary>
/// Adds up what the devices on a hub have asked for, and compares it with what that hub can
/// actually supply.
///
/// Over-subscribing a bus-powered hub is a common and badly diagnosed fault: devices drop off
/// under load, or refuse to enumerate, and Windows reports nothing beyond a generic failure. A
/// bus-powered hub has 500 mA in total for itself and everything behind it, and four ports will
/// cheerfully ask for more than that.
///
/// One honest caveat runs through all of this. bMaxPower is what a device *requests* in its
/// configuration descriptor, not what it draws. A device can request 500 mA and idle at 50. So an
/// over-subscribed hub here means "these devices asked for more than the hub promises", which is a
/// real risk rather than a measured fault, and it is worded that way.
/// </summary>
public static class PowerBudget
{
    /// <summary>A bus-powered hub gets 500 mA in total from its upstream port.</summary>
    public const int BusPoweredHubTotalMilliamps = 500;

    /// <summary>A self-powered hub must offer 500 mA per port.</summary>
    public const int SelfPoweredPerPortMilliamps = 500;

    /// <summary>A bus-powered hub need only offer 100 mA per port.</summary>
    public const int BusPoweredPerPortMilliamps = 100;

    public static List<HubPowerReport> Analyse(IReadOnlyList<UsbDeviceReport> devices)
    {
        var reports = new List<HubPowerReport>();

        foreach (IGrouping<string, UsbDeviceReport> group in devices.GroupBy(d => d.HubPath))
        {
            HubPowerInfo? info = ReadHubPower(group.Key);
            if (info is null) continue;

            int requested = group.Sum(d => d.MaxPowerMilliamps ?? 0);
            reports.Add(Evaluate(group.Key, info.IsBusPowered, info.ControlCurrentMilliamps,
                                 info.PortCount, requested, group.Count()));
        }

        return reports;
    }

    /// <summary>
    /// The budget arithmetic on its own, with no hardware access, so it can be tested directly.
    /// </summary>
    public static HubPowerReport Evaluate(string hubPath, bool isBusPowered, int controlCurrentMilliamps,
                                          int portCount, int requestedMilliamps, int deviceCount)
    {
        // Only a bus-powered hub has a genuine shared budget: it runs itself and everything behind
        // it from the 500 mA of its upstream port, so what is left for the ports is the total less
        // its own control current.
        //
        // A self-powered hub has no such pool. It has its own supply and must guarantee 500 mA per
        // port. Multiplying that by the port count would produce a total that looks like a budget,
        // is not one, and could only ever mislead, so it is left unset.
        int? available = isBusPowered
            ? Math.Max(0, BusPoweredHubTotalMilliamps - controlCurrentMilliamps)
            : null;

        bool over = available is int cap && requestedMilliamps > cap;

        return new HubPowerReport
        {
            HubPath = hubPath,
            IsBusPowered = isBusPowered,
            IsRootHub = hubPath.Contains("root_hub", StringComparison.OrdinalIgnoreCase),
            PortCount = portCount,
            HubControlCurrentMilliamps = controlCurrentMilliamps,
            AvailableMilliamps = available,
            RequestedMilliamps = requestedMilliamps,
            PerPortAllowanceMilliamps = isBusPowered
                ? BusPoweredPerPortMilliamps
                : SelfPoweredPerPortMilliamps,
            DeviceCount = deviceCount,
            OverSubscribed = over,
            Note = over
                ? $"The devices on this hub have asked for {requestedMilliamps} mA between them, but "
                + $"a bus-powered hub has only {available} mA to give. Devices may drop out under "
                + "load. Note that this is what they requested, not what they are drawing."
                : null,
        };
    }


    private sealed record HubPowerInfo(bool IsBusPowered, int ControlCurrentMilliamps, int PortCount);

    /// <summary>
    /// Reads the hub descriptor. USB_NODE_INFORMATION is a node type followed by a packed
    /// USB_HUB_DESCRIPTOR, so the fields sit at fixed byte offsets:
    ///   6      bNumberOfPorts
    ///   10     bHubControlCurrent, in mA
    ///   75     HubIsBusPowered, after the 64-byte remove-and-power mask
    /// </summary>
    private static unsafe HubPowerInfo? ReadHubPower(string hubPath)
    {
        SafeFileHandle hub = UsbHubIo.OpenHub(hubPath);
        if (hub.IsInvalid) { hub.Dispose(); return null; }

        using (hub)
        {
            byte[] node = new byte[128];
            fixed (byte* p = node)
            {
                if (!Native.Win32.DeviceIoControl(hub, UsbHubIo.IoctlGetNodeInformation,
                                                  p, (uint)node.Length, p, (uint)node.Length,
                                                  out _, IntPtr.Zero))
                    return null;
            }

            return new HubPowerInfo(
                IsBusPowered: node[75] != 0,
                ControlCurrentMilliamps: node[10],
                PortCount: node[6]);
        }
    }
}
