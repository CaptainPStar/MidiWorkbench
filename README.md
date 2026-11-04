# MidiWorkbench

.NET 10 foundation for a standalone MIDI composition and performance application. `MidiWorkbench` is a working name.

Avalonia and Dock provide a rearrangeable desktop workspace. The musical model, generation and transport have no UI dependency. Audio remains in external instruments.

## Included

- Dockable Outline, Patterns, Perform and Routing panes; local layout persistence and reset.
- Section outline: meter, length, root, scale, density, intensity, variation and seed.
- Deterministic demonstration generators for drums, bass, pulse and a sustained pad note.
- Note list editing, adding, deleting and locks; fixed patterns survive regeneration.
- Meter-aware bar/beat display, tempo changes retaining musical position.
- In-memory playback, MIDI Clock messages, bounded event capture and explicit overflow status.
- Validated, versioned JSON projects and JSON event captures; CLI generation/validation/simulation.
- Fifteen executable specifications covering generation, timing, ownership, persistence and capture.

This is a foundation, not the finished sequencer. Playback sends only to memory and is silent. Physical MIDI I/O, the MusicSessionTrace drummer, keyboard recording, scene launching, piano roll, SMF export and background generation during playback remain planned.

## Build and run

Install a stable .NET 10 SDK. `global.json` accepts newer .NET 10 feature bands.

```powershell
./scripts/verify.ps1
dotnet run --project src/MidiWorkbench.Desktop
```

On Linux/macOS: `bash scripts/verify.sh`. Windows is the initial target for real MIDI integration; the memory engine and shell are platform-neutral.

```powershell
dotnet run --project src/MidiWorkbench.Cli -- new song.midiwork.json
dotnet run --project src/MidiWorkbench.Cli -- validate song.midiwork.json
dotnet run --project src/MidiWorkbench.Cli -- simulate song.midiwork.json take.json
```

The CLI simulates the first section using synthetic timestamps. It does not measure wall-clock scheduling or send MIDI to devices. Capture files are JSON, not Standard MIDI Files.

## First session

1. Adjust the outline and Generate. Vary advances the seed and regenerates.
2. Select a part and a note; edit its values and Apply note. Enable Lock to preserve it during regeneration. Added notes start locked. Deleting a note freezes that part as a fixed pattern, preserving the deletion.
3. Play to exercise transport and event capture. Stop before editing or regenerating in this build.
4. Save the project or Save take. Each Play starts a new bounded capture; save the previous take first if needed.
5. Drag pane tabs to rearrange/dock/float. Reset layout restores the default arrangement.

Project save/open never starts playback. Dock layout is local machine state and is independent of the song. The current UI edits/plays the first section; multi-section data is retained on load/save.

## Verification status

Created 2026-11-04. No .NET SDK was available in the execution environment and the SDK download timed out. The solution has not been compiled, restored, visually exercised or run against hardware. Structural checks passed; see docs/status.md for exact coverage and remaining checks. Package versions are pinned to the versions observed in MusicSessionTrace, not independently restored here.

## Documentation

- [Product and requirements](docs/product.md)
- [Architecture](docs/architecture.md)
- [Frontend decision](docs/decisions/001-frontend.md)
- [Timing and MIDI](docs/decisions/002-timing.md)
- [Project model](docs/project-model.md)
- [Research and references](docs/research.md)
- [Implementation milestones](docs/roadmap.md)
- [Status and validation](docs/status.md)

No source files were copied from MusicSessionTrace. Its code and documentation informed the architecture and dependency selection. No repository license has been selected; third-party dependencies retain their own licenses.
