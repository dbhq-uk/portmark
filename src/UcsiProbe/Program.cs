using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using UcsiProbe;
using UcsiProbe.Probes;

// ucsiprobe - throwaway spike tool.
//
// Answers one question about the machine it runs on: can USB-C cable properties (the e-marker
// data the UCSI PPM holds) be read from user mode, and at what cost?
//
//   A  user mode, no admin, no registry change
//   B  user mode, after a one-time elevated TestInterfaceEnabled registry change
//   C  only via UcsiControl.exe from the Microsoft MUTT package
//   D  not readable on this hardware
//
// See CLEANROOM.md: no upstream WhatCable source was read.

bool enableFlag = args.Contains("--enable-test-interface");
bool disableFlag = args.Contains("--disable-test-interface");
bool discover = args.Contains("--discover");
bool sequence = args.Contains("--sequence");
bool ucsi = args.Contains("--ucsi");
string jsonPath = GetOption(args, "--json") ?? "ucsiprobe-report.json";

var report = new Report
{
    Machine = MachineInfo.Collect(),
    Elevated = MachineInfo.IsElevated(),
};

Console.WriteLine("ucsiprobe - USB-C cable property access spike");
Console.WriteLine(new string('=', 78));
Section("MACHINE");
Console.WriteLine($"  {report.Machine.Manufacturer} {report.Machine.Model}  (SKU {report.Machine.Sku})");
Console.WriteLine($"  BIOS {report.Machine.BiosVersion}");
Console.WriteLine($"  Windows {report.Machine.OsVersion} ({report.Machine.OsDisplayVersion}) {report.Machine.Architecture}");
Console.WriteLine($"  Elevated: {report.Elevated}");
Console.WriteLine($"  UCSI 2.x capable build (>= 22621): {MachineInfo.SupportsUcsi2x()}");

// ---------------------------------------------------------------- device inventory
Section("UCM-UCSI DEVICES");
report.UcmDevices = Devices.EnumerateUcmDevices();
if (report.UcmDevices.Count == 0)
{
    Console.WriteLine("  none present. This machine has no UCM-UCSI device; UCSI cannot be reached.");
}
foreach (UcmDevice d in report.UcmDevices)
{
    Console.WriteLine($"  {d.Description}");
    Console.WriteLine($"    instance id          : {d.InstanceId}");
    Console.WriteLine($"    service              : {d.Service}   driver {d.DriverVersion}");
    Console.WriteLine($"    TestInterfaceEnabled : {(d.TestInterfaceEnabled ? "1 (set)" : "not set")}");
    Console.WriteLine($"    published interfaces : {(d.InterfaceClasses.Count == 0 ? "(none)" : string.Join(", ", d.InterfaceClasses))}");
}

UcmDevice? primary = report.UcmDevices.FirstOrDefault();
report.TestInterfaceEnabledFlagSet = primary?.TestInterfaceEnabled ?? false;
report.TestInterfaceFlagKey = primary?.InstanceId is { } pid ? Devices.TestInterfaceFlagKeyPath(pid) : null;

// ---------------------------------------------------------------- optional flag mutation
if ((enableFlag || disableFlag) && primary?.InstanceId is { } instanceId)
{
    Section(enableFlag ? "SETTING TestInterfaceEnabled" : "CLEARING TestInterfaceEnabled");
    if (!report.Elevated)
    {
        Console.WriteLine("  refused: this requires administrator rights. Re-run from an elevated prompt.");
        return 2;
    }
    if (!MutateFlag(instanceId, enable: enableFlag)) return 3;
    RestartDevice(instanceId);

    // Re-read so the report reflects reality after the change.
    report.UcmDevices = Devices.EnumerateUcmDevices();
    primary = report.UcmDevices.FirstOrDefault();
    report.TestInterfaceEnabledFlagSet = primary?.TestInterfaceEnabled ?? false;
    Console.WriteLine($"  TestInterfaceEnabled now: {(report.TestInterfaceEnabledFlagSet ? "1 (set)" : "not set")}");
    Console.WriteLine($"  published interfaces now: {string.Join(", ", primary?.InterfaceClasses ?? [])}");
}

