# Spike: can USB-C cable properties be read from user mode on Windows?

**Answer: B.** Cable and connector properties can be read from an ordinary, non-elevated user-mode
process, after a one-time registry change that requires administrator rights once.

Findings are reproducible with the diagnostic commands described at the end.

---

## The machine

| | |
|---|---|
| Model | Lenovo ThinkPad T16 Gen 2, AMD (type 21K7, Ryzen 7 PRO 7840U) |
| Firmware | BIOS R2FET70W (1.50, May 2026), EC 1.33. The UCSI PPM is the embedded controller, reached through Lenovo's `UsbCTabl` SSDT |

| OS | Windows 11 Pro 25H2, build 10.0.26200.9445, x64 |
| UCM device | `ACPI\USBC000\0`, "UCM-UCSI ACPI Device" |
| Client driver | `UcmUcsiAcpiClient.sys` 10.0.26100.8972 |
| Class extension | `UcmUcsiCx.sys` 10.0.26100.9278 |

This is a stock consumer laptop. No WDK, no test-signed drivers, no MUTT package installed.

---

## Verdict in the brief's terms

- **A - user mode, no admin, no registry change.** Ruled out. Evidence below.
- **B - user mode, after a one-time elevated `TestInterfaceEnabled` change.** **This is the answer.**
  Once the flag is set, a normal unelevated process opens the interface and executes UCSI commands.
- **C - only via `UcsiControl.exe`.** Not required. `UcsiControl.exe` is not installed on this
  machine and was never used.
- **D - not readable.** Ruled out.

---

## Stage A: the documented surfaces, all negative

Every one of these ran unelevated with no registry change.

| Path | Result |
|---|---|
| WMI `Win32_PnPEntity` where `PNPClass='UCM'` | 1 instance. PnP metadata only, no cable or Type-C properties. |
| `root\wmi` class sweep for `ucsi|typec|usbpd|connector` | No Type-C or UCSI provider registered. |
| WinRT `DeviceInformation.FindAllAsync` on the UCM interfaces | Devices returned, no Type-C or e-marker properties. |
| `CreateFile` on `{4cedf9cf-…}` and `{ae05a169-…}` | **Handles open fine, unelevated.** All UCSI IOCTLs rejected with `ERROR_INVALID_FUNCTION (1)`. |
| `\\.\USB Type C`, `\\.\UCSI`, `\\.\UcmUcsiCx` | `ERROR_FILE_NOT_FOUND (2)`. |
| `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX` | Works, but reports negotiated link speed only. Not cable data, as expected. |

Windows exposes no supported user-mode API for e-marker data. That part of the brief is confirmed.

---

## Resolving the open question: the sample GUID claim

The brief flagged a disputed claim: that `GUID_DEVINTERFACE_UCSI_TEST`
(`6c846eea-9649-46b3-…`) and control codes `0x401/0x402/0x403` belong to the WDK *sample* driver
and are therefore not openable on a stock machine.

**Both halves of the dispute turn out to be partly right, and the conclusion drawn from it was
wrong.**

The sample GUID really is sample-only. A byte scan of all **473** driver binaries in
`System32\drivers` found `6c846eea-9649-46b3` in **none** of them, and it is not registered as a
device interface class. So anyone testing only that GUID would correctly conclude it does not
exist - and would incorrectly conclude the test interface is unreachable.

The shipping driver has its **own** test interface. It was found like this, without reading any
third-party source:

1. `UcmUcsiCx.sys` contains the UTF-16 string `TestInterfaceEnabled`, so the flag is honoured by
   the shipping driver, not merely by the sample. (`UcmUcsiAcpiClient.sys` does not contain it.)
2. Scanning the `.rdata` section of `UcmUcsiCx.sys` for GUID-shaped constants (valid UUID version
   and variant nibbles, 8-byte aligned) yielded 10 candidates.
3. Cross-referencing each against every other driver binary on the system eliminated the shared
   PnP and WDF infrastructure GUIDs. Exactly two were referenced by `UcmUcsiCx.sys` and nothing
   else: `0d3bc324-0125-4e95-b25a-7b393333ea9a` and `c500c63a-6efe-433b-84a7-c0740d5dc97f`.
4. Setting `TestInterfaceEnabled = 1` and restarting the device published exactly one new
   interface class: **`{0d3bc324-0125-4e95-b25a-7b393333ea9a}`**, as predicted.

