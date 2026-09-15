namespace Portmark.Core.Usb;

/// <summary>
/// The link evidence for one connection: the hub's EX_V2 report with the companion port on the same
/// connector added, and the speed capabilities in the device's BOS descriptor. Either is null when
/// it could not be read.
/// </summary>
public sealed record UsbLinkReading(ConnectionSpeedInfo? Link, BosSpeedCapability? Bos);

/// <summary>
/// Keeps each connected device's link evidence between scans, so a watcher polling every second or
/// two does not send every attached device a BOS descriptor request, and its hub the EX_V2 and
/// connector requests, on every poll.
///
/// The evidence belongs to one connection, and none of it changes while that connection lasts: a
/// device that re-enumerates is given a new address, and one that is unplugged leaves the scan. So
/// the key is the hub, the port, the IDs and the address, and a connection a scan does not see is
/// forgotten at once. A device that returns, or comes back as something else, is read fresh.
///
/// The one-shot commands scan without a cache and always read fresh. Not thread-safe: one watcher
/// owns one cache.
/// </summary>
public sealed class UsbLinkCache
{
    private Dictionary<string, UsbLinkReading> _entries = [];
    private Dictionary<string, UsbLinkReading> _seen = [];

    /// <summary>Identity of one connection: which socket, what is in it, and which enumeration.</summary>
    public static string KeyOf(string hubPath, UsbConnection c)
        => $"{hubPath}|{c.Port}|0x{c.VendorId:X4}|0x{c.ProductId:X4}|{c.Address}";

    /// <summary>The cached reading for this connection, or a fresh one read now.</summary>
    public UsbLinkReading GetOrRead(string key, Func<UsbLinkReading> read)
    {
        if (!_seen.TryGetValue(key, out UsbLinkReading? reading) && !_entries.TryGetValue(key, out reading))
            reading = read();

        _seen[key] = reading;
        return reading;
    }

    /// <summary>Ends one scan, forgetting every connection it did not ask about.</summary>
    public void EndScan()
    {
        _entries = _seen;
        _seen = [];
    }
}
