# Project model

## Implemented schema 1

SongProject → Sections → Parts → Pattern → MidiNotes. Outline owns meter, bars, scale, root, density, intensity, variation and seed. Each part owns a logical destination and one-based channel. PatternKind is Fixed, Generative or Hybrid. Notes have stable IDs, tick/duration, pitch/velocity and a lock.

Data uses immutable collections. The compiler produces a plan consumed by one transport owner. Project loading validates musical bounds and refuses unsupported schema versions. No compatibility or migration promise is made for pre-release schema 1.

The demonstration generator creates four named roles. Its Pad is a sustained single note, not a completed chord/voice-leading generator. Drums are a simple seeded grid, not the MusicSessionTrace synthetic drummer. Pulse is a scale sequence, not a completed input-driven arpeggiator.

## Planned schema expansion

| Object | Responsibility |
|---|---|
| Rig | Endpoint bindings, controller mappings, timing offsets and device capabilities |
| Track | Stable musical part, input source, processors and output destination |
| Pattern | Fixed notes, a generator recipe or a hybrid with edits/locks |
| Scene | Coordinated pattern selections and macro values |
| Section | Length, harmony/development, scene/material references and transitions |
| Song | Section ordering and optional arrangement timeline |
| Bank / setlist | Reusable collection and performance order |
| Take | Captured inputs, emitted outputs and performance journal |

Track identity should ultimately be project-wide rather than duplicated per section. The compact scaffold embeds parts per section; introduce migration when scene/track references are implemented. Do not silently reinterpret existing IDs.

Recipe metadata: generator ID/version, stable PRNG algorithm/version, seed per part, source motif references, constraints, scope and accepted candidate. A seed alone does not preserve a live performance or guarantee compatibility across algorithm changes.

Manual edits need explicit override records and deletion tombstones. Until these exist, added notes are locked, changed notes can be locked, and deletion freezes the whole pattern. Warn/document this behavior instead of recreating deleted notes on the next Generate.

Development curves use musical time and typed parameter IDs. Density, intensity and variation are distinct. Optional device mappings translate a parameter into configured CC/NRPN/SysEx messages. No universal filter/FM mapping is assumed. Untransmitted synth changes and synthesis remain out of scope.

## Capture

PerformanceTake records timestamped emitted messages in seconds from transport start, initial BPM, tempo-change records and completeness. The current sink is memory only. Input recording and performance-action journals are future streams. Exported captures never claim that the hardware sounded at the recorded submission timestamp.

Separate source input, processed output and commands to avoid feedback or double-processing during replay. SMF export needs a tempo map; preserve wall time and transport gaps explicitly. Store JSON sidecars for events that do not map naturally into SMF. Device patches are optional references, not mandatory recall requirements.
