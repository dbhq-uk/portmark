# AGENTS.md

Guidance for AI agents (and people) working in this repository.

## What this is

**Portmark** reads back what Windows already knows about your USB-C ports and
will not show you: the negotiated Power Delivery contract, DisplayPort Alternate
Mode, e-marker cable data, and the attached devices. A native Windows CLI on
.NET 10, single self-contained executable, no installer and no runtime to
install. A tray app is in progress and **is not shipped yet** - do not describe
it as available.

## Layout

```
src/Portmark.Core/Ucsi/          # UCSI command and response decoding
src/Portmark.Core/Usb/           # device descriptors, the device tree
src/Portmark.Core/Native/        # the P/Invoke layer
src/Portmark.Core/Model/         # the JSON contract types
src/Portmark.Core/PortmarkReader.cs
src/Portmark.Cli/Program.cs      # commands, output, exit codes
tests/Portmark.Core.Tests/       # decoder tests over captured byte vectors
docs/SPIKE.md                    # how the interface was reached, and what was ruled out
assets/                          # icon sources and the icon build script
```

## The constraints that must not be broken

Everything else here is a preference. These are not.

**1. Nothing is ever inferred from the shape of a connector.** A USB-C socket
tells you the shape of the socket and nothing else. This is the rule the whole
project exists to keep, and the output is worse-looking because of it.

In practice:

- A field that was not reported is `null`. Never a zero, never a plausible
  default
- Say **why** something is unknown. "This PC's controller does not report cable
  information" and "this cable has no e-marker" are different claims, and only
  one of them is usually supported
- An all-zero response means the hardware had nothing to say. Do not decode it
  into fields
- If two explanations cannot be distinguished, say so rather than picking the
  likelier one

A change that makes Portmark state something confident and wrong is a
regression even if the output reads better.

**2. Portmark only reads.** It sends no UCSI command that changes port state: no
role swaps, no resets, no power renegotiation, no firmware commands. There is no
code path that could, and that is a promise in `SECURITY.md` rather than a
description of the current state. Adding a write command would break the
security claim the extended tier rests on.

**3. One system change, on explicit request, with admin rights.** `portmark
enable` sets the single registry DWORD `TestInterfaceEnabled` and `portmark
disable` removes it. Nothing else touches the machine. Never set it implicitly
because a read failed, and never leave it set as a side effect of another
command - exit code `2` exists to tell the user a setup step is needed so they
can make that call themselves.

**4. The trade-off is stated, not buried.** While the interface is enabled, any
program running as the user can send commands to the USB-C power controller, not
only Portmark - including commands Portmark refuses to send. Microsoft ships it
off for that reason. Every place that offers the extended tier says this, and
says the zero-setup tier needs none of it.

**5. The JSON is the contract.** camelCase, a `schemaVersion`, and the raw bytes
plus CCI value for every response so a reader can redo the decoding themselves.
Do not remove the raw bytes to tidy the output: they are what makes a decode
checkable rather than trusted.

**6. No network, no telemetry.** Portmark collects nothing and sends nothing
anywhere. Output stays on the machine unless the user pastes it somewhere.

## Conventions

- Exit codes: `0` read successfully, `1` error, `2` a one-time setup step is
  needed, `3` this PC cannot report this data. `3` is not a failure - it is the
  honest answer, and it is the answer the README promises
- House style: British English, plain hyphens, **no em dashes**, no trailing
  full stops on headings
- `.editorconfig` covers the mechanical style
- Comments explain **why**, particularly where something is non-obvious or was
  got wrong once. Several comments here exist because a subtly wrong version
  shipped first, and they say so. Keep that habit

## Validating a change

```powershell
dotnet build Portmark.slnx
dotnet test Portmark.slnx
```

Requires the .NET 10 SDK on Windows.

The decoder tests run on byte vectors captured from real hardware, so the suite
passes with **no USB-C device attached**. When you add a decoder, add its
vector - a decoder with no captured vector is untested however many unit tests
surround it.

Where a decoded value can be checked against the physical world, check it and
say so in the test. The Power Delivery decoder is verified against a charger
whose printed rating matches what the bytes decode to, and that is worth more
than any amount of self-consistent unit testing.

`docs/SPIKE.md` records how the in-box UCSI interface was reached and what was
ruled out on the way, including that the control codes differ from Microsoft's
published samples. Read it before concluding something is unreachable.
