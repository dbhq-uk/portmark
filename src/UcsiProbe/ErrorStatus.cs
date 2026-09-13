namespace UcsiProbe;

/// <summary>
/// Decodes a UCSI GET_ERROR_STATUS response.
///
/// The first 16 bits are a bit field of error conditions. This matters for honesty: it is the
/// difference between "the PPM does not implement this command" and "the PPM implements it and
/// there is genuinely nothing attached to report".
/// </summary>
public static class ErrorStatus
{
    private static readonly (int Bit, string Meaning)[] Conditions =
    [
        (0,  "unrecognised command"),
        (1,  "non-existent connector number"),
        (2,  "invalid command-specific parameters"),
        (3,  "incompatible connector partner"),
        (4,  "CC communication error"),
        (5,  "command failed because of dead battery"),
        (6,  "contract negotiation failed"),
        (7,  "overcurrent"),
        (8,  "undefined"),
        (9,  "port partner rejected swap"),
        (10, "hard reset"),
        (11, "PPM policy conflict"),
        (12, "swap rejected"),
        (13, "reverse current protection"),
    ];

    public static string Describe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return "response too short to decode";

        ushort flags = (ushort)(data[0] | (data[1] << 8));
        if (flags == 0) return "no error reported";

        var set = Conditions.Where(c => (flags & (1 << c.Bit)) != 0).Select(c => c.Meaning).ToList();
        return set.Count > 0
            ? $"0x{flags:X4}: {string.Join("; ", set)}"
            : $"0x{flags:X4}: unrecognised error bits";
    }

    /// <summary>True when the PPM explicitly said it does not know the command.</summary>
    public static bool IsUnrecognisedCommand(ReadOnlySpan<byte> data)
        => data.Length >= 2 && (data[0] & 0x01) != 0;
}
