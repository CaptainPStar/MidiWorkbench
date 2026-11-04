# Synth and MIDI sequencer workflow references

Independent primary-source research, 2026-11-04. This document records equipment workflows and evaluation questions; it contains no MidiWorkbench verdict. Product differences are context, not requirements to reproduce every hardware feature. No acoustic or hardware acceptance is established by reading manuals.

## Sources and observed workflows

### Novation Circuit Tracks user guide

[Using synths, MIDI tracks and drums](https://userguides.novationmusic.com/hc/en-gb/articles/26207128061458-Using-Circuit-Tracks-Synths-MIDI-Tracks-and-Drums), sections “Recording a synth pattern in real time,” “Non-quantised record,” and “Scenes.” Accessed 2026-11-04.

- Establish a drum rhythm or click, rehearse notes against it, then enable Record when ready. The recording cursor changes colour. Recording can be toggled without stopping playback.
- Recorded notes replay on the next pass. Quantised and finer micro-step capture are selectable. Manual editing provides correction after playing.
- Scenes store coordinated pattern chains. Scene assignment and selection are separate operations. A queued scene has distinct feedback and starts at the relevant pattern boundary without losing synchronisation.
- Scene copies support related arrangements while preserving an earlier version. Scene chains turn these units into a longer arrangement.

[Circuit Tracks appendix](https://userguides.novationmusic.com/hc/en-gb/articles/25494477003154-Circuit-Tracks-appendix), “MIDI channels,” “MIDI I/O,” “Clock settings,” and “MIDI thru configuration.” Accessed 2026-11-04.

- Track MIDI channels are assigned independently of physical connections. The four drum tracks share a channel; instrument tracks have separate defaults.
- Note, CC, Program Change, and Clock transmission/reception have independent settings. Receiving notes does not itself prove clock configuration.
- Disabling clock receive selects internal timing. Automatic clock receive follows an available external source, otherwise internal time. Clock transmit separately controls whether clock reaches the rest of the rig.
- The Thru connector can pass received data or duplicate the unit's output. Those topologies behave differently and must be understood when connecting multiple devices.

### Arturia KeyStep Pro support and manual

[KeyStep Pro MIDI](https://support.arturia.com/hc/en-us/articles/4405741095570-KeyStep-Pro-MIDI), “Can I change the tempo of an external synth.” Accessed 2026-11-04.

- Sending clock is an explicit setting; the receiving synthesizer must also be configured to accept external clock. A working note route alone does not establish synchronisation.

[KeyStep Pro Patterns](https://support.arturia.com/hc/en-us/articles/4405748127250-KeyStep-Pro-Patterns), “Can I undo Real-time recording,” “Can I edit the velocity values of individual notes in a chord,” and “Is there a simple way to create a pitch/gate/velocity pattern.” Accessed 2026-11-04.

- Real-time recording loops and initially overdubs. The documented recovery workflow clears or edits offending steps, or records over them with overdub disabled; real-time recording has no undo.
- Individual chord notes expose gate, velocity, pitch and timing controls. Copying an articulation pattern to other steps reduces repetitive work.
- Mono recording limits each step to one note and disables overdub, illustrating that bass-line capture and chord capture may need different collision semantics.

[KeyStep Pro Arranging / Transposing](https://support.arturia.com/hc/en-us/articles/4405741093394-KeyStep-Pro-Arranging-Transposing), “Can I edit the order of patterns in a chain once the chain is created,” and “Can I mix playback of patterns of different tracks in a chain.” Accessed 2026-11-04.

- Chains belong to individual tracks. Chain order is recreated rather than edited. This is a concrete example of the distinction between per-part progression and coordinated scene progression.

[KeyStep Pro manual, revision 1.3](https://downloads.arturia.net/products/keystep-pro/manual/keystep-pro_Manual_1_3_EN.pdf), section 5.4.3.2, printed page 74. Search-index excerpt inspected; full document fetch exceeded browser limit. Do not treat this as verification of the latest firmware.

- Record plus Play captures notes into the current loop. Quantisation directly changes capture placement, and disabling it retains more played irregularity. Drum capture only accepts notes corresponding to the configured drum mapping.

### Novation Peak user guide and hardware guidance

[Peak in detail](https://userguides.novationmusic.com/hc/en-gb/articles/25494693870354-Novation-Peak-in-detail), “Clock source,” “Assign MIDI channel,” “Arp MIDI mode,” and “Patch select.” Accessed 2026-11-04.

- Incoming channel assignment must match the sender. USB and DIN clock sources can be selected explicitly; clock loss behaviour depends on mode. Some modes retain the last observed tempo.
- External clock drives tempo-synchronised arpeggio, delay, and LFO functions. The guide notes that many sequencers send clock only while recording or playing.
- Arpeggiator output can additionally transmit generated notes, affecting feedback-loop risks when its output is returned through a sequencer.
- Program/bank-change handling is independently configurable. A project recalling MIDI notes does not imply that the instrument's current patch is correct.

[Local control and MIDI output on Novation synths](https://support.novationmusic.com/hc/en-gb/articles/207563085-Local-control-and-MIDI-output-on-Novation-Synths). Accessed 2026-11-04.

- Local Control determines whether a synthesizer's own keyboard/controls operate its internal sound engine. With Local disabled, a sequencer return route is needed to sound that instrument from its keyboard. Thru monitoring therefore has a different purpose from capture arm.

## Neutral workflow questions for future evaluation

1. Can a player establish port, channel, clock direction, and monitoring before committing musical material? Is silence diagnosable at those distinct layers?
2. Can the player rehearse a phrase, identify the recording target, capture it, hear the following pass, and correct a mistake without losing useful material?
3. Can a known-good phrase or whole section remain available while a related variation is prepared and compared? Are temporary audition and committed edits distinguishable?
4. Can the player tell which material is selected, sounding, queued, armed, or unsaved? Does a transition preserve its intended musical boundary?
5. Can short patterns become a deliberate arrangement with repeats and transitions, then be recalled or exported? Does recall make clear what still depends on the physical rig?
6. Do note duration, velocity, voicing, register, drum mapping, timing, and loop-edge behaviour support the intended musical role? Structural MIDI validity cannot establish subjective musical quality or audible timing.

These questions are derived from the primary workflows above. They are review prompts, not claims that a particular control layout or hardware interaction must be copied.