The control codes differ too. Scanning for constants with device type 4627 (`FILE_DEVICE_UCSI`)
found precisely two in `UcmUcsiCx.sys`, and none at all in `UcmUcsiAcpiClient.sys`, `UcmCx.sys` or
`UsbPmApi.sys` - so they are codes the class extension *receives*, not ones it sends downstream.

| | WDK sample | In-box `UcmUcsiCx.sys` |
|---|---|---|
| Interface GUID | `6c846eea-9649-46b3-…` (absent here) | `0d3bc324-0125-4e95-b25a-7b393333ea9a` |
| Control codes | `0x401`, `0x402`, `0x403` | `0x404` (`0x12131010`), `0x406` (`0x12131018`) |

Sending the sample's codes to the in-box interface returns `ERROR_NOT_SUPPORTED (50)`. That single
error is what makes the interface look dead to anyone carrying the sample's constants.

---

## The calling convention

Recovered by observation, not by guessing: an undersized output buffer makes a METHOD_BUFFERED
driver report the size it wants without performing the operation, so buffer shapes can be mapped
without sending anything meaningful.

- **`0x406` takes and returns a 48-byte buffer.** 48 is exactly the UCSI data structure:
  `VERSION(2) + RESERVED(2) + CCI(4) + CONTROL(8) + MESSAGE_IN(16) + MESSAGE_OUT(16)`.
  METHOD_BUFFERED shares one buffer, so the call is a read-modify-write: write `CONTROL`, read back
  `CCI` and `MESSAGE_IN`. Any other input size returns `ERROR_INSUFFICIENT_BUFFER (122)`.
- **`0x404` takes a 1-byte buffer.** It is *not* a required handshake. Calling it poisons the
  handle: every subsequent `0x406` on that handle returns `STATUS_INVALID_DEVICE_STATE`. The
  working sequence never calls it.
- **Commands need retry with backoff.** A command issued while the PPM is still settling returns
  `STATUS_INVALID_DEVICE_STATE (Win32 22)`. Retrying at 150 ms intervals clears it. A fresh handle
  per command is also required.
- **Never send `ACK_CC_CI`.** This corrects an assumption carried into the spike. UCSI requires the
  acknowledgement handshake *of the operating system's policy manager*, and on Windows that is
  `UcmUcsiCx`, which is driving the same controller. Sending it ourselves steals a completion
  Windows was waiting for. Measured over the fifteen seconds following a full read: with
  acknowledgements the device dropped out of PnP enumeration for about two seconds; without them it
  stayed enumerable **15/15**. Reads succeed either way, because Windows performs the handshake on
  our behalf. A passive reader must not touch it. Reproduce with `portmark stress` against
  `portmark stress --no-ack`.

## Data actually read, unelevated

```
UCSI VERSION = 0x0100   CCI = 0x82000000        (Command Completed + Not Supported, bit 25;
                                                 originally misread here as Reset Completed,
                                                 which is bit 27. See "The CCI indicator bits")

GET_CAPABILITY   CCI=0x80001000 (completed, 16-byte payload)
                 46400000029400000300020100020001
                 -> 2 connectors, attributes 0x00004046, 3 alternate modes,
                    BC 1.2, PD 2.0, Type-C 1.0

connector 1      GET_CONNECTOR_STATUS -> 00002B202CB1041301
                 attached, partner DFP, USB Power Delivery, consuming power
connector 2      GET_CONNECTOR_STATUS -> 00003B402CB1041300
                 attached, partner UFP, USB Power Delivery, supplying power
```

This is genuine UCSI traffic from a non-elevated process. The spike is answered.

---

## What is not yet proven

Stated plainly, because the difference between a trusted tool and a guessing one is admitting this.

