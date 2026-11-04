# Architecture

The application separates musical material, prepared plans, machine routing, transport and presentation. Domain, generation, persistence and engine have no Avalonia dependency. Only MIDI is produced; instruments remain external.

| Project | Responsibility |
|---|---|
| Domain | Versioned projects, outlines, patterns, generator settings, banks, rigs and takes |
| Generation | Seeded section/part candidates, drummer, sequencer, arpeggio and stochastic algorithms |
| Engine | Musical/host time, compiled plans, route resolution, scheduling, input, ownership and capture |
| Persistence | Validated JSON migration, atomic files and SMF export |
| Application | Project history, sections, arrangement compilation, candidates, recording, banks and take recall |
| Midi.Windows | WinMM ports, polling for endpoint changes and high-resolution wait |
| Desktop | Docking, piano roll/drum grid, generation, performance and routing controls |
| Cli | Headless project/generation/bank workflows and MIDI diagnostics |
| Specs | Executable deterministic regressions and session lifecycle tests |

## Thread ownership

`WorkbenchSession` owns project state and port lifetimes. Immutable project snapshots and compiled plans cross the engine boundary. Generation, file access and phrase conversion occur outside the scheduler. Accepted edits have undo/redo history; candidates remain separate until accepted. Candidate preparation uses immutable snapshots, request identities and a per-session computation gate. Supersession, cancellation, selection changes and project reload invalidate stale results. Computation may finish after cancellation, but cannot publish or overlap the next computation.

`TransportHost` owns one worker and `Transport`. Ordinary commands use a bounded channel; Stop/Panic/Exit use a separate urgent path. Input callbacks timestamp and enqueue only. Each scheduler iteration limits input work so feedback or heavy input cannot starve transport commands. Status is published as immutable snapshots; the UI refresh does not drive timing.

Immediate ports receive queued messages at their deadlines. Scheduled ports accept future timestamps and must implement cancellation. WinMM is immediate. The worker's high-resolution wait reduces wake granularity; timestamps do not certify physical output timing. Shared process/runtime pauses and driver behavior still require measurement.

The worker merges eligible per-port FIFO heads by deadline, rotating equal-deadline ties across ports. Newly pumped output participates in the same merge. This preserves each port's order while preventing one port's dense chord from delaying all other ports' clocks. Waiting is capped at the nearer of one millisecond and the next pending deadline. A bounded, process-once preparation run exercises managed scheduler paths against isolated silent ports before the first real transport origin; it opens no devices and never enters a user's capture.

## Timing and ownership

Musical time is 960 PPQ. MIDI Clock is 24 pulses per quarter note. Tempo changes preserve musical position. A plan keeps note events separate from clock generation. External clock following is a future mode; internal master owns timing in this version.

Route resolution deduplicates physical ports and includes clock-only endpoints. A project-wide destination set provides stable metadata for queued section changes. Stable port slots open only the active plan’s required outputs and clock endpoints; additional outputs are prepared before a queued transition is accepted, outside the scheduler. Port/channel/pitch ownership combines generated and thru sources. A skipped attack cannot release another source's note. Pedal state is explicitly released during emergency cleanup.

Stop preserves the defined queued-output tail and completes after immediate pending releases. Panic cancels future scheduled output, clears pending output and releases notes/pedals. Stale pending attacks and clock pulses are counted and skipped instead of burst-transmitted after a stall. Output faults stop playback and attempt release on other outputs.

Queued plans activate at a musical boundary after the existing lookahead. Selection and launch are independent. Scene changes preserve clock continuity and release the outgoing section's owned notes. Live auditions use separate plan identities and do not commit the project; returning restores accepted material. Accepting commits an undoable project edit and queues the prepared plan. Cancelling that launch does not undo the committed edit. Each queued section version has a distinct plan identity mapped to an immutable section snapshot. Active-plan identity changes at the actual boundary, so accepting an edit cannot prematurely change the displayed meter or length.

## Capture and recall

Capture records input, submitted output and actions separately. Thru retains its routed channel and logical destination. Cancelled future submissions are reconciled out of saved output. Input loss and capture/journal overflow mark a take incomplete.

Recording is armed while stopped and committed as locked notes after Stop. The target is stable during recording. Loop passes overdub into the section; cross-boundary note tails are currently shortened. Raw input retains controllers, while phrase conversion represents sustain as note duration. This distinction is visible in the saved take versus the editable phrase.

Completed takes can be archived atomically by the application and recovered on startup. Active capture still uses bounded in-memory storage; it is not a crash-recoverable streaming recorder. An incomplete take is explicitly marked. An output timestamp is a submission/deadline, not proof of sound onset.

The recent-take cache loads at most the newest 16 archive files at startup and trims saved copies toward 16 takes / 200,000 input-plus-output events. It always retains the latest take, even if an imported take alone exceeds that event budget. Files are never deleted by cache eviction. Unsaved takes are retained; sessions without automatic archiving stop accepting new recordings at the unsaved-history limit until a take is saved as JSON. A failed automatic archive leaves the current capture intact and blocks replacement until a successful archive or explicit JSON recovery save. UI transport safety predicates use a side-effect-free running snapshot, so archive failure cannot disable the recovery picker.

Replay uses captured output timing rather than regenerating recipes. Recorded clock messages are replayed without an additional generated clock stream. Current-rig destinations or explicit endpoint mappings determine playback routing. SMF export retains tempo and arrangement meter changes.

## Device boundary

WinMM exposes immediate short-message I/O. Input timestamps are host arrival; driver timestamps are not calibrated. Endpoint IDs are name plus occurrence. The watcher marks removed opened outputs faulted; reconnecting reopens them on Play. Real enumeration, disconnect/reconnect, sustain, dense traffic and long-session timing remain hardware acceptance tests.

Windows MIDI Services remains a candidate backend. Capability and cancellation behavior must be measured before replacing WinMM. Large SysEx, external sync, Link and device parameter profiles remain outside the current implementation.

## MusicSessionTrace reuse

The deterministic drummer planner and personas were ported into Generation, with golden-output specifications for 4/4, 3/4 and 6/8. New grouped-meter recipes have explicit versioning. Adaptive audio followers and the original application's shared timing/UI infrastructure were not copied. MusicSessionTrace itself was not modified.
