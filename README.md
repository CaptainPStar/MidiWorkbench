# MidiWorkbench

.NET 10 MIDI composition and performance workstation for external instruments. Avalonia and Dock provide a rearrangeable desktop workspace. The application generates and records MIDI; it does not synthesize or record audio.

## Workflows

- Outline a section, prepare a candidate, audition it, accept or discard it, then edit and arrange.
- Record a keyboard phrase, preserve it as locked material, develop other parts around it, then perform and capture.

Accepted notes are saved with their generator recipes. Opening a project does not regenerate material or start playback. Routes and docking layouts are machine-local.

## Implemented

- Sections, song order and repeats; song banks and setlists; undo/redo.
- Piano roll and drum grid with multi-note selection, group moves/resizing, copy/paste, duplication, velocity and locks.
- Step sequencer, arpeggiator, Euclidean rhythm, stochastic, random-walk, chord and pad generators. Settings include scale/range, gate, probability, ratchets, swing and source phrases.
- Synthetic drummer ported from MusicSessionTrace, nine personas, explicit grouping for odd meters and versioned recipes.
- Background generation and seeded variation with cancellation, scoped candidates, live audition/return and boundary acceptance.
- MIDI routing, channel overrides, clock-only outputs, endpoint delays, Stop and Panic.
- Quantized section launching and live section mute/solo. Selecting a section never launches it.
- Keyboard input, thru, loop overdub and protected recorded notes.
- Separate input/output/action capture, completed-take recovery, take replay and Standard MIDI File export.
- WinMM input/output and a dedicated transport worker; Memory and Loopback adapters for portable testing.
- CLI operations for projects, generators, arranged-song simulation, MIDI export, banks and loopback measurements.

The application builds and runs on Windows. Windows virtualMIDI delivery, shutdown and reopen checks pass on four ports; physical synth timing and unplug/reconnect acceptance remain separate. See [verification status](docs/status.md) for measured results and limits.

## Build and run

Install .NET 10, then:

```powershell
./scripts/verify.ps1
dotnet run --project src/MidiWorkbench.Desktop
```

On Linux/macOS use `bash scripts/verify.sh`. Device MIDI is Windows-only; portable builds expose silent Memory and Loopback endpoints.

Build a self-contained Windows desktop and CLI:

```powershell
./scripts/publish.ps1 win-x64
```

Or `bash scripts/publish.sh win-x64`. Output is under `artifacts/win-x64/`. Run `desktop/MidiWorkbench.Desktop.exe`; no SDK is needed on the target computer.

## First session

1. The output bar identifies simulation, missing routes and MIDI outputs. Simulation sends no MIDI to instruments. Open Routing, assign each destination an output and channel, choose the input, enable Clock where wanted, then Apply routing.
2. Outline: set meter, root, scale and musical amounts. Preview a section or a part generator. Audition, then Accept or Discard.
3. Patterns: click empty space to add a note; drag it to move, or drag its right edge to resize. Right-click/Delete removes. Start beat is counted from 1; length is in quarter-note beats (0.25 = a sixteenth note), independent of meter. Pitch shows the note name and MIDI number. Locked notes survive regeneration. Pattern kind explicitly controls Fixed/Hybrid/Generative behavior.
4. Perform: arm a part or New part, then play the section. Stop commits keyboard notes. Arm changes require stopped playback.
5. Arrange sections with repeats or queue a section at a beat/bar boundary. Song banks hold reusable project snapshots and setlist order.
6. Save the project, export the song as MIDI, or recall a captured take. A DAW or MusicSessionTrace can capture the performance; built-in recording remains available for phrase development.

See [live generation and editing](docs/live-generation.md) for audition, variation, cancellation and keyboard controls.

Save writes the accepted project. An unaccepted preview is separate and is not included until accepted. Closing or opening another project prompts for unsaved project/bank changes and unaccepted section changes. Save remembers the file path; Save as writes another file. Stopping and editing retain unapplied section settings until the accepted outline or selected section changes.

| Shortcut | Action |
|---|---|
| F5 | Play/stop the song, or the armed section |
| F8 | Stop |
| Ctrl+Shift+Space | Panic: release notes and pedals immediately |
| Ctrl+S / Ctrl+Shift+S | Save / Save as |
| Ctrl+O | Open while stopped |
| Ctrl+Z / Ctrl+Shift+Z | Undo / redo project edits |

`scripts/verify.ps1` also renders the desktop offscreen and exercises its controls without sending OS input. Images and results are written to `artifacts/desktop-ux`. Tests isolate local settings using `MIDIWORKBENCH_SETTINGS_DIRECTORY`; ordinary launches use `%LOCALAPPDATA%\MidiWorkbench`.

Arpeggiators can use captured notes as their source. Generator settings preserve that material in the recipe. Change only the part you intend to vary; Fixed material remains protected until explicitly converted.

## CLI

```sh
dotnet run --project src/MidiWorkbench.Cli -- new song.midiwork.json
dotnet run --project src/MidiWorkbench.Cli -- sections song.midiwork.json
dotnet run --project src/MidiWorkbench.Cli -- vary song.midiwork.json section-1 42 variation.midiwork.json
dotnet run --project src/MidiWorkbench.Cli -- simulate-song song.midiwork.json take.json
dotnet run --project src/MidiWorkbench.Cli -- export-song song.midiwork.json song.mid
dotnet run --project src/MidiWorkbench.Cli -- validate-take take.json
dotnet run --project src/MidiWorkbench.Cli -- export-take take.json take.mid
dotnet run --project src/MidiWorkbench.Cli -- bank-create set.midiworkbank.json song.midiwork.json
dotnet run --project src/MidiWorkbench.Cli -- devices
dotnet run --project src/MidiWorkbench.Cli -- probe OUTPUT_ID INPUT_ID 30
```

`simulate-song` uses synthetic time. `probe` sends notes and clock through a cable or software loop and reports arrival latency and clock spacing error. Neither measures acoustic onset. See [timing policy](docs/decisions/002-timing.md).

## Documentation

- [Product and requirements](docs/product.md)
- [Architecture](docs/architecture.md)
- [Frontend decision](docs/decisions/001-frontend.md)
- [Project model](docs/project-model.md)
- [Research](docs/research.md)
- [Roadmap](docs/roadmap.md)
- [Original integration plan](docs/integration.md)
- [Verification status](docs/status.md)

No repository license has been selected. Third-party dependencies retain their licenses; see [THIRD_PARTY.md](THIRD_PARTY.md).