1. **This machine cannot report cable data at all, and the reason is now known.**

   `GET_CABLE_PROPERTY` completes on both connectors and returns a zero-length payload. That held
   with a 65W charger attached, and still held with a USB-C dock attached as well.

   That alone does not prove much: neither cable is guaranteed to carry an e-marker, so "no
   e-marker" remained a live explanation on the evidence of empty responses.

   The decisive evidence is elsewhere, in `GET_CAPABILITY`, and does not depend on what is plugged
   in - the controller declares this before any cable is considered, and would declare it with the
   ports empty. Its `bmOptionalFeatures` field reads **`0x000094`**:

   | Bit | Feature | This controller |
   |---|---|---|
   | 2 | AlternateModeDetailsAvailable | yes |
   | 4 | PdoDetailsAvailable | yes |
   | **5** | **CableDetailsAvailable** | **no** |
   | 7 | PdResetNotificationSupported | yes |

   The controller explicitly declares that it does not provide cable details. It is not refusing
   the command - `CCI` shows Command Completed with the Error bit clear, `CONTROL` echoes back
   `11 00 01`, and `GET_ERROR_STATUS` returns "no error reported" rather than "unrecognised
   command". It answers the question honestly, and the honest answer is that it has nothing.

   **No software can extract cable data on this machine.** That is a firmware limitation of the
   ThinkPad T16 Gen 2, not a property of any cable.

   Two consequences:

   - The `CableProperty` decoder remains **unvalidated against real field data**, and cannot be
     validated on this machine. It needs hardware whose controller advertises bit 5.
   - `bmOptionalFeatures` bit 5 is the correct capability check, and is far more reliable than
     inferring anything from an empty response. portmark reads it before issuing the query and
     says plainly that the PC cannot report cable information, rather than blaming the cable.
2. **One machine.** A Lenovo ThinkPad T16 Gen 2. The crowd-sourced ~70% figure is not evidence
   about any other specific machine, and this result is not either.
3. **`VERSION` reads `0x0100`,** i.e. UCSI 1.0 semantics, even though the OS build supports UCSI
   2.x. `GET_CABLE_PROPERTY` exists in 1.x, so this does not block the product, but the 2.0 field
   layouts must not be assumed on this hardware.

## What this machine actually yields

Cable data being unavailable does not mean nothing is. Measured on the ThinkPad T16 Gen 2:

| UCSI command | Result |
|---|---|
| `GET_CAPABILITY` | Works. 2 connectors, PD 2.0, Type-C 1.0, BC 1.2, 3 alternate modes. |
| `GET_CONNECTOR_CAPABILITY` | Works. USB 2.0, USB 3.x, alternate modes, dual role power, provider and consumer. |
| `GET_CONNECTOR_STATUS` | Works. Attachment, partner type, power direction, power operation mode, and the RDO. |
| `GET_PDOS` | Works, and **verified**. The attached supply decodes to 5V/3A, 9V/3A, 15V/3A, 20V/3.25A - exactly the 65W charger plugged in. |
| `GET_CAM_SUPPORTED` / `GET_CURRENT_CAM` | Work. `GET_CAM_SUPPORTED` returns `0x03`. `GET_CURRENT_CAM` returns `0` on both connectors, including the empty one, so on its own it does not say a mode is active. |
| `GET_ALTERNATE_MODES` | **Works.** The spike first reported it declined; that was portmark's own encoding error. See below. |
| `GET_CABLE_PROPERTY` | Not advertised, and answered with the **Not Supported** indicator (CCI `0x82000000`), exactly as an undefined opcode is. See below. |

Two of those deserve comment.

**The PDO path is the real product on hardware like this.** It answers the question most users
actually have - how much power can this supply deliver, and how much am I drawing - and it is
verifiable against the label on the charger. It works on a controller that cannot report cables at
all.

**`GET_ALTERNATE_MODES` works, and the spike's "declined" finding was portmark's own mistake.**
The first encoding packed the command-specific fields contiguously from bit 19, which put the
connector number where the specification has reserved bits. The controller therefore received
connector number zero, and answered CCI `0xC0000000`: Command Completed plus the **Error**
indicator (bit 30), with `GET_ERROR_STATUS` immediately afterwards reporting `0x0002`,
non-existent connector number. It was answering the question it was asked. The spike swept 96
combinations of the wrong encoding and read every Error as an empty success, because its
indicator constants sat two bits low (next paragraph).

With the connector number at bits 24-30 (UCSI Table 4-17; Linux `UCSI_GET_ALTMODE_CONNECTOR_NUMBER`
shifts by 24) the controller lists its modes, two per response:

```
connector 1  offset 0  EF17 01000000  8780 01000000   -> 0x17EF Lenovo, 0x8087 Intel Thunderbolt 3
             offset 2  01FF 03000000                  -> 0xFF01 DisplayPort
connector 2  offset 0  EF17 01000000  01FF 03000000   -> 0x17EF Lenovo, 0xFF01 DisplayPort
             offset 2  (zero length)
```

Three modes on connector 1 and two on connector 2. That matches `bNumAltModes = 3` from
`GET_CAPABILITY`, and it matches the machine: connector 1 is the USB4 port and connector 2 the
USB 3.2 port. With recipient SOP (the attached partner) and a 65W charger on connector 1, both
connectors return zero length: no partner modes were reported with this charger attached.

