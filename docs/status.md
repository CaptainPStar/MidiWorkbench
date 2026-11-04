# Verification status

## 2026-11-04 integration, Linux x64, .NET SDK 10.0.100

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
- Generation and direct note editing require stopped playback. Prepared section launches and section mute/solo use quantized replacement. Changing the active rig requires Stop.
- Live mute/solo applies to a known playing section, not arbitrary tracks across an arrangement or recalled take.
- Active takes are bounded to 100,000 capture entries. Overflow/loss is reported. Completed takes are archived; in-progress capture is not streamed durably and cannot be recovered after a crash.
- Recorded loop passes overdub. Notes crossing the section end are shortened. Raw take input preserves pedals, while converted phrase notes encode sustain as duration.
- Per-track clip scenes, count-in, retrospective capture, dedicated keyboard step entry, controller learn and device parameter automation remain follow-on work; see [roadmap.md](roadmap.md).
- Physical sound state is outside project/take recall.

`dotnet test` does not execute these specifications. Use the verify script or run `tests/MidiWorkbench.Specs` directly.
