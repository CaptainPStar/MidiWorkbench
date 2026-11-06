# Minilogue xd first-use acceptance - 2026-11-06

The user confirmed audible playback and clean note release on a Korg minilogue xd connected to Studio 24c MIDI OUT, receiving on channel 1. Audio was monitored through Studio 24c input 2. The user also confirmed the packaged desktop Open -> Routing -> Play workflow worked.

## Accepted checks

- Eight-second phrase: four single notes, C major, D minor, held C, silence. Eleven note pairs at velocity 64, no more than three concurrent notes, clock disabled. Listener: "Yes - sequence audible and ended cleanly."
- Stop and Panic: separate held triads released after about three seconds, before their scheduled 6.25-second release. Listener: "Both released cleanly." Independent Sol review verified matching pitches 60/64/67 and balanced captured ownership. Captures precede fallback cleanup.
- Native desktop: packaged revision `950e97fc0b7b2455ed488a689c272db310d66e36`, dedicated rehearsal profile. User opened the validated project with the normal file picker, applied the Studio 24c output/channel route and played it. User response: "Worked."

The user additionally confirmed the native edit -> Save as -> reopen -> playback check worked: the requested pitch/length edit survived reopening and played as expected. The chosen saved file has not been independently inspected.

The user also confirmed looping playback, the native Stop button and F8 Panic: "Worked. Both stopped cleanly, no hanging notes." This adds attended native-control acceptance to the earlier engine-driven cleanup checks.

The first cleanup harness attempt called Capture during playback, hit the expected guard and ran finally-Panic cleanup. It is excluded from acceptance. The corrected harness reads the stopped capture and checks pre-command events retrospectively. No production changes were needed.

## Scope and remaining work

These are listener-confirmed outcomes for the current patch and setup. They do not measure acoustic latency or establish general musical usefulness. Live section transitions, actual keyboard recording, external clock synchronization, physical unplug/reconnect, long sessions and DAW import remain to be exercised on the intended setup. Stop/Panic were first exercised through an artifact harness using the application engine, then independently confirmed by the user through the native Stop button and F8 during looping playback.

Local evidence and the loadable fixture are in `artifacts/minilogue-listening-20261106/`: `first-sound.json`, `first-sound-run.json`, `cleanup-run.json`, `desktop-feedback.json`, harness and listening-status notes. The actual audio connector and synth patch were not visually inspected. This report supplements the earlier physical cable-loop measurements; it does not replace their timing evidence.
