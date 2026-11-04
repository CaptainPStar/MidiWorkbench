# ADR 002: Native clock and prepared MIDI plans

Status: architecture accepted; WinMM implemented as the first backend; Windows MIDI Services not yet evaluated; measurements pending.

Musical planning can happen ahead of playback. Expensive generation is never part of a timing deadline. Keep monotonic host time, musical position, MIDI synchronization and physical device latency separate.

## Planned backend

Evaluate Windows MIDI Services first for timestamped output and input timestamps. Keep an adapter contract so a documented legacy fallback can be added if the user's installation or interface requires it. Do not assume API availability, driver behavior or cancellation semantics from the operating-system name alone.

`IMidiBackend` and its ports implement enumeration, open, immediate or scheduled submission, cancellation, fault reporting and input callbacks. WinMM (no dependency, immediate send, host-arrival input timestamps, polled endpoint changes) is implemented and compile-checked; it has not run on Windows. Still needed per adapter: enumeration, stable endpoint identity, input subscription, timestamp conversion, scheduled-output capability, bounded lookahead, clock/transport support, disconnect notification and cancellation/flush semantics, reported as capabilities, not assumptions. `cli probe` measures loopback latency for the gate below. UMP and legacy MIDI translation belong here, not in generators.

## Scheduling policy

- One clock authority at a time. Internal master first, external MIDI follower later, Link separately.
- MIDI Clock is 24 PPQN, derived from the finer internal musical timeline.
- Handle Start, Continue, Stop and Song Position Pointer separately; not every receiver supports all of them.
- Swing/humanization offset note events without distorting sync pulse timing.
- Absolute deadlines prevent cumulative sleep drift. A precise timestamp does not guarantee precise physical delivery.
- Bound lookahead; long queues impair live edits and cancellation. Reconcile active notes when plans change.
- Under output congestion, coalesce replaceable controller values and protect clock/note release ordering. Never interleave transactional NRPN or SysEx incorrectly.
- Large SysEx/patch transfers require an explicit policy before live use.
- Track note ownership by source and endpoint/channel/pitch. Sustain and retrigger policies must be tested.
- Device offsets are rig configuration. Do not promise automatic acoustic latency calibration without an external measurement path.

## Measurement gate

Measure median/p95/p99/max deadline error, sustained drift, physical loopback latency, inter-port skew and overload counts. Test idle/load, tempo changes, dense chords, controller traffic, stop/restart, USB removal, sleep/resume and long sessions. Software loopback does not certify DIN or acoustic timing. Numeric release targets must follow baseline measurements on the intended interfaces; no invented timing guarantee is accepted.

## Sources

- https://midi.org/summary-of-midi-1-0-messages
- https://midi.org/about-midi-part-3midi-messages
- https://microsoft.github.io/MIDI/
- https://microsoft.github.io/MIDI/sdk-reference/MidiClock/
- https://microsoft.github.io/MIDI/sdk-reference/MidiEndpointConnection/
- https://www.ableton.com/en/manual/synchronizing-with-link-tempo-follower-and-midi/
- https://www.expert-sleepers.co.uk/usamo.html

Reviewed 2026-11-04. Windows MIDI Services documentation describes timestamp scheduling and warns against excessive queued messages. USAMO is an alternative workflow reference; its audio-encoded transport is outside this MIDI-only foundation.
