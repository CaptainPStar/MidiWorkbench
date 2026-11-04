# Windows scheduler diagnosis and timing follow-up, 2026-11-04

This follow-up starts from merged `98c4ebb` and retains the earlier report as historical evidence. Only approved virtualMIDI endpoints Drum, Pad1, Pad2 and Lead received output. No OS input automation or physical MIDI interface was used.

## Reproduced causes and corrections

The earlier callback-arrival metric combined scheduler work with the native adapter and callback path. Probe-only bounded output wrappers now record entry and return times around each adapter `Submit`; musical deadlines are reconstructed independently from the fixed 120 BPM plan. Analysis and CSV writing happen after Stop.

JSON `DeadlineToDispatch` and `ClockDeadlineToDispatch` measure lateness against the planned musical deadline. `DispatchToArrival` measures adapter-entry to callback arrival; `AdapterCall` measures the complete adapter call. Legacy `Latency*` fields instead subtract the message's assigned host timestamp, which can be clamped to the current scheduler iteration. They can understate musical deadline lateness and are not interchangeable with the new deadline metrics. The comparison table below uses the reconstructed planned deadlines.

The instrumented baseline found:

- Note dispatch-to-callback p99 was at most 0.056 ms and maximum at most 0.099 ms. First-chord deadline-to-dispatch delays of 14.10–15.58 ms therefore occurred before the native adapter call.
- Clock dispatch skew between ports was p99 1.401 ms, maximum 2.828 ms, closely matching callback-arrival skew of p99 1.365 ms, maximum 2.795 ms.
- The scheduler drained one output's complete pending queue before the next output. A clock pulse on another output could wait behind a 16-note chord. Port index also overrode earlier musical deadlines on other outputs.
- Captured predicates in recurring scheduler methods allocated on otherwise empty iterations. Running the new regression against merged source reproduced 696,000 allocated bytes over 1,000 iterations.

Corrections preserve per-port FIFO, cancellation, note ownership and musical tempo semantics:

- Pending queue heads are merged by deadline. Equal deadlines rotate between ports; Pump output is collected before dispatch so first-beat events receive the same treatment.
- A bounded, one-time managed preparation pass uses a separate manual host and silent no-op ports before the worker samples a real take's origin. It opens no backend and cannot write synthetic material into the user's capture.
- The worker caps its ordinary one-millisecond wait at the nearest pending deadline. It uses the existing platform wait, without a spin loop or process-wide timer changes.
- Recurring captured predicates were replaced with direct collection traversal/compaction. The allocation regression now stays within its 256-byte budget for 1,000 empty iterations; periodic status snapshots remain intentional allocations.

`TimingRegressionSpecs` covers cross-port clocks and chord fairness, earlier-deadline precedence, empty iteration allocation, preparation isolation and deadline-capped worker waits. The original safety suites, including scheduled-port cancellation with thru, continue to pass. An independent source review found no FIFO or cancellation blocker. The portable suite passed 55/55 groups after these changes.

## Native results

All runs use four outputs, 16-note chords at eighth-note spacing and 120 BPM. Count gates require matching native note payloads, balanced releases, exact clock counts, zero late attacks/skipped clocks, and successful active shutdown and output reopen. They do not impose a physical timing certification threshold.

| Measurement | Instrumented baseline, 20 seconds | Fair dispatch and preparation, 20 seconds | Final deadline-aware wait, idle 60 seconds |
|---|---:|---:|---:|
| First-chord deadline-to-dispatch maximum across ports | 14.10–15.58 ms | 3.96–4.03 ms | 4.87–4.95 ms |
| Clock deadline-to-dispatch p99 across ports | 1.968–3.183 ms | 1.965–2.056 ms | 0.998–1.120 ms |
| Inter-port clock dispatch skew p99 / maximum | 1.401 / 2.828 ms | 0.129 / 0.268 ms | 0.183 / 0.719 ms |
| Inter-port clock callback skew p99 / maximum | 1.365 / 2.795 ms | 0.110 / 0.177 ms | 0.130 / 0.338 ms |
| Absolute clock spacing error p99 across ports | 1.768–2.760 ms | 1.769–1.779 ms | 0.888–0.890 ms |

The final idle run delivered 3,840 balanced note pairs and 2,880 clock pulses per output. Capture contained 42,256 entries and remained complete. Maximum absolute clock spacing error was 2.208 ms. Note dispatch-to-callback p99 remained 0.053–0.056 ms. All four active shutdown/reopen checks passed.

