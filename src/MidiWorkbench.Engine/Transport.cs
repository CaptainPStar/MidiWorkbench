using System.Collections.Immutable;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Engine;

public interface IMidiSink
{
    void Send(double seconds, string destination, byte status, byte data1 = 0, byte data2 = 0);
}

public sealed class MemoryMidiSink(int capacity = 100000) : IMidiSink
{
    private readonly List<CapturedMidi> events = new();
    public bool Overflowed { get; private set; }
    public int Count => events.Count;
    public void Send(double seconds, string destination, byte status, byte data1 = 0, byte data2 = 0)
    {
        if (events.Count >= capacity) { Overflowed = true; return; }
        events.Add(new(seconds, destination, status, data1, data2));
    }
    public ImmutableArray<CapturedMidi> Snapshot() => events.ToImmutableArray();
    public void Clear() { events.Clear(); Overflowed = false; }
}

// Single-owner engine. Hosts must serialize Start/Pump/Stop/SetTempo calls.
public sealed class Transport(IMidiSink sink)
{
    private PlaybackPlan? plan;
    private int next;
    private long nextClock;
    private readonly Dictionary<(string Destination, int Channel, int Pitch), int> held = new();
    private readonly TempoClock clock = new();
    private readonly HashSet<(string Destination, string Owner)> active = new();
    public bool Running { get; private set; }
    public long LateNoteOns { get; private set; }
    public double Position(double now) => Running ? Math.Max(0, clock.TickAt(now)) : 0;
    public void Start(PlaybackPlan value, double now, double bpm)
    {
        if (Running) Stop(now);
        plan = value; next = 0; nextClock = 0; LateNoteOns = 0;
        clock.Start(now, bpm); Running = true;
        foreach (var destination in value.Destinations) sink.Send(now, destination, 0xFA);
    }
    public void SetTempo(double now, double bpm) => clock.SetTempo(now, bpm);
    public void Pump(double now)
    {
        if (!Running || plan is null) return;
        var tick = clock.TickAt(now);
        var sent = 0;
        // A stalled host must not burst an entire backlog into a device.
        while (nextClock < plan.LengthTicks && nextClock <= tick && sent < 256)
        {
            if (now - clock.SecondsAt(nextClock) <= .025)
                foreach (var destination in plan.Destinations) sink.Send(now, destination, 0xF8);
            nextClock += MusicalTime.MidiClockTicks; sent++;
        }
        if (nextClock < tick) nextClock = ((long)tick / MusicalTime.MidiClockTicks + 1) * MusicalTime.MidiClockTicks;
        while (next < plan.Events.Length && plan.Events[next].Tick <= tick)
        {
            var message = plan.Events[next++];
            var key = (message.Destination, message.Status & 15, (int)message.Data1);
            var on = (message.Status & 0xF0) == 0x90;
            if (on && now - clock.SecondsAt(message.Tick) > .025) { LateNoteOns++; continue; }
            if (on)
            {
                active.Add((message.Destination, message.Owner));
                held.TryGetValue(key, out var owners); held[key] = owners + 1;
                if (owners == 0) sink.Send(now, message.Destination, message.Status, message.Data1, message.Data2);
            }
            else if (active.Remove((message.Destination, message.Owner)) && held.TryGetValue(key, out var owners))
            {
                if (owners == 1) { held.Remove(key); sink.Send(now, message.Destination, message.Status, message.Data1, 0); }
                else held[key] = owners - 1;
            }
        }
        if (tick >= plan.LengthTicks) Stop(now);
    }
    public void Stop(double now)
    {
        foreach (var key in held.Keys)
            sink.Send(now, key.Destination, (byte)(0x80 | key.Channel), (byte)key.Pitch, 0);
        held.Clear(); active.Clear();
        if (Running && plan is not null)
            foreach (var destination in plan.Destinations) sink.Send(now, destination, 0xFC);
        Running = false;
    }
}
