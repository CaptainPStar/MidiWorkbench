# MidiWorkbench

.NET 10 foundation for a standalone MIDI composition and performance application. `MidiWorkbench` is a working name.

Avalonia and Dock provide a rearrangeable desktop workspace. The musical model, generation and transport have no UI dependency. Audio remains in external instruments.

## Included

- Dockable Outline, Patterns, Perform and Routing panes; local layout persistence and reset.
- Section outline: meter, length, root, scale, density, intensity, variation and seed.
- Drummer with nine personas (complexity, fills, swing, half-time) in any meter; demonstration bass, pulse and pad generators.
- Note list editing, adding, deleting and locks; fixed patterns survive regeneration.
- Machine-local rig: destination → output and channel, per-output clock and delay, keyboard input.
- Transport worker with lookahead scheduling, loop, Stop and Panic; one MIDI Clock stream per output.
- Keyboard recording with thru into a part or a new part; loop passes overdub; sustain pedal handled.
- Windows MIDI output and input through WinMM. Implemented, not yet run on Windows or hardware.
- Versioned JSON projects (schema 2, migrates schema 1), takes with separate inputs, outputs and actions, Standard MIDI File export.
- CLI: new, validate, upgrade, simulate, export, devices, probe.
- 43 executable specifications, including adapter conformance and drummer equivalence with the original planner.

Scene launching, piano roll, undo, multi-section playback, external clock following and background generation during playback remain planned.

## Build and run

Install a stable .NET 10 SDK. `global.json` accepts newer .NET 10 feature bands.

```powershell
./scripts/verify.ps1
dotnet run --project src/MidiWorkbench.Desktop
```

On Linux/macOS: `bash scripts/verify.sh`. Device MIDI is Windows-only; other platforms offer the silent Memory output and a Loopback endpoint.

```powershell
dotnet run --project src/MidiWorkbench.Cli -- new song.midiwork.json
dotnet run --project src/MidiWorkbench.Cli -- simulate song.midiwork.json take.json
dotnet run --project src/MidiWorkbench.Cli -- export song.midiwork.json song.mid
dotnet run --project src/MidiWorkbench.Cli -- devices
dotnet run --project src/MidiWorkbench.Cli -- probe OUTPUT_ID INPUT_ID 30
```

`simulate` uses synthetic time and memory output. `probe` sends notes through a physical loop (output cable into input) and reports arrival latency; arrival is host time at the input callback, not sound onset.

## First session

1. Routing: choose an output for each destination and, for recording, an input. Apply saves the rig to `LocalApplicationData/MidiWorkbench/rig.json`.
2. Outline: adjust and Generate. Drums selects Basic or a drummer persona. Vary advances the seed.
3. Patterns: select a note, edit, Apply note. Lock preserves it during regeneration. Added and recorded notes start locked. Deleting a note freezes that part as a fixed pattern.
4. Perform: choose Record into, then Play (optionally Loop). Played notes go through to the part's output and are added on Stop.
5. Save, Save take (`.json` or `.mid`) or Export MIDI. Panic releases all notes immediately.

Project save/open never starts playback. Routing and dock layout are local machine state, independent of the song. The UI edits and plays the first section; other sections are retained on load/save.

## Verification status

Builds and passes all specifications on Linux with SDK 10.0.403; the desktop was exercised under a virtual X display. Not yet built or run on Windows or with MIDI hardware; see docs/status.md.

## Documentation

- [Product and requirements](docs/product.md)
- [Architecture](docs/architecture.md)
- [Frontend decision](docs/decisions/001-frontend.md)
- [Timing and MIDI](docs/decisions/002-timing.md)
- [Project model](docs/project-model.md)
- [Research and references](docs/research.md)
- [Implementation milestones](docs/roadmap.md)
- [End-to-end integration plan](docs/integration.md)
- [Status and validation](docs/status.md)

No repository license has been selected; third-party dependencies retain their own licenses.
