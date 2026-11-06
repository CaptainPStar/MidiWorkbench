# Verification status

## 2026-11-06 minilogue xd listening and native first use

- The user confirmed the intended phrase played audibly and ended cleanly on a minilogue xd through Studio 24c MIDI OUT, channel 1. Separate engine-driven Stop and Panic checks also released cleanly.
- The user confirmed native Open -> Routing -> Play, edit -> Save as -> reopen -> playback, looping playback, the Stop button and F8 Panic. Both native stop controls released cleanly with no hanging notes. No application fix was required. See the [first-use report](reports/minilogue-first-use-2026-11-06.md) for evidence and the remaining musical/session checks.

## 2026-11-06 Studio 24c physical cable loop

- The user-connected Studio 24c DIN OUT-to-IN loop passes the packaged 30-second probe and 60-second dense playback under repeated offscreen UI workload. All 3,840 note pairs and 2,880 clocks arrived; the complete 10,564-message stream matched submitted bytes and order.
- Active Stop, Panic and shutdown released sixteen held notes and sustain. Stop/Panic were checked before disposal. Closing and reopening both ports preserved exact send/receive delivery.
- Four UI rounds passed 1,092 assertions, overlapping 59.658/60 seconds. Loaded clock-spacing error p99/max was 0.860/0.979 ms. Dense sixteen-note chord latency p99/max was 15.818/16.782 ms, including physical serialization. Timing measurements are descriptive.
- No production change was required. See the [physical cable-loop report](reports/physical-loopback-2026-11-06.md) for test-isolation corrections, evidence and limits. Physical unplug/reconnect and broader instrument/session acceptance remain open. The first-use section above records the subsequent bounded listening and native desktop checks.

## 2026-11-05 musical workflow refinement

- At application revision `f884d1c`, locked restore and serial Release verification pass with zero warnings/errors: 61 core specification groups and 273 offscreen desktop checks.
- Musical corrections preserve harmonic voices, note visibility, silent MIDI endings, explicit export channel choices, protected edits and true draft discard. Section rename changes metadata only. Song/bank identity and New song are usable through the desktop; scoped part preview remains reachable while editing lower generator fields.
- Independent research-led, blind musical reviews exercised intentional sketches, captured phrases, related sections, live queue/audition controls, arrangement and bank recall. Reproduced findings were corrected and regression-tested. See the [musical workflow report](reports/musical-workflows-2026-11-04.md) for revision-specific evidence and acceptance bounds.
- The final fresh Sol 6.1 review at `f884d1c` found no actionable production issue in its tested scope. Its 147 exported note pairs matched the accepted arrangement exactly; bank recall, protected Loopback capture, live audition/Return, queue cancellation, launch and F8 passed. This is bounded software acceptance, not physical or acoustic proof.
- The current five-minute virtualMIDI run delivered 19,200 balanced note pairs and 14,400 clocks per approved port, with no late attacks, skipped clocks, ownership residue or endpoint errors. Sustain cleanup, shutdown and reopen passed. Capture correctly reported its planned 100,000-entry overflow while independent output traces remained intact.
- Eighteen UI workload rounds passed 4,914 assertions; at least 299.929 of 300 seconds overlapped individual rounds. Clock-spacing error p99 was 0.873–0.887 ms, maximum 12.460 ms. Planned clock deadline-to-dispatch p99 was 0.997–1.133 ms, maximum 12.842 ms. These tails remain a limitation; no hard-real-time or physical synth certification is claimed.

The sections below record earlier revisions and workloads. November 6 adds cable-loop measurements and user-confirmed minilogue playback, native file opening/saving/reopening, looping, Stop and Panic. Broader musical workflows, acoustic timing measurements and physical unplug/reconnect remain open.

## 2026-11-04 timing and workflow follow-up

- Locked restore and serial Release verification pass with zero warnings/errors: 55 core specification groups and 100 offscreen desktop assertions.
- Draft protection now covers section/part selection, drummer settings, generator settings and undo/redo. Floating panes and owned confirmation dialogs receive Stop/Panic shortcuts. File dialogs require stopped playback; existing-path project Save remains available while playing.
- Saved take history is bounded, older files remain loadable, selected takes can be saved explicitly, and archive failure retains capture with a tested JSON recovery path. Output faults take precedence over optional-input notices.
- Deadline-ordered, fair cross-port dispatch, isolated scheduler preparation and deadline-aware waits improve measured virtual timing. The final 60-second idle and 300-second load probes pass note/clock integrity, sustain cleanup, shutdown and reopen on all four approved ports.
- The five-minute run delivered 19,200 balanced note pairs and 14,400 clocks per port, with no late attacks or skipped clocks. It deliberately exceeded the 100,000-entry capture limit: incomplete capture was reported while output continued correctly.
- Load-run clock spacing error p99 was 0.864–0.875 ms, maximum 6.636 ms; callback inter-port clock skew p99/max was 0.131/3.124 ms. These measured improvements do not certify physical synth timing.

See [workflow and verification evidence](reports/performance-acceptance-2026-11-04.md) and [timing diagnosis](reports/windows-timing-2026-11-04.md). The earlier sections remain historical measurements of earlier revisions. Physical instrument and native human acceptance remain open.

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

Windows build, enumeration and single-interface cable-loop checks are complete. Remaining physical and attended checks:

1. Extend attended native acceptance to generation/audition, section launching, banks and export. File opening, Save as/reopening, routing, basic playback, looping, Stop and F8 Panic have passed with the user.
2. Measure multi-interface physical timing and inter-port skew where the intended setup requires them.
3. Extend instrument checks to sustained keyboard input, tempo changes, pedal behavior, repeated notes, queued launches and external clock synchronization. Basic minilogue playback and audible Stop/Panic cleanup pass; single-sequencer dense notes also pass the Studio 24c cable loop.
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
