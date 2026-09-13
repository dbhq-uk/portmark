using Portmark.Core.Model;

namespace Portmark.Core.Usb;

public enum UsbChangeKind
{
    Attached,
    Detached,
}

/// <summary>One device arriving or leaving.</summary>
public sealed record UsbChange(UsbChangeKind Kind, UsbDeviceReport Device);

/// <summary>
/// Watches for devices arriving and leaving, and reports what changed.
///
/// This is the form the information actually wants to take. Asking "what is plugged in" is a
/// question you have to remember to ask; being told "that drive came up at 480 Mbps when it could
/// do 5 Gbps" at the moment you plug it in is the whole product.
///
/// Polling is deliberate. The alternative, RegisterDeviceNotification, needs a window and a
/// message pump, which a console tool does not have and which buys nothing here: a poll of the hub
/// tree costs a few milliseconds, and a second of latency is imperceptible when the trigger is a
/// human pushing a plug into a socket.
/// </summary>
public static class UsbWatcher
{
    /// <summary>
    /// Yields a batch of changes each time the set of attached devices differs from the last look.
    /// Runs until cancelled. Never yields an empty batch.
    /// </summary>
    public static IEnumerable<IReadOnlyList<UsbChange>> Watch(TimeSpan interval, CancellationToken cancel)
    {
        Dictionary<string, UsbDeviceReport> previous = Snapshot();

        while (!cancel.IsCancellationRequested)
        {
            try
            {
                cancel.WaitHandle.WaitOne(interval);
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            if (cancel.IsCancellationRequested) yield break;

            Dictionary<string, UsbDeviceReport> current = Snapshot();
            List<UsbChange> changes = Diff(previous, current);
            previous = current;

            if (changes.Count > 0) yield return changes;
        }
    }

    /// <summary>Compares two snapshots. Exposed so the diffing can be tested without hardware.</summary>
    public static List<UsbChange> Diff(
        IReadOnlyDictionary<string, UsbDeviceReport> before,
        IReadOnlyDictionary<string, UsbDeviceReport> after)
    {
        var changes = new List<UsbChange>();

        foreach ((string key, UsbDeviceReport device) in after)
            if (!before.ContainsKey(key))
                changes.Add(new UsbChange(UsbChangeKind.Attached, device));

        foreach ((string key, UsbDeviceReport device) in before)
            if (!after.ContainsKey(key))
                changes.Add(new UsbChange(UsbChangeKind.Detached, device));

        return changes;
    }

    /// <summary>
    /// Identity of a device for diffing purposes.
    ///
    /// Vendor and product alone are not enough: two identical dongles in two ports are two
    /// devices, and replugging one into a different port is a real change worth reporting. The
    /// hub and port pin it to a physical socket.
    /// </summary>
    public static string KeyOf(UsbDeviceReport d)
        => $"{d.HubPath}|{d.Port}|{d.VendorId}|{d.ProductId}";

    private static Dictionary<string, UsbDeviceReport> Snapshot()
    {
        var map = new Dictionary<string, UsbDeviceReport>();
        foreach (UsbDeviceReport d in UsbDeviceScanner.ScanAll())
            map[KeyOf(d)] = d;
        return map;
    }
}
