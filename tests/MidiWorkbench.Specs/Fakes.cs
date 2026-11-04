using System.Collections.Immutable;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;

namespace MidiWorkbench.Specs;

// Adapter double for conformance specs: records submissions with the test clock and can disconnect.
sealed class FakeBackend(Func<double> clock) : IMidiBackend
{
    public readonly Dictionary<string, FakePort> Ports = new();
    public event Action? EndpointsChanged { add { } remove { } }
    public IReadOnlyList<MidiEndpoint> Endpoints() => Ports.Keys.Select(k => new MidiEndpoint(k, k, false, true)).ToArray();
    public FakePort Add(string id, bool scheduled) => Ports[id] = new FakePort(id, scheduled, clock);
    public IMidiOutputPort OpenOutput(string endpointId) => Ports[endpointId];
    public IMidiInputPort OpenInput(string endpointId, Action<TimedMidi> received) => throw new MidiPortException("No inputs.");
    public void Dispose() { }
}

sealed class FakePort(string id, bool scheduled, Func<double> clock) : IMidiOutputPort
{
    public readonly List<(double SubmittedAt, TimedMidi Message)> Sent = new();
    public string EndpointId => id;
    public bool ScheduledSend => scheduled;
    public bool Faulted { get; private set; }
    public void Disconnect() => Faulted = true;
    public void Submit(TimedMidi message)
    {
        if (Faulted) throw new MidiPortException("device removed");
        Sent.Add((clock(), message));
    }
    public void Cancel(double after) => Sent.RemoveAll(s => s.Message.Seconds > after);
    public void Dispose() { }
    public int Count(int kind) => Sent.Count(s => (s.Message.Status & 0xF0) == kind && (kind != 0x90 || s.Message.Data2 > 0));
}

static class Build
{
    public static Outline Outline(int bars = 1, Meter? meter = null) => new("Test", bars, meter ?? new Meter(), 48, [0, 2, 4, 5, 7, 9, 11], .5, .5, .5, 1);
    public static Part Part(string id, string destination, int channel, params MidiNote[] notes) =>
        new(id, id, PartRole.Phrase, destination, channel, new(id, PatternKind.Fixed, notes.ToImmutableArray()));
    public static Section Section(int bars, params Part[] parts) => new("test", Outline(bars), parts.ToImmutableArray());
    public static ResolvedPlan Plan(Section section, Rig? rig = null) =>
        RouteResolver.Resolve(PlanCompiler.Compile(section), rig ?? MidiWorkbench.Domain.Rig.Memory(section.Parts.Select(p => p.Destination)));
    public static ResolvedPlan Plan(params MidiNote[] notes) => Plan(Section(1, Part("part", "Test", 1, notes)));
    public static Rig Rig(params (string Destination, string Endpoint)[] routes) =>
        new(MidiWorkbench.Domain.Rig.CurrentSchemaVersion, routes.Select(r => r.Endpoint).Distinct().Select(e => new RigEndpoint(e)).ToImmutableArray(),
            routes.Select(r => new DeviceRoute(r.Destination, r.Endpoint)).ToImmutableArray());
}
