using System.Management;
using Windows.Devices.Enumeration;

namespace UcsiProbe.Probes;

/// <summary>
/// The documented, supported surfaces: WMI/CIM and WinRT. Checked first, because if either
/// carried cable data the product would need no elevation and no registry change at all.
/// </summary>
internal static class HighLevelProbes
{
    /// <summary>Property names that would indicate genuine Type-C or e-marker data.</summary>
    private static readonly string[] InterestingTerms =
        ["cable", "emarker", "e-marker", "ucsi", "typec", "type-c", "type_c", "pdo", "powerdelivery",
         "contract", "watt", "voltage", "current", "alternatemode", "thunderbolt", "displayport"];

    internal static void Run(Report report, string stage)
    {
        ProbeWmiPnpEntity(report, stage);
        ProbeWmiTypeCClasses(report, stage);
        ProbeWinRt(report, stage);
    }

    private static void ProbeWmiPnpEntity(Report report, string stage)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2", "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'UCM'");
            var hits = new List<string>();
            int count = 0;

            foreach (ManagementBaseObject mo in searcher.Get())
            {
                count++;
                foreach (PropertyData prop in mo.Properties)
                {
                    if (prop.Value is null) continue;
                    string name = prop.Name.ToLowerInvariant();
                    if (InterestingTerms.Any(t => name.Contains(t)))
                        hits.Add($"{prop.Name}={prop.Value}");
                }
                mo.Dispose();
            }

            report.Add(new Attempt
            {
                Stage = stage,
                Api = "WMI Win32_PnPEntity (PNPClass='UCM')",
                Target = @"root\cimv2",
                Success = hits.Count > 0,
                Result = hits.Count > 0 ? string.Join("; ", hits) : $"{count} instance(s), no cable/Type-C properties exposed",
                Error = hits.Count > 0 ? null : "class exposes PnP metadata only, no UCSI payload",
            });
        }
        catch (Exception ex)
        {
            report.Add(new Attempt
            {
                Stage = stage, Api = "WMI Win32_PnPEntity", Target = @"root\cimv2",
                Success = false, Error = ex.Message,
            });
        }
    }

    /// <summary>Looks for any WMI class in root\wmi that might carry Type-C or PD data.</summary>
    private static void ProbeWmiTypeCClasses(Report report, string stage)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();
            var matches = new List<string>();

            using var classSearcher = new ManagementObjectSearcher(
                scope, new WqlObjectQuery("SELECT * FROM meta_class"));
            foreach (ManagementBaseObject cls in classSearcher.Get())
            {
                string name = cls["__CLASS"]?.ToString() ?? "";
                string lower = name.ToLowerInvariant();
                if (lower.Contains("ucsi") || lower.Contains("typec") || lower.Contains("type_c")
                    || lower.Contains("usbpd") || lower.Contains("connector"))
                    matches.Add(name);
                cls.Dispose();
            }

            report.Add(new Attempt
            {
                Stage = stage,
                Api = @"WMI meta_class scan of root\wmi",
                Target = "classes matching ucsi|typec|usbpd|connector",
                Success = matches.Count > 0,
                Result = matches.Count > 0 ? string.Join(", ", matches) : "no matching WMI classes",
                Error = matches.Count > 0 ? null : "no Type-C/UCSI WMI provider registered",
            });
        }
        catch (Exception ex)
        {
            report.Add(new Attempt
            {
                Stage = stage, Api = @"WMI meta_class scan of root\wmi", Target = "root\\wmi",
                Success = false, Error = ex.Message,
            });
        }
    }

    /// <summary>
    /// WinRT device enumeration over the interfaces the UCM stack publishes. Windows.Devices.Usb
    /// itself has no Type-C surface, so the question is whether DeviceInformation exposes any
    /// Type-C properties on those interfaces.
    /// </summary>
    private static void ProbeWinRt(Report report, string stage)
    {
        foreach ((string name, Guid guid) in UcsiAccess.CandidateInterfaces)
        {
            try
            {
                string selector = $"System.Devices.InterfaceClassGuid:=\"{{{guid}}}\"";
                DeviceInformationCollection devices =
                    DeviceInformation.FindAllAsync(selector, []).AsTask().GetAwaiter().GetResult();

                var props = new List<string>();
                foreach (DeviceInformation d in devices)
                    foreach (KeyValuePair<string, object> p in d.Properties)
                        if (p.Value is not null && InterestingTerms.Any(t => p.Key.ToLowerInvariant().Contains(t)))
                            props.Add($"{p.Key}={p.Value}");

                report.Add(new Attempt
                {
                    Stage = stage,
                    Api = "WinRT DeviceInformation.FindAllAsync",
                    Target = $"{name} {{{guid}}}",
                    Success = props.Count > 0,
                    Result = props.Count > 0
                        ? string.Join("; ", props)
                        : $"{devices.Count} device(s), no Type-C/cable properties",
                    Error = props.Count > 0 ? null : "WinRT exposes no e-marker data on this interface",
                });
            }
            catch (Exception ex)
            {
                report.Add(new Attempt
                {
                    Stage = stage, Api = "WinRT DeviceInformation.FindAllAsync",
                    Target = $"{name} {{{guid}}}", Success = false, Error = ex.Message,
                });
            }
        }
    }
}
