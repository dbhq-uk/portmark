# Contributing

The most valuable contribution is a **hardware report**. Whether a USB-C port controller reports
cable details is undocumented, and the only way to learn how common it is, is to collect it. Run
`portmark --human` and open a [hardware report](../../issues/new?template=hardware-report.yml).

## The one rule

**Nothing is inferred from the shape of a connector.** A USB-C socket tells you the shape of the
socket and nothing else.

In practice this means:

- A field that was not reported is `null`, never a zero and never a plausible default.
- Say *why* something is unknown. "This PC's controller does not report cable information" and
  "this cable has no e-marker" are different claims, and only one is usually supported.
- An all-zero response means the hardware had nothing to say. Do not decode it into fields.
- If you cannot distinguish two explanations, say so rather than picking the likelier one.

A pull request that makes Portmark state something confident and wrong will be rejected even if
the output looks better. Output that looks worse but is true is the product.

## Building

```powershell
dotnet build Portmark.slnx
dotnet test Portmark.slnx
```

Requires the .NET 10 SDK on Windows.

## Tests

Decoder tests run on byte vectors captured from real hardware, so the suite passes with no USB-C
device attached. When you add a decoder, add its vector.

Where a decoded value can be checked against the physical world, check it and say so in the test.
The power delivery decoder is verified against a charger whose printed rating matches what the
bytes decode to; that is worth more than any amount of self-consistent unit testing.

## Style

`.editorconfig` covers the mechanical parts. Beyond that: comments explain *why*, particularly
where something is non-obvious or was got wrong once. Several comments in this codebase exist
because a subtly wrong version shipped first, and they say so.
