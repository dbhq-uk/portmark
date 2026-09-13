# Security

## Reporting

Report vulnerabilities through [GitHub's private advisory form](../../security/advisories/new).
Please do not open a public issue for anything exploitable.

## What Portmark does to your machine

Portmark **only reads** from USB and port controller interfaces. It sends no UCSI command that
changes port state: no role swaps, no resets, no power renegotiation, no firmware commands. There
is no code path that could.

It makes exactly one change to the system, only when you explicitly ask, and only with
administrator rights:

```
portmark enable     # sets one registry DWORD, TestInterfaceEnabled
portmark disable    # removes it
```

## The trade-off of the extended tier, stated plainly

Windows ships the port controller interface switched off, and says why: to stop it "being
accessible to unauthorized users on a retail system".

While it is enabled, **any program running as your user can send commands to your USB-C power
controller**, not only Portmark. That includes commands Portmark itself refuses to send.

This is a real trade-off, not a formality. The zero-setup features — device enumeration, alternate
modes, video capability, slow-link detection — need none of it. If you do not need the extended
tier, do not enable it, and run `portmark disable` when you are finished with it.

## Privacy

Portmark makes no network requests, collects nothing, and sends nothing anywhere. Its output stays
on your machine unless you paste it somewhere yourself.
