using MidiWorkbench.Domain;

namespace MidiWorkbench.Engine;

// Single-owner engine; only TransportHost's worker (or a test) calls it. Emits messages in deadline order per port.
public sealed class Transport(Transport.Emit emit, Transport.EmitOwned? emitOwned = null)
{
    public delegate void Emit(int port, int destination, TimedMidi message);
    public delegate void EmitOwned(int port, int destination, int owner, TimedMidi message);
    public const double LateLimitSeconds = .025;
    private const int Budget = 4096;
    private ResolvedPlan? plan;
    private int next;
    private long cycleBase, sectionBase;
    private readonly List<(long Tick, long Length)> sections = new();
    private ResolvedPlan? queuedPlan;
    private HashSet<int> activePorts = [], queuedPorts = [];
    private long queuedTick;
    private long nextClock;
    private bool loop;
    private double emittedUntil;
    private readonly Dictionary<(int Port, int Channel, int Pitch), (int Owners, int Destination)> held = new();
    private readonly HashSet<int> active = new();
    private readonly HashSet<(int Port, int Channel)> channels = new();
    private readonly HashSet<(int Port, int Channel)> sustain = new();
    private readonly List<(int Port, int Destination, TimedMidi Message)> releases = new();
    private readonly TempoClock clock = new();
    public bool Running { get; private set; }
    public double Bpm => clock.Bpm;
    public long LateNoteOns { get; private set; }
    public long SkippedClocks { get; private set; }
    public double LastTempoChangeTime { get; private set; }
    public long SectionRevision { get; private set; }
    public string? ActivePlanTag => Running ? plan?.Tag : null;
    public string? QueuedPlanTag => Running ? queuedPlan?.Tag : null;
    private static HashSet<int> RequiredPorts(ResolvedPlan value) =>
        value.Events.Select(e => e.Port).Concat(value.ClockPorts).ToHashSet();
    internal bool NeedsPort(int port) => Running && (activePorts.Contains(port) || queuedPorts.Contains(port));
    internal void CountLate(TimedMidi message)
    {
        if (message.Status == 0xF8) SkippedClocks++;
        else if ((message.Status & 0xF0) == 0x90 && message.Data2 > 0) LateNoteOns++;
    }
    public double Position(double now)
    {
        if (!Running || plan is null) return 0;
        var absolute = Math.Max(0, clock.TickAt(now));
        while (sections.Count > 1 && sections[1].Tick <= absolute) sections.RemoveAt(0);
        var section = sections[0];
        var tick = Math.Max(0, absolute - section.Tick);
        return loop ? tick % section.Length : Math.Min(tick, section.Length);
    }
    public void Start(ResolvedPlan value, double now, double bpm, bool repeat)
    {
        if (Running) Stop(now);
        plan = value; next = 0; cycleBase = 0; sectionBase = 0; queuedPlan = null; nextClock = 0; loop = repeat; LateNoteOns = 0; SkippedClocks = 0;
        held.Clear(); active.Clear(); channels.Clear(); sustain.Clear(); releases.Clear();
        activePorts = RequiredPorts(value); queuedPorts.Clear();
        sections.Clear(); sections.Add((0, value.LengthTicks)); SectionRevision++;
        clock.Start(now, bpm); Running = true; emittedUntil = now;
        foreach (var port in value.ClockPorts) emit(port, -1, new(now + value.OffsetSeconds[port], 0xFA));
    }
    public void SetTempo(double now, double bpm)
    {
        LastTempoChangeTime = Running ? Math.Max(now, emittedUntil) : now;
        clock.SetTempo(LastTempoChangeTime, bpm);
    }
    public void QueuePlan(ResolvedPlan value, long quantumTicks, double now)
    {
        if (!Running || plan is null) throw new InvalidOperationException("Transport is stopped.");
        if (quantumTicks <= 0) throw new ArgumentOutOfRangeException(nameof(quantumTicks));
        if (!plan.Endpoints.SequenceEqual(value.Endpoints) || !plan.Destinations.SequenceEqual(value.Destinations) ||
            !plan.ClockPorts.SequenceEqual(value.ClockPorts) || !plan.OffsetSeconds.SequenceEqual(value.OffsetSeconds))
            throw new ArgumentException("Queued plan must use the current endpoints, destinations and clock configuration.");
        var nowTick = clock.TickAt(now);
        var after = Math.Max(nowTick, clock.TickAt(emittedUntil));
        // A pending launch stops lookahead at its boundary, so a replacement can
        // use that boundary until it is reached, even if the horizon extends beyond it.
        var nextBoundary = (Math.Floor(after / quantumTicks) + 1) * quantumTicks;
        var boundary = queuedPlan is not null && queuedTick > nowTick && queuedTick % quantumTicks == 0 && queuedTick < nextBoundary
            ? queuedTick : nextBoundary;
        if (boundary > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(quantumTicks));
        queuedTick = (long)boundary; queuedPlan = value; queuedPorts = RequiredPorts(value);
    }
    public void CancelQueuedPlan() { queuedPlan = null; queuedPorts.Clear(); }
    public void CancelQueuedPlan(double now)
    {
        if (queuedPlan is not null && clock.TickAt(now) >= queuedTick)
            throw new InvalidOperationException("The queued boundary has already passed.");
        queuedPlan = null; queuedPorts.Clear();
    }
    public void Pump(double now, double horizon)
    {
        if (!Running || plan is null) return;
        clock.TrimBefore(now);
        var retained = 0;
        for (var i = 0; i < releases.Count; i++)
            if (releases[i].Message.Seconds > now) releases[retained++] = releases[i];
        if (retained < releases.Count) releases.RemoveRange(retained, releases.Count - retained);
        var horizonTick = clock.TickAt(horizon);
        emittedUntil = Math.Max(emittedUntil, horizon);
        var events = plan.Events;
        var clocked = plan.ClockPorts.Length > 0;
        for (var budget = Budget; budget > 0; budget--)
        {
            if (next >= events.Length && loop && events.Length > 0) { cycleBase += plan.LengthTicks; next = 0; }
            var eventTick = next < events.Length ? cycleBase + events[next].Tick : long.MaxValue;
            var clockTick = clocked && (loop || nextClock < sectionBase + plan.LengthTicks || queuedPlan is not null) ? nextClock : long.MaxValue;
            var tick = Math.Min(eventTick, clockTick);
            if (queuedPlan is not null && queuedTick <= tick && queuedTick <= horizonTick)
            {
                // Keep the launch cancellable until the actual boundary.
                if (clock.SecondsAt(queuedTick) > now) break;
                var transitionAt = Math.Max(clock.SecondsAt(queuedTick), now);
                foreach (var (port, channel) in sustain)
                    emit(port, -1, new(transitionAt + plan.OffsetSeconds[port], (byte)(0xB0 | channel), 64, 0));
                sustain.Clear();
                foreach (var (heldKey, h) in held)
                    emit(heldKey.Port, h.Destination, new(transitionAt + plan.OffsetSeconds[heldKey.Port], (byte)(0x80 | heldKey.Channel), (byte)heldKey.Pitch, 0));
                held.Clear(); active.Clear();
                plan = queuedPlan; queuedPlan = null; activePorts = queuedPorts; queuedPorts = []; sectionBase = queuedTick; cycleBase = sectionBase; next = 0; events = plan.Events;
                sections.Add((sectionBase, plan.LengthTicks)); SectionRevision++;
                continue;
            }
            if (tick == long.MaxValue || tick > horizonTick) break;
            var due = clock.SecondsAt(tick);
            if (clockTick <= eventTick)
            {
                nextClock += MusicalTime.MidiClockTicks;
                // A stalled host must not burst a backlog of pulses into a device.
                if (now - due > LateLimitSeconds) { SkippedClocks++; continue; }
                foreach (var port in plan.ClockPorts) emit(port, -1, new(Math.Max(due + plan.OffsetSeconds[port], now), 0xF8));
                continue;
            }
            var m = events[next++];
            var at = Math.Max(due + plan.OffsetSeconds[m.Port], now);
            var key = (m.Port, m.Status & 15, (int)m.Data1);
            if ((m.Status & 0xF0) == 0x90 && m.Data2 > 0)
            {
                if (now - due > LateLimitSeconds) { LateNoteOns++; continue; }
                active.Add(m.Owner); channels.Add((m.Port, m.Status & 15));
                held.TryGetValue(key, out var h); held[key] = (h.Owners + 1, m.Destination);
                if (emitOwned is not null) emitOwned(m.Port, m.Destination, m.Owner, new(at, m.Status, m.Data1, m.Data2));
                else if (h.Owners == 0) emit(m.Port, m.Destination, new(at, m.Status, m.Data1, m.Data2));
            }
            // A skipped attack never acquired ownership, so it cannot release another owner's note.
            else if ((m.Status & 0xF0) == 0x80 || (m.Status & 0xF0) == 0x90 && m.Data2 == 0)
            {
                if (!active.Remove(m.Owner) || !held.TryGetValue(key, out var h)) continue;
                var off = new TimedMidi(at, m.Status, m.Data1, 0);
                if (emitOwned is not null) emitOwned(m.Port, m.Destination, m.Owner, off);
                if (h.Owners > 1)
                {
                    held[key] = (h.Owners - 1, h.Destination);
                    if (emitOwned is not null && at > now) releases.Add((m.Port, m.Destination, off));
                    continue;
                }
                held.Remove(key);
                if (emitOwned is null) emit(m.Port, m.Destination, off);
                if (at > now) releases.Add((m.Port, m.Destination, off));
            }
            else
            {
                if ((m.Status & 0xF0) == 0xB0 && m.Data1 == 64)
                {
                    var pedal = (m.Port, m.Status & 15);
                    if (m.Data2 >= 64) sustain.Add(pedal); else sustain.Remove(pedal);
                }
                emit(m.Port, m.Destination, new(at, m.Status, m.Data1, m.Data2));
            }
        }
        if (!loop && next >= events.Length && clock.TickAt(now) >= sectionBase + plan.LengthTicks && queuedPlan is null) Stop(now);
    }
    // Ordinary stop: messages already scheduled inside the lookahead still play, so releases follow them.
    public void Stop(double now)
    {
        if (!Running || plan is null) return;
        var at = Math.Max(now, emittedUntil);
        foreach (var (port, channel) in sustain) emit(port, -1, new(at + plan.OffsetSeconds[port], (byte)(0xB0 | channel), 64, 0));
        foreach (var (key, h) in held) emit(key.Port, h.Destination, new(at + plan.OffsetSeconds[key.Port], (byte)(0x80 | key.Channel), (byte)key.Pitch, 0));
        foreach (var port in plan.ClockPorts) emit(port, -1, new(at + plan.OffsetSeconds[port], 0xFC));
        held.Clear(); active.Clear(); sustain.Clear(); releases.Clear(); queuedPlan = null; queuedPorts.Clear(); Running = false;
    }
    // Callers cancel scheduled output first. Releases everything immediately, including releases that were cancelled.
    public void Panic(double now)
    {
        if (plan is null) return;
        foreach (var (port, channel) in sustain) emit(port, -1, new(now, (byte)(0xB0 | channel), 64, 0));
        foreach (var (key, h) in held) emit(key.Port, h.Destination, new(now, (byte)(0x80 | key.Channel), (byte)key.Pitch, 0));
        foreach (var r in releases) emit(r.Port, r.Destination, r.Message with { Seconds = now });
        foreach (var (port, channel) in channels) emit(port, -1, new(now, (byte)(0xB0 | channel), 123, 0));
        if (Running) foreach (var port in plan.ClockPorts) emit(port, -1, new(now, 0xFC));
        held.Clear(); active.Clear(); sustain.Clear(); releases.Clear(); queuedPlan = null; queuedPorts.Clear(); Running = false;
    }
}