So the practical consequence is the reverse of what this section first said. portmark can name
the modes each port supports and the modes an attached device offers, from UCSI, without needing
a Billboard device. What it still must not do is take `GET_CURRENT_CAM` at face value: this
controller returns `0` for an empty port, against the specification's `0xFF`, so index 0 can mean
either "the first listed mode" or "nothing". The first release said "an alternate mode is active"
about a bare charger on the strength of that 0.

A second capture settles how far the index can be trusted. With a USB-C DisplayPort adapter on
connector 2 (Billboard `0x343C`, which reports DisplayPort Alternate Mode "entered successfully"),
`GET_CURRENT_CAM` returns `1`, and offset 1 in that port's list is DisplayPort. So the index was
right that time. The iPhone capture further down gives the same `1` with no display attached, so a
non-zero index is not proof on its own. Meanwhile the partner list (recipient SOP) stays empty and the partner
flags in `GET_CONNECTOR_STATUS` stay `0x01` (alternate mode bit clear), exactly as with the charger:
this controller never describes the partner's modes, so neither of those can be used to veto its
index. portmark therefore names a non-zero index as the controller's own statement, says so, and
treats index 0 as unconfirmed. A mode counts as confirmed only when the connector status says an
alternate mode is in operation. A partner that lists a mode can enter it, which is not the same as
having entered it. Recipient SOP' (the cable)
also returns zero length on both ports, with the charger and with the adapter; neither is a known
e-marked cable, so that is not yet evidence either way about cable modes.

