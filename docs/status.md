# Verification status

## 2026-11-04, Linux, SDK 10.0.403

- `bash scripts/verify.sh`: restore, Release build with 0 warnings, 43/43 specifications. The real-time session recording spec passed in five consecutive runs.
- CLI `new`, `validate`, `upgrade`, `simulate`, `export` and `devices` succeeded. `export` output is recognised as a type-1 MIDI file at 960 PPQ.
- `probe loopback loopback 4` (software loopback, no device): 16/16 matched; host scheduling error median 0.25 ms, p99 8.5 ms. This measures the portable sleep-based wait under an idle container, not Windows or a device.
- Desktop under Xvfb: launch, routing edit and Apply, rig persisted and reloaded after restart, loop playback with live bar display, arm New part, Play/Stop. No pointer drag, floating windows or file pickers were exercised.

Not run: Windows build and desktop, WinMM adapter, high-resolution wait, any MIDI hardware, the ADR 002 measurement gate.

## Specifications

Generation and drummer (determinism, bounds across meters/personas, equivalence with the original planner for 4/4, 3/4, 6/8, recipe persistence); locks and fixed patterns; tempo and tempo map; validation and schema migration; project/rig roundtrip; routing (missing route, channel override, shared-endpoint clock and ownership, offsets); transport (stop, lookahead stop, clock, late notes, stall without burst, loop, panic); adapter conformance against a fake device (disconnect, stop, backlog, deadline order, scheduled timestamps, thru, input overflow); capture, recorder, loopback recording and session recording; MIDI file export.

Run `scripts/verify.ps1` or `bash scripts/verify.sh`. These are a dependency-free console harness; `dotnet test` does not run them.

## Windows and hardware acceptance

1. `devices` lists interfaces; unplugging one while the app runs updates Routing within a few seconds.
2. `probe OUT IN 60` through a MIDI cable loop, idle and under CPU load. Record median/p95/p99/max here.
3. Desktop: route parts to real synths; play, loop, tempo change while playing, Stop, Panic. No hanging notes.
4. Unplug an output during playback: playback stops, other outputs release notes, the error names the output; Play after reconnecting works.
5. Record from a keyboard into a new part and an existing part, with sustain; thru reaches the synth; Save take as `.mid` opens in a DAW.
6. Clock-following receivers start, stay in tempo and stop with the transport; per-output delay shifts that output only.
7. Close during playback; the worker exits and notes are released.

## Known limitations

- WinMM sends immediately and gives no driver timestamps; scheduling precision is the worker's wake granularity. Windows MIDI Services has not been evaluated.
- WinMM endpoint IDs are name plus occurrence; renaming or reordering identical devices can swap routes.
- Input timestamps are host arrival times; driver timestamps are not calibrated.
- Thru passes through the transport worker (up to about 1 ms plus wait granularity). Not yet measured.
- Tempo changes apply after the 10 ms lookahead already scheduled.
- First section only; no scenes or arranger. Generation and note edits require Stop.
- Drums use complexity, not section density. Other meters than 4/4, 3/4 and 6/8 use whole-beat grouping and drop hits past the bar end.
- Recorded notes crossing the section end are shortened; loop passes overdub into the same part.
- Deleting a note freezes that pattern; there is no UI conversion back to generative.
- Capture holds 100,000 messages and flags overflow; the next Play clears it. Thru output is not captured, only its input.
- Late notes (more than 25 ms) and late clock pulses are skipped and counted; this policy is provisional.
- Layout save errors during shutdown may not remain visible after closing.
- Atomic JSON replacement is not a crash-recovery system.
