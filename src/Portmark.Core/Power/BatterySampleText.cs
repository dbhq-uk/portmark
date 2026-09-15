namespace Portmark.Core.Power;

/// <summary>
/// The plain-English rendering of 'portmark battery --sample'. It lives here rather than in the CLI
/// so the words it prints can be tested, not only the numbers under them: the first version printed
/// "gaining 0.0W" beside a note saying a zero change was not a zero flow.
/// </summary>
public static class BatterySampleText
{
    private const string Indent = "               ";

    /// <param name="wrap">Wraps a paragraph to a width; the CLI passes its own.</param>
    public static void Write(IReadOnlyList<BatterySample> samples, TextWriter output, Func<string, int, string> wrap)
    {
        foreach (BatterySample s in samples)
        {
            output.WriteLine(samples.Count > 1 ? $"Sample, battery {s.Index}" : "Sample");
            output.WriteLine(FormattableString.Invariant($"  Interval     {s.Elapsed.TotalSeconds:0.0}s"));
            if (s.StartMilliwattHours is int start)
                output.WriteLine($"  Capacity     {start} mWh at the start, "
                               + (s.EndMilliwattHours is int end ? $"{end} mWh at the end" : "not reported at the end"));

            // A zero change is printed as no change, not as "0W": the note says why it is not a
            // measured zero flow.
            if (s.AverageNetMilliwatts is int average)
                output.WriteLine(average == 0
                    ? "  Average      no change in reported capacity over the interval"
                    : $"  Average      {DescribeFlow(average)} net, averaged over the interval");
            if (s.Note is not null)
                output.WriteLine(Indent + wrap(s.Note, 60).Replace(Environment.NewLine, Environment.NewLine + Indent));
            output.WriteLine();
        }

        if (samples.Count > 0) output.WriteLine(wrap(BatteryTelemetry.CoarseReportingNote, 76));
    }

    public static string DescribeFlow(int milliwatts) => milliwatts < 0
        ? FormattableString.Invariant($"losing {-milliwatts / 1000.0:0.0#}W")
        : FormattableString.Invariant($"gaining {milliwatts / 1000.0:0.0#}W");
}
