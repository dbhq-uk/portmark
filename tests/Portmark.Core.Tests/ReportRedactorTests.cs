using System.Text.Json;
using System.Text.Json.Nodes;
using Portmark.Core.Model;
using Portmark.Core.Reporting;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// The hardware report is written to be pasted into a public issue, so anything that identifies a
/// person or a particular machine must be gone from it by default. These tests pin down what is
/// removed, that the same value always becomes the same placeholder, and that nothing a decoder
/// needs (raw bytes, CCI values, the UCSI device's own ID) is lost along the way.
/// </summary>
public class ReportRedactorTests
{
    private static readonly IdentityHints Hints = new(
        MachineName: "DESKTOP-7QX2K1P",
        UserName: "jsmith",
        UserProfilePath: @"C:\Users\jsmith",
        UserDomainName: "DESKTOP-7QX2K1P");

    [Fact]
    public void SerialNumbersBecomeStablePlaceholders_SoDevicesStayDistinguishable()
    {
        JsonNode root = JsonNode.Parse("""
            {
              "devices": [
                { "vendorId": "0x0BDA", "serialNumber": "000001000000" },
                { "vendorId": "0x0781", "serialNumber": "4C530001231121115442" },
                { "vendorId": "0x0BDA", "serialNumber": "000001000000" }
              ]
            }
            """)!;

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        Assert.Equal("[serial-1]", (string?)root["devices"]![0]!["serialNumber"]);
        Assert.Equal("[serial-2]", (string?)root["devices"]![1]!["serialNumber"]);
        Assert.Equal("[serial-1]", (string?)root["devices"]![2]!["serialNumber"]);
        Assert.Equal("0x0BDA", (string?)root["devices"]![0]!["vendorId"]);

        RedactionEntry first = Assert.Single(redactor.Entries, e => e.Placeholder == "[serial-1]");
        Assert.Equal(new[] { "devices[0].serialNumber", "devices[2].serialNumber" }, first.Locations);
        Assert.Equal(2, redactor.Entries.Count);
    }

    [Fact]
    public void AValueRedactedInTheJsonIsAlsoRedactedWhereverElseItAppears()
    {
        JsonNode root = JsonNode.Parse("""
            {
              "reading": { "devices": [ { "serialNumber": "4C530001231121115442" } ] },
              "human": [ "Cruzer Blade", "  Serial       4C530001231121115442" ]
            }
            """)!;

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        Assert.Equal("  Serial       [serial-1]", (string?)root["human"]![1]);
        RedactionEntry entry = Assert.Single(redactor.Entries);
        Assert.Contains("human[1]", entry.Locations);
    }

    [Fact]
    public void InstanceIdKeepsTheHardwareIdButHidesTheInstanceSegment()
    {
        // USB instance IDs end in the device's serial number when it has one. The enumerator and
        // hardware ID before it say what the device is, which is what a report reader needs.
        JsonNode root = JsonNode.Parse("""
            { "a": { "ucmDeviceInstanceId": "USB\\VID_0BDA&PID_8153\\000001000000" } }
            """)!;

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        Assert.Equal(@"USB\VID_0BDA&PID_8153\[instance-1]", (string?)root["a"]!["ucmDeviceInstanceId"]);
        Assert.Equal("device instance ID", Assert.Single(redactor.Entries).Kind);
    }

    [Fact]
    public void AcpiInstanceNumberIsNotIdentifying_AndIsLeftAlone()
    {
        // The UCSI device on the ThinkPad T16 Gen 2 is ACPI\USBC000\0. The trailing 0 is the ACPI
        // unique ID, the same on every machine with one connector manager, and hiding it would
        // only make the report harder to read.
        JsonNode root = JsonNode.Parse("""
            { "capability": { "ucmDeviceInstanceId": "ACPI\\USBC000\\0" } }
            """)!;

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        Assert.Equal(@"ACPI\USBC000\0", (string?)root["capability"]!["ucmDeviceInstanceId"]);
        Assert.Empty(redactor.Entries);
    }

