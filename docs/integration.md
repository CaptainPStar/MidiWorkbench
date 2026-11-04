# End-to-end integration plan

Planned 2026-11-04 from source inspection, a Linux build/spec run (see status.md) and MusicSessionTrace at f9d8e5a.

## Target slice

On Windows, one MIDI interface, one keyboard, external synths:

open project → generate drums (ported drummer) and bass → loop playback with MIDI Clock to routed endpoints → record a keyboard phrase in the loop → phrase becomes a locked part → save project, rig and take → export SMF → reopen and replay the same compiled plan.

This closes roadmap milestones 1–2 for one section. Scenes, launching, piano roll and background generation during playback stay out.

## Gaps in the current code

| # | Gap | Location | Consequence once hardware exists |
|---|---|---|---|
| 1 | Clock and Start/Stop are sent once per logical destination | `Transport.cs:42,54,84` | Four parts routed to one endpoint receive 4× F8 per pulse |
| 2 | Note ownership keyed by destination, not endpoint | `Transport.cs:31,61` | Two destinations on the same endpoint/channel can cut each other's notes |
| 3 | `Rig`/`DeviceRoute` unused; `DeviceRoute.Channel` overlaps `Part.Channel` | `Model.cs:47,87-88` | No resolution step; channel authority undefined |
| 4 | Worker pumps under the lock the UI polls and edits take | `WorkbenchSession.cs:107,136` | Device I/O would block on UI calls |
| 5 | Sink sends at pump time; `WaitOne(2)` | `Transport.cs:6-9`, `WorkbenchSession.cs:141` | Windows default timer resolution (~15.6 ms) gives jitter near the 25 ms late threshold |
| 6 | No input path, loop or panic | — | Performance-led workflow and recording impossible |
| 7 | Take holds output only; no recipe metadata beyond version/seed | `Model.cs:44-45,90-94` | CAP-01 and reproducible drummer recall unmet |
| 8 | Specs exercise only the memory sink | `Specs/Program.cs` | AGENTS adapter tests (disconnect, stop, backlog, timestamp, ownership) have no harness |

## Phases

Each phase ends with `scripts/verify.sh` green and new specs listed. Phases 1, 3a and 4 are fully portable and testable on Linux; 2, 3b and 5 need Windows and hardware.

### 0 — Baseline

- Enable `RestorePackagesWithLockFile`; commit generated `packages.lock.json`.
- Windows: build, verify.ps1, desktop smoke (status.md manual list).
- Exit: roadmap milestone 0.

### 1 — Engine seams (portable)

1. **Rig model and store.** `Rig` schema 1 in its own file (default `LocalApplicationData/MidiWorkbench/rig.json`). `DeviceRoute(Destination, EndpointId, ChannelOverride?, OffsetMs)`; endpoints carry `SendClock`. Part channel stays authoritative unless overridden.
2. **Resolution.** `PlanCompiler.Compile(section)` stays rig-free. New `RouteResolver.Resolve(plan, rig, endpoints) → ResolvedPlan` with endpoint indices, wire channels and offsets. Missing or offline endpoint fails before Start with a named destination.
3. **Backend contract** (Engine, BCL only):
   - `IMidiBackend`: enumerate endpoints with stable IDs, open, `EndpointsChanged`.
   - `IMidiOutputPort`: `Capabilities` (scheduled send, max lookahead), `Submit(timestamped messages)`, `Cancel/Flush`, `Faulted`.
   - `IMidiInputPort`: callback delivers host-timestamped bytes; implementation only timestamps and enqueues.
   - `MemoryBackend` replaces `MemoryMidiSink` as one implementation; `FakeBackend` injects disconnect, slow submit and timestamp skew.
