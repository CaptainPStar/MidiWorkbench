# Implementation status and next gates

## Implemented application path

Outline → prepare candidate → audition → accept/edit → arrange → perform → capture → save/reopen/replay.

Keyboard → loop recording → locked phrase → scoped variation of other parts → song/bank recall.

The implementation includes project migrations, dedicated transport, WinMM adapter, port routing, clock-only outputs, drummer and seven configurable generators, piano roll/drum grid, undo/redo, arrangement repeats, quantized section launches, live section mute/solo, take replay and bank/setlist persistence. Automated and desktop validation is recorded in [status.md](status.md).

## Hardware release gate

Windows execution, real MIDI interfaces and external instruments must validate timing, sustained notes, Stop/Panic, disconnect/reconnect and long-session capture. Cross-publishing a Windows executable is not this acceptance test. Measure clock/notes under UI and CPU load before promising live-performance timing bounds.

## Remaining recording and editing work

- Streaming active takes with crash recovery, rather than bounded memory plus completed-take archives.
- Count-in, retrospective capture and dedicated keyboard step entry.
- Cross-loop note ties, take comping and nondestructive quantization.
- Deletion tombstones, multi-note editing and richer conditional-note controls.
- Asynchronous candidate preparation and acceptance during playback; current generation/editing requires Stop.

## Broader composition and performance

- Shared project-wide pattern pools and per-track clip scenes.
- Development curves, coordinated density budgets, protected motifs and alternative comparisons.
- Controller learn, relative encoders, pickup, macro banks and feedback.
- Seamless multi-song setlist transitions and programmable processors.

## Optional extensions

Device parameter profiles and CC/NRPN/SysEx curves; external MIDI clock following and loss policy; Windows MIDI Services; Link; external MusicSessionTrace analysis feeds. Synthesis and audio recording remain out of scope.