    [Fact]
    public void AShortNumericInstanceSegment_IsOnlyLeftAloneUnderAcpi()
    {
        // The same four digits are an ACPI unique ID under ACPI\, but under USB\ the last segment is
        // the device's serial number, and a short serial is still a serial.
        JsonNode acpi = JsonNode.Parse("""{ "a": { "ucmDeviceInstanceId": "ACPI\\USBC000\\1234" } }""")!;
        var acpiRedactor = new ReportRedactor(Hints);
        acpiRedactor.Redact(acpi);
        Assert.Equal(@"ACPI\USBC000\1234", (string?)acpi["a"]!["ucmDeviceInstanceId"]);
        Assert.Empty(acpiRedactor.Entries);

        JsonNode usb = JsonNode.Parse("""{ "b": { "deviceInstanceId": "USB\\VID_0BDA&PID_8153\\1234" } }""")!;
        var usbRedactor = new ReportRedactor(Hints);
        usbRedactor.Redact(usb);
        Assert.Equal(@"USB\VID_0BDA&PID_8153\[instance-1]", (string?)usb["b"]!["deviceInstanceId"]);
        Assert.Equal("device instance ID", Assert.Single(usbRedactor.Entries).Kind);

        // In one report, the USB serial's digits are removed wherever they appear, the ACPI ID
        // included: a redacted value must not survive anywhere in the file.
        JsonNode both = JsonNode.Parse("""
            {
              "a": { "ucmDeviceInstanceId": "ACPI\\USBC000\\1234" },
              "b": { "deviceInstanceId": "USB\\VID_0BDA&PID_8153\\1234" }
            }
            """)!;
        new ReportRedactor(Hints).Redact(both);
        Assert.Equal(@"USB\VID_0BDA&PID_8153\[instance-1]", (string?)both["b"]!["deviceInstanceId"]);
        Assert.Equal(@"ACPI\USBC000\[instance-1]", (string?)both["a"]!["ucmDeviceInstanceId"]);
    }

    [Fact]
    public void DevicePathsAreReplacedWhole()
    {
        JsonNode root = JsonNode.Parse("""
            { "hubPath": "\\\\?\\USB#VID_2109&PID_2817#000000000#{f18a0e88-c30c-11d0-8815-00a0c906bed8}" }
            """)!;

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        Assert.Equal("[path-1]", (string?)root["hubPath"]);
        Assert.Equal("device path", Assert.Single(redactor.Entries).Kind);
    }

    [Fact]
    public void MachineNameUserNameAndProfilePathAreReplacedInAnyString()
    {
        JsonNode root = JsonNode.Parse("""
            {
              "error": "could not open c:\\users\\JSMITH\\AppData\\Local\\x on DESKTOP-7QX2K1P",
              "human": [ "Signed in as jsmith", "C:\\Users\\alice\\Desktop" ]
            }
            """)!;

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        // The profile path matches in any case, as Windows paths do.
        Assert.Equal(@"could not open [user-profile]\AppData\Local\x on [machine-name]", (string?)root["error"]);
        Assert.Equal("Signed in as [user-name]", (string?)root["human"]![0]);
        Assert.Equal(@"C:\Users\[user-name-in-path-1]\Desktop", (string?)root["human"]![1]);
    }

    [Fact]
    public void NamesAreOnlyMatchedAsWholeWords()
    {
        // A name that happens to appear inside a hex payload must not corrupt the raw bytes.
        var hints = new IdentityHints(MachineName: "BEEF", UserName: "jsmith", UserProfilePath: null, UserDomainName: null);
        JsonNode root = JsonNode.Parse("""
            { "human": [ "jsmithson", "BEEFY" ], "raw": "00BEEF00", "name": "jsmith" }
            """)!;

        var redactor = new ReportRedactor(hints);
        redactor.Redact(root);

        Assert.Equal("jsmithson", (string?)root["human"]![0]);
        Assert.Equal("BEEFY", (string?)root["human"]![1]);
        Assert.Equal("00BEEF00", (string?)root["raw"]);
        Assert.Equal("[user-name]", (string?)root["name"]);
    }

