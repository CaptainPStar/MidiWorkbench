using System.Collections.Immutable;
using System.Diagnostics;

namespace MidiWorkbench.Engine;

public readonly record struct TimedMidi(double Seconds, byte Status, byte Data1 = 0, byte Data2 = 0);
public sealed class MidiPortException(string message, Exception? inner = null) : Exception(message, inner);
public sealed record MidiEndpoint(string Id, string Name, bool Input, bool Output);

public interface IMidiBackend : IDisposable
{
    IReadOnlyList<MidiEndpoint> Endpoints();
    IMidiOutputPort OpenOutput(string endpointId);
    // The callback runs on a driver thread: it may only timestamp and enqueue.
    IMidiInputPort OpenInput(string endpointId, Action<TimedMidi> received);
    event Action? EndpointsChanged;
}

public interface IMidiOutputPort : IDisposable
{
    string EndpointId { get; }
    // True: Submit accepts future timestamps and Cancel drops them. False: the host releases each message at its deadline.
    bool ScheduledSend { get; }
    bool Faulted { get; }
    // Must not block. Throws MidiPortException when the endpoint fails or disappears.
    void Submit(TimedMidi message);
    void Cancel(double after);
}

public interface IMidiInputPort : IDisposable
{
    string EndpointId { get; }
    bool Faulted => false;
}

public interface IHostWait : IDisposable
{
    void Wait(TimeSpan duration);
}

public sealed class SleepWait : IHostWait
{
    public void Wait(TimeSpan duration) => Thread.Sleep(duration);
    public void Dispose() { }
}

public static class HostTime
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

// Silent endpoints. "memory" accepts scheduled output; "loopback" returns output to its input at release time.
public sealed class MemoryBackend(Func<double>? clock = null, int capacity = 100000) : IMidiBackend
{
    public const string MemoryId = "memory";
    public const string LoopbackId = "loopback";
    private readonly Func<double> now = clock ?? (() => HostTime.Now);
    private readonly int limit = capacity;
    private readonly Lock gate = new();
    private readonly List<Action<TimedMidi>> loopbackListeners = new();
    private readonly List<TimedMidi> memory = new();
    public event Action? EndpointsChanged { add { } remove { } }
    public IReadOnlyList<MidiEndpoint> Endpoints() =>
        [new(MemoryId, "Memory", false, true), new(LoopbackId, "Loopback", true, true)];
    public ImmutableArray<TimedMidi> MemorySnapshot() { lock (gate) return memory.ToImmutableArray(); }
    public IMidiOutputPort OpenOutput(string endpointId) => endpointId switch
    {
        MemoryId => new Port(this, MemoryId, true),
        LoopbackId => new Port(this, LoopbackId, false),
        _ => throw new MidiPortException($"Unknown endpoint '{endpointId}'.")
    };
    public IMidiInputPort OpenInput(string endpointId, Action<TimedMidi> received)
    {
        if (endpointId != LoopbackId) throw new MidiPortException($"'{endpointId}' has no input.");
        lock (gate) loopbackListeners.Add(received);
        return new Input(this, received);
    }
    public void Dispose() { }
    private sealed class Port(MemoryBackend owner, string id, bool scheduled) : IMidiOutputPort
    {
        public string EndpointId => id;
        public bool ScheduledSend => scheduled;
        public bool Faulted => false;
        public void Submit(TimedMidi message)
        {
            Action<TimedMidi>[] listeners;
            lock (owner.gate)
            {
                if (scheduled) { if (owner.memory.Count < owner.limit) owner.memory.Add(message); return; }
                listeners = owner.loopbackListeners.ToArray();
            }
            var arrival = message with { Seconds = owner.now() };
            foreach (var listener in listeners) listener(arrival);
        }
        public void Cancel(double after)
        {
            if (scheduled) lock (owner.gate) owner.memory.RemoveAll(m => m.Seconds > after);
        }
        public void Dispose() { }
    }
    private sealed class Input(MemoryBackend owner, Action<TimedMidi> listener) : IMidiInputPort
    {
        public string EndpointId => LoopbackId;
        public void Dispose() { lock (owner.gate) owner.loopbackListeners.Remove(listener); }
    }
}

public sealed class CompositeBackend : IMidiBackend
{
    private readonly IMidiBackend[] backends;
    public event Action? EndpointsChanged;
    public CompositeBackend(params IMidiBackend[] backends)
    {
        this.backends = backends;
        foreach (var b in backends) b.EndpointsChanged += () => EndpointsChanged?.Invoke();
    }
    public IReadOnlyList<MidiEndpoint> Endpoints() => backends.SelectMany(b => b.Endpoints()).ToArray();
    private IMidiBackend Owner(string id) => backends.FirstOrDefault(b => b.Endpoints().Any(e => e.Id == id))
        ?? throw new MidiPortException($"Endpoint '{id}' is not connected.");
    public IMidiOutputPort OpenOutput(string endpointId) => Owner(endpointId).OpenOutput(endpointId);
    public IMidiInputPort OpenInput(string endpointId, Action<TimedMidi> received) => Owner(endpointId).OpenInput(endpointId, received);
    public void Dispose() { foreach (var b in backends) b.Dispose(); }
}
