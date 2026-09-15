using System.Text.Json;
using Portmark.Core.Usb4;

namespace Portmark.Cli;

/// <summary>
/// portmark usb4: what Windows' USB4 drivers say about the USB4 domain, its ports and tunnels.
///
/// The one command that reads through ETW rather than UCSI or the USB hub IOCTLs, and so the one
/// read that needs administrator rights. It is built to change nothing: the trace session exists
/// for a few seconds and is stopped before the command returns. If the stop or the temporary
/// folder's deletion fails, the command says so on stderr, naming what was left.
/// </summary>
internal static class Usb4Command
{
    public static int Run(string[] args)
    {
        bool human = args.Contains("--human");
        int fromIndex = Array.IndexOf(args, "--from");
        string? from = fromIndex >= 0 && fromIndex + 1 < args.Length ? args[fromIndex + 1] : null;
        if (fromIndex >= 0 && from is null)
            return Fail("--from needs the path of a tracerpt XML file.");

        string? xml;
        if (from is not null)
        {
            // Decoding a capture someone already made needs no rights and starts no session.
            try { xml = File.ReadAllText(from); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"could not read {from}: {ex.Message}");
            }
        }
        else
        {
            if (!Program.IsElevated())
            {
                Console.Error.WriteLine("portmark usb4 needs administrator rights.");
                Console.Error.WriteLine(Program.Wrap(
                    "It reads the USB4 drivers' own trace events, which means starting an ETW trace "
                  + "session for their kernel-mode providers, and Windows requires administrator "
                  + "rights for that. Nothing on this PC is changed: the session runs for a few "
                  + "seconds and is stopped again. Re-run it from an elevated terminal."));
                return 2;
            }

            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try
            {
                if (human) Console.Error.WriteLine("Reading the USB4 drivers' rundown, a few seconds...");
                Usb4Collection collection = Usb4Collector.Collect(Usb4Collector.DefaultWindow, cancel.Token);

                // Said whatever else happened, and on stderr so the JSON on stdout stays parseable:
                // the command promises to leave nothing behind, so anything it did leave is named.
                foreach (string problem in collection.CleanupProblems)
                    Console.Error.WriteLine($"portmark: warning: {Program.Wrap(problem)}");

                xml = collection.Xml;
                if (xml is null) return Fail(collection.Error ?? "the USB4 trace could not be collected.");
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        Usb4Report report;
        try { report = Usb4RundownParser.Parse(xml); }
        catch (System.Xml.XmlException ex) { return Fail($"the trace XML could not be read: {ex.Message}"); }

        if (human) PrintHuman(report);
        else Console.WriteLine(JsonSerializer.Serialize(report, Program.JsonOptions));

        // No domain is the honest answer for a PC the USB4 drivers do not describe, not a failure.
        return report.Domains.Count == 0 ? 3 : 0;
    }

    private static void PrintHuman(Usb4Report report)
    {
        if (report.Explanation is not null)
        {
            Console.WriteLine(Program.Wrap(report.Explanation));
            Console.WriteLine();
        }

        foreach (Usb4DomainReport domain in report.Domains)
        {
            string pci = domain.PciVendorId is not null ? $", PCI {domain.PciVendorId}:{domain.PciDeviceId}" : "";
            Console.WriteLine($"USB4 domain {domain.DomainId}{pci}{(domain.AcpiName is not null ? $" ({domain.AcpiName})" : "")}");
            Console.WriteLine($"  Powered down {domain.PoweredDown switch { true => "yes, according to the host router", false => "no", null => "not reported" }}");
            Console.WriteLine();

            if (domain.Routers.Count == 0)
            {
                Console.WriteLine("  No router was described.");
                Console.WriteLine();
            }

            foreach (Usb4RouterReport router in domain.Routers)
            {
                Console.WriteLine($"  Router {router.TopologyId}{(router.IsHostRouter ? " (host router)" : "")}");
                if (router.VendorName is not null || router.ModelName is not null)
                    Console.WriteLine($"    Name         {string.Join(" ", new[] { router.VendorName, router.ModelName }.Where(s => s is not null))}");
                if (router.VendorId is not null)
                    Console.WriteLine($"    ID           {router.VendorId}:{router.ProductId}"
                                    + (router.RegisteredVendorName is not null ? $", vendor ID registered to {router.RegisteredVendorName}" : ""));
                if (router.RouterUsb4Version is not null)
                    Console.WriteLine($"    USB4 version {(router.Usb4MajorVersion is int v ? $"{v} " : "not recognised ")}({router.RouterUsb4Version})");

                foreach (Usb4PortReport port in router.Ports)
                {
                    string facing = port.IsDownstreamFacing switch { true => ", downstream-facing", false => ", upstream-facing", null => "" };
                    Console.WriteLine($"    Port on lane adapters {port.Lane0AdapterNumber?.ToString() ?? "?"} and {port.Lane1AdapterNumber?.ToString() ?? "?"}{facing}");
                    Console.WriteLine($"      Link       {port.LinkState}");
                    if (port.Reason is not null)
                        Console.WriteLine($"      Why        {Indent(port.Reason, 60, 17)}");

                    if (port.LinkActive)
                    {
                        Console.WriteLine($"      Speed      {port.CurrentLinkSpeed ?? $"not recognised ({port.Raw.GetValueOrDefault("CurrentLinkSpeed")})"}");
                        Console.WriteLine($"      Width      {port.NegotiatedLinkWidth ?? $"not recognised ({port.Raw.GetValueOrDefault("NegotiatedLinkWidth")})"}"
                                        + (port.LaneBonded is bool bonded ? $", lanes {(bonded ? "" : "not ")}bonded" : ""));
                        Console.WriteLine($"      Mode       {port.Tbt3CompatibleMode switch { true => "Thunderbolt 3 compatible", false => "USB4", null => "not reported" }}");
                        if (port.CableUsb4Version is not null)
                            Console.WriteLine($"      Cable      CableUsb4Version {port.CableUsb4Version}, kept raw: its encoding is not published");
                        if (port.DownstreamRouterTopologyId is not null)
                            Console.WriteLine($"      Connects   router {port.DownstreamRouterTopologyId}");
                    }

                    if (port.SupportedLinkSpeeds.Count > 0 || port.SupportedLinkWidths.Count > 0)
                        Console.WriteLine($"      Supports   {string.Join(", ", port.SupportedLinkSpeeds)}; {string.Join(" or ", port.SupportedLinkWidths)}");
                }

                if (router.Adapters.Count > 0)
                {
                    Console.WriteLine("    Adapters");
                    foreach (Usb4AdapterReport a in router.Adapters)
                    {
                        string tunnel = a.IsTunneled switch { true => "tunnelling", false => "not tunnelling", null => "tunnel state not reported" };
                        Console.WriteLine($"      {a.AdapterNumber?.ToString() ?? "?",-3}{a.Kind ?? $"type {a.AdapterType} not recognised",-18} {tunnel}");
                    }
                }

                Console.WriteLine();
            }
        }
    }

    private static string Indent(string text, int width, int indent) =>
        Program.Wrap(text, width).Replace(Environment.NewLine, Environment.NewLine + new string(' ', indent));

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"portmark: {message}");
        return 1;
    }
}
