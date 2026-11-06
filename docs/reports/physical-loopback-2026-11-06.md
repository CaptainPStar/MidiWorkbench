# Studio 24c physical cable-loop acceptance — 2026-11-06

The confirmed Studio 24c DIN output-to-input cable passed message delivery, dense traffic, active Stop/Panic/shutdown cleanup, and input/output reopening on this workstation. No production defect was found. This closes the cable-loop acceptance gap; instrument sound, unplug/reconnect, and attended native UI acceptance remain open.

## Build and setup

- Main source: `e0d8e14f5785844eb0bdf7a0370e35232ace29cf`; source, tests and scripts are identical to packaged revision `950e97fc0b7b2455ed488a689c272db310d66e36`.
- Windows 10.0.19045. The user confirmed one cable between Studio 24c MIDI OUT and MIDI IN on November 6. No synth was connected to this loop.
- Only `winmm-out:Studio 24c MIDI Out#1` and `winmm-in:Studio 24c MIDI In#1` were opened. Channel 1, 120 BPM. GS Wavetable was excluded.
- Initial measurement used the delivered self-contained CLI. Dense tests used the same application engine/WinMM source through an artifact-only probe; its build passed with zero warnings/errors. The repository's virtual-port allowlist was unchanged.

## Measurements

| Test | Delivered notes | Clock pulses | Result |
|---|---:|---:|---|
| Packaged CLI, 30 seconds | 120/120 attacks | 1,440/1,440 | No missing attacks, late attacks or skipped clocks |
| Dense idle, 60 seconds | 3,840/3,840 balanced pairs | 2,880/2,880 | 10,564 complete messages match submitted bytes and order |
| Isolated cleanup confirmation, 8 seconds | 512/512 balanced pairs | 384/384 | Delivery, active cleanup and reopened delivery pass |
| Dense with UI workload, 60 seconds | 3,840/3,840 balanced pairs | 2,880/2,880 | 10,564 complete messages match submitted bytes and order |

The packaged CLI measured note-attack loop latency median **1.36 ms**, p99 **1.40 ms**, maximum **1.83 ms**. Its observer covers attacks and clocks, not note releases or pedal state.

Dense tests send sixteen-note chords every 250 ms with 187.5 ms gates and held sustain. Under UI workload, planned-note-time to input-callback latency was median **9.028 ms**, p99 **15.818 ms**, maximum **16.782 ms**. Captured chord arrivals are serialized across the physical connection. Clock-spacing absolute error was p99 **0.860 ms**, maximum **0.979 ms**; planned clock deadline to adapter entry was p99 **0.991 ms**, maximum **1.025 ms**. Idle dense clock-spacing error was p99 **0.868 ms**, maximum **1.284 ms**. These are observed statistics, not guaranteed timing ceilings.

All dense delivery cases had zero unmatched releases, held-note residue, reported late attacks, skipped clocks or endpoint errors. Capture was complete. Raw received streams independently matched every submitted status/data byte, including transport and clock messages.

Four repeated offscreen Avalonia test rounds passed **1,092 assertions**. Individual test-process intervals overlapped **59.658 of 60 seconds** of dense playback, including process startup/teardown. These tests use in-process pointer, keyboard and rendered UI workflows, not attended OS input.

## Active cleanup and recovery

Each final cleanup case first confirmed sixteen received attacks, sustain-on on channel 1, and no releases. Stop and Panic results were checked before host disposal. Every case received sixteen matching releases, sustain-off and one MIDI Stop, with exact submitted/received stream equality. Under UI workload, the last release/pedal callback arrived **25.977 ms** after Stop was requested, **17.412 ms** after Panic, and **17.402 ms** after shutdown. Input and output were then both closed and reopened; a new note-on, note-off and pedal-off arrived exactly.

The first idle probe's cleanup result is not accepted as a clean full run: a preceding host-disposal CC123 callback contaminated its Stop case. The trace shows that callback before the next MIDI Start. The harness now drains the preceding teardown before clearing a case. A separate isolation fix checks Stop/Panic before disposal, preventing disposal from masking missing cleanup. The isolated 8-second confirmation and final 60-second loaded run both pass these stronger checks. The first run's dense playback segment remains independently valid.

A Sol 6.1 reviewer independently inspected the harness and raw cleanup streams. This covers single-sequencer note ownership on one cable loop; shared-key/thru arbitration remains covered by software specifications, not by this physical test.

## Evidence and limits

Local evidence: `artifacts/physical-acceptance-20261106/` contains probe logs, JSON results, submitted/received CSVs, the isolated harness, UI round logs/renders, independent analysis, and SHA-256 manifest. Re-run analysis with `python artifacts/physical-acceptance-20261106/analyze.py`.

Only run the artifact probe while the same cable loop is confirmed:

```powershell
dotnet artifacts/physical-acceptance-20261106/Harness/bin/Release/net10.0/PhysicalProbe.dll --confirmed-studio24c-loopback artifacts/physical-acceptance-20261106/new-run.json 60
```

No driver or application changes were needed. The existing 61 core specification groups and 273 desktop checks remain the software baseline; the physical workload adds repeated desktop checks on identical source. No physical unplug/reconnect, sleep/resume, multi-hour soak, actual synth/acoustic behavior, native human/file-picker use, or external DAW import is certified here.
