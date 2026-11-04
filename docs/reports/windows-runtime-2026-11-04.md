# Windows runtime and virtual MIDI validation, 2026-11-04

Executed on Windows 10.0.19045 using .NET 10. Only the explicitly approved virtualMIDI ports Drum, Pad1, Pad2 and Lead received output. Studio 24c and Microsoft GS Wavetable Synth were enumerated but never opened for output by this validation.

## Safety defects corrected

- Generated/replayed sustain now has an explicit release on Stop, Panic and outgoing section transitions. Sustain ownership is shared between generated and keyboard sources, so stopping generated playback preserves a live keyboard owner's pedal.
- Panic during the pending tail of an earlier Stop releases notes actually submitted to the port. Clearing planned ownership during Stop previously allowed cancellation of the only remaining release.
- Disposal cancels scheduled output and performs immediate cleanup. Repeated disposal is safe and disposed hosts reject new commands.
- An input backlog containing attacks is discarded before Panic cleanup. Input overflow with thru armed releases held keyboard notes and sustain, then accepts fresh input. Input loss remains visible in take completeness.
- Failed thru ownership is cleared when the old port is disarmed or replaced; reopening the same endpoint can play the same pitches again.
- Pre-start input is excluded from take capture, avoiding negative timestamps. Invalid Start tempo is rejected before changing the current take.
- Input ports expose fault state. The WinMM watcher marks removed inputs; the transport worker observes that state without UI polling, stops playback, releases notes/pedals and marks capture incomplete. The session archives the incomplete take, disposes the lost input, disarms recording and reopens the configured input on rearm or a new start. An offline unarmed keyboard does not block output playback: reopening is attempted, a named notice reports failure, and capture metadata reflects the actually opened input. Arming recording still requires its input.
- Transport status exposes the actual queued plan identity, clearing it on cancellation or activation.

`BackendSafetySpecs` covers these cases with deterministic adapter doubles, plus a background session input-loss/rearm/reopen regression. The complete portable specification executable passed 53/53 groups after the input lifecycle changes. Physical removal was not performed; the disconnect regression injects adapter fault state.

## Native virtual MIDI acceptance

The manual probe has a fixed endpoint allowlist and requires explicit opt-in. It is intentionally separate from normal verification:

```powershell
dotnet run --project tests/MidiWorkbench.WindowsProbe -c Release -- --approved-virtual artifacts/windows-validation/virtual-midi-idle60.json 60
```

The 60-second runs use 16-note chords at eighth-note spacing on four independent outputs at 120 BPM. Each output delivered 3,840 attacks, 3,840 corresponding releases and 2,880 clock pulses. Every note matched the submitted channel, pitch and velocity sequence. Both runs had zero late attacks, skipped clocks, unmatched releases or held notes, and complete capture. Each run also tested disposal during an active 16-note chord with sustain held: all four outputs received 16 releases and sustain-off, then each native output was closed and reopened successfully.

| Virtual port | Idle arrival median / p99 / max, ms | Load-window arrival median / p99 / max, ms |
|---|---:|---:|
| Drum | 0.273 / 0.577 / 17.730 | 0.281 / 0.525 / 16.962 |
| Pad1 | 0.663 / 1.212 / 18.155 | 0.666 / 4.158 / 17.337 |
| Pad2 | 1.048 / 1.951 / 18.556 | 1.052 / 3.115 / 17.708 |
| Lead | 1.433 / 2.419 / 19.264 | 1.431 / 4.472 / 18.403 |

Absolute clock spacing error p99 ranged from 1.770 to 2.624 ms idle and 1.794 to 2.838 ms in the load window. The respective worst samples were 11.847 ms and 26.783 ms. Inter-port clock arrival skew median/p99/max was 0.063/1.374/10.135 ms idle and 0.069/1.400/26.972 ms in the load window.

The load window overlapped repeated headless Avalonia rendering, screenshots, pointer/keyboard editing and generation/audition/accept workflows, and the complete portable specification run. The UX worker completed about five full 28-check repetitions before a later live Return timeout; this was not an uninterrupted 60-second saturation workload. That timeout was traced to the headless harness omitting the dispatcher's periodic transport refresh; the harness was corrected to pump that refresh explicitly. Subsequent full verification passed 53 portable specification groups and the expanded desktop suite; see the [UI report](windows-ui-2026-11-04.md). The timing run's workload description remains limited to the actual overlapping repetitions.

Raw reports are under `artifacts/windows-validation/`: `virtual-midi-idle60.json`, `virtual-midi-load60.json`, `virtual-midi-final.json` and `virtual-midi-startup-diagnostic.json`. Artifacts are local ignored outputs, not checked-in source.

## Timing interpretation and remaining limits

Arrival latency measures the difference between host callback arrival and the captured scheduler submission/deadline timestamp. It includes managed worker processing and serialization of simultaneous messages; it does not isolate the native driver's transit time. A short diagnostic localized its largest note delays to the first chord: first-chord maxima 13.278–14.804 ms, while p99 after that chord was 0.484/0.892/1.301/1.731 ms for Drum/Pad1/Pad2/Lead. This supports a startup-path explanation, but does not establish which runtime or driver activity caused it.

The 26.972 ms inter-port skew outlier under load remains a real measured limit. Count and ownership checks pass; tight physical timing acceptance has not been established. No acoustic timing, physical cable loop, external synth, unplug/reconnect, sleep/resume, hour-long run or other driver/interface acceptance was performed. WinMM remains immediate-send, with name-plus-occurrence endpoint identity and two-second removal polling.

The input close path follows the native stop/reset/close lifecycle. Microsoft documents that these calls wait for outstanding callbacks; callbacks here only timestamp/enqueue and do not call input lifecycle functions. See [Microsoft's WinMM callback guidance](https://microsoft.github.io/MIDI/kb/porting-midi-libraries/). Emergency sustain cleanup is explicit; [Microsoft's reset documentation](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-midioutreset) likewise identifies sustain-off in addition to note-offs as necessary cleanup.
