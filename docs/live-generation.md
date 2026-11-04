# Live generation and orchestration

Start a section with Loop enabled. Preview a section or part while it plays; preparation runs outside the transport. The candidate remains separate from the saved project.

- **Preview section** uses the current outline controls. **Vary** prepares a new seeded variation of the accepted section, preserving generator settings, fixed patterns and locked notes.
- **Audition** queues the candidate at the chosen beat/bar boundary. It does not commit the project.
- **Return** restores the accepted section at a boundary, or cancels an audition that has not started.
- **Accept** commits the candidate and queues its playback. Cancelling that launch does not undo the accepted project edit; use Undo after stopping to revert the edit.
- **Discard** cancels a queued audition or returns a playing audition to accepted material at a beat boundary, then removes the candidate.
- **Cancel preparation** prevents an unfinished result from becoming a candidate. Superseded requests cannot publish stale results.

Starting another preparation cancels a queued audition but leaves an already-playing audition active. Return, Discard or another launch changes what is playing. Selecting another section cancels a queued audition or returns a playing audition to accepted material at the next beat.

Live candidate playback requires the selected, playing section without recording armed. Arrangement playback can continue while a candidate is prepared, but live replacement of an arrangement is not supported. Direct note editing and history operations still require Stop.

Generation is serialized per session. Cancellation returns control promptly; an already-running algorithm may finish computing, but its result is ignored and the next computation waits for it. Generation never runs on the transport worker.

## Note editing

Use Shift-click to extend selection, Ctrl+A to select all, and dragging empty space to select an area. Group moves preserve relative timing and pitch while respecting section and MIDI bounds. Copy/paste and duplication assign new note IDs. A group operation is one undo step.

Deleting notes makes the pattern Fixed so regeneration cannot silently restore deleted notes. Convert the pattern explicitly before generating it again. Other edits preserve the recipe and protect edited material with locks.

## Headless variation

```sh
dotnet run --project src/MidiWorkbench.Cli -- vary song.json section-1 42 variation.json
```

The same section and seed reproduce the same material. Configurable parts receive deterministic per-part seeds, including parts whose previous recipe had an explicit seed. Fixed material remains unchanged. Previewing or varying never starts MIDI output on its own.

Performance capture can remain in a DAW or MusicSessionTrace. Existing built-in MIDI recording is available for phrase development; durable capture expansion is deferred.
