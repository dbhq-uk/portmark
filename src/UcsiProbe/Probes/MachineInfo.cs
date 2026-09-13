using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace UcsiProbe.Probes;

/// <summary>Identifies the machine so a spike result can be attributed to specific hardware.</summary>
internal static class MachineInfo
{
    internal static Machine Collect()
    {
        using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using RegistryKey? cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        string build = cv?.GetValue("CurrentBuild")?.ToString() ?? "?";
        string ubr = cv?.GetValue("UBR")?.ToString() ?? "?";

        return new Machine
        {
            Manufacturer = bios?.GetValue("SystemManufacturer")?.ToString(),
            Model = bios?.GetValue("SystemProductName")?.ToString(),
            Sku = bios?.GetValue("SystemSKU")?.ToString(),
            BiosVersion = bios?.GetValue("BIOSVersion")?.ToString(),
            OsVersion = $"{Environment.OSVersion.Version.Major}.0.{build}.{ubr}",
            OsDisplayVersion = cv?.GetValue("DisplayVersion")?.ToString(),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
        };
    }

    internal static bool IsElevated()
    {
        using WindowsIdentity id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Windows 11 22H2 September Update (build 22621) is the floor for UCSI 2.x support.</summary>
    internal static bool SupportsUcsi2x() => Environment.OSVersion.Version.Build >= 22621;
}
