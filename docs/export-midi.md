# MIDI export channels

Song and section exports have two channel policies:

- **Stored project channels** uses each part's saved MIDI channel. This is the default for Application API calls and CLI export, so the same project exports consistently on another machine.
- **Current routing overrides** applies the selected rig's channel override for each logical destination. A missing route or an unset override uses the part's stored channel. Desktop export defaults to this choice so the exported channels follow the visible Routing controls.

Routing-aware export reads configuration only. Devices can be disconnected; no input or output port is opened. Hardware endpoint IDs, clock settings and endpoint delays are not written into the song's musical timeline. Logical destination tracks remain separate. Neither policy changes the project or rig.

CLI export remains portable by default. Opt into channel overrides with a rig file:

```powershell
dotnet run --project src/MidiWorkbench.Cli -- export-song song.midiwork.json song.mid
dotnet run --project src/MidiWorkbench.Cli -- export-song song.midiwork.json routed.mid --rig studio-rig.json
dotnet run --project src/MidiWorkbench.Cli -- export song.midiwork.json section.mid --rig studio-rig.json
```

`export` writes the first section; `export-song` writes the arrangement, including repeats, meter changes and trailing silence. `--rig` works offline and only supplies channel overrides; it does not replace the session's rig or connect its keyboard input.

Take export is different: it preserves the already captured output channels and submission timing, with keyboard input on a separate track. Current routing is never applied again. Selecting the appropriate tracks in a DAW avoids playing both captured thru output and its source input at once.
