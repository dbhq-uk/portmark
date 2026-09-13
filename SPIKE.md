# Spike: can USB-C cable properties be read from user mode on Windows?

**Answer: B.** Cable and connector properties can be read from an ordinary, non-elevated user-mode
process, after a one-time registry change that requires administrator rights once.

Date: 2026-09-13. Tool: `src/UcsiProbe` (throwaway). Evidence: `spike-*.json` in this repo.

---

## The machine

| | |
|---|---|
| Model | LENOVO 21K7CTO1WW — ThinkPad T16 Gen 2 |
| BIOS | R2FET70W (1.50) |
| OS | Windows 11 Pro 25H2, build 10.0.26200.9445, x64 |
| UCM device | `ACPI\USBC000\0`, "UCM-UCSI ACPI Device" |
| Client driver | `UcmUcsiAcpiClient.sys` 10.0.26100.8972 |
| Class extension | `UcmUcsiCx.sys` 10.0.26100.9278 |

This is a stock consumer laptop. No WDK, no test-signed drivers, no MUTT package installed.

---

## Verdict in the brief's terms

- **A — user mode, no admin, no registry change.** Ruled out. Evidence below.
- **B — user mode, after a one-time elevated `TestInterfaceEnabled` change.** **This is the answer.**
  Once the flag is set, a normal unelevated process opens the interface and executes UCSI commands.
- **C — only via `UcsiControl.exe`.** Not required. `UcsiControl.exe` is not installed on this
  machine and was never used.
- **D — not readable.** Ruled out.

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
exist — and would incorrectly conclude the test interface is unreachable.

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
`UsbPmApi.sys` — so they are codes the class extension *receives*, not ones it sends downstream.

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
UCSI VERSION = 0x0100   CCI = 0x82000000        (Command Completed + Reset Completed)

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
   with a 65W charger attached, and still held with a Thunderbolt dock attached — and a Thunderbolt
   cable is always e-marked. So the first explanation, "no e-marker on this cable", was wrong.

   The answer is in `GET_CAPABILITY`. Its `bmOptionalFeatures` field reads **`0x000094`**:

   | Bit | Feature | This controller |
   |---|---|---|
   | 2 | AlternateModeDetailsAvailable | yes |
   | 4 | PdoDetailsAvailable | yes |
   | **5** | **CableDetailsAvailable** | **no** |
   | 7 | PdResetNotificationSupported | yes |

   The controller explicitly declares that it does not provide cable details. It is not refusing
   the command — `CCI` shows Command Completed with the Error bit clear, `CONTROL` echoes back
   `11 00 01`, and `GET_ERROR_STATUS` returns "no error reported" rather than "unrecognised
   command". It answers the question honestly, and the honest answer is that it has nothing.

   **No software can extract cable data on this machine.** That is a firmware limitation of the
   ThinkPad T16 Gen 2, not a property of any cable.

   Two consequences:

   - The `CableProperty` decoder remains **unvalidated against real field data**, and cannot be
     validated on this machine. It needs hardware whose controller advertises bit 5.
   - `bmOptionalFeatures` bit 5 is the correct capability check, and is far more reliable than
     inferring anything from an empty response. Portmark reads it before issuing the query and
     says plainly that the PC cannot report cable information, rather than blaming the cable.
2. **One machine.** A Lenovo ThinkPad T16 Gen 2. The crowd-sourced ~70% figure is not evidence
   about any other specific machine, and this result is not either.
3. **`VERSION` reads `0x0100`,** i.e. UCSI 1.0 semantics, even though the OS build supports UCSI
   2.x. `GET_CABLE_PROPERTY` exists in 1.x, so this does not block the product, but the 2.0 field
   layouts must not be assumed on this hardware.

## The security question, which is a product question

Microsoft disables this interface by default, and says why: "to prevent it from being accessible to
unauthorized users on a retail system." Once `TestInterfaceEnabled = 1`, **any unelevated process
on the machine can drive the USB-C Power Delivery controller** — including the `SET_*` commands
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

## Provenance

No upstream WhatCable source was read. Both upstream repositories are unlicensed or ambiguously
licensed, which was checked before anything else. See `CLEANROOM.md`.