    [Fact]
    public void AUserNamedLikeAWordPortmarkPrints_DoesNotEatTheOutput()
    {
        // Whole-word matching alone is not enough: a user called "port" or "power" is a whole word
        // in every "Port 1" and "Power" line portmark prints. Such names are too common in the
        // output to search for, and the rest are matched in their own case, so "Grace" the user is
        // redacted where "grace" the word is not. Profile paths still match in any case.
        var hints = new IdentityHints(MachineName: "power", UserName: "port", UserProfilePath: @"C:\Users\port", UserDomainName: "Al");
        JsonNode root = JsonNode.Parse("""
            {
              "human": [ "Port 1", "Power        45W", "port 2 has no power", "Al is short", "c:\\USERS\\Port\\AppData" ],
              "name": "port"
            }
            """)!;

        var redactor = new ReportRedactor(hints);
        redactor.Redact(root);

        Assert.Equal("Port 1", (string?)root["human"]![0]);
        Assert.Equal("Power        45W", (string?)root["human"]![1]);
        Assert.Equal("port 2 has no power", (string?)root["human"]![2]);
        Assert.Equal("Al is short", (string?)root["human"]![3]);
        Assert.Equal(@"[user-profile]\AppData", (string?)root["human"]![4]);
        Assert.Equal("port", (string?)root["name"]);

        var named = new ReportRedactor(new IdentityHints(MachineName: null, UserName: "Grace", UserProfilePath: null, UserDomainName: null));
        JsonNode other = JsonNode.Parse("""{ "human": [ "Signed in as Grace", "a grace period" ] }""")!;
        named.Redact(other);
        Assert.Equal("Signed in as [user-name]", (string?)other["human"]![0]);
        Assert.Equal("a grace period", (string?)other["human"]![1]);
    }

    [Fact]
    public void RawDataFields_AreNeverTreatedAsText_EvenWhenTheWholeValueIsAName()
    {
        // A user or serial of "00000000" is a whole-word match for an all-zero payload. The raw
        // bytes are what make a report checkable, so they are never searched for names.
        var hints = new IdentityHints(MachineName: "00000000", UserName: "00000000", UserProfilePath: null, UserDomainName: null);
        JsonNode root = JsonNode.Parse("""
            {
              "serialNumber": "00000000",
              "raw": { "connectorStatusHex": "00000000", "connectorStatusCci": "00000000" },
              "exchange": { "controlHex": "00000000", "cci": "00000000", "payloadHex": "00000000" },
              "pdos": { "raw": "00000000", "raw2": "00000000", "objectsHex": [ "00000000" ] },
              "note": "Signed in as 00000000"
            }
            """)!;

        var redactor = new ReportRedactor(hints);
        redactor.Redact(root);

        Assert.Equal("[serial-1]", (string?)root["serialNumber"]);
        Assert.Equal("00000000", (string?)root["raw"]!["connectorStatusHex"]);
        Assert.Equal("00000000", (string?)root["raw"]!["connectorStatusCci"]);
        Assert.Equal("00000000", (string?)root["exchange"]!["controlHex"]);
        Assert.Equal("00000000", (string?)root["exchange"]!["cci"]);
        Assert.Equal("00000000", (string?)root["exchange"]!["payloadHex"]);
        Assert.Equal("00000000", (string?)root["pdos"]!["raw"]);
        Assert.Equal("00000000", (string?)root["pdos"]!["raw2"]);
        Assert.Equal("00000000", (string?)root["pdos"]!["objectsHex"]![0]);
        Assert.Equal("Signed in as [serial-1]", (string?)root["note"]);
    }

    [Fact]
    public void TheRedactionListNeverRepeatsTheValuesItRemoved()
    {
        JsonNode root = JsonNode.Parse("""
            {
              "serialNumber": "4C530001231121115442",
              "note": "DESKTOP-7QX2K1P jsmith C:\\Users\\jsmith"
            }
            """)!;

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        string listed = JsonSerializer.Serialize(redactor.Entries);
        Assert.DoesNotContain("4C530001231121115442", listed);
        Assert.DoesNotContain("DESKTOP-7QX2K1P", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jsmith", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4C530001231121115442", root.ToJsonString());
        Assert.DoesNotContain("jsmith", root.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AReadingWithNothingIdentifyingComesBackByteForByte()
    {
        // Built from the ThinkPad T16 Gen 2 vectors used elsewhere in these tests. Redaction must
        // never touch the raw bytes or CCI values: they are what makes the report checkable.
        var report = new PortmarkReport
        {
            Machine = new MachineReport { Manufacturer = "LENOVO", Model = "21K7CTO1WW", BiosVersion = "R2FET53W (1.33 )" },
            Capability = new CapabilityReport { UcmDeviceInstanceId = @"ACPI\USBC000\0", UcsiVersion = "2.0" },
            Connectors =
            {
                new ConnectorReport
                {
                    Index = 1,
                    Raw = new RawReport { ConnectorStatusHex = "E2A20A0000D00200004000000000", ConnectorStatusCci = "0x020E0000" },
                },
            },
        };
        JsonNode root = JsonSerializer.SerializeToNode(report)!;
        string before = root.ToJsonString();

        var redactor = new ReportRedactor(Hints);
        redactor.Redact(root);

        Assert.Equal(before, root.ToJsonString());
        Assert.Empty(redactor.Entries);
    }
}
