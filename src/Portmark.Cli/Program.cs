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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            PrintHelp();
            return ExitOk;
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine("portmark 0.1.0");
            return ExitOk;
        }

        string? command = args.FirstOrDefault(a => !a.StartsWith('-'));
        return command switch
        {
            "doctor" => Doctor(),
            "explore" => Explore.Run(),
            "altmodes" => AltModeSweep.Run(),
            "billboard" => Billboard(),
            "stress" => Stress(noAck: args.Contains("--no-ack")),
            "enable" => SetTestInterface(enabled: true),
            "disable" => SetTestInterface(enabled: false),
            null or "read" => Read(human: args.Contains("--human")),
            _ => Fail($"unknown command '{command}'. Try --help."),
        };
    }

    private static int Read(bool human)
    {
        PortmarkReport report = PortmarkReader.Read();

        if (human) PrintHuman(report);
        else Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));

        return report.Capability.Status switch
        {
            CapabilityStatus.Ok => ExitOk,
            CapabilityStatus.NeedsSetup => ExitNeedsSetup,
            _ => ExitUnsupported,
        };
    }

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
            Console.WriteLine($"Billboard device {b.VendorId}:{b.ProductId}");
            foreach (Portmark.Core.Model.AlternateModeReport m in b.Modes)
                Console.WriteLine($"  [{m.Index}] {m.Name}  mode {m.ModeNumber}  -> {m.State}");

            Console.WriteLine(b.CarriesVideo
                ? "  Video: DisplayPort alternate mode entered successfully."
                : b.SupportsVideo
                    ? "  Video: DisplayPort is offered but was not entered."
                    : "  Video: this adapter offers no DisplayPort alternate mode.");
            Console.WriteLine();
        }

        return ExitOk;
    }

    private static void PrintHuman(PortmarkReport report)
    {
        Console.WriteLine($"{report.Machine.Manufacturer} {report.Machine.Model}");
        Console.WriteLine();

        // Billboard data needs no setup, so it is worth showing even when the UCSI path is not
        // available. Printing it only on the happy path threw away the one answer this machine
        // could give with no configuration at all.
        PrintBillboards(report);

        if (report.Capability.Status != CapabilityStatus.Ok)
        {
            Console.WriteLine(Wrap(report.Capability.Explanation));
            if (report.Capability.Remedy is not null)
            {
                Console.WriteLine();
                Console.WriteLine(Wrap(report.Capability.Remedy));
            }
            return;
        }

        if (report.Capability.Features is { CableDetailsAvailable: false })
        {
            Console.WriteLine(Wrap(
                "Note: this PC's port controller does not report cable information, so the cable "
              + "rows below will say so. Port and power details are unaffected. This is a firmware "
              + "limitation, not a property of your cables."));
            Console.WriteLine();
        }

        foreach (ConnectorReport connector in report.Connectors)
        {
            Console.WriteLine($"Port {connector.Index}");
            Console.WriteLine($"  {connector.Summary}");

            if (connector.Capability is { } cap)
            {
                var supports = new List<string>();
                if (cap.SupportsUsb2) supports.Add("USB 2.0");
                if (cap.SupportsUsb3) supports.Add("USB 3.x");
                if (cap.SupportsAlternateModes) supports.Add("alternate modes");
                if (cap.SupportsDualRolePower) supports.Add("dual role power");
                if (cap.SupportsAudioAccessory) supports.Add("audio accessory");
                Console.WriteLine($"  Port supports {string.Join(", ", supports)}");
            }

            if (connector.ActiveAlternateModeIndex is int cam)
                Console.WriteLine($"  Alt mode     index {cam} active (identity unavailable on this PC)");

            PowerReport power = connector.Power;
            if (power.DataAvailable)
            {
                if (power.Negotiated is { } n)
                    Console.WriteLine($"  Negotiated   {n.Display}");
                if (power.PartnerSource.Count > 0)
                {
                    Console.WriteLine($"  Supply offers");
                    foreach (PowerObjectReport pdo in power.PartnerSource)
                        Console.WriteLine($"    - {pdo.Display}");
                }
                if (power.LocalSource.Count > 0)
                {
                    Console.WriteLine($"  This PC offers");
                    foreach (PowerObjectReport pdo in power.LocalSource)
                        Console.WriteLine($"    - {pdo.Display}");
                }
            }

            CableReport cable = connector.Cable;
            if (cable.DataAvailable)
            {
                Console.WriteLine($"  Speed        {cable.Speed?.Display ?? "not reported by the cable"}");
                Console.WriteLine($"  Power        {DescribeCurrent(cable)}");
                Console.WriteLine($"  Plug         {cable.PlugType ?? "unknown"}");
                Console.WriteLine($"  Construction {(cable.ActiveCable == true ? "active" : "passive")}");
                Console.WriteLine($"  Video        {cable.VideoNote}");
            }
            else if (cable.Reason is not null)
            {
                Console.WriteLine($"  Why          {Wrap(cable.Reason, 60).Replace(Environment.NewLine, Environment.NewLine + "               ")}");
            }

            Console.WriteLine();
        }
    }

    private static void PrintBillboards(PortmarkReport report)
    {
        foreach (Portmark.Core.Model.BillboardReport b in report.Billboards)
        {
            Console.WriteLine($"Adapter {b.VendorId}:{b.ProductId}");
            foreach (Portmark.Core.Model.AlternateModeReport m in b.Modes)
                Console.WriteLine($"  {m.Name}: {m.State}");
            Console.WriteLine(b.CarriesVideo
                ? "  Video        yes, DisplayPort is active through this adapter"
                : b.SupportsVideo
                    ? "  Video        supported but not currently active"
                    : "  Video        this adapter offers no DisplayPort mode");
            Console.WriteLine();
        }
    }

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

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"portmark: {message}");
        return ExitError;
    }

    private static string Wrap(string text, int width = 76)
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
              portmark enable          switch on the port controller interface (needs admin, once)
              portmark disable         switch it back off (needs admin)

            OPTIONS
              --human                  human-readable output instead of JSON
              --version                print the version
              --help                   this text

            EXIT CODES
              0  ports were read
              1  an error occurred
              2  a one-time setup step is needed; run 'portmark enable' as administrator
              3  this PC cannot report cable data

            NOTES
              Fields that the hardware did not report are null in JSON, and say so in --human
              output. Nothing is inferred from the shape of a connector.

              portmark only ever reads. It never sends a command that changes port state.
            """);
    }
}
