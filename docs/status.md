# Verification status

2026-11-04: source foundation created without a .NET SDK.

2026-11-04, later: on Linux with SDK 10.0.403, `bash scripts/verify.sh` restored, built Release with 0 warnings and passed 15/15 specifications. CLI `new`, `validate` and `simulate` succeeded on `samples/opening.midiwork.json`. Not yet run: Windows build, desktop/graphical smoke test, hardware. Package lock files are not committed.

## Implemented source

Seven application/library projects and a console specification project; deterministic outline generator; locked/fixed material preservation; musical time/tempo; memory transport and bounded capture; JSON persistence; CLI; Avalonia/Dock shell and basic note editor.

## Checks performed here

- Project and solution XML parses.
- Every project/package reference is resolved in the source graph/central version list.
- Target framework and source namespace/project boundaries checked.
- Bash verification script parses.
- Documentation links to local files resolve.
- ZIP contents match the source manifest.

## Specifications

Fifteen C# specifications, passing on Linux: deterministic generation, note bounds across seeds/meters, protected edits, fixed patterns, tempo continuity, meter length, validation rejection, note release, 24-PPQN clock, late events, skipped-owner isolation, repeated-note ordering, bounded capture, JSON roundtrip, complete simulated playback.

Run `scripts/verify.ps1` or `bash scripts/verify.sh`. These are a dependency-free console test harness, not an xUnit/MSTest project. `dotnet test` alone does not execute them.

## Manual desktop acceptance

1. Start with no device: no audio/MIDI hardware should be opened.
2. Generate in 4/4 and 7/8; inspect bounds; edit/lock/regenerate; save/open.
3. Play/stop; compare displayed meter and captured event times; alter tempo while playing.
4. Save take; confirm start/clock/note-off/stop events and completeness flag.
5. Split and float panes; restart and reset; test disconnected monitor recovery.
6. Invalid projects/layouts show actionable errors without starting transport.
7. Close during playback and verify worker lifetime ends.

## Known limitations

- Silent in-memory output only; no real MIDI I/O or synthesis.
- First section only in the current UI and CLI simulation; no scene launcher or arranger yet.
- Basic generated drums are not the existing synthetic drummer.
- Pad is a sustained single note; pulse is a seeded scale pattern.
- No piano roll, keyboard capture, undo, SMF export or performance journal yet.
- Generation and note edits require Stop. UI changes to outline apply on Generate.
- Deleting a note freezes that pattern. There is not yet a UI conversion back to generative.
- Capture holds up to 100,000 messages; full capture is flagged incomplete. Next Play clears it.
- Prototype timer and late-event policies are not production timing guarantees.
- Layout save errors during shutdown may not remain visible after closing; add persistent diagnostics before release.
- Atomic JSON replacement is not a crash-recovery system.
- Repository: https://github.com/CaptainPStar/MidiWorkbench. Initial source was delivered as an independent package before repository initialization.
