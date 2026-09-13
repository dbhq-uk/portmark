using Portmark.Core.Model;
using Portmark.Core.Ucsi;

namespace Portmark.Cli;

/// <summary>
/// Exploratory probe of everything this controller advertises but portmark does not yet read.
///
/// Cable details are unavailable on the machine this was written against, but two other optional
/// features are advertised: alternate mode details and PDO details. Between them they should carry
/// the negotiated power contract and whether the port can carry video, which is most of what the
/// product promises. This command issues those commands and dumps what comes back, so the decoding
/// can be written against real bytes rather than guessed from the specification.
///
/// Every command issued here is a GET_*. Nothing changes port state.
/// </summary>
internal static class Explore
{
    public static int Run()
    {
        var capability = new CapabilityReport();
        UcsiConnection? connection = Portmark.Core.PortmarkReader.DetectCapability(capability);
        if (connection is null)
        {
            Console.WriteLine($"Cannot reach the controller: {capability.Explanation}");
            return 1;
        }

        Console.WriteLine($"UCSI {capability.UcsiVersion}, {capability.ConnectorCount} connector(s)");
        PpmFeatureReport? f = capability.Features;
        Console.WriteLine($"optional features {f?.OptionalFeaturesHex}: "
                        + $"cable={Yn(f?.CableDetailsAvailable)} "
                        + $"altModes={Yn(f?.AlternateModeDetailsAvailable)} "
                        + $"pdos={Yn(f?.PowerDataObjectDetailsAvailable)}");
        Console.WriteLine();

        int connectors = capability.ConnectorCount ?? 0;
        for (byte c = 1; c <= connectors; c++)
        {
            Console.WriteLine($"=== connector {c} ===");

            Show(connection, "GET_CONNECTOR_CAPABILITY", UcsiProtocol.CmdGetConnectorCapability,
                 UcsiProtocol.ForConnector(UcsiProtocol.CmdGetConnectorCapability, c));

            Show(connection, "GET_CONNECTOR_STATUS", UcsiProtocol.CmdGetConnectorStatus,
                 UcsiProtocol.ForConnector(UcsiProtocol.CmdGetConnectorStatus, c));

            Show(connection, "GET_CAM_SUPPORTED", UcsiProtocol.CmdGetCamSupported,
                 UcsiProtocol.ForConnector(UcsiProtocol.CmdGetCamSupported, c));

            Show(connection, "GET_CURRENT_CAM", UcsiProtocol.CmdGetCurrentCam,
                 UcsiProtocol.ForConnector(UcsiProtocol.CmdGetCurrentCam, c));

            foreach ((string label, byte recipient) in new[]
                     { ("connector", (byte)0), ("partner SOP", (byte)1), ("cable SOP'", (byte)2) })
                for (byte index = 0; index < 3; index++)
                    Show(connection, $"GET_ALTERNATE_MODES {label} #{index}",
                         UcsiProtocol.CmdGetAlternateModes,
                         UcsiProtocol.GetAlternateModes(recipient, c, index));

            foreach ((string label, bool partner, bool source) in new[]
                     {
                         ("local sink", false, false), ("local source", false, true),
                         ("partner sink", true, false), ("partner source", true, true),
                     })
                Show(connection, $"GET_PDOS {label}", UcsiProtocol.CmdGetPdos,
                     UcsiProtocol.GetPdos(c, partner, 0, 3, source));

            Console.WriteLine();
        }

        return 0;
    }

    private static void Show(UcsiConnection connection, string label, byte command, ulong control)
    {
        UcsiResult r = connection.Execute(command, control);
        string state = !r.Ok
            ? $"FAILED {r.Error}"
            : r.NotSupported
                ? "not supported by this controller"
                : r.Payload.Length == 0
                    ? $"completed, empty (CCI 0x{r.Cci:X8})"
                    : $"{Convert.ToHexString(r.Payload)}  (len {r.DataLength}, CCI 0x{r.Cci:X8})";

        Console.WriteLine($"  {label,-36} {state}");
    }

    private static string Yn(bool? value) => value == true ? "yes" : value == false ? "no" : "?";
}
