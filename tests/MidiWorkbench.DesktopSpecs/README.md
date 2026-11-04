# Offscreen desktop workflow checks

Run from the repository root:

```powershell
dotnet run --project tests/MidiWorkbench.DesktopSpecs -c Release
```

An optional first argument selects the evidence output directory. The default is `artifacts/desktop-ux`.

The executable uses Avalonia.Headless with the real Skia renderer and application theme. It opens no native windows and sends no operating-system input. Pointer events are delivered at rendered control centers; shortcuts use the headless input backend. Every run uses a fresh settings directory, so existing rigs, layouts and projects remain separate.

Checks cover initial clean state and simulation status; action visibility at 1000×650; preview, audition, acceptance, undo and redo; live audition and return at a beat boundary; transport shortcuts; saving/reloading; dirty-close cancellation; lossless beat-field edits; route layout and draft preservation during endpoint refresh; and overlapping note paint/hit order. A 100,000-note viewport render benchmark records index creation, median and maximum frame times. It writes PNGs at 1000×650 and 1380×900, a save-decision image, control bounds and a pass/fail report. The executable returns a failing exit code if any assertion fails.

Additional draft-protection checks verify that clean accepted projects protect unaccepted previews and outline settings on close, cancellation retains the draft, ordinary tempo/Stop reloads retain settings, preview note-edit actions are disabled, and adding a destination retains unapplied routing fields.

A resize regression starts a stopped drum editor in a short viewport, expands it, and checks a lower lane's rendered pixels. This catches stale viewport culling that can leave newly exposed rows blank until another edit or playback.

The save check supplies an already-chosen path through test-only reflection, then uses Ctrl+S and independently reloads the saved file. Native file pickers are outside this harness. Reflection also accesses the existing session and controls for assertions and drives the normal transport refresh method because the harness has no native dispatcher main loop. Production code has no test hooks.

These checks supplement the portable specification suite. They do not certify native Windows input, monitor scaling, physical MIDI timing or instrument output. Inspect the generated images for visual hierarchy, clipping and contrast after layout changes.
