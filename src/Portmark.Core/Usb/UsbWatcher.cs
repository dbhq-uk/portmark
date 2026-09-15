using Portmark.Core.Model;

namespace Portmark.Core.Usb;

public enum UsbChangeKind
{
    Attached,
    Detached,
}

/// <summary>One device arriving or leaving.</summary>
public sealed record UsbChange(UsbChangeKind Kind, UsbDeviceReport Device);

/// <summary>One poll's news: devices that came or went, and ports that have just entered a fault.</summary>
public sealed record UsbWatchBatch(IReadOnlyList<UsbChange> Changes, IReadOnlyList<UsbPortStatusReport> Faults);

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
/// human pushing a plug into a socket. Everything polled is a hub IOCTL; the port controller is
/// never asked.
/// </summary>
public static class UsbWatcher
{
    /// <summary>
    /// Yields a batch of changes each time the set of attached devices differs from the last look.
    /// Runs until cancelled. Never yields an empty batch.
    /// </summary>
    public static IEnumerable<IReadOnlyList<UsbChange>> Watch(TimeSpan interval, CancellationToken cancel)
    {
        foreach (UsbWatchBatch batch in WatchWithFaults(interval, cancel))
            if (batch.Changes.Count > 0) yield return batch.Changes;
    }

    /// <summary>
    /// As <see cref="Watch"/>, and also yields each port as its hub starts reporting a fault, once
    /// per occurrence. A device that fails enumeration never arrives, so without this the one
    /// plug-in the user most needs to hear about produced silence.
    /// </summary>
    public static IEnumerable<UsbWatchBatch> WatchWithFaults(TimeSpan interval, CancellationToken cancel)
    {
        UsbScanReport scan = UsbDeviceScanner.Scan();
        Dictionary<string, UsbDeviceReport> previous = Index(scan.Devices);
        var faults = new UsbPortFaultTracker();
        faults.Prime(scan.PortStatuses);

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

            scan = UsbDeviceScanner.Scan();
            Dictionary<string, UsbDeviceReport> current = Index(scan.Devices);
            List<UsbChange> changes = Diff(previous, current);
            List<UsbPortStatusReport> entered = faults.Update(scan.PortStatuses);
            previous = current;

            if (changes.Count > 0 || entered.Count > 0) yield return new UsbWatchBatch(changes, entered);
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

    /// <summary>Identity of a port: the hub and the socket, whatever is or is not in it.</summary>
    public static string PortKeyOf(UsbPortStatusReport p) => $"{p.HubPath}|{p.Port}";

    private static Dictionary<string, UsbDeviceReport> Index(IEnumerable<UsbDeviceReport> devices)
    {
        var map = new Dictionary<string, UsbDeviceReport>();
        foreach (UsbDeviceReport d in devices)
            map[KeyOf(d)] = d;
        return map;
    }
}

/// <summary>
/// Decides when a port's fault is a new occurrence, so it is reported once rather than on every
/// poll for as long as the device stays plugged in.
///
/// An occurrence begins when a port reports a fault it was not already in, and ends when the port
/// reports empty or connected. Enumerating and reset do not end it: Windows retries a failing
/// device, and a retry that fails the same way is the same event the user was already told about.
/// </summary>
public sealed class UsbPortFaultTracker
{
    private readonly Dictionary<string, int> _active = [];

    /// <summary>Records the faults present when watching starts, without reporting them.</summary>
    public void Prime(IEnumerable<UsbPortStatusReport> ports)
    {
        _active.Clear();
        Update(ports);
    }

    /// <summary>
    /// Takes one scan's port statuses (every port not empty and not connected) and returns the
    /// ports that have just entered a fault.
    /// </summary>
    public List<UsbPortStatusReport> Update(IEnumerable<UsbPortStatusReport> ports)
    {
        var entered = new List<UsbPortStatusReport>();
        var seen = new HashSet<string>();

        foreach (UsbPortStatusReport p in ports)
        {
            string key = UsbWatcher.PortKeyOf(p);
            seen.Add(key);

            if (!p.IsFault) continue;
            if (_active.TryGetValue(key, out int code) && code == p.ConnectionStatusCode) continue;

            _active[key] = p.ConnectionStatusCode;
            entered.Add(p);
        }

        // A port absent from the scan is empty or connected, which ends its occurrence.
        foreach (string key in _active.Keys.Where(k => !seen.Contains(k)).ToList())
            _active.Remove(key);

        return entered;
    }
}