Fair dispatch spreads a simultaneous chord across outputs: the first output no longer receives its entire chord before other outputs get their first notes. Its individual note latency can increase while cross-port alignment improves. Aggregate chord duration is still subject to the adapter calls and runtime/OS scheduling.

## Five-minute run and mixed workflow load

The 300-second native run passed all count, payload, ownership and shutdown/reopen gates. Each output delivered 19,200 attacks, 19,200 releases and 14,400 clock pulses. There were zero late attacks, skipped clocks, unmatched releases, remaining held notes or endpoint errors.

Capture stopped at exactly 100,000 entries and correctly reported `Complete: false`; this was explicitly expected by the probe and did not interrupt MIDI output. The independent adapter traces and callbacks validate events after capture reached its limit. This is a bounded overflow acceptance result, not a complete five-minute saved take.

| Five-minute load result | Measurement |
|---|---:|
| Note planned deadline-to-dispatch p99 across ports | 2.481–2.558 ms |
| Note planned deadline-to-dispatch maximum after first chord | 3.603 ms |
| First-chord planned deadline-to-dispatch maximum across ports | 9.423–9.489 ms |
| Clock planned deadline-to-dispatch p99 across ports | 0.997–1.135 ms |
| Clock planned deadline-to-dispatch maximum | 7.182 ms |
| Absolute clock spacing error p99 across ports / maximum | 0.864–0.875 / 6.636 ms |
| Inter-port clock dispatch skew p99 / maximum | 0.194 / 0.520 ms |
| Inter-port clock callback skew p99 / maximum | 0.131 / 3.124 ms |
| Note adapter-entry-to-callback p99 across ports / maximum | 0.061–0.063 / 1.860 ms |

The remaining callback skew tail exceeds the adapter dispatch skew: callback arrival is affected by work after the engine's submissions and must remain a separately measured limit. No physical timing guarantee follows from the count gates passing.

The concurrent workload completed 58 full 96-check Avalonia offscreen workflow rounds between 21:08:16 and 21:13:19 UTC. It repeatedly exercised generation, rendering, note editing, playback, modal guards and floating windows, with logs for every round. The native planned window was approximately 21:08:09.319–21:13:09.319 UTC. Their intersection was approximately 293.3 seconds; the first 6.7 seconds of the native run preceded the workload loop. This is a five-minute run with about 4 minutes 53 seconds of recorded concurrent mixed load, rather than 300 full seconds of uninterrupted load.

Native UTC was inferred from the saved monotonic origin and a same-machine QPC/UTC reference pair; UI summary timestamps have whole-second precision. `load300-overlap.json` retains the calculation and its precision note. Windows documents [cross-process QPC consistency](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps); the inferred wall-clock intersection remains approximate.

## Evidence and reproducibility

Local ignored artifacts are under `artifacts/timing-diagnosis/`:

- `baseline20.json`, `fixed20.json`, `final-idle60.json` and `final-load300.json` contain summaries.
- Each report has four adjacent `*.dispatch.csv` traces with captured host timestamp, adapter entry/return timestamp and payload.
- `baseline-regression/` is a temporary replay of the new regression methods against the owning engine files from `98c4ebb`, reproducing all three original defects without modifying the shared checkout.
- `ui-load/round-*.txt` and `ui-load/summary.txt` retain the 58 workload repetitions. `load300-reference.json` and `load300-overlap.json` record the time-window reconciliation.

```powershell
dotnet run --project tests/MidiWorkbench.WindowsProbe -c Release -- --approved-virtual artifacts/timing-diagnosis/final-idle60.json 60
dotnet run --project tests/MidiWorkbench.WindowsProbe -c Release -- --approved-virtual artifacts/timing-diagnosis/final-load300.json 300 --expect-capture-overflow
```

The 300-second mode deliberately keeps the production 100,000-entry capture capacity. Its explicit expected-overflow flag requires capture to stop at exactly that limit and report incomplete; native dispatch/callback matching remains independent of the truncated capture. Failure summaries and dispatch traces are retained even when a count gate fails.

Physical cable timing, acoustic timing, actual removal/reconnect and sleep/resume remain unverified. These results demonstrate a measured improvement and virtual note/clock integrity, not a hard real-time guarantee.
