# Project model

## Implemented schema 2

SongProject → Sections → Parts → Pattern → MidiNotes. Outline owns meter, bars, scale, root, density, intensity, variation and seed. Each part owns a role, a logical destination and a one-based channel. PatternKind is Fixed, Generative or Hybrid. Notes have stable IDs, tick/duration, pitch/velocity and a lock.

Patterns carry an optional Recipe: generator (`scaffold` or `drummer`), generator version, random algorithm (`xorshift32`), seed and, for the drummer, persona, complexity, fills, swing and half-time. Regeneration keeps a part's generator unless the user selects another.

Schema 1 files migrate on load: `generatorVersion`/`generatorSeed` become a scaffold recipe. Other versions and unknown properties are rejected. `cli upgrade` rewrites a file in schema 2.

Roles Drums, Bass, Pulse and Pad are generated. Phrase parts hold recorded or manual material and are never generated. Pad is a sustained single note and Pulse a scale sequence; neither is a finished chord or arpeggiator generator.

## Rig schema 1

Endpoints (ID, send clock, delay 0–100 ms), routes (destination → endpoint, optional channel override) and an optional input endpoint. Validated on load; unsupported versions are rejected. Machine-local, never part of a song.

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

Manual edits need explicit override records and deletion tombstones. Until these exist, added and recorded notes are locked, changed notes can be locked, and deletion freezes the whole pattern. Warn/document this behavior instead of recreating deleted notes on the next Generate.

Development curves use musical time and typed parameter IDs. Density, intensity and variation are distinct. Optional device mappings translate a parameter into configured CC/NRPN/SysEx messages. No universal filter/FM mapping is assumed. Untransmitted synth changes and synthesis remain out of scope.

## Capture

PerformanceTake schema 2: start time, initial BPM, completeness, outputs (endpoint, destination), inputs (rig input endpoint), action journal (start, stop, end, panic, tempo, arm, fault) and tempo changes. Seconds are submission or host-arrival times from transport start. Thru output is not captured separately.

Saving a take as `.mid` converts times to ticks through the recorded tempo map, one track per destination plus an input track. Section export writes one track per part at the project tempo. Exported files never claim that hardware sounded at the recorded time. Device patches remain optional references, not recall requirements.