4. **Transport changes.** Ownership keyed by (endpoint, channel, pitch); clock per clock-enabled endpoint; bounded lookahead window with absolute deadlines; loop region; `Panic` (release owned notes, then CC 123 on used channels) distinct from `Stop`.
5. **Transport host.** One worker owns `Transport` and ports. Commands via bounded channel (Start, Stop, Panic, SetTempo, SetLoop, SwapPlan-at-boundary later). Status as an immutable snapshot published with `Volatile.Write`. Capture into a preallocated ring drained off the worker. `WorkbenchSession` keeps project state and stops sharing a lock with the scheduler.
6. **Adapter conformance specs**, parameterised by backend, run against `FakeBackend`: disconnect mid-note, stop/panic release, backlog bound and fault, timestamp monotonicity and offset, ownership with two destinations on one endpoint, single clock stream per endpoint.

Exit: CLI `simulate` and specs run through the resolved path; gaps 1–5 and 8 closed in portable code.

### 2 — Windows output adapter

- New `MidiWorkbench.Midi.Windows` (`net10.0-windows`), referenced by Desktop and CLI only on Windows. Engine stays portable.
- Spike both candidates behind the contract: Windows MIDI Services (timestamped scheduling, ADR 002 preference) and WinMM (proven in MusicSessionTrace through DryWetMidi 8.0.3). Pick by measurement, not assumption.
- Scheduler wait: high-resolution waitable timer (pattern exists in MusicSessionTrace `SchedulerWait.cs`), measured.
- CLI: `devices`, `probe ENDPOINT` (loopback cable) reporting median/p95/p99/max error, drift and overload counts per ADR 002.
- Desktop Routing pane: endpoint picker per destination, clock toggle per endpoint, save rig, Panic button, offline state.
- Same conformance specs plus a hardware run of disconnect/reconnect.

Exit: roadmap milestone 1.

### 3 — Input and recording

a) Portable:
- Recorder off the worker: input ring → ticks via tempo-map snapshot; note pairing, sustain (CC 64) handling, loop-wrap.
- Take schema 2: input stream, output stream and action journal kept separate (CAP-01).
- Captured phrase → new part with locked notes (`Fixed`), via the normal edit path; never auto-regenerated.
- SMF type-1 writer in Persistence (BCL, tempo map, 960 PPQ). No DryWetMidi dependency; MusicSessionTrace writers use 480 PPQ and a fixed tempo.

b) Windows:
- Input adapter with driver timestamps where available, otherwise host arrival labelled uncalibrated.
- Thru routing goes through the transport worker; measure added latency before deciding on a direct path.
- Feedback guard: refuse a route whose output reaches the recording input on the same device unless confirmed.

### 4 — Drummer port

- Source: MusicSessionTrace `DrumPatternPlanner.cs`, `PersonaDrumPlanner.cs`, `DrummerPersonas.cs` and the option/hit records from `DrumAccompaniment.cs` (~200 lines, BCL only, xorshift32, deterministic). Do not port `SessionBeatTimeline` or the adaptive/audio followers.
- Convert output from sample frames to ticks (`BeatPosition × 960`); replace the fixed 40 ms duration with a tick length; round swung positions explicitly.
- Meters: exact behaviour for 4/4, 3/4, 6/8 first; other meters through an explicit beat-grouping rule with bounds specs (7/8 included).
- Project schema 2: recipe metadata (generator ID/version, PRNG ID, per-part seed, persona and drum options). Explicit 1→2 migration; unknown versions still fail.
- Specs ported from `DrumTests.cs:10-29`, `DrummerResilienceTests.cs:17-44`, `DrumFollowerTests.cs:122-135`, translated from samples to ticks. Golden test: straight 4/4, 3/4, 6/8 hit positions equal the original planner's.

### 5 — Acceptance

Run the target slice on the intended interfaces. Record measurements in status.md. Only then update README claims about physical MIDI and the drummer.

## Decisions needed

| Decision | Recommendation | Blocks |
|---|---|---|
| Copy drummer source from MusicSessionTrace (no license in either repository) | Copy the BCL subset with an origin note; shared package later if both apps keep it | Phase 4 |
| Output backend | Decide after the phase 2 spike on the target Windows build and interfaces | Phase 2 |
| Route channel semantics | Part channel authoritative; rig override optional | Phase 1 |
| Thru path | Through the worker first; direct path only if measured latency is unacceptable | Phase 3b |
