using MidiWorkbench.Domain;
using System.Collections.Immutable;

namespace MidiWorkbench.Engine;

public sealed class TempoClock
{
    public double Bpm { get; private set; } = 120;
    public double OriginSeconds { get; private set; }
    public double OriginTick { get; private set; }
    public double TickAt(double seconds) => OriginTick + (seconds - OriginSeconds) * Bpm * MusicalTime.Ppq / 60;
    public double SecondsAt(double tick) => OriginSeconds + (tick - OriginTick) * 60 / (Bpm * MusicalTime.Ppq);
    public void SetTempo(double seconds, double bpm)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(bpm) || bpm is < 20 or > 400) throw new ArgumentOutOfRangeException(nameof(bpm));
        OriginTick = TickAt(seconds); OriginSeconds = seconds; Bpm = bpm;
    }
    public void Start(double seconds, double bpm)
    {
        SetTempo(seconds, bpm); OriginSeconds = seconds; OriginTick = 0;
    }
}

// Piecewise-constant tempo from transport start; used off the scheduler for recording and export.
public sealed class TempoMap
{
    private readonly (double Seconds, double Tick, double Bpm)[] segments;
    public TempoMap(double initialBpm, IEnumerable<TempoChange> changes)
    {
        var list = new List<(double, double, double)> { (0, 0, initialBpm) };
        foreach (var c in changes.Where(c => c.Seconds > 0).OrderBy(c => c.Seconds))
        {
            var last = list[^1];
            list.Add((c.Seconds, last.Item2 + (c.Seconds - last.Item1) * last.Item3 * MusicalTime.Ppq / 60, c.Bpm));
        }
        segments = list.ToArray();
    }
    public IEnumerable<(long Tick, double Bpm)> Changes => segments.Select(s => ((long)Math.Round(s.Tick), s.Bpm));
    public double TickAt(double seconds)
    {
        var s = segments[0];
        foreach (var candidate in segments) { if (candidate.Seconds > seconds) break; s = candidate; }
        return s.Tick + (seconds - s.Seconds) * s.Bpm * MusicalTime.Ppq / 60;
    }
}

public sealed record PlannedMessage(long Tick, string Owner, string Destination, byte Status, byte Data1, byte Data2);
public sealed record PlaybackPlan(long LengthTicks, ImmutableArray<PlannedMessage> Events, ImmutableArray<string> Destinations);

public static class PlanCompiler
{
    public static PlaybackPlan Compile(Section section)
    {
        new SongProject(SongProject.CurrentSchemaVersion, "Validation", 120, [section]).Validate();
        var messages = new List<PlannedMessage>();
        foreach (var part in section.Parts)
        foreach (var note in part.Pattern.Notes)
        {
            var channel = part.Channel - 1;
            messages.Add(new(note.Tick, part.Id + "/" + note.Id, part.Destination, (byte)(0x90 | channel), (byte)note.Pitch, (byte)note.Velocity));
            messages.Add(new(note.EndTick, part.Id + "/" + note.Id, part.Destination, (byte)(0x80 | channel), (byte)note.Pitch, 0));
        }
        // Off before on preserves repeated notes at a shared boundary.
        return new(section.Outline.LengthTicks, messages.OrderBy(m => m.Tick).ThenBy(m => m.Status & 0xF0).ToImmutableArray(),
            section.Parts.Select(p => p.Destination).Distinct(StringComparer.Ordinal).ToImmutableArray());
    }
}

public sealed record ResolvedMessage(long Tick, int Port, int Destination, int Owner, byte Status, byte Data1, byte Data2);
public sealed record ResolvedPlan(long LengthTicks, ImmutableArray<ResolvedMessage> Events, ImmutableArray<string> Endpoints,
    ImmutableArray<string> Destinations, ImmutableArray<int> ClockPorts, ImmutableArray<double> OffsetSeconds);

public static class RouteResolver
{
    public static ResolvedPlan Resolve(PlaybackPlan plan, Rig rig)
    {
        rig.Validate();
        var routes = rig.Routes.ToDictionary(r => r.Destination, StringComparer.Ordinal);
        var missing = plan.Destinations.Where(d => !routes.ContainsKey(d)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"No route for {string.Join(", ", missing.Select(m => $"'{m}'"))}. Set it in Routing.");
        var endpoints = plan.Destinations.Select(d => routes[d].EndpointId).Distinct(StringComparer.Ordinal).ToImmutableArray();
        var endpointConfig = rig.Endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        var events = plan.Events.Select(m =>
        {
            var route = routes[m.Destination];
            var status = route.Channel is { } channel ? (byte)(m.Status & 0xF0 | channel - 1) : m.Status;
            if (!owners.TryGetValue(m.Owner, out var owner)) owners[m.Owner] = owner = owners.Count;
            return new ResolvedMessage(m.Tick, endpoints.IndexOf(route.EndpointId), plan.Destinations.IndexOf(m.Destination), owner, status, m.Data1, m.Data2);
        }).ToImmutableArray();
        return new(plan.LengthTicks, events, endpoints, plan.Destinations,
            Enumerable.Range(0, endpoints.Length).Where(i => endpointConfig[endpoints[i]].SendClock).ToImmutableArray(),
            endpoints.Select(e => endpointConfig[e].OffsetMilliseconds / 1000).ToImmutableArray());
    }
}