**The CCI indicator bits were two positions low.** UCSI places Not Supported at bit 25, Cancel
Completed 26, Reset Completed 27, Busy 28, Acknowledge 29, Error 30 and Command Completed 31
(Microsoft's `UCSI_CCI` and Linux `ucsi.h` agree). The spike and the first release used Busy 26,
Acknowledge 27, Error 28 and Not Supported 23. Two consequences. The Error answer above was
invisible. And the controller's answer to `GET_CABLE_PROPERTY`, CCI `0x82000000`, is Command
Completed plus **Not Supported**, not a bare completion. That answer is byte-for-byte what the
controller returns for an undefined opcode (`0x7F`). The firmware reports the command as
unsupported in the same way it reports a command that does not exist: not an error, not an empty
answer, an unsupported command.
`GET_ERROR_STATUS` stays clear afterwards because Not Supported is not an error condition. It is
also the same answer whether the connector number is placed at bit 16 or bit 24, so the cable
result is not another encoding slip.

## A second path that needs no setup at all

The spike was framed around UCSI, and UCSI needs the registry change. That framing turned out to
be too narrow.

A USB-C adapter that supports an Alternate Mode exposes a **USB Billboard device**: USB device
class `0x11`, whose BOS descriptor carries a Billboard capability listing every Alternate Mode it
supports by SVID, with a two-bit state for each saying whether that mode was entered. SVID `0xFF01`
is DisplayPort.

On the test machine the dock's adapter reports exactly that:

```
Billboard device 0x343C:0x0000
  [0] DisplayPort Alternate Mode  mode 0  -> entered successfully
```

which matches the monitor genuinely working through it.

**This path is reached through documented USB hub IOCTLs** - `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX`
and `IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION`. Verified with `TestInterfaceEnabled` cleared
and without elevation: the video answer still comes back. No registry change, no administrator
rights, no test interface.

That reshapes the product into two tiers:

| Tier | Needs | Gives |
|---|---|---|
| **Zero setup** | nothing | Alternate modes by SVID, and whether DisplayPort is active. Works for every user on first run. |
| **One-time admin** | `TestInterfaceEnabled` | Port state, partner, power direction, the negotiated PD contract, the supply's full PDO list, and cable e-marker data *where the controller supports it*. |

The first tier is the better first-run experience by a distance, and it is the answer to the "no
video" half of the headline claim. It also means a machine that cannot do UCSI at all is not a
machine that gets nothing.

One implementation note worth keeping. `USB_NODE_CONNECTION_INFORMATION_EX` is byte-packed, because
the `USB_DEVICE_DESCRIPTOR` it embeds is declared with `pshpack1`. Reading `ConnectionStatus` at the
naturally aligned offset 32 rather than the packed offset 31 makes every port on every hub look
disconnected, which presents as "there are no Billboard devices" rather than as an error.

## The security question, which is a product question

Microsoft disables this interface by default, and says why: "to prevent it from being accessible to
unauthorized users on a retail system." Once `TestInterfaceEnabled = 1`, **any unelevated process
on the machine can drive the USB-C Power Delivery controller** - including the `SET_*` commands
this tool deliberately never sends.

That is the real cost of path B, and it is larger than "a first-run elevation step". Any shipped
product must state it in plain language before asking a user to enable it, and should offer to turn
it back off. This needs a decision before the tray app is built, not after.

---

## Reproducing this

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

ucsiprobe                              # stage A only, no changes, no admin
ucsiprobe --enable-test-interface      # elevated, one time: sets the flag, restarts the device
ucsiprobe --ucsi                       # unelevated, reads UCSI data
ucsiprobe --disable-test-interface     # elevated: puts the machine back as found
```

Diagnostic modes used during the spike, kept because they are how the above was found:
`--discover` (buffer shape sweep) and `--sequence` (call ordering experiments).


## A narrow second path to cable information: the USB4 trace events

Found while checking the cable verdict. Windows' USB4 host router driver emits TraceLogging
rundown events describing the domain, and two of their fields are derived from the cable rather
than the link: `CableUsb4Version` on the port event (from `PORT_CS_18[7:0]`) and `CableInfo` on
the router event. Neither is the e-marker's identity, but both come from the Connection Manager's
knowledge of the cable, which this machine's UCSI controller never exposes.

Captured on the ThinkPad T16 Gen 2 (AMD), elevated, with a DisplayPort adapter on connector 2 and
nothing on the USB4 port:

```
logman create trace portmark-usb4 -p {575BA31F-2B45-58C2-64FD-F5DC757B6137} 0xFFFFFFFFFFFFFFFF 0xFF -o usb4.etl -ets
logman update trace portmark-usb4 -p {AE795D36-2B11-5EFB-C7E0-5D552BC55D6C} 0xFFFFFFFFFFFFFFFF 0xFF -ets
logman stop portmark-usb4 -ets
tracerpt usb4.etl -o usb4.xml -of XML -y

DeviceRouterInformation  RouterUSB4Version=0x20  ConnectionManagerUSB4Version=0x10
                         VendorID=0x438 (AMD)  ProductID=0x20A  CableInfo=0x0
PortInformation          IsDFP=1  SupportedLinkSpeeds=0xC  SupportedLinkWidths=0x3
                         CurrentLinkSpeed=0x0  CableUsb4Version=0x0  Tbt3CompatibleMode=0  IsLastPort=1
```

Three things to note. The host router reports exactly one port, which matches the machine: only
connector 1 is USB4. The fields read zero because the domain was powered down with no USB4 device
attached; they need a USB4 or Thunderbolt device on that port to mean anything, and that capture
has not been made yet. And the session needs administrator rights and `Get-WinEvent` cannot decode
these self-describing events (it reports error 15003); `tracerpt` can. Microsoft's field table
calls `CableUsbVersion` a Boolean, but the event on this machine names it `CableUsb4Version` and
the register field is eight bits wide, so decode it from the emitted metadata rather than the
documentation.

## The cable does say one thing, through the supply

A 100W charger on connector 1 answered a question this machine was supposed to be unable to
answer. Its power objects are `2C91110A 2CD11200 2CB11400 F4411600`: 5V/3A, 9V/3A, 15V/3A and
**20V at 5A**. `GET_CABLE_PROPERTY` is still Not Supported, and the cable's alternate modes
(recipient SOP') are still zero length. But the 5A object is itself evidence about the cable.

Two rules combine:

- A USB-C cable may carry **3A** without declaring anything. Above that it must be electronically
  marked. There is no unmarked 5A cable.
- A source must **read that marking, over the cable**, before offering more than 3A. The USB PD
  compliance tests fail a non-captive source that advertises above 3A without first sending
  Discover Identity to the cable.

So the charger has already performed the cable read that this PC's controller cannot perform, and
it published the result in the only place this PC can see: its own advertisement. A supply
offering 5A is a supply that has satisfied itself the cable carries 5A.

What that licenses, and what it does not:

| | |
|---|---|
| Sound | The cable in use carries at least the advertised current |
| Not sound | That it is electronically marked. A **captive** cable is the standing exception, and captive is indistinguishable from marked from this end, so portmark states both |
| Not sound | Anything about data speed. A 100W cable can be USB 2.0, and current and speed are unrelated |
| Not sound | Anything the cable itself said. The claim rests on the supply's word |

This is the only deduction portmark makes. It lives in its own `inferred` object in the JSON,
separate from the fields the cable reported, with the observation, the rule and the conclusion
each stated so a reader can check the reasoning rather than trust it.

The threshold catches more than 5A chargers. The 65W Lenovo supply advertises 20V at 3.25A, which
is also above 3A, and that supply has a captive cable: the exception is not hypothetical. A 30W
supply tops out at 3A and licenses nothing, so portmark says nothing.

## The ninth byte, and a fault the rest of the machine denied

`GET_CONNECTOR_STATUS` returns nine bytes on this controller and portmark decoded eight of them.
The ninth carries the battery charging status at bits 64 and 65, which is the field Windows drives
its own slow-charging notification from. Reading it cost nothing: the byte was already on the wire.

| Connector | Ninth byte | Meaning |
|---|---|---|
| 1, charger attached | `0x01` | nominal charging rate |
| 2, supplying a DisplayPort adapter | `0x00` | not charging |

Decoding it turned up a disagreement worth more than the field itself. With a 100W charger on
connector 1:

```
Supply offers   5V/3A, 9V/3A, 15V/3A, 20V at 5A     (2C91110A 2CD11200 2CB11400 F4411600)
Contract        5V at 3A, 15W                        (RDO 0x1304B12C, object position 1)
Controller      nominal charging rate                 (status byte 8 = 0x01)
Battery         46 percent, falling to 43 during the session
Windows         Charging = True, ChargeRate = 0 throughout
```

Sampled every two seconds for twelve seconds, the contract never moved, so it is not a transient
caught mid-negotiation. Sampled 45 seconds apart, the battery fell by 590 mWh, about 47W of net
drain. The machine was running down on a 100W charger while both Windows and the port controller
reported it as charging normally.

Two conclusions for the product:

- **The controller's charging status cannot be the only signal.** It said nominal throughout. A
  tool that reported only that field would have reported that everything was fine.
- **The measurement is the offer against the contract**, both read from the hardware, and it is
  what portmark now reports. The controller's opinion is shown beside it rather than instead of
  it, and where they disagree, both are printed.

The battery charge is read through `GetSystemPowerStatus` for one purpose. A nearly full battery
draws very little and that is correct behaviour, so without the charge the honest wording has to
offer that explanation, and here it would have been the wrong one. At 43 percent it is ruled out.
The charge is deliberately not used to decide whether the battery is charging: Windows reported
charging while the battery fell, and the WMI charge rate read zero throughout, so neither is
evidence of anything.

This machine still cannot report a cable. It can now report that it is being starved by one.

## An iPhone, and the limit of the current-mode index

Connector 2 was read with two different devices on it, and the readings are byte for byte the
same:

```
                     status                 CURRENT_CAM  partner modes  alt mode flag
DisplayPort adapter  00003B402CB1041300     01           (none)         clear
iPhone               00003B402CB1041300     01           (none)         clear
```

The adapter genuinely had DisplayPort entered: its Billboard descriptor says so independently.
The iPhone, with no display attached, almost certainly did not. Index 1 is DisplayPort in that
port's mode list in both cases, and nothing available on this machine tells the two apart. The
controller never lists a partner's modes and never sets the alternate mode flag, so its index is
the only signal and it cannot be checked.

portmark therefore still names the mode, because on the adapter it was right and withholding it
would have lost a true answer, but it marks every such naming as uncorroborated in both the human
output and the JSON. Naming it plainly would have been right once and wrong once, and there is no
way from here to know which time.

The iPhone also settles two ranked leads, both negative on this hardware:

- **Partner sink PDOs.** `GET_PDOS` for the partner's sink capability returns four zero bytes with
  the phone attached. The controller does not describe a partner's power capability any more than
  it describes its modes.
- **The source capability type field.** Sweeping `GET_PDOS` with capability types 0, 1 and 2
  returns identical data on both connectors; type 3 returns the Error indicator. The controller
  does not distinguish current, advertised and maximum, so there is nothing there to report.

One thing the phone did answer cleanly. It is on a USB-C port, confirmed by
`IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES`, which reports the connector type per hub port and needs
no elevation. It declares USB 2.1 and negotiated 480 Mbps, so the link is the phone's own ceiling
rather than a cable limit, and portmark correctly does not flag it as underperforming.
