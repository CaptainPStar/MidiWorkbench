# Implementation milestones

## 0 — Validate the foundation

Restore/build on .NET 10, run specifications, test docking and file pickers on Windows, check floating-window layout recovery and shutdown. Commit real package lock files. Resolve compile/API differences before further features.

Exit: all specs pass and a generated, edited project survives save/open and produces a complete memory capture. No physical timing claim.

## 1 — Physical MIDI and clock

Implement device enumeration/output/input adapter with Windows MIDI Services capability detection. Add bounded scheduler command queues, ownership/retrigger policies, panic, endpoint faults, timing metrics and physical loopback harness. Replace the demonstration wait loop with measured scheduling behavior. Add rig persistence and selection.

Exit: sustained notes release, clock is measured under load, disconnect/reconnect and stop/restart behave explicitly. Only then permit hardware performance claims.

## 2 — Drummer and keyboard capture

Extract/test appropriate MusicSessionTrace planner code and integrate it with shared timing. Add input recording, step entry, overdub, count-in, retrospective input/output buffers, sustain handling and SMF export with tempo mapping. Avoid routing generated output back into its own input.

Exit: keyboard phrase → playback → drummer accompaniment → captured MIDI replay works through actual devices.

## 3 — Editors and structured generation

Piano roll, drum grid, note conditions, probability, microtiming, arpeggiator, Euclidean and stochastic generators. Add recipe versioning, edit overrides/tombstones, undo/redo, candidate comparison, protected motifs, chord/voice-leading and development curves. Background generation and revision checks replace stopped-only editing.

Exit: outline-led and performance-led paths both produce editable, reproducible material; regeneration cannot overwrite protected work.

## 4 — Scenes and performance

Project-wide tracks, patterns, scenes, sections, song timeline and banks/setlists. Quantized launches with cancel, note-tail/retrigger policy, coordinated transitions, macro assignments, temporary changes and controller feedback. Prepare next sections without interrupting current playback.

Exit: perform a multi-section song using controllers and save/replay actual emitted events and action history.

## 5 — Optional extensions

Device parameter profiles (CC/NRPN/SysEx), MIDI curves, external clock following, Link, external MusicSessionTrace analysis feed, multiple playheads, density budgets and a programmable generator interface. Each requires its own acceptance tests; none blocks the initial MIDI performance workflow.