// ---------------------------------------------------------------- stage A
Section("STAGE A - documented user-mode surfaces (no admin, no registry change)");
HighLevelProbes.Run(report, "A");
bool gotWithoutFlag = !report.TestInterfaceEnabledFlagSet && UcsiAccess.TryAll(report, "A");
PrintStage(report, "A");

// ---------------------------------------------------------------- stage B
bool gotWithFlag = false;
Section("STAGE B - UCSI test interface (requires TestInterfaceEnabled)");
if (report.TestInterfaceEnabledFlagSet)
{
    gotWithFlag = UcsiAccess.TryAll(report, "B");
    PrintStage(report, "B");
}
else
{
    Console.WriteLine("  TestInterfaceEnabled is not set, so the test interface is not published.");
    Console.WriteLine($"  To exercise this stage, from an elevated prompt run:");
    Console.WriteLine($"      ucsiprobe --enable-test-interface");
    Console.WriteLine($"  which sets {report.TestInterfaceFlagKey} = 1 (DWORD) and restarts the device,");
    Console.WriteLine($"  and afterwards run  ucsiprobe --disable-test-interface  to put the machine back.");
}

// ---------------------------------------------------------------- IOCTL discovery
if (discover || sequence || ucsi)
{
    Guid testIface = UcsiAccess.CandidateInterfaces[0].Guid;

    // Restarting the device republishes the test interface, which lets an experiment that closes
    // it be followed by one that still needs it. Only possible when elevated.
    IoctlSequence.DeviceReviver? revive = null;
    if (report.Elevated && primary?.InstanceId is { } rid)
    {
        revive = () =>
        {
            RestartDevice(rid);
            return Devices.EnumerateInterfacePaths(testIface, out _).FirstOrDefault();
        };
    }

    List<string> testPaths = Devices.EnumerateInterfacePaths(testIface, out _);
    if (testPaths.Count == 0 && revive is not null)
    {
        Console.WriteLine();
        Console.WriteLine("Test interface not currently published; restarting device to republish it.");
        string? p = revive();
        if (p is not null) testPaths = [p];
    }

    if (testPaths.Count == 0)
    {
        Section("DISCOVERY");
        Console.WriteLine($"  interface {{{testIface}}} is not published.");
        Console.WriteLine("  Enable the test interface first, and re-run elevated so the device can be restarted.");
    }
    else
    {
        if (ucsi)
        {
            Section("UCSI SESSION - drive the PPM through the test interface");
            string? p = Devices.EnumerateInterfacePaths(testIface, out _).FirstOrDefault()
                        ?? (revive is not null ? revive() : null);
            if (p is null) Console.WriteLine("  test interface not available.");
            else gotWithFlag |= UcsiSession.Run(report, "B", p);
        }
        if (sequence)
        {
            Section("SEQUENCE - call ordering and buffer sizes on the test interface");
            IoctlSequence.Run(report, "sequence", testPaths[0], revive);
        }
        if (discover)
        {
            Section("DISCOVERY - buffer shape sweep of the in-box test interface");
            string? p = Devices.EnumerateInterfacePaths(testIface, out _).FirstOrDefault()
                        ?? (revive is not null ? revive() : null);
            if (p is null)
            {
                Console.WriteLine("  interface no longer openable and cannot be revived.");
            }
            else
            {
                Console.WriteLine($"  probing {p}");
                IoctlDiscovery.Run(report, "discover", p);
                PrintStage(report, "discover");
                if (!report.Attempts.Any(a => a.Stage == "discover"))
                    Console.WriteLine("  every candidate IOCTL was rejected for every buffer shape.");
            }
        }
    }
}

// ---------------------------------------------------------------- stage A fallback
Section("FALLBACK - USB hub descriptors (link speed only, not cable data)");
UsbHubProbe.Run(report, "fallback");
PrintStage(report, "fallback");

// ---------------------------------------------------------------- stage C
Section("STAGE C - UcsiControl.exe (Microsoft MUTT package)");
string? ucsiControl = FindUcsiControl();
Console.WriteLine(ucsiControl is null
    ? "  UcsiControl.exe not found on PATH or in the usual MUTT install locations."
    : $"  found: {ucsiControl}");
report.Add(new Attempt
{
    Stage = "C", Api = "file search", Target = "UcsiControl.exe",
    Success = ucsiControl is not null,
    Result = ucsiControl,
    Error = ucsiControl is null ? "not installed" : null,
});

