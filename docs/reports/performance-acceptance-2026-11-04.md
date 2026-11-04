# Timing and workflow follow-up, 2026-11-04

Follow-up to the merged PR #1 Windows baseline (`98c4ebb`). Work ran on Windows 10.0.19045, .NET 10.0.401. Only the authorized virtualMIDI ports Drum, Pad1, Pad2 and Lead received test output. No physical instrument, audio endpoint or OS input automation was used in this follow-up.

## Changes and independent review

- Scheduler output merges eligible per-port FIFO heads by deadline, rotating equal deadlines between ports. Dense chords no longer drain one port completely before servicing the others. Pending deadlines cap the worker's normal one-millisecond wait.
- A bounded, isolated preparation run exercises cold managed paths against silent ports before the first real transport origin. It performs no generation, storage, device opening or UI work. Steady empty scheduler iterations no longer allocate captured predicates.
- Section changes, part-generator selection and history actions protect unapplied outline, drummer and generator settings. Cancel retains both the selection and its draft. Shared dialog gating prevents duplicate actions and timer-based re-enabling of a pending button.
- Stop/Panic shortcuts reach floating pane contents and owned confirmation dialogs. Native file pickers require stopped playback; saving an existing project path remains available during playback.
- Recent archived takes use a bounded cache. Older files remain loadable, and Save selected writes the chosen take. A failed archive retains the current capture and permits an explicit JSON save elsewhere before another performance.
- Independent red-team review caught a recovery blocker: checking playback through the ordinary status method retried the failed archive before opening the recovery picker. Safety predicates now use a side-effect-free snapshot, and the desktop regression exercises the actual save-button path with a simulated picker result.

Sol implemented and measured scheduler changes; Terra implemented desktop safeguards and workflow tests; Luna independently reviewed correctness and recovery paths. The orchestrator reviewed the diffs, added take-history/recovery and fault-priority regressions, and ran integration verification. No review claim here substitutes for hardware or human acceptance.

## Measurement method

The manual Windows probe requires `--approved-virtual` and uses a fixed endpoint allowlist. Each port plays 16-note chords every eighth note, with MIDI Clock at 120 BPM. A probe-only, preallocated trace wraps the actual native output port. It records adapter entry/return separately from scheduled musical deadlines and input-callback arrival. File output and analysis occur after Stop. Clock skew compares the corresponding pulses across all four ports.

`DeadlineToDispatch` uses the planned musical grid and transport origin. `DispatchToArrival` measures callback arrival relative to native adapter entry. The older `Latency*` fields use the message timestamp, which can be clamped for immediate dispatch; they must not be substituted for planned-deadline latency. Instrumentation adds some measurement overhead. These are host software-loopback results, not acoustic onset or physical MIDI cable timing.

The merged baseline reproduced unfair cross-port ordering and 696,000 allocated bytes across 1,000 empty scheduler steps. The new regression requires no more than 256 bytes across those iterations. A 20-second baseline probe localized the large first-chord delay before adapter entry (14.10–15.58 ms); callback transit p99 was at most 0.056 ms. These measurements motivated the bounded preparation and dispatch changes.

## Results

Serial `scripts/verify.ps1` passed locked restore, warning-free Release build, all 55 core specification groups and all 100 offscreen desktop checks, including the new archive recovery case. Screenshots and logs are under `artifacts/desktop-ux/` and `artifacts/performance-verify.txt`.

The final 60-second idle and 300-second load probes passed all delivery, shutdown and reopen gates. The longer run delivered 19,200 balanced note pairs and 14,400 clocks per port with no late attacks, skipped clocks or held notes. Its capture reached exactly 100,000 entries and correctly reported incomplete. During the load window, 58 full 96-check UI-suite repetitions ran from 21:08:16 to 21:13:19 UTC; the final four archive-recovery checks were added and passed in subsequent serial verification. See [detailed timing measurements](windows-timing-2026-11-04.md) for exact overlap, distributions and remaining outliers.

## Reproduction

```powershell
./scripts/verify.ps1
dotnet run --project tests/MidiWorkbench.WindowsProbe -c Release --no-build -- --approved-virtual artifacts/timing-diagnosis/final-idle60.json 60
dotnet run --project tests/MidiWorkbench.WindowsProbe -c Release --no-build -- --approved-virtual artifacts/timing-diagnosis/final-load300.json 300 --expect-capture-overflow
```

The longer probe expects the ordinary 100,000-entry capture buffer to fill. It requires capture to be marked incomplete, and independently compares native output with received events using its separately bounded dispatch trace. This is an output-continuity/overflow test, not a complete five-minute recording test. It also verifies balanced note release, sustain-off on shutdown, native close and reopen.

## Limits

No physical cable/synth, hot-unplug, sleep/resume, hour-long session or human native-desktop acceptance was performed. Native Windows UI automation remains stopped at the user's earlier Escape request; UI evidence uses offscreen Avalonia input and rendering. Automatic take serialization still runs outside the scheduler but can occupy the UI thread while stopped. Active capture is not durable streaming storage, and crash recovery of an in-progress take is not provided.
