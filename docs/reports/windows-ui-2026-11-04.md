# Windows interface validation, 2026-11-04

The desktop application was built and launched on Windows 10 x64 with .NET SDK 10.0.401. Its native accessibility tree exposed transport, outline, note editing and docking controls. Windows graphics capture timed out twice. The user subsequently stopped OS UI automation with Escape; no further OS input was sent.

## Reproducible desktop checks

`tests/MidiWorkbench.DesktopSpecs` uses Avalonia.Headless 12.1.4 with Skia rendering, actual offscreen pointer/keyboard input, and a unique settings directory for every run. It exercises the production MainWindow. The headless host has no native dispatcher main loop, so its event pump explicitly calls the production transport refresh method in addition to processing dispatcher jobs. This corrected an earlier test timeout where Return's enabled state was stale. It was not a transport Return failure.

Coverage includes preview isolation, audition, live audition/return, acceptance, undo/redo, project save/reload, Stop/Panic shortcuts, unsaved close cancellation and safe focus. It checks pinned generation controls at 1000×650, routing widths and nonoverlap, pinned Apply routing, and preservation of output/channel/clock/delay/input drafts during endpoint refresh. Note fields retain exact 1-, 7- and 319-tick positions/durations after applying their beat display, and a quarter-beat change commits 240 ticks. Pixel checks and pointer selection agree on the topmost overlapping note after viewport indexing.

Run `scripts/verify.ps1` for the full build, core specifications and desktop workflow checks. Final verification passes 53/53 core groups and 76 desktop assertions, with zero Release build warnings/errors. Exact checks are emitted in `artifacts/desktop-ux/results.txt`. Images cover initial, preview, edited, routing and unsaved-dialog states at 1000×650 and 1380×900. Artifacts are ignored build outputs.

## Design review and changes

Independent design and UX agents reviewed source and rendered states. Preview/Audition/Accept remain visible outside the scrolling settings area; unused candidate controls are hidden until relevant. Generator fields are shown only for the selected algorithm. MIDI routing has a persistent status bar and readable stacked destination fields with a pinned Apply action. Hot-plug refresh preserves unapplied settings. Output selection never automatically assigns a physical instrument.

The interface distinguishes saved/accepted material, unaccepted previews, queued playback and active playback. Saving and closing guard accepted project/bank changes; closing or opening also warns about unaccepted previews and section settings. Note editing is disabled while a preview is displayed. A restrained dark theme uses teal for primary actions and muted red for Panic. Note names accompany MIDI pitches; note positions/durations use quarter-note beats. Density, intensity and variation expose numerical values.

## Rendering measurement

A distributed 100,000-note pattern across 256 bars was measured at 1000×650, scale 1. The final run built the cached note index in 73.19 ms; 30 invalidated viewport frames took a median 6.43 ms and maximum 15.26 ms. The exact latest run is in `artifacts/desktop-ux/editor-benchmark.txt`. The cached time index avoids scanning every stored note on each playhead frame; painting preserves authored stacking order.

This is an offscreen viewport measurement, not native desktop frame timing, an all-notes-visible worst case, high-DPI acceptance or physical MIDI timing. Human workflow testing on the intended synth setup and manual docking/multi-monitor checks remain outstanding.

## Design basis

Keyboard shortcuts retain standard Windows conventions and leave text editing to text controls; focus starts on Cancel in destructive close decisions. See [Microsoft keyboard interaction guidance](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions). Preview selection remains separate from playback; queued changes expose their boundary state, informed by [Ableton's clip launch quantization model](https://www.ableton.com/en/live-manual/12/launching-clips/). These are design references, not claims of equivalent feature coverage.
