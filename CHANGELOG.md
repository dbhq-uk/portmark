# Changelog

## 0.3.0 - 2026-09-15

### New commands

- `portmark battery [--sample SECONDS] [--json]`: the batteries' own charge, rate, voltage, health
  and cycle count, read from the Windows battery driver. `--sample` averages the net charge flow
  over an interval. Only the battery is read.
- `portmark usb4 [--from FILE]`: what Windows' USB4 drivers report about links, speed, width and
  tunnelled protocols, from their trace events. Needs administrator rights; `--from` decodes a
  saved tracerpt XML file without them.
- `portmark report [--out PATH] [--no-redact]`: writes one file to attach to a hardware report.
  Serial numbers, device paths and user and machine names are removed by default. Nothing is sent
  anywhere.
- `portmark usb --json` and `portmark tree --json`.

### Fixes

- Cable speed from `GET_CABLE_PROPERTY` was decoded with its exponent and mantissa swapped.
- The slow-device check compared a device's declared USB version with its link, which flagged
  full-speed-only USB 2.0 devices such as mice and headsets. It now uses the hub's capability
  flags, the device's BOS descriptor and the port's supported protocols, including the companion
  port that shares its connector.
- The negotiated contract (RDO) was decoded with a 3-bit object position and a fixed-supply layout
  for every kind of supply, and was matched against the wrong list when this PC was supplying.
- Only the first four power objects were read. Up to seven standard-range objects are now read.
- Programmable (PPS) objects inflated "offers up to", and malformed ranged objects could feed the
  cable rating deduction.
- The battery charging-rate bits were decoded while this PC was supplying power, where UCSI does
  not define them.
- Wording no longer presents a negotiated contract as measured power, clears a supply because it
  advertises power, or treats a high battery percentage as proof a small contract is expected.

### JSON changes

`schemaVersion` is now `"2"`.

Added:

- `machine.batteries[]`, `machine.batteriesNote`, including `cycleCount`
- `connectors[].identity`, with `partner` and `cable` Discover Identity declarations, and
  `raw.pdMessageExchanges`. `idHeader.vendorName`; UFP VDOs carry `usb20DeviceCapable` and
  `usb20DeviceCapableBillboardOnly`
- `raw.pdoExchanges`, and position and request-flag fields on power objects and the negotiated
  contract
- `cable.laneDirectionalityConfigurable`
- `vendorName` beside vendor IDs on USB devices, Billboard devices, alternate modes and port
  statuses
- In `usb --json` and `tree --json`: `linkEvidence` per device, and `portStatuses` for hub ports
  reporting a status other than empty or connected
- Billboard `truncated` and `truncationNote`

Changed type (now nullable):

- `negotiated.operatingCurrentMilliamps` and `negotiated.maxOperatingCurrentMilliamps`: null where
  the selected supply's request carries no current, such as a battery supply
- Billboard `preferredModeIndex`
- `cable.supportsAlternateModes`: null for passive cables, where UCSI does not define it

Changed values:

- `power.maxAvailableMilliwatts` counts fixed objects and the stated power of EPR adjustable
  objects, not a programmable object's maximum voltage times maximum current
- Adjustable-voltage (AVS) objects have `kind` `"adjustable"` instead of `"unrecognised"`, and
  malformed ranged objects are now `"unrecognised"`
- `negotiated` is decoded against this PC's own objects when it is supplying, and left undecoded
  when the power direction is unknown
- Cable speed values follow the corrected bit layout
- Cable plug end type 3 reads "Other (not USB)"
- Billboard state 1 reads "not attempted or exited"
- USB `speed` can read "SuperSpeedPlus, 10 Gbps or above"; `expectedSpeed` and
  `isUnderperforming` come from capability evidence, not the declared USB version
- Connector `summary` text describes the negotiated values as a contract

## 0.2.0

See the [v0.2.0 release](https://github.com/dbhq-uk/portmark/releases/tag/v0.2.0).
