using System.Text.RegularExpressions;
using Portmark.Core.Model;

namespace Portmark.Core.Usb;

/// <summary>
/// Arranges scanned devices into the tree they physically form, rather than the flat list the
/// hub scan produces.
///
/// This is the difference between "six USB devices are attached" and "a dock on this port, with
/// four things behind it". It also makes the cause of a slow link visible: a device sitting behind
/// a USB 2.0 hub is a different problem from the same device plugged directly into the PC, and a
/// flat list hides which one you have.
/// </summary>
public static class UsbTopology
{
    /// <summary>
    /// Builds the tree. Hubs are matched to the interface they expose by vendor and product ID.
    /// Where two hubs share both, the match is ambiguous and those hubs are left at the top level
    /// rather than nested under a guess.
    /// </summary>
    public static List<UsbTreeNode> Build(IReadOnlyList<UsbDeviceReport> devices)
    {
        // One node per hub interface, holding whatever the scan found on its ports.
        var hubNodes = devices
            .GroupBy(d => d.HubPath)
            .ToDictionary(
                g => g.Key,
                g => new UsbTreeNode
                {
                    Label = DescribeHubPath(g.Key),
                    IsRootHub = IsRootHub(g.Key),
                    Children = g.OrderBy(d => d.Port)
                                .Select(d => new UsbTreeNode { Device = d, Label = Describe(d) })
                                .ToList(),
                });

        // A hub appears twice: once as a device on its parent's port, and once as an interface of
        // its own. Join those two views so the tree has real depth.
        foreach (UsbTreeNode node in hubNodes.Values.SelectMany(n => n.Children).ToList())
        {
            if (node.Device is not { IsHub: true } hub) continue;

            List<string> candidates = hubNodes.Keys
                .Where(path => MatchesDevice(path, hub))
                .ToList();

            if (candidates.Count != 1)
            {
                // Either no interface for this hub, or several identical hubs and no way to tell
                // them apart from vendor and product alone. Say so rather than nest it wrongly.
                if (candidates.Count > 1) node.AmbiguousTopology = true;
                continue;
            }

            UsbTreeNode child = hubNodes[candidates[0]];
            node.Children.AddRange(child.Children);
            child.Adopted = true;
        }

        return hubNodes.Values
            .Where(n => !n.Adopted)
            .OrderByDescending(n => n.IsRootHub)
            .ToList();
    }

    private static bool MatchesDevice(string hubPath, UsbDeviceReport hub)
    {
        Match m = Regex.Match(hubPath, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        return string.Equals($"0x{m.Groups[1].Value}", hub.VendorId, StringComparison.OrdinalIgnoreCase)
            && string.Equals($"0x{m.Groups[2].Value}", hub.ProductId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootHub(string hubPath)
        => hubPath.Contains("root_hub", StringComparison.OrdinalIgnoreCase);

    private static string DescribeHubPath(string hubPath)
    {
        if (IsRootHub(hubPath)) return "This PC";

        Match m = Regex.Match(hubPath, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        return m.Success
            ? $"Hub 0x{m.Groups[1].Value.ToUpperInvariant()}:0x{m.Groups[2].Value.ToUpperInvariant()}"
            : "Hub";
    }

    private static string Describe(UsbDeviceReport d)
        => d.Product ?? d.Manufacturer ?? $"Unidentified {d.DeviceClass} device";
}
