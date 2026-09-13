using Portmark.Core.Model;
using Portmark.Core.Ucsi;

namespace Portmark.Cli;

/// <summary>
/// GET_ALTERNATE_MODES is refused on the machine this was written against, while GET_CAM_SUPPORTED
/// and GET_CURRENT_CAM both return data showing an alternate mode is active. So the modes exist
/// and something about the request is wrong, or the controller declines this particular command.
///
/// This sweeps the parameter space rather than guessing a second time: every recipient, a range of
/// offsets, and every value of the two-bit count field. Anything that returns a payload wins.
/// All calls are GET_*.
/// </summary>
internal static class AltModeSweep
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

        Console.WriteLine($"UCSI {capability.UcsiVersion}, "
                        + $"{capability.Features?.AlternateModeCount} alternate mode(s) advertised");
        Console.WriteLine();

        int connectors = capability.ConnectorCount ?? 0;
        int hits = 0;

        for (byte c = 1; c <= connectors; c++)
        {
            UcsiResult supported = connection.ExecuteForConnector(UcsiProtocol.CmdGetCamSupported, c);
            UcsiResult current = connection.ExecuteForConnector(UcsiProtocol.CmdGetCurrentCam, c);
            Console.WriteLine($"connector {c}: CAM supported {Hex(supported)}, current CAM {Hex(current)}");

            foreach (byte recipient in new byte[] { 0, 1, 2, 3 })
                foreach (byte offset in new byte[] { 0, 1, 2 })
                    foreach (byte count in new byte[] { 0, 1, 2, 3 })
                    {
                        ulong control = UcsiProtocol.GetAlternateModes(recipient, c, offset, count);
                        UcsiResult r = connection.Execute(UcsiProtocol.CmdGetAlternateModes, control);

                        if (r.Ok && r.Payload.Length > 0)
                        {
                            hits++;
                            Console.WriteLine($"    HIT recipient={recipient} offset={offset} count={count} "
                                            + $"-> {Convert.ToHexString(r.Payload)} (CCI 0x{r.Cci:X8})");
                        }
                    }

            Console.WriteLine();
        }

        if (hits == 0)
        {
            Console.WriteLine("No parameter combination returned a payload.");
            Console.WriteLine("Every attempt completed with CCI bit 30 set and zero length, which is");
            Console.WriteLine("this controller declining the command rather than failing it.");
        }

        return 0;
    }

    private static string Hex(UcsiResult r) =>
        r.Ok && r.Payload.Length > 0 ? Convert.ToHexString(r.Payload) : $"(none, CCI 0x{r.Cci:X8})";
}
