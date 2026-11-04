# Project model

## Project schema 3

`SongProject` owns a stable ID, name, quarter-note BPM, sections and an optional arrangement. Arrangement entries reference section IDs and repeat counts; an empty arrangement uses section order. Projects migrate from schemas 1 and 2 on load. Unknown schemas and unknown JSON properties fail visibly.

Each section embeds an outline and parts. The outline specifies meter, bars, root, scale, density, intensity, variation and seed. Parts own logical destinations, one-based channels, mute/solo flags and a pattern. Physical endpoints never belong to the project.

Patterns are Fixed, Generative or Hybrid. Notes have stable IDs, tick/duration, pitch/velocity and a lock. Added and recorded notes are locked. Deleting a note freezes its pattern to preserve the deletion. The pattern-kind control explicitly converts it back when regeneration is wanted. Undo/redo covers accepted project edits.

Recipes retain generator ID/version, PRNG, seed and settings. Configurable settings include source-note snapshots, step controls, pitch range, gate, probability, ratchets, swing, chord progression and meter grouping. Accepted notes are persisted: reopening does not require the generator or recompute notes. Unknown recipe versions cannot regenerate silently. Legacy drummer recipes retain the original algorithm; new grouped recipes preserve their version separately.

Generation candidates are separate section snapshots tied to the source project. Audition does not accept a candidate. Acceptance validates the revision and creates one undoable change. Scoped generation leaves other parts and protected material alone.

## Rig schema 1

The local rig stores endpoints, clock toggles, delay (0–100 ms), destination routes, optional channel overrides and keyboard input. Clock-only endpoints work without a musical route. Project-wide resolution gives section launches a stable port/destination map. Editing a project or opening a bank never implicitly changes the rig.

WinMM endpoint IDs use name plus occurrence because that API does not supply stable identities. Identical-device reordering and renamed devices require checking the rig.

## Bank schema 1

A `SongBank` holds independent project snapshots and an ordered list of bank-song IDs. The order can repeat songs. Add current snapshots the project; Update explicitly replaces a selected snapshot. Opening a bank song does not start it. Nested projects migrate through the normal project loader.

## Take schema 2

A `PerformanceTake` preserves separate input MIDI, submitted output MIDI and actions, with initial tempo, tempo changes and a completeness flag. Optional meter and project identity metadata preserve compatibility with older schema-2 takes. Input time is host arrival; immediate output time is submission, and scheduled output time is its requested delivery deadline. These are not acoustic onset measurements.

Thru is captured after channel routing with a logical destination. Cancelled future output is removed when cancellation succeeds. Input loss and bounded capture/journal exhaustion invalidate completeness. Ordinary Stop waits for pending releases before a completed take is archived. Completed takes can be persisted automatically and recovered on startup; active recording is still bounded in memory.

Take replay uses captured output timing, including controller and clock messages. Musical destinations resolve through the current rig. Endpoint-only output needs an unambiguous binding or explicit mapping; clock-only outputs remain independent of musical routes. Inputs remain a separate source stream, not a second copy of replayed thru.

SMF type-1 export uses 960 PPQ. Song export preserves arrangement repeats and meter changes. Take export converts captured times through the tempo map and includes separate input and output tracks. Select the desired tracks in a DAW to avoid playing the same keyboard performance twice.

## Extension boundaries

Sections currently embed their own parts; project-wide shared pattern pools and arbitrary per-track clip scenes need a later schema. Deletion tombstones, development curves and richer live processors are later work. Device profiles for CC/NRPN/SysEx and external clock following remain optional extensions. Synth patch recall is not required.
