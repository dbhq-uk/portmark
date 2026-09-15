using Portmark.Core.Power;

namespace Portmark.Core.Ucsi;

/// <summary>
/// Compares the power contract in force against what the attached supply offers, and pairs it
/// with the controller's own opinion of the charging rate.
///
/// This is the power half of the question portmark already answers for data. A supply offering
/// 100W while a 15W contract is in force is the thing people notice as "it charges slowly", and
/// Windows shows neither number. Both halves come from the hardware: the offers from GET_PDOS and
/// the contract from the RDO in GET_CONNECTOR_STATUS.
///
/// The controller's own view is a separate field, bits 64 and 65 of GET_CONNECTOR_STATUS, and it
/// is the field Windows drives its own slow-charging notification from. It is reported beside the
/// measurement rather than instead of it, because on the machine this was written against the two
/// disagree: 15W was in force against a 100W supply with the battery at 46 percent, and the
/// controller called that a nominal charging rate throughout. Reporting only the controller's view
/// would have hidden the gap, and reporting only the gap would have hidden that the firmware is
/// content with it. Neither is worth more than the other, so both are shown.
/// </summary>
public static class ChargeDiagnostic
{
    // Battery charging status, GET_CONNECTOR_STATUS bits 64-65.
    public const int NotCharging = 0;
    public const int Nominal = 1;
    public const int Slow = 2;
    public const int VerySlow = 3;

    /// <summary>
    /// How much of the offer has to go unused before it is worth mentioning. Half. A PC that took
    /// 45W of an available 65W is doing nothing interesting; one that took 15W of 100W is.
    /// </summary>
    public const int ShortfallDivisor = 2;

    public static string StatusLabel(int status) => status switch
    {
        NotCharging => "not charging",
        Nominal => "nominal charging rate",
        Slow => "slow charging rate",
        VerySlow => "very slow charging rate",
        _ => $"unknown ({status})",
    };

    /// <summary>
    /// True when less than half the offered power was taken. Only meaningful while this PC is the
    /// one consuming: when it is supplying, a small contract is the attached device's choice and
    /// says nothing about this machine.
    /// </summary>
    public static bool IsUnderNegotiated(int? negotiatedMilliwatts, int? offeredMilliwatts, bool consuming)
        => consuming
        && negotiatedMilliwatts is int taken and > 0
        && offeredMilliwatts is int offered and > 0
        && taken * ShortfallDivisor < offered;

    /// <summary>
    /// What was measured, what the controller thinks of it, and what cannot be concluded. Null
    /// when the contract is a reasonable share of the offer, which needs no explanation.
    ///
    /// It states no cause. A battery that is nearly full draws little and that is correct
    /// behaviour, not a fault, and this cannot be told apart from a PC or supply that will not go
    /// higher. Saying "your charger is faulty" on this evidence would be the confident wrong
    /// answer the project exists to avoid.
    /// </summary>
    /// <summary>
    /// The charge above which a battery drawing little is simply a battery with nothing left to
    /// take on. Below it, that explanation is not available and the gap needs another one.
    /// </summary>
    public const int NearlyFullPercent = 90;

    /// <param name="battery">
    /// The batteries' own readings, or null when no battery device answered, in which case only the
    /// percentage from GetSystemPowerStatus is available and the wording is as it was without them.
    /// </param>
    public static string? Explain(int? negotiatedMilliwatts, int? offeredMilliwatts, bool consuming,
                                  int? batteryChargingStatus, int? batteryPercent = null,
                                  BatteryFlow? battery = null)
    {
        if (!IsUnderNegotiated(negotiatedMilliwatts, offeredMilliwatts, consuming)) return null;

        int taken = negotiatedMilliwatts!.Value;
        int offered = offeredMilliwatts!.Value;

        string controller = batteryChargingStatus switch
        {
            Slow or VerySlow => $"The controller agrees, reporting a {StatusLabel(batteryChargingStatus.Value)}.",
            Nominal => "The controller nonetheless reports a nominal charging rate, so its firmware is "
                     + "not treating this as a shortfall.",
            NotCharging => "The controller reports it is not charging.",
            _ => "The controller did not report a charging rate.",
        };

        string flow = battery is null ? "" : Flow(battery) + " ";

        // The warning that Windows can say charging while the battery falls is only worth giving when
        // the battery has not already said which way its charge is going. The first version dropped
        // it only for a measured drain, so a measured gain read "net charge of 12W" and then warned
        // that the battery might be falling. A zero rate is not trusted, so it keeps the warning.
        bool directionMeasured = battery?.RateMilliwatts is int rate && rate != 0;

        // The battery percentage can take one explanation away, never grant one. Below the
        // threshold a full battery cannot be the reason. Above it, a full battery might be, but a
        // machine at 97 percent under load can still be draining on a bad contract, so that is
        // offered as a possibility and not as a verdict.
        string percentage = batteryPercent switch
        {
            null => "Whether the battery is nearly full could not be read. A battery with little left to take "
                  + "on draws little and that is correct, so a low contract is not on its own a fault.",
            >= NearlyFullPercent => $"The battery is at {batteryPercent} percent, which could explain a low "
                  + "contract: a battery with little left to take on draws little. It does not rule out a fault.",
            _ => $"The battery is at {batteryPercent} percent, so a full battery does not explain this, though a "
               + "charge limit could. Check the cable and which port the supply is in"
               + (directionMeasured
                   ? "."
                   : ", and be aware that Windows can report this as charging while the battery falls."),
        };

        // An offer is not delivery. A supply whose higher rail fails leaves exactly this contract
        // behind, so the supply is never cleared.
        return $"A {Watts(taken)} contract is in force, but this supply offers up to {Watts(offered)}. "
             + $"{controller} {flow}{percentage} Why the contract is lower, and whether the supply can deliver what "
             + "it advertises, cannot be read from here.";
    }

    /// <summary>
    /// The battery's measured charge flow. It is the machine's load and the supply's delivery
    /// netted together inside the battery, so it is never presented as what the charger or cable
    /// carries: 15W can arrive while the battery reports -8W.
    ///
    /// A zero is not trusted. Microsoft notes some batteries report only discharging rates, and
    /// this machine's WMI charge rate read zero while the battery fell.
    ///
    /// When not every battery could be read, the gap is the whole sentence: the rate is unknown by
    /// construction, and the batteries that answered are not presented as the machine.
    /// </summary>
    private static string Flow(BatteryFlow battery) => battery.CoverageNote ?? battery.RateMilliwatts switch
    {
        int drain and < 0 when battery.OnExternalPower =>
            $"The battery measures a net drain of {Watts(-drain)} while on external power: it is losing charge "
          + "despite the charger. Windows' charging indicator is not evidence either way. That figure is the "
          + "battery's own charge flow, not the power arriving through the cable.",
        int drain and < 0 =>
            $"The battery measures a net drain of {Watts(-drain)} and does not report external power. That figure "
          + "is the battery's own charge flow, not the power arriving through the cable.",
        int gain and > 0 =>
            $"The battery measures a net charge of {Watts(gain)}. That is the battery's own charge flow, not the "
          + "power arriving through the cable, so it does not show how much the supply is delivering.",
        0 => "The battery reports a rate of zero. Some batteries report only discharging rates, so this is not "
           + "taken as evidence that the charge is holding.",
        _ when battery.Discharging =>
            "The battery reports it is discharging but did not report a rate, so the drain could not be measured.",
        _ => "The battery did not report its charge rate in watts, so whether it is gaining or losing charge "
           + "could not be measured.",
    };

    private static string Watts(int milliwatts) => $"{milliwatts / 1000.0:0.#}W";
}
