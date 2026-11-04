using MidiWorkbench.Domain;

namespace MidiWorkbench.Engine;

// Single-owner engine; only TransportHost's worker (or a test) calls it. Emits messages in deadline order per port.
public sealed class Transport(Transport.Emit emit)
{
    public delegate void Emit(int port, int destination, TimedMidi message);
    public const double LateLimitSeconds = .025;
    private const int Budget = 4096;
    private ResolvedPlan? plan;
    private int next;
    private long cycleBase;
    private long nextClock;
    private bool loop;
    private double emittedUntil;
    private readonly Dictionary<(int Port, int Channel, int Pitch), (int Owners, int Destination)> held = new();
    private readonly HashSet<int> active = new();
    private readonly HashSet<(int Port, int Channel)> channels = new();
    private readonly List<(int Port, int Destination, TimedMidi Message)> releases = new();
    private readonly TempoClock clock = new();
    public bool Running { get; private set; }
    public double Bpm => clock.Bpm;
    public long LateNoteOns { get; private set; }
    public long SkippedClocks { get; private set; }
    public double Position(double now)
    {
        if (!Running || plan is null) return 0;
        var tick = Math.Max(0, clock.TickAt(now));
        return loop ? tick % plan.LengthTicks : Math.Min(tick, plan.LengthTicks);
    }
    public void Start(ResolvedPlan value, double now, double bpm, bool repeat)
    {
        if (Running) Stop(now);
        plan = value; next = 0; cycleBase = 0; nextClock = 0; loop = repeat; LateNoteOns = 0; SkippedClocks = 0;
        held.Clear(); active.Clear(); channels.Clear(); releases.Clear();
        clock.Start(now, bpm); Running = true; emittedUntil = now;
        foreach (var port in value.ClockPorts) emit(port, -1, new(now + value.OffsetSeconds[port], 0xFA));
    }
    public void SetTempo(double now, double bpm) => clock.SetTempo(now, bpm);
    public void Pump(double now, double horizon)
    {
        if (!Running || plan is null) return;
        releases.RemoveAll(r => r.Message.Seconds <= now);
        var horizonTick = clock.TickAt(horizon);
        emittedUntil = Math.Max(emittedUntil, horizon);
        var events = plan.Events;
        var clocked = plan.ClockPorts.Length > 0;
        for (var budget = Budget; budget > 0; budget--)
        {
            if (next >= events.Length && loop && events.Length > 0) { cycleBase += plan.LengthTicks; next = 0; }
            var eventTick = next < events.Length ? cycleBase + events[next].Tick : long.MaxValue;
            var clockTick = clocked && (loop || nextClock < plan.LengthTicks) ? nextClock : long.MaxValue;
            var tick = Math.Min(eventTick, clockTick);
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
            if ((m.Status & 0xF0) == 0x90)
            {
                if (now - due > LateLimitSeconds) { LateNoteOns++; continue; }
                active.Add(m.Owner); channels.Add((m.Port, m.Status & 15));
                held.TryGetValue(key, out var h); held[key] = (h.Owners + 1, m.Destination);
                if (h.Owners == 0) emit(m.Port, m.Destination, new(at, m.Status, m.Data1, m.Data2));
            }
            // A skipped attack never acquired ownership, so it cannot release another owner's note.
            else if (active.Remove(m.Owner) && held.TryGetValue(key, out var h))
            {
                if (h.Owners > 1) { held[key] = (h.Owners - 1, h.Destination); continue; }
                held.Remove(key);
                var off = new TimedMidi(at, m.Status, m.Data1, 0);
                emit(m.Port, m.Destination, off);
                if (at > now) releases.Add((m.Port, m.Destination, off));
            }
        }
        if (!loop && next >= events.Length && clock.TickAt(now) >= plan.LengthTicks) Stop(now);
    }
    // Ordinary stop: messages already scheduled inside the lookahead still play, so releases follow them.
    public void Stop(double now)
    {
        if (!Running || plan is null) return;
        var at = Math.Max(now, emittedUntil);
        foreach (var (key, h) in held) emit(key.Port, h.Destination, new(at + plan.OffsetSeconds[key.Port], (byte)(0x80 | key.Channel), (byte)key.Pitch, 0));
        foreach (var port in plan.ClockPorts) emit(port, -1, new(at + plan.OffsetSeconds[port], 0xFC));
        held.Clear(); active.Clear(); releases.Clear(); Running = false;
    }
    // Callers cancel scheduled output first. Releases everything immediately, including releases that were cancelled.
    public void Panic(double now)
    {
        if (plan is null) return;
        foreach (var (key, h) in held) emit(key.Port, h.Destination, new(now, (byte)(0x80 | key.Channel), (byte)key.Pitch, 0));
        foreach (var r in releases) emit(r.Port, r.Destination, r.Message with { Seconds = now });
        foreach (var (port, channel) in channels) emit(port, -1, new(now, (byte)(0xB0 | channel), 123, 0));
        if (Running) foreach (var port in plan.ClockPorts) emit(port, -1, new(now, 0xFC));
        held.Clear(); active.Clear(); releases.Clear(); Running = false;
    }
}
