<img src="assets/icons/portmark-128.png" width="96" align="right" alt="">

# Portmark

[![CI](https://github.com/dbhq-uk/portmark/actions/workflows/ci.yml/badge.svg)](https://github.com/dbhq-uk/portmark/actions/workflows/ci.yml)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4.svg)](#install)
[![Built with .NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](#building)
[![Tests](https://img.shields.io/badge/tests-25%20passing-brightgreen.svg)](#building)

**Find out what your USB-C ports, chargers and adapters can actually do - and get told
"unknown" when your PC genuinely cannot tell you.**

*A native Windows CLI for USB-C: USB Power Delivery contracts, DisplayPort Alternate Mode,
e-marker cable data, and connected device details. No Electron, no telemetry, single executable.
A tray app is in progress and is not shipped yet.*

<!-- TODO before launch: replace with a screenshot of the tray popover, and an animated GIF of a
     cable being plugged in and the reading changing. A screenshot above the fold is the single
     biggest driver of stars on a utility repo. -->

USB-C connectors are identical and their capabilities are not. One charger delivers 5W, another
65W. One adapter carries video, another cannot. Windows negotiates all of this on every connection
and then shows you almost none of it. Portmark reads it back out.

```
$ portmark --human

Adapter 0x343C:0x0000
  DisplayPort Alternate Mode: entered successfully
  Video        yes, DisplayPort is active through this adapter

Port 1
  Downstream facing port, over USB Power Delivery, drawing power,
  supply offers up to 65W, drawing 5V at 3A (15W).
  Port supports USB 2.0, USB 3.x, alternate modes, dual role power
  Negotiated   5V at 3A (15W)
  Supply offers
    - 5V at 3A (15W)
    - 9V at 3A (27W)
    - 15V at 3A (45W)
    - 20V at 3.25A (65W)
```

## It tells you when you are losing speed

The question behind most USB-C frustration is not "what is this cable" but "why is this slow".
Portmark compares what each device declares it can do against the link it actually negotiated:

```
Samsung Portable SSD T7
  Speed        High, 480 Mbps
  USB version  3.2

  ** RUNNING SLOWER THAN IT COULD **
  Running at 480 Mbps, but this device declares USB 3.2, which allows 5 Gbps or
  above. The usual cause is a USB 2.0 cable, or a USB 2.0 hub between this device
  and the PC. The device and the port are probably both fine.
```

Both halves of that comparison come from the hardware - `bcdUSB` from the device's own descriptor
and the negotiated speed from the hub - so it is a measurement, not a guess. Windows knows this and
never tells you.

## Why you cannot just look at the connector

Two USB-C cables can be physically identical and differ by a factor of twenty in power and eighty
in data rate. The plug tells you nothing. Windows negotiates the real answer on every connection,
uses it internally, and never shows you. Portmark surfaces it.

## The rule this project is built on

**Nothing is ever inferred from the shape of a connector.** A USB-C socket tells you the shape of
the socket and nothing else.

When a field cannot be determined, Portmark says so and says why. It will tell you "this PC's port
controller does not report cable information" rather than "this cable has no e-marker", because
those are different claims and only one of them is supported by the evidence. Fields that were not
reported are `null` in the JSON, never zero, never a plausible-looking default.

This costs the tool some confident-sounding output. That is the point.

## Two tiers

Portmark reads from two independent sources. The first needs nothing at all.

| Tier | Requires | Tells you |
|---|---|---|
| **Zero setup** | nothing - works on first run | Every attached USB device, read from its own descriptors. Alternate modes by SVID, whether **DisplayPort is currently active**, and whether anything is **running slower than it could**. |
| **Extended** | a one-time administrator step | Port state and partner, power direction, the negotiated PD contract, the supply's full voltage/current profile, and cable e-marker data *where the controller supports it*. |

Most of what people want is in the first tier. You can ignore the second entirely.

## Install

Download `portmark.exe` from [Releases](../../releases). It is a single self-contained executable
with no installer and no runtime to install.

```powershell
portmark --human      # plain English
portmark              # JSON
```

## Usage

```
portmark                 read all ports, print JSON
portmark --human         read all ports, print plain English
portmark usb             list every attached USB device from its own descriptors
portmark tree            show attached devices as the tree they physically form
portmark billboard       report alternate modes and video capability
portmark power           compare what devices asked for against what each hub can supply
portmark watch           report devices arriving and leaving as it happens
portmark enable          one-time administrator setup for the extended tier
portmark disable         undo it
```

Exit codes: `0` read successfully, `1` error, `2` a one-time setup step is needed, `3` this PC
cannot report this data.

The JSON is the contract. It is camelCase, includes the raw bytes and CCI values for every
response so you can check or redo the decoding yourself, and carries a `schemaVersion`.

```powershell
portmark | ConvertFrom-Json | Select-Object -ExpandProperty connectors
```

## The extended tier, and its cost

The extended tier needs one registry value set, once, as administrator. **You should understand
what it does before running it.**

Windows exposes port controller data through an interface that ships switched off, and Microsoft
says why: to stop it "being accessible to unauthorized users on a retail system". While it is on,
**any program running as you can send commands to your USB-C power controller**, not just Portmark.

Portmark itself only ever reads. It sends no command that changes port state - no role swaps, no
resets, no power renegotiation - and there is no code path that could. But enabling the interface
does not only enable Portmark.

`portmark disable` turns it back off, and is worth running when you are done.

## What Portmark cannot tell you

Being specific about this matters more than the feature list.

- **Cable e-marker data depends entirely on your PC's controller.** Many controllers do not
  advertise `CableDetailsAvailable`, and when they do not, no software on any operating system can
  extract cable details from them. Portmark checks that capability bit and tells you plainly rather
  than blaming your cable.
- **Video is reported from Billboard descriptors, not guessed.** If an adapter exposes no Billboard
  device, video capability is reported as unknown. An adapter without one is not an adapter without
  video.
- **UCSI carries no video field.** Any tool telling you a *cable* does or does not carry video from
  UCSI alone is guessing.
- **Roughly a third of machines will return little or nothing.** Portmark detects this on first run
  and says so, rather than showing you a blank panel or a confident wrong answer.

## Frequently asked

**Does this work on Windows 10?**
The zero-setup tier does. The extended tier needs a UCSI 2.x capable build, which means Windows 11
22H2 September Update or later. Portmark degrades rather than failing.

**Why does it say my cable is "not reported" when I know it is a 100W cable?**
Almost certainly because your PC's port controller does not advertise `CableDetailsAvailable`. The
cable is fine; the controller will not describe it. `portmark --human` says which case you are in.

**Is it safe? It wants administrator rights.**
Only for the optional extended tier, only once, and only to set a single registry value. Portmark
never sends a command that changes port state. Read [the cost of the extended tier](#the-extended-tier-and-its-cost)
before deciding - the honest answer is that enabling it has a real trade-off.

**Does it phone home?**
No. No telemetry, no network calls, no analytics. It is a single executable that reads your
hardware and prints the result.

## Hardware coverage

Verified on a Lenovo ThinkPad T16 Gen 2 running Windows 11 25H2. On that machine the power
delivery decoding was checked against physical reality: the attached supply decodes to 5V/3A,
9V/3A, 15V/3A and 20V/3.25A, exactly the profile printed on the 65W charger.

That is one machine. **If you run Portmark, please open an issue with the output of
`portmark --human`** - particularly whether your controller reports cable details. That capability
is not documented anywhere and the only way to find out how common it is, is to collect it.

## Repository topics

When published, tag the repository with: `usb-c`, `usb-power-delivery`, `windows`, `csharp`,
`dotnet`, `ucsi`, `thunderbolt`, `displayport`, `hardware-info`, `system-tray`, `utility`,
`usb`, `cli`, `winui`, `e-marker`.

## Contributing

The most valuable contribution is a [hardware report](../../issues/new?template=hardware-report.yml):
whether your PC's controller reports cable details is undocumented, and collecting it is the only
way to find out how common it is. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Building

```powershell
dotnet publish src/Portmark.Cli -c Release
dotnet test Portmark.slnx
```

Requires the .NET 10 SDK. The tests run on captured hardware byte vectors and need no USB-C device
attached.

## How this was built

Portmark was written against the USB-IF UCSI specification, the USB Billboard Device Class specification,
Microsoft Learn documentation, and direct observation of the Windows driver stack. The reverse
engineering that made it possible - including recovering the in-box UCSI interface GUID and control
codes, which differ from the ones in Microsoft's published samples - is documented in
[docs/SPIKE.md](docs/SPIKE.md), along with what could not be established and why.

Portmark is not affiliated with, derived from, or endorsed by any other USB-C inspection tool.

## Licence

MIT. See [LICENSE](LICENSE).