// ---------------------------------------------------------------- verdict
(report.Verdict, report.VerdictReason) = Decide(report, gotWithoutFlag, gotWithFlag, ucsiControl);

Section("VERDICT");
Console.WriteLine($"  {report.Verdict}");
Console.WriteLine($"  {report.VerdictReason}");
if (report.CablePropertyBytes.Count > 0)
{
    Console.WriteLine("  GET_CABLE_PROPERTY responses:");
    foreach (string line in report.CablePropertyBytes) Console.WriteLine($"    {line}");
}

File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine();
Console.WriteLine($"Full evidence written to {Path.GetFullPath(jsonPath)} ({report.Attempts.Count} recorded calls).");
return 0;

// ---------------------------------------------------------------- helpers

static (Verdict, string) Decide(Report r, bool withoutFlag, bool withFlag, string? ucsiControl)
{
    if (r.UcmDevices.Count == 0)
        return (Verdict.D_NotReadable, "No UCM-UCSI device is present, so there is no PPM to query.");
    if (withoutFlag)
        return (Verdict.A_UserModeNoChanges, "GET_CABLE_PROPERTY returned data with no admin rights and no registry change.");
    if (withFlag)
        return (Verdict.B_UserModeAfterRegistryFlag, "GET_CABLE_PROPERTY returned data from user mode once TestInterfaceEnabled was set.");
    if (r.TestInterfaceEnabledFlagSet)
        return (ucsiControl is not null ? Verdict.C_UcsiControlOnly : Verdict.D_NotReadable,
                "TestInterfaceEnabled is set but no interface accepted the UCSI IOCTLs from user mode.");
    return (Verdict.Undetermined_StageBNotExercised,
            "Stage A returned nothing and TestInterfaceEnabled is not set. Re-run elevated with --enable-test-interface to settle B vs D.");
}

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine(new string('-', 78));
}

static void PrintStage(Report r, string stage)
{
    foreach (Attempt a in r.Attempts.Where(x => x.Stage == stage))
    {
        string mark = a.Success ? "ok  " : "FAIL";
        Console.WriteLine($"  [{mark}] {a.Api} :: {a.Target}");
        if (a.Detail is not null) Console.WriteLine($"         {a.Detail}");
        if (a.Result is not null) Console.WriteLine($"         -> {a.Result}");
        if (a.DataHex is not null) Console.WriteLine($"         -> data {a.DataHex}");
        if (a.Error is not null) Console.WriteLine($"         !! {a.Error}");
    }
}

static bool MutateFlag(string instanceId, bool enable)
{
    string sub = $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters";
    try
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(sub, writable: true);
        if (k is null) { Console.WriteLine($"  could not open HKLM\\{sub}"); return false; }
        if (enable) k.SetValue("TestInterfaceEnabled", 1, RegistryValueKind.DWord);
        else k.DeleteValue("TestInterfaceEnabled", throwOnMissingValue: false);
        Console.WriteLine($"  {(enable ? "set" : "deleted")} HKLM\\{sub}\\TestInterfaceEnabled");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  registry write failed: {ex.Message}");
        return false;
    }
}

static void RestartDevice(string instanceId)
{
    Console.WriteLine($"  restarting device {instanceId} via pnputil ...");
    var psi = new ProcessStartInfo("pnputil.exe", $"/restart-device \"{instanceId}\"")
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    using Process? p = Process.Start(psi);
    if (p is null) { Console.WriteLine("  could not launch pnputil"); return; }
    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        Console.WriteLine($"    {line.TrimEnd()}");
    Thread.Sleep(2000);   // give PnP time to republish interfaces
}

static string? FindUcsiControl()
{
    var candidates = new List<string>
    {
        @"C:\Program Files (x86)\USBTest\x64\UcsiControl.exe",
        @"C:\Program Files (x86)\USBTest\x86\UcsiControl.exe",
        @"C:\Program Files\USBTest\x64\UcsiControl.exe",
    };
    foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        if (!string.IsNullOrWhiteSpace(dir))
            candidates.Add(Path.Combine(dir.Trim(), "UcsiControl.exe"));

    return candidates.FirstOrDefault(File.Exists);
}

static string? GetOption(string[] argv, string name)
{
    int i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}
