# Implementation status and next gates

## Implemented application path

Outline → prepare candidate → audition → accept/edit → arrange → perform → capture → save/reopen/replay.

Keyboard → loop recording → locked phrase → scoped variation of other parts → song/bank recall.

The implementation includes project migrations, dedicated transport, WinMM adapter, port routing, clock-only outputs, drummer and seven configurable generators, multi-note piano roll/drum editing, undo/redo, arrangement repeats, quantized section launches, live section mute/solo, take replay and bank/setlist persistence. Background generation, seeded variation and live audition/return/acceptance support experimentation while a section plays. Automated and desktop validation is recorded in [status.md](status.md).

## Hardware release gate

Windows execution and four-port virtualMIDI delivery/cleanup are now validated. The physical MIDI interfaces and external instruments still need timing, sustained-note, Stop/Panic, disconnect/reconnect and long-session acceptance. The observed virtual clock/skew outliers remain documented limits; no live-performance timing bound is promised. See [Windows runtime evidence](reports/windows-runtime-2026-11-04.md).

## Current priority: generation and orchestration

Background candidate preparation, explicit live audition/return/acceptance at musical boundaries, reproducible variation and efficient note editing come before expanding performance capture. A DAW or MusicSessionTrace can record MIDI output.

## Remaining recording and editing work

- Deferred: streaming active takes with crash recovery; external performance capture takes priority over building a second recorder.
- Count-in, retrospective capture and dedicated keyboard step entry.
- Cross-loop note ties, take comping and nondestructive quantization.
- Deletion tombstones and richer conditional-note controls; multi-note editing is implemented and tested.
- Direct editing during playback and candidate replacement across a full arrangement; current live acceptance is section-scoped.

## Broader composition and performance

- Shared project-wide pattern pools and per-track clip scenes.
- Development curves, coordinated density budgets, protected motifs and alternative comparisons.
- Controller learn, relative encoders, pickup, macro banks and feedback.
- Seamless multi-song setlist transitions and programmable processors.

## Optional extensions

Device parameter profiles and CC/NRPN/SysEx curves; external MIDI clock following and loss policy; Windows MIDI Services; Link; external MusicSessionTrace analysis feeds. Synthesis and audio recording remain out of scope.
