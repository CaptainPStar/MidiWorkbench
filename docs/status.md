# Verification status

## 2026-11-04 Windows integration

- Windows 10.0.19045, x64, .NET SDK 10.0.401: locked restore and Release build pass without warnings/errors. The complete engine/application specification executable passes 53/53 groups.
- All 76 reproducible Avalonia offscreen pointer, keyboard, rendering and workflow assertions pass in normal verification. See [UI evidence](reports/windows-ui-2026-11-04.md) for coverage, minimum-size review and measured dense-note rendering.
- Self-contained Windows x64 desktop and CLI publish successfully on this workstation.
- The native desktop process launched on Windows and its accessibility tree was inspected. Native graphics capture timed out; the user stopped Windows UI automation with Escape. Further interactive testing used in-process headless Avalonia, not OS input. This does not establish human/manual desktop acceptance.
- Corrected pedal/note cleanup, Panic during a pending Stop, queued input after Panic, input overflow/fault recovery, offline optional-input playback, natural-completion recording saves and oversized-document writes. Added independent regressions for these cases.
- Independent design and UX reviews led to pinned preview actions, relevant generator fields, output/queue status, musical note fields, unsaved-change guards, readable routing at 1000×650, and routing draft preservation during endpoint refresh.
- Approved native virtualMIDI probes on Drum, Pad1, Pad2 and Lead pass count, ownership, sustain cleanup, shutdown and reopen checks. Two 60-second runs each delivered 3,840 balanced note pairs and 2,880 clocks per port. See [runtime measurements](reports/windows-runtime-2026-11-04.md).
- The load-window clock spacing error reached 26.783 ms and inter-port arrival skew reached 26.972 ms. These outliers remain measured limitations; no tight physical MIDI timing claim is made.

The earlier Linux/cross-publish evidence below describes the PR #1 baseline. Windows execution is now tested as described above. Physical cable/synth, native unplug/reconnect, sleep/resume and hour-long acceptance still require the intended setup.

## 2026-11-04 live generation continuation

- Release verification passes 51/51 specification groups, including live generation, transport replacement, batch note editing and independent workflow regressions.
- Independent tests cover superseded requests, project reload/disposal, candidate audition and return, discarded queued auditions, fixed/locked material, note balance and clock continuity.
- Batch edits cover group bounds, ID collisions, clipboard validation, extreme offsets and note budgets.
- Meter-snapshot regressions cover 4/4 → 3/4 acceptance and 7/8 audition/return; the displayed section metadata changes at the actual transport boundary.
- CLI `vary` produces byte-identical projects for repeated source/seed input. The result validates and simulates with 1,076 output events, zero late notes and complete capture. Export was independently parsed: type 1, 960 PPQ, five tracks, 153 balanced note pairs and 16 seconds.
- Desktop pointer/keyboard checks pass for shift/marquee selection, group move/resize/delete, undo/redo, copy/paste/duplicate and velocity/lock changes. Live checks cover 4/4 → 3/8 audition, preparing 5/8 without changing the sounding meter, return to 4/4, acceptance of 5/8 and live mute/solo. No observed blocker.
- Windows desktop and CLI self-contained packages cross-publish; execution on Windows remains untested.
- Durable capture development is deferred in favor of generation/orchestration; a DAW or MusicSessionTrace can capture performance output.

## Earlier integration, Linux x64, .NET SDK 10.0.100

- Release solution build succeeds with zero warnings and errors.
- `bash scripts/verify.sh`: 47/47 specification groups pass. Groups include the original cases, transport regressions, configurable generators, composition workflows and independent end-to-end integration tests.
- Generator coverage includes 168 generator/meter/seed combinations, musical bounds, captured harmony, protected material, recipe versions, note-budget enforcement and original drummer golden output.
- Independent review reproduced destination-changing launch, armed mute/solo and clock-only replay defects; regression tests now pass after correction. Additional regressions cover unavailable unused outputs, failed launches preserving current playback, overlapping notes after dropped late attacks and section identity at the actual playback boundary.
- CLI new/validate, scoped generation, arranged-song simulation/export, take validation/export and bank creation/listing succeed.
- Exported song and take files were parsed independently with Python Mido: type 1, 960 PPQ, five tracks, 161 balanced note pairs and 16-second duration for the demo.
- Desktop runs under Xvfb. Pointer checks cover note creation, moving, resizing and saving; agent checks also cover undo/redo. A UI-saved schema-3 project was read back and its edited pitch/tick/duration verified. Native file picker opened and saved successfully.
- Self-contained `win-x64` desktop and CLI packages cross-publish successfully. This verifies packaging, not execution on Windows.

Software loopback probe (4 seconds, concurrent development container): 16/16 notes matched, 192/192 clock pulses, zero late attacks and skipped pulses. Note arrival latency median 0.01 ms, p99/max 9.13 ms; absolute clock spacing error p99 1.31 ms, max 4.82 ms; pulse-span error −5.89 ms. These are one short software-loopback sample, not hardware or long-term drift certification.

## Hardware acceptance still required

1. Build/run on Windows and enumerate the intended MIDI interfaces.
2. Record physical cable-loopback measurements, idle and under CPU/UI load, including clock timing and inter-port skew.
3. Play dense chords/drums and sustained keyboard input; change tempo; exercise Stop/Panic, pedal release, repeated notes and queued launches.
4. Unplug/reconnect input and output devices; verify named faults, note release on remaining outputs and successful reopen.
5. Record multiple takes, save/reopen songs and banks, replay takes and import SMF into the intended DAW.
6. Test long sessions, sleep/resume and interface/driver-specific behavior. Establish timing thresholds from those measurements.

## Remaining limits

- WinMM is immediate-send; Windows MIDI Services has not been implemented or benchmarked. No hard-real-time guarantee.
- WinMM endpoint identity is name plus occurrence. Input timestamps are host arrival, not calibrated driver times. Hardware MIDI is Windows-only.
- Background generation works during playback. Live audition/return/acceptance targets the selected playing section without recording armed; arrangement replacement is not supported. Direct note editing and history operations require Stop. Changing the active rig requires Stop.
- Live mute/solo applies to a known playing section, not arbitrary tracks across an arrangement or recalled take.
- Active takes are bounded to 100,000 capture entries. Overflow/loss is reported. Completed takes are archived; in-progress capture is not streamed durably and cannot be recovered after a crash.
- Recorded loop passes overdub. Notes crossing the section end are shortened. Raw take input preserves pedals, while converted phrase notes encode sustain as duration.
- Per-track clip scenes, count-in, retrospective capture, dedicated keyboard step entry, controller learn and device parameter automation remain follow-on work; see [roadmap.md](roadmap.md).
- Physical sound state is outside project/take recall.

`dotnet test` does not execute these specifications. Use the verify script or run `tests/MidiWorkbench.Specs` directly.
