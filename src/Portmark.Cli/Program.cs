using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Portmark.Core;
using Portmark.Core.Model;
using Portmark.Core.Ucsi;

namespace Portmark.Cli;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitNeedsSetup = 2;
    private const int ExitUnsupported = 3;

    internal const string Version = "0.2.0";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        // Windows consoles default to a legacy code page, which turns the tree's box-drawing
        // characters into question marks. Ask for UTF-8 and carry on if the host refuses.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            PrintHelp();
            return ExitOk;
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine($"portmark {Version}");
            return ExitOk;
        }

        // The value after --out is a path, not a command, even though it does not start with '-'.
        string? command = args.Where((a, i) => !a.StartsWith('-') && (i == 0 || args[i - 1] != "--out"))
                              .FirstOrDefault();
        return command switch
        {
            "doctor" => Doctor(),
            "explore" => Explore.Run(),
            "altmodes" => AltModeSweep.Run(),
            "billboard" => Billboard(),
            "usb" => UsbDevices(),
            "tree" => UsbTree(),
            "power" => PowerBudgetReport(),
            "battery" => Battery(args),
            "watch" => Watch(),
            "stress" => Stress(noAck: args.Contains("--no-ack")),
            "usb4" => Usb4Command.Run(args),
            "enable" => SetTestInterface(enabled: true),
            "disable" => SetTestInterface(enabled: false),
            "report" => HardwareReport.Run(args),
            null or "read" => Read(human: args.Contains("--human")),
            _ => Fail($"unknown command '{command}'. Try --help."),
        };
    }

    private static int Read(bool human)
    {
        PortmarkReport report = PortmarkReader.Read();

        if (human) WriteHuman(report, Console.Out);
        else Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));

        return ExitCodeFor(report);
    }

    /// <summary>The exit code a read ends with. Shared so 'report' exits exactly as a read would.</summary>
    internal static int ExitCodeFor(PortmarkReport report) => report.Capability.Status switch
    {
        CapabilityStatus.Ok => ExitOk,
        CapabilityStatus.NeedsSetup => ExitNeedsSetup,
        _ => ExitUnsupported,
    };

    /// <summary>
    /// Does a full read, then watches whether the device stays enumerable. Measures how much the
    /// read disturbs the port controller, and whether the acknowledgement traffic is the cause.
    /// </summary>
    private static int Stress(bool noAck)
    {
        UcsiConnection.SendAcknowledgements = !noAck;   // default for a normal read is off
        Console.WriteLine($"Full read with acknowledgements {(noAck ? "OFF" : "ON")}, then watching enumeration.");

        PortmarkReport report = PortmarkReader.Read();
        Console.WriteLine($"  read status: {report.Capability.Status}, connectors: {report.Connectors.Count}");
        Console.WriteLine();

        int present = 0;
        const int checks = 15;
        for (int i = 1; i <= checks; i++)
        {
            bool found = UcsiDevice.FindDevices().Count > 0;
            if (found) present++;
            Console.WriteLine($"  +{i,2}s device enumerable: {(found ? "yes" : "NO")}");
            Thread.Sleep(1000);
        }

        Console.WriteLine();
        Console.WriteLine($"device enumerable {present}/{checks} in the {checks}s after a full read");
        return ExitOk;
    }

    /// <summary>Repeats the discovery steps so intermittent failures can be seen rather than guessed at.</summary>
    private static int Doctor()
    {
        const int rounds = 10;
        Console.WriteLine($"Running discovery {rounds} times to check stability.");
        Console.WriteLine();

        int deviceOk = 0, interfaceOk = 0, readOk = 0;
        for (int i = 1; i <= rounds; i++)
        {
            IReadOnlyList<string> devices = UcsiDevice.FindDevices();
            int devErr = Portmark.Core.Native.DeviceInterfaces.LastError;

            string? iface = Portmark.Core.Native.DeviceInterfaces.FindFirst(UcsiProtocol.TestInterface);
            int ifaceErr = Portmark.Core.Native.DeviceInterfaces.LastError;

            string readState = "-";
            if (iface is not null)
            {
                UcsiConnection? c = UcsiConnection.TryOpen();
                UcsiResult? state = c?.ReadState();
                readState = state is null ? "no connection" : state.Ok ? "ok" : state.Error ?? "failed";
                if (state?.Ok == true) readOk++;
            }

            if (devices.Count > 0) deviceOk++;
            if (iface is not null) interfaceOk++;

            Console.WriteLine($"  {i,2}. devices={devices.Count} (err {devErr})  "
                            + $"interface={(iface is not null ? "yes" : "no")} (err {ifaceErr})  read={readState}");
        }

        Console.WriteLine();
        Console.WriteLine($"devices found {deviceOk}/{rounds}, interface found {interfaceOk}/{rounds}, "
                        + $"data block read {readOk}/{rounds}");
        return ExitOk;
    }

    /// <summary>
    /// Reads USB Billboard descriptors. Unlike the UCSI path this needs no registry change and no
    /// administrator rights, so it is worth reporting on its own.
    /// </summary>
    private static int Billboard()
    {
        Portmark.Core.Usb.BillboardReader.Tracing = Environment.GetCommandLineArgs().Contains("--verbose");
        List<Portmark.Core.Model.BillboardReport> found = Portmark.Core.Usb.BillboardReader.FindAll();
        if (Portmark.Core.Usb.BillboardReader.Tracing)
        {
            foreach (string line in Portmark.Core.Usb.BillboardReader.Trace) Console.WriteLine(line);
            Console.WriteLine();
        }

        if (found.Count == 0)
        {
            Console.WriteLine("No USB Billboard devices found.");
            Console.WriteLine(Wrap(
                "Only USB-C adapters that support an Alternate Mode expose one, and not every "
              + "adapter does. Nothing can be concluded about video capability from its absence."));
            return ExitOk;
        }

        foreach (Portmark.Core.Model.BillboardReport b in found)
        {
            Console.WriteLine($"Billboard device {VendorLabel(b.VendorId, b.VendorName)}:{b.ProductId}");
            foreach (Portmark.Core.Model.AlternateModeReport m in b.Modes)
                Console.WriteLine($"  [{m.Index}] {ModeLabel(m.Name, m.Svid, m.VendorName)}  mode {m.ModeNumber}  -> {m.State}");
            if (b.TruncationNote is not null)
                Console.WriteLine($"  {Wrap(b.TruncationNote, 70).Replace(Environment.NewLine, Environment.NewLine + "  ")}");

            Console.WriteLine(b.CarriesVideo
                ? "  Video: DisplayPort alternate mode entered successfully."
                : b.SupportsVideo
                    ? "  Video: DisplayPort is offered but was not entered."
                    : b.Truncated
                        ? "  Video: no DisplayPort mode in the part of the descriptor that could be read."
                        : "  Video: this adapter offers no DisplayPort alternate mode.");
            Console.WriteLine();
        }

        return ExitOk;
    }

    /// <summary>
    /// The --human rendering. Takes a writer rather than printing so 'report' can put the exact
    /// same text in its file.
    /// </summary>
    internal static void WriteHuman(PortmarkReport report, TextWriter output)
    {
        output.WriteLine($"{report.Machine.Manufacturer} {report.Machine.Model}");
        output.WriteLine();

        // Battery IOCTLs need no setup either, so the batteries are shown whatever the port
        // controller can do.
        PrintBatteries(report.Machine.Batteries, report.Machine.BatteriesNote, output);

        // Billboard data needs no setup, so it is worth showing even when the UCSI path is not
        // available. Printing it only on the happy path threw away the one answer this machine
        // could give with no configuration at all.
        PrintBillboards(report, output);

        if (report.Capability.Status != CapabilityStatus.Ok)
        {
            output.WriteLine(Wrap(report.Capability.Explanation));
            if (report.Capability.Remedy is not null)
            {
                output.WriteLine();
                output.WriteLine(Wrap(report.Capability.Remedy));
            }
            return;
        }

        if (report.Capability.Features is { CableDetailsAvailable: false })
        {
            output.WriteLine(Wrap(
                "Note: this PC's port controller does not report cable information, so the cable "
              + "rows below will say so. Port and power details are unaffected. This is a firmware "
              + "limitation, not a property of your cables."));
            output.WriteLine();
        }

        foreach (ConnectorReport connector in report.Connectors)
        {
            output.WriteLine($"Port {connector.Index}");
            output.WriteLine($"  {connector.Summary}");

            if (connector.Capability is { } cap)
            {
                var supports = new List<string>();
                if (cap.SupportsUsb2) supports.Add("USB 2.0");
                if (cap.SupportsUsb3) supports.Add("USB 3.x");
                if (cap.SupportsAlternateModes) supports.Add("alternate modes");
                if (cap.SupportsDualRolePower) supports.Add("dual role power");
                if (cap.SupportsAudioAccessory) supports.Add("audio accessory");
                output.WriteLine($"  Port supports {string.Join(", ", supports)}");
            }

            if (connector.SupportedAlternateModes is { } modes)
                output.WriteLine(modes.DataAvailable && modes.Modes.Count > 0
                    ? $"  Alt modes    {string.Join(", ", modes.Modes.Select(m => ModeLabel(m.Name, m.Svid, m.VendorName)))}{(modes.Complete ? "" : " (list incomplete)")}"
                    : $"  Alt modes    {(modes.DataAvailable ? "none listed" : "not listed by this controller")}");
            if (connector.PartnerAlternateModes is { DataAvailable: true, Modes.Count: > 0 } offered)
                output.WriteLine($"  Offered      {string.Join(", ", offered.Modes.Select(m => ModeLabel(m.Name, m.Svid, m.VendorName)))}{(offered.Complete ? "" : " (list incomplete)")}");
            if (connector.ActiveAlternateMode is { } active)
                output.WriteLine($"  Current mode {ModeLabel(active.Name, active.Svid, active.VendorName)}"
                                + (connector.ActiveAlternateModeConfirmed
                                    ? ""
                                    : ", on the controller's word alone and not confirmed"));

            PowerReport power = connector.Power;
            if (power.DataAvailable)
            {
                if (power.Negotiated is { } n)
                    output.WriteLine($"  Negotiated   {n.Display}");
                if (connector.BatteryChargingStatus is { } charging)
                    output.WriteLine($"  Charging     {charging}, according to the controller");
                if (power.PartnerSource.Count > 0)
                {
                    // When this PC is the source, the attached device is not the supply; any
                    // source objects it lists are what it could offer as a dual-role device.
                    output.WriteLine(connector.PowerDirection == "supplying"
                        ? "  Attached device offers"
                        : "  Supply offers");
                    foreach (PowerObjectReport pdo in power.PartnerSource)
                        output.WriteLine($"    - {pdo.Display}");
                }
                if (power.LocalSource.Count > 0)
                {
                    output.WriteLine($"  This PC offers");
                    foreach (PowerObjectReport pdo in power.LocalSource)
                        output.WriteLine($"    - {pdo.Display}");
                }
                if (power.PowerDiagnosis is not null)
                {
                    output.WriteLine();
                    output.WriteLine("  ** CONTRACT FAR BELOW WHAT THIS SUPPLY OFFERS **");
                    output.WriteLine($"  {Wrap(power.PowerDiagnosis, 72).Replace(Environment.NewLine, Environment.NewLine + "  ")}");
                }
            }

            CableReport cable = connector.Cable;
            if (cable.DataAvailable)
            {
                output.WriteLine($"  Speed        {cable.Speed?.Display ?? "not reported by the cable"}");
                output.WriteLine($"  Power        {DescribeCurrent(cable)}");
                output.WriteLine($"  Plug         {cable.PlugType ?? "unknown"}");
                output.WriteLine($"  Construction {(cable.ActiveCable == true ? "active" : "passive")}");
                output.WriteLine($"  Video        {cable.VideoNote}");
            }
            else if (cable.Reason is not null)
            {
                output.WriteLine($"  Why          {Wrap(cable.Reason, 60).Replace(Environment.NewLine, Environment.NewLine + "               ")}");
            }

            if (cable.Inferred is { } inferred)
            {
                output.WriteLine($"  Cable rating at least {inferred.MinimumCurrentRatingMilliamps / 1000.0:0.##}A, deduced not reported");
                string deduction = $"{inferred.Evidence} {inferred.Basis} {inferred.Conclusion}";
                output.WriteLine($"               {Wrap(deduction, 60).Replace(Environment.NewLine, Environment.NewLine + "               ")}");
            }

            // Discover Identity, only where the controller could be asked. Each line is the
            // device's or cable's own declaration, and says so.
            if (connector.Identity is { } identity)
            {
                if (identity.Partner is { DataAvailable: true, Declaration: { } deviceSays })
                    output.WriteLine($"  Device ID    {Wrap(deviceSays, 60).Replace(Environment.NewLine, Environment.NewLine + "               ")}");
                if (identity.Cable is { DataAvailable: true, Declaration: { } cableSays })
                    output.WriteLine($"  Cable ID     {Wrap(cableSays, 60).Replace(Environment.NewLine, Environment.NewLine + "               ")}");
                if (!identity.Requested && connector.Connected == true && identity.Reason is { } notAsked)
                    output.WriteLine($"  Identity     {Wrap(notAsked, 60).Replace(Environment.NewLine, Environment.NewLine + "               ")}");
            }

            output.WriteLine();
        }
    }

    /// <summary>Shows attached devices as the tree they physically form.</summary>
    private static int UsbTree()
    {
        Portmark.Core.Model.UsbScanReport scan = Portmark.Core.Usb.UsbDeviceScanner.Scan();
        List<Portmark.Core.Model.UsbDeviceReport> devices = scan.Devices;
        List<Portmark.Core.Model.UsbTreeNode> roots = Portmark.Core.Usb.UsbTopology.Build(devices, scan.PortStatuses);

        if (Environment.GetCommandLineArgs().Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(roots, JsonOptions));
            return ExitOk;
        }

        if (roots.Count == 0)
        {
            Console.WriteLine("No USB devices found.");
            return ExitOk;
        }

        foreach (Portmark.Core.Model.UsbTreeNode root in roots)
        {
            PrintNode(root, "", true, isRoot: true);
            Console.WriteLine();
        }

        int slow = devices.Count(d => d.IsUnderperforming);
        if (slow > 0)
            Console.WriteLine($"{slow} device(s) running slower than they could. Run 'portmark usb' for detail.");

        int faults = scan.PortStatuses.Count(p => p.IsFault);
        if (faults > 0)
            Console.WriteLine($"{faults} port(s) where the hub reports a failed connection. Run 'portmark usb' for detail.");

        return ExitOk;
    }

    private static void PrintNode(Portmark.Core.Model.UsbTreeNode node, string prefix, bool last, bool isRoot = false)
    {
        if (isRoot)
        {
            Console.WriteLine(node.Label);
        }
        else
        {
            string branch = last ? "└─ " : "├─ ";
            if (node.PortStatus is { } status)
            {
                string fault = status.IsFault ? "  ** fault **" : "";
                Console.WriteLine($"{prefix}{branch}{node.Label}: {status.Description}{fault}");
                return;
            }

            string detail = node.Device is { } d
                ? $"  [{VendorLabel(d.VendorId, d.VendorName)}:{d.ProductId}, {d.Speed}]"
                : "";
            string warn = node.Device?.IsUnderperforming == true ? "  ** slow **" : "";
            string ambiguous = node.AmbiguousTopology ? "  (position uncertain: identical hubs)" : "";
            Console.WriteLine($"{prefix}{branch}{node.Label}{detail}{warn}{ambiguous}");
        }

        string childPrefix = isRoot ? "" : prefix + (last ? "   " : "│  ");
        for (int i = 0; i < node.Children.Count; i++)
            PrintNode(node.Children[i], childPrefix, i == node.Children.Count - 1);
    }

    /// <summary>
    /// Adds up what the devices on each hub asked for against what that hub can supply.
    /// Over-subscribing a bus-powered hub is a common fault that Windows diagnoses as nothing at
    /// all: devices drop out under load, or refuse to enumerate, with no explanation.
    /// </summary>
    private static int PowerBudgetReport()
    {
        List<Portmark.Core.Model.UsbDeviceReport> devices = Portmark.Core.Usb.UsbDeviceScanner.ScanAll();
        List<Portmark.Core.Model.HubPowerReport> hubs = Portmark.Core.Usb.PowerBudget.Analyse(devices);

        if (hubs.Count == 0)
        {
            Console.WriteLine("No USB hubs found.");
            return ExitOk;
        }

        foreach (Portmark.Core.Model.HubPowerReport h in hubs.OrderByDescending(x => x.OverSubscribed))
        {
            string name = h.IsRootHub ? "This PC" : "Hub";
            string powered = h.IsBusPowered ? "bus-powered" : "self-powered";
            Console.WriteLine($"{name}  ({powered}, {h.PortCount} ports, {h.DeviceCount} device(s) attached)");
            Console.WriteLine($"  Requested    {h.RequestedMilliamps} mA across attached devices");
            if (h.AvailableMilliamps is int available)
                Console.WriteLine($"  Shared pool  {available} mA"
                                + (h.HubControlCurrentMilliamps > 0
                                   ? $"  (500 mA total, less {h.HubControlCurrentMilliamps} mA for the hub itself)"
                                   : ""));
            else
                Console.WriteLine("  Shared pool  none, this hub has its own power supply");
            Console.WriteLine($"  Per port     {h.PerPortAllowanceMilliamps} mA guaranteed");

            if (h.Note is not null)
            {
                Console.WriteLine();
                Console.WriteLine("  ** OVER-SUBSCRIBED **");
                Console.WriteLine($"  {Wrap(h.Note, 70).Replace(Environment.NewLine, Environment.NewLine + "  ")}");
            }

            Console.WriteLine();
        }

        int over = hubs.Count(h => h.OverSubscribed);
        Console.WriteLine(over > 0
            ? $"{over} hub(s) over-subscribed."
            : "No hub is over-subscribed.");
        return ExitOk;
    }

    /// <summary>
    /// Reports devices arriving and leaving as it happens. This is the form the information
    /// actually wants to take: you learn a drive came up slow at the moment you plug it in,
    /// rather than having to remember to go and ask.
    /// </summary>
    private static int Watch()
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        Console.WriteLine("Watching USB ports. Plug something in, or press Ctrl+C to stop.");
        Console.WriteLine();

        foreach (Portmark.Core.Usb.UsbWatchBatch batch in
                 Portmark.Core.Usb.UsbWatcher.WatchWithFaults(TimeSpan.FromSeconds(1), cancel.Token))
        {
            foreach (Portmark.Core.Usb.UsbChange change in batch.Changes)
            {
                Portmark.Core.Model.UsbDeviceReport d = change.Device;
                string name = d.Product ?? d.Manufacturer ?? $"Unidentified {d.DeviceClass} device";
                string stamp = DateTime.Now.ToString("HH:mm:ss");

                if (change.Kind == Portmark.Core.Usb.UsbChangeKind.Attached)
                {
                    Console.WriteLine($"[{stamp}] + {name}");
                    Console.WriteLine($"          {VendorLabel(d.VendorId, d.VendorName)}:{d.ProductId}, {d.DeviceClass}, {d.Speed}");
                    if (d.MaxPowerMilliamps is int ma) Console.WriteLine($"          requests up to {ma} mA");
                    if (d.LinkDiagnosis is not null)
                    {
                        Console.WriteLine("          ** RUNNING SLOWER THAN IT COULD **");
                        Console.WriteLine($"          {Wrap(d.LinkDiagnosis, 64).Replace(Environment.NewLine, Environment.NewLine + "          ")}");
                    }
                }
                else
                {
                    Console.WriteLine($"[{stamp}] - {name}  ({VendorLabel(d.VendorId, d.VendorName)}:{d.ProductId})");
                }

                Console.WriteLine();
            }

            PrintPortFaults(batch.Faults);
        }

        Console.WriteLine("Stopped.");
        return ExitOk;
    }

    /// <summary>Lists every attached USB device, read from the devices themselves.</summary>
    private static int UsbDevices()
    {
        Portmark.Core.Model.UsbScanReport scan = Portmark.Core.Usb.UsbDeviceScanner.Scan();
        List<Portmark.Core.Model.UsbDeviceReport> devices = scan.Devices;

        if (Environment.GetCommandLineArgs().Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(scan, JsonOptions));
            return ExitOk;
        }

        if (devices.Count == 0 && scan.PortStatuses.Count == 0)
        {
            Console.WriteLine("No USB devices found.");
            return ExitOk;
        }

        int slow = devices.Count(d => d.IsUnderperforming);
        Console.WriteLine($"{devices.Count} USB device(s) attached"
                        + (slow > 0 ? $", {slow} running slower than they could" : ""));
        Console.WriteLine();

        foreach (Portmark.Core.Model.UsbDeviceReport d in devices.OrderBy(x => x.IsHub ? 1 : 0))
        {
            // String descriptors are optional and some devices omit them, so fall back through
            // what is actually available rather than showing a bare class name as if it were a
            // product name.
            string name = d.Product
                       ?? d.Manufacturer
                       ?? $"Unidentified {d.DeviceClass} device";
            Console.WriteLine($"{name}{(d.IsHub ? "  [hub]" : "")}");
            Console.WriteLine($"  ID           {d.VendorId}:{d.ProductId}");
            if (d.VendorName is not null) Console.WriteLine($"  Vendor ID    {d.VendorId} is registered to {d.VendorName}");
            if (d.Manufacturer is not null) Console.WriteLine($"  Maker        {d.Manufacturer}");
            if (d.SerialNumber is not null) Console.WriteLine($"  Serial       {d.SerialNumber}");
            Console.WriteLine($"  Class        {d.DeviceClass}");
            Console.WriteLine($"  Speed        {d.Speed}");
            Console.WriteLine($"  USB version  {d.UsbVersion}");
            if (d.LinkEvidence?.PortProtocols is { } protocols)
                Console.WriteLine($"  Port         the hub reports {protocols}"
                                + (d.LinkEvidence.CompanionPortProtocols is { } shared
                                    ? $", and {shared} on companion port {d.LinkEvidence.CompanionPortNumber} of the same connector"
                                    : ""));
            Console.WriteLine(d.MaxPowerMilliamps is int ma
                ? $"  Requests     up to {ma} mA"
                : "  Requests     not reported");

            if (d.LinkDiagnosis is not null)
            {
                Console.WriteLine();
                Console.WriteLine("  ** RUNNING SLOWER THAN IT COULD **");
                Console.WriteLine($"  {Wrap(d.LinkDiagnosis, 70).Replace(Environment.NewLine, Environment.NewLine + "  ")}");
            }

            Console.WriteLine();
        }

        PrintPortStatuses(scan.PortStatuses);
        return ExitOk;
    }

    /// <summary>
    /// The batteries' own readings, and with --sample the average net charge flow over an
    /// interval. Only battery IOCTLs are sent, so this never touches the port controller, and the
    /// wait happens only when it is asked for.
    /// </summary>
    private static int Battery(string[] args)
    {
        int at = Array.IndexOf(args, "--sample");
        if (at < 0)
        {
            List<BatteryReport> batteries = Portmark.Core.Power.BatteryTelemetry.ReadAll(out string? note);
            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(new { batteries, batteriesNote = note }, JsonOptions));
            else
                PrintBatteries(batteries, note, Console.Out);
            return batteries.Any(b => b.Present) ? ExitOk : ExitUnsupported;
        }

        if (at + 1 >= args.Length || !int.TryParse(args[at + 1], out int seconds) || seconds <= 0)
            return Fail("--sample needs a whole number of seconds, for example 'portmark battery --sample 60'.");

        Console.WriteLine($"Sampling battery capacity over {seconds} seconds. Only the battery is read.");
        Console.WriteLine();

        List<Portmark.Core.Power.BatterySample> samples =
            Portmark.Core.Power.BatteryTelemetry.Sample(TimeSpan.FromSeconds(seconds), out string? sampleNote);

        List<BatteryReport> ends = samples.Select(s => s.End ?? s.Start).OfType<BatteryReport>().ToList();
        PrintBatteries(ends, sampleNote, Console.Out);

        // Rendered in Core so the words are tested, not only the numbers.
        Portmark.Core.Power.BatterySampleText.Write(samples, Console.Out, Wrap);
        return samples.Count > 0 ? ExitOk : ExitUnsupported;
    }

    private static void PrintBatteries(List<BatteryReport> batteries, string? note, TextWriter output)
    {
        if (batteries.Count == 0)
        {
            if (note is not null)
            {
                output.WriteLine($"Battery      {Wrap(note, 60).Replace(Environment.NewLine, Environment.NewLine + "             ")}");
                output.WriteLine();
            }
            return;
        }

        foreach (BatteryReport b in batteries)
        {
            string name = string.Join(", ", new[] { b.DeviceName, b.Manufacturer }.OfType<string>());
            output.WriteLine((batteries.Count > 1 ? $"Battery {b.Index}" : "Battery")
                            + (name.Length > 0 ? $"  ({name})" : ""));

            if (!b.Present)
            {
                output.WriteLine($"  {b.Reason ?? "No battery is in this battery slot."}");
                output.WriteLine();
                continue;
            }

            if (b.ChargePercent is int percent)
                output.WriteLine($"  Charge       {percent} percent"
                                + (b.RemainingCapacityMilliwattHours is int left && b.FullChargeCapacityMilliwattHours is int full
                                    ? $", {Wh(left)} of {Wh(full)}"
                                    : ""));
            else
                output.WriteLine("  Charge       not reported");

            if (b.PowerState is { } state)
            {
                var flags = new List<string>();
                if (state.OnExternalPower) flags.Add("on external power");
                if (state.Charging) flags.Add("charging");
                if (state.Discharging) flags.Add("discharging");
                if (state.Critical) flags.Add("critical");
                output.WriteLine($"  State        {(flags.Count > 0 ? string.Join(", ", flags) : "no state flags set")}, as the battery reports it");
            }

            output.WriteLine(b.RateMilliwatts switch
            {
                int rate and not 0 => $"  Rate         {DescribeFlow(rate)}, the battery's own charge flow, not cable power",
                0 => "  Rate         zero as reported; some batteries report only discharging rates",
                _ => "  Rate         not reported",
            });

            if (b.VoltageMillivolts is int mv) output.WriteLine($"  Voltage      {mv / 1000.0:0.00}V");
            if (b.HealthPercent is int health)
                output.WriteLine($"  Health       {health} percent, {Wh(b.FullChargeCapacityMilliwattHours!.Value)} full charge "
                                + $"against {Wh(b.DesignCapacityMilliwattHours!.Value)} design, the battery's own estimate");
            if (b.CycleCount is int cycles) output.WriteLine($"  Cycles       {cycles}");
            if (b.Chemistry is not null) output.WriteLine($"  Chemistry    {b.Chemistry}");

            foreach (string? text in new[] { b.Note, b.Reason })
                if (text is not null)
                    output.WriteLine($"  Note         {Wrap(text, 60).Replace(Environment.NewLine, Environment.NewLine + "               ")}");

            output.WriteLine();
        }
    }

    private static string DescribeFlow(int milliwatts) => Portmark.Core.Power.BatterySampleText.DescribeFlow(milliwatts);

    private static string Wh(int milliwattHours) => $"{milliwattHours / 1000.0:0.0} Wh";

    /// <summary>
    /// Ports whose hub reports something other than empty or connected. They have no working
    /// device to list, which is why they need listing: a failed enumeration is otherwise invisible.
    /// </summary>
    private static void PrintPortStatuses(IReadOnlyList<Portmark.Core.Model.UsbPortStatusReport> ports)
    {
        foreach (Portmark.Core.Model.UsbPortStatusReport p in ports.OrderByDescending(x => x.IsFault))
        {
            Console.WriteLine($"{Portmark.Core.Usb.UsbTopology.HubLabel(p.HubPath)}, port {p.Port}");
            if (p.VendorId is not null) Console.WriteLine($"  ID           {p.VendorId}:{p.ProductId}");
            Console.WriteLine($"  Status       {p.ConnectionStatus}");
            if (p.IsFault)
            {
                Console.WriteLine();
                Console.WriteLine("  ** PORT FAULT **");
            }
            string said = char.ToUpperInvariant(p.Description[0]) + p.Description[1..] + ".";
            Console.WriteLine($"  {Wrap(said, 70).Replace(Environment.NewLine, Environment.NewLine + "  ")}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Ports whose hub has just started reporting a failed connection, once per occurrence. In the
    /// hub's words and attributed to it: nothing read here knows what caused the failure.
    /// </summary>
    private static void PrintPortFaults(IReadOnlyList<Portmark.Core.Model.UsbPortStatusReport> faults)
    {
        foreach (Portmark.Core.Model.UsbPortStatusReport f in faults)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            Console.WriteLine($"[{stamp}] ! {Portmark.Core.Usb.UsbTopology.HubLabel(f.HubPath)}, port {f.Port}  ** PORT FAULT **");
            Console.WriteLine($"          {Wrap($"{f.Description} ({f.ConnectionStatus}).", 64).Replace(Environment.NewLine, Environment.NewLine + "          ")}");
            Console.WriteLine();
        }
    }

    private static void PrintBillboards(PortmarkReport report, TextWriter output)
    {
        foreach (Portmark.Core.Model.BillboardReport b in report.Billboards)
        {
            output.WriteLine($"Adapter {VendorLabel(b.VendorId, b.VendorName)}:{b.ProductId}");
            foreach (Portmark.Core.Model.AlternateModeReport m in b.Modes)
                output.WriteLine($"  {ModeLabel(m.Name, m.Svid, m.VendorName)}: {m.State}");
            if (b.TruncationNote is not null)
                output.WriteLine($"  {Wrap(b.TruncationNote, 70).Replace(Environment.NewLine, Environment.NewLine + "  ")}");
            output.WriteLine(b.CarriesVideo
                ? "  Video        yes, DisplayPort is active through this adapter"
                : b.SupportsVideo
                    ? "  Video        supported but not currently active"
                    : b.Truncated
                        ? "  Video        not in the part of the descriptor that could be read"
                        : "  Video        this adapter offers no DisplayPort mode");
            output.WriteLine();
        }
    }

    /// <summary>
    /// A vendor ID with the name registered to it in brackets, as in "0x05AC (Apple, Inc.)", or the
    /// bare ID when the vendor list does not name it. The brackets are the registered name, not
    /// the maker: the help text says so, and the device's own manufacturer string is shown apart.
    /// </summary>
    private static string VendorLabel(string vendorId, string? vendorName)
        => vendorName is null ? vendorId : $"{vendorId} ({vendorName})";

    /// <summary>
    /// A mode's name, with the registered vendor name added only where the name is the bare SVID.
    /// A mode that already has a name of its own, such as DisplayPort, is left as it is.
    /// </summary>
    private static string ModeLabel(string name, string svid, string? vendorName)
        => vendorName is not null && name.Contains(svid, StringComparison.OrdinalIgnoreCase)
            ? $"{name} ({vendorName})"
            : name;

    private static string DescribeCurrent(CableReport cable)
    {
        if (cable.CurrentCapabilityMilliamps is not int ma) return "not reported by the cable";
        return cable.MaxWattsAt20Volts is int watts
            ? $"{ma} mA, about {watts}W at 20V"
            : $"{ma} mA";
    }

    /// <summary>
    /// Turns the test interface on or off. This is the only operation that changes the machine,
    /// and the only one that needs administrator rights.
    /// </summary>
    private static int SetTestInterface(bool enabled)
    {
        IReadOnlyList<string> devices = UcsiDevice.FindDevices();
        if (devices.Count == 0)
            return Fail("no USB-C connector manager device on this PC, so there is nothing to enable.");

        string instanceId = devices[0];

        if (!IsElevated())
        {
            Console.Error.WriteLine(
                $"portmark {(enabled ? "enable" : "disable")} needs administrator rights.");
            Console.Error.WriteLine("Re-run it from an elevated terminal.");
            return ExitError;
        }

        if (enabled)
        {
            Console.WriteLine("This switches on the USB-C port controller's test interface.");
            Console.WriteLine();
            Console.WriteLine(Wrap(
                "Be aware of what that means: while it is on, any program running as you can send "
              + "commands to your USB-C power controller, not just portmark. Windows ships it "
              + "switched off for that reason. Run 'portmark disable' to switch it back off."));
            Console.WriteLine();
        }

        if (!UcsiDevice.SetTestInterfaceEnabled(instanceId, enabled, out string? error))
            return Fail(error ?? "the registry change failed.");

        Console.WriteLine($"{(enabled ? "Set" : "Cleared")} TestInterfaceEnabled on {instanceId}.");

        if (!RestartDevice(instanceId))
        {
            Console.WriteLine("The device could not be restarted automatically. Reboot to apply.");
            return ExitOk;
        }

        Console.WriteLine(enabled
            ? "Done. Run 'portmark --human' to read your ports."
            : "Done. The test interface is off again.");
        return ExitOk;
    }

    private static bool RestartDevice(string instanceId)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", $"/restart-device \"{instanceId}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? process = Process.Start(psi);
            if (process is null) return false;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Give PnP a moment to republish the interface before anything tries to open it.
            Thread.Sleep(2000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static int Fail(string message)
    {
        Console.Error.WriteLine($"portmark: {message}");
        return ExitError;
    }

    internal static string Wrap(string text, int width = 76)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return string.Join(Environment.NewLine, lines);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            portmark - read what your USB-C cables and ports can actually do

            USAGE
              portmark                 read all ports, print JSON
              portmark --human         read all ports, print plain English
              portmark report          read all ports, write one file to attach to a hardware report
              portmark battery         read the batteries' own charge, rate and health
              portmark battery --json  the same, as JSON
              portmark battery --sample SECONDS
                                       also average the battery's net charge flow over SECONDS
              portmark enable          switch on the port controller interface (needs admin, once)
              portmark disable         switch it back off (needs admin)
              portmark usb4            what Windows' USB4 drivers report: links, speed, tunnels
                                       (needs admin; reads a few seconds of trace events)
              portmark usb4 --from F   decode a tracerpt XML file instead (no admin needed)

            OPTIONS
              --human                  human-readable output instead of JSON
              --out PATH               where 'report' writes (default portmark-report-<time>.json here)
              --no-redact              keep serial numbers, device paths and names in the report
              --version                print the version
              --help                   this text

            EXIT CODES
              0  ports were read
              1  an error occurred
              2  a one-time setup step is needed; run 'portmark enable' as administrator
              3  this PC cannot report the data this command reads: cable data for a port read,
                 or, for 'battery', no battery answered
              'report' writes its file whichever of 0, 2 or 3 the reading gives, then exits with
              that code. It exits 1 only when the file could not be written.

            NOTES
              Fields that the hardware did not report are null in JSON, and say so in --human
              output. Nothing is inferred from the shape of a connector.

              A name in brackets after a vendor ID, as in 0x05AC (Apple, Inc.), is the name that
              ID is registered to in the USB ID Repository. It says who holds the number, not who
              made the device.

              portmark only ever reads. It never sends a command that changes port state.

              'report' only writes a file. It sends nothing and opens nothing; sharing the file
              is up to you.
            """);
    }
}
