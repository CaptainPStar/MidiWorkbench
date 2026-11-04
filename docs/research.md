# Research and design references

Research consolidated 2026-11-04. Manufacturer/developer documentation establishes advertised behavior; no competing device or application was physically tested. Design implications below are proposals.

| Reference | Relevant capability | Application |
|---|---|---|
| OXI ONE MKII | Stochastic probabilities, phrase lock/capture, multiple sequencer modes | Generate → inspect → lock → edit; show rules separately from resulting notes |
| Torso T-1 | Parametric rhythm/harmony, cycles, temporary controls | Play density/motion/repetition with a small set of controls |
| Squarp Hapax | Independent patterns, sections/songs, non-destructive MIDI effects | Shared processing for live, recorded and generated sources |
| Elektron | Per-step parameter locks and trigger conditions | Short patterns with structured multi-pass development |
| Midicake ARP | Coordinated parametric arpeggiators | Repeatable complexity and shared harmony |
| NDLR | Drone, pad and motif roles | Role presets without fixed track-count limits |
| KeyStep Pro | Keyboard capture and step editing | Fast movement between playing and editing |
| Turing Machine | Evolving or locked random loops | Preserve useful discoveries before developing them |
| Ableton Live | MIDI generation/transforms, Capture, Follow Actions | Retrospective capture and a consistent creation/arrangement workflow |
| Bitwig | Note effects and modular Note Grid | Composable processing, harmony and expression |
| Playtime / ReaLearn | Clip recording/launching; controller mapping and feedback | Cancellable launches, controller integration, explicit timing policies |
| HY sequencers | Focused analog-style and multitrack sequencing | Compact editors with progressive disclosure |
| Numerology | Modular sequencing and modulation | Separate rhythm/pitch/modulation for advanced use |
| Fugue Machine Rubato | Multiple playheads and automation | Reinterpret a shared motif |
| Strudel | Composable patterns and MIDI I/O | Optional later programmable generation, away from scheduler |
| Cyma Forma RND | Coordinated generated instruments/sequences and part takeover | Immediate multi-part audition with selective locks and recall |

## Cyma Forma RND

Manufacturer manual at firmware 1.4 lists subtractive, FM, acid, noise, speech, Karplus–Strong, supersaw and additive synthesis, up to four instruments, Euclidean sequences, loop/drone/one-shot patch types and MIDI takeover. USB can output generated sequences. RND Pixels supports seed-based sound recall and sharing; older descriptions of permanently losing every patch do not describe the full current workflow.

Borrow coherent multi-part generation, retained constraints, part takeover and immediate audition. Do not add synthesis. Optional future device profiles can map sound parameters to MIDI. A MIDI-only app does not know the acoustic result; user audition remains authoritative. The RND manual explicitly says drone patches do not output MIDI sequences, so MIDI export must not be assumed to reproduce all internal sound behavior.

## Sources

- https://docs.oxiinstruments.com/oxi-one-mkii-manual/stochastic-mode/
- https://docs.oxiinstruments.com/oxi-one-mkii-manual/matriceal-mode/
- https://docs.torsoelectronics.com/t1/what-is-the-t1/t1-overview/
- https://squarp.net/hapax/manual/modepattern/
- https://squarp.net/hapax/manual/modefx/
- https://www.elektron.se/wp-content/uploads/2025/06/Digitakt-2-User-Manual_ENG_OS1.15_250625.pdf
- https://manuals.midicake.com/arp/introduction-to-arp/
- https://manuals.midicake.com/arp/chords-and-scales/
- https://conductivelabs.com/wp-content/uploads/2020/02/The-NDLR-User-Manual-v1.7.pdf
- https://dl.arturia.net/products/keystep-pro/manual/keystep-pro_Manual_2_5_2_EN.pdf
- https://www.musicthing.co.uk/Turing-Machine/
- https://www.ableton.com/en/live-manual/12/midi-tools/
- https://www.ableton.com/en/manual/recording-new-clips/
- https://www.ableton.com/en/live-manual/11/launching-clips/
- https://www.bitwig.com/userguide/latest/note_fx/
- https://www.bitwig.com/userguide/latest/the_grid_devices/
- https://www.helgoboss.org/projects/playtime
- https://www.helgoboss.org/projects/realearn
- https://hy-plugins.com/product/hy-seq16x3/
- https://hy-plugins.com/hy-rpe2release/
- https://files.five12.com/Numerology4Manual.pdf
- https://alexandernaut.com/fugue-machine-rubato/manual/
- https://strudel.cc/learn/input-output/
- https://www.cymaforma.com/pages/rnd-synth-manual
- https://www.cymaforma.com/pages/rnd-pixels
- https://www.cymaforma.com/pages/rnd-synth-v1-4-release-note

## Design tests

Compare unconstrained randomness, weighted generation and protected-motif mutation over the same section. Count useful retained ideas and observe how much editing each requires. Prototype scene changes with keyboard/controller while playing. Measure MIDI scheduling independently from UI convenience and subjective musical usefulness.
