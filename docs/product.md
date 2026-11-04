# Product and requirements

The application controls external instruments through MIDI and provides a dependable clock. It supports live performance, jamming, song development and MIDI studio capture. It does not produce or record audio.

## Confirmed scope

- .NET 10; independent application with reusable components from MusicSessionTrace considered individually.
- Both outline-led composition and performance-led discovery are optional, compatible workflows.
- Stable timing, meter-aware beat display, MIDI routing, recording and playback.
- Synthetic drummer, configurable sequencers, arpeggiators and stochastic generators.
- Pattern/beat editing and phrase capture from a master keyboard.
- Songs, banks, sections, variations and reproducible recall.
- Rearrangeable docking layout.
- Device-specific MIDI parameter mapping and filter/controller variation are optional later features.
- No requirement to capture synth changes that are not transmitted over MIDI. No synthesis engines, audio drivers or plug-in hosting.

## Workflows

Outline-led: outline → generate → play/perform → vary/edit → capture → recall.
Performance-led: play → capture/vary → develop arrangement → perform → recall.

A captured phrase can become a motif or section. A generated section can be freely performed. There are no incompatible project modes.

An outline specifies duration, meter, root/scale, optional harmonic progression, roles, density, intensity, variation, development curves and constraints. The planner prepares coordinated patterns, motif variations and fills ahead of playback. Users audition through their external synths, edit notes/arrangement, and keep useful versions. Background preparation must not block the transport. Generated proposals remain separate from the accepted arrangement until committed.

Density controls event/part activity. Intensity controls accents, velocity and mapped expression. Variation controls departure from existing material. These must not collapse into one randomize control. Pitch, rhythm, articulation and orchestration eventually have independent variation amounts and locks.

## Requirements and priorities

| ID | Requirement | Priority |
|---|---|---|
| CLK-01 | One monotonic host clock and authoritative musical timeline | Foundation |
| CLK-02 | Tempo changes preserve musical position; meter/grouping are explicit | Foundation |
| CLK-03 | External clock output is independent of UI refresh | Foundation |
| MIDI-01 | Logical track destinations resolve through a local rig | First hardware milestone |
| MIDI-02 | Owned note release on stop, replacement, faults and disconnect | First hardware milestone |
| MIDI-03 | External MIDI clock following with explicit lost-clock policy | Later |
| GEN-01 | Seeded, repeatable section generation outside scheduler | Foundation |
| GEN-02 | Locked edits and fixed material survive regeneration | Foundation |
| GEN-03 | Step, drum, arpeggiator, Euclidean, stochastic and chord generators | Incremental |
| GEN-04 | Scoped regeneration with alternatives, constraints and provenance | Composition milestone |
| GEN-05 | Existing synthetic drummer integrated as a shared-timeline consumer | Early |
| EDIT-01 | Notes, drum lanes, gate, velocity, probability, microtiming and conditions | Editor milestone |
| EDIT-02 | Input capture, overdub, retrospective capture and step entry | Recording milestone |
| ARR-01 | Independent patterns, scenes, sections, song order and setlists | Arrangement milestone |
| ARR-02 | Quantized launches, cancellable queued actions and explicit note-tail policy | Arrangement milestone |
| CAP-01 | Preserve input MIDI, output MIDI and performance actions separately | Recording milestone |
| CAP-02 | Bound capture queues and report incomplete captures | Foundation |
| SAVE-01 | Versioned projects; atomic publication; unsupported versions rejected | Foundation |
| UX-01 | Docking, persistent transport, meter-aware display and controller access | Incremental |
| UX-02 | Selecting/editing never accidentally launches a pattern | Foundation |
| CTRL-01 | Device profiles, MIDI CC/NRPN/SysEx mapping and parameter curves | Optional later |

## Generation vocabulary

Probability, weighted pitch choice, conditional events, random walks, mutation, Euclidean rhythm, independent lane lengths, phrase grammar, voice leading and deterministic seeds. Polymeter and polyrhythm must have distinct controls.

Generators and MIDI transformations are separate. An arpeggiator can transform keyboard chords or a recorded chord pattern. Fixed and generated sources use the same transpose, scale, velocity, gate, echo, ratchet and groove processors.

## Performance controls

Per-track mute, input arm, pattern selection and a few assigned macros. Scene macros can coordinate density, motion, register, variation and fills across parts. Temporary changes should revert on release or commit explicitly. Stop and Panic have distinct semantics; ordinary stop follows defined tail policy, Panic releases owned notes immediately.

Controllers: learn, relative encoder modes, absolute knob pickup, context banks and feedback where supported. Hardware capability discovery must precede device-specific promises.

## Creative backlog

Protected motifs; arrangement-wide density budgets; related variation branches; phrase-aware transitions; rhythmic borrowing; multiple playheads; rehearsal/stage control sets; retrospective section bookmarks. Treat these as proposed experiments, not committed first-release features.

## MIDI-only adaptation

Adaptation uses MIDI input, explicit controls, arrangement state or a future external analysis feed. The new app does not hear guitar or synth audio. Audio analysis remains outside its scope. LLMs and network services are not required for generation or live operation.
