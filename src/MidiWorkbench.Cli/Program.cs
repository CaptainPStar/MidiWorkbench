using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Midi.Windows;
using MidiWorkbench.Persistence;

const string usage = """
    Usage:
      new PROJECT
      validate PROJECT
      upgrade PROJECT                rewrite in the current schema
      simulate PROJECT TAKE          synthetic timing, memory output
      export PROJECT FILE.mid
      devices
      probe OUTPUT INPUT [SECONDS]   loopback timing measurement
    """;
try
{
    switch (args)
    {
        case ["new", var output]:
            ProjectStore.Save(output, WorkbenchSession.CreateDemo()); Console.WriteLine(output); return 0;
        case ["validate", var input]:
            var p = ProjectStore.Load(input); Console.WriteLine($"Valid: {p.Sections.Length} sections"); return 0;
        case ["upgrade", var path]:
            ProjectStore.Save(path, ProjectStore.Load(path)); Console.WriteLine($"Schema {SongProject.CurrentSchemaVersion}: {path}"); return 0;
        case ["simulate", var source, var target]:
            return Simulate(ProjectStore.Load(source), target);
        case ["export", var source, var target]:
            var project = ProjectStore.Load(source);
            ProjectStore.WriteAtomic(target, MidiFile.FromSection(project.Sections[0], project.Bpm)); Console.WriteLine(target); return 0;
        case ["devices"]:
            using (var backend = Backend())
                foreach (var e in backend.Endpoints()) Console.WriteLine($"{(e.Output ? "out" : "   ")} {(e.Input ? "in" : "  ")}  {e.Id}");
            return 0;
        case ["probe", var output, var input, ..var rest] when rest.Length <= 1:
            var seconds = rest.Length == 1 ? double.Parse(rest[0], CultureInfo.InvariantCulture) : 10;
            if (seconds is < 1 or > 600) throw new ArgumentException("Probe duration must be 1–600 seconds.");
            return Probe(output, input, seconds);
        default:
            Console.Error.WriteLine(usage); return 1;
    }
}
catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or JsonException or MidiPortException or FormatException)
{ Console.Error.WriteLine(ex.Message); return 2; }

static IMidiBackend Backend() => WindowsMidi.CreateBackend() is { } windows ? new CompositeBackend(new MemoryBackend(), windows) : new MemoryBackend();

static int Simulate(SongProject p, string target)
{
    var plan = RouteResolver.Resolve(PlanCompiler.Compile(p.Sections[0]), Rig.Memory(WorkbenchSession.Destinations(p)));
    var backend = new MemoryBackend();
    using var host = new TransportHost(new HostOptions(Background: false));
    host.Start(plan, plan.Endpoints.Select(backend.OpenOutput).ToArray(), p.Bpm, false);
    var duration = plan.LengthTicks * 60d / (p.Bpm * MusicalTime.Ppq);
    for (var time = 0d; time <= duration + .05; time += .001) host.Step(time);
    var status = host.Status;
    var take = WorkbenchSession.ToTake(host.Capture(), null);
    ProjectStore.SaveTake(target, take);
    Console.WriteLine($"{take.Outputs.Length} events; late notes {status.LateNoteOns}; complete {take.Complete}");
    return take.Complete && !status.Running ? 0 : 2;
}

// Sends one note per eighth at 120 BPM through OUTPUT and matches arrivals on INPUT. Arrival is host time at the input callback.
static int Probe(string outputId, string inputId, double seconds)
{
    using var backend = Backend();
    var arrivals = new ConcurrentQueue<TimedMidi>();
    using var host = new TransportHost(new HostOptions(Wait: WindowsMidi.CreateWait()));
    using var input = backend.OpenInput(inputId, m => { if ((m.Status & 0xF0) == 0x90 && m.Data2 > 0) arrivals.Enqueue(m); });
    using var output = backend.OpenOutput(outputId);
    var bars = Math.Max(1, (int)Math.Ceiling(seconds / 2));
    var notes = Enumerable.Range(0, bars * 8).Select(i => new MidiNote($"p{i}", i * 480L, 240, 60 + i % 12, 100)).ToImmutableArray();
    var outline = new Outline("Probe", bars, new Meter(), 48, [0], 0, 0, 0, 1);
    var section = new Section("probe", outline, [new("probe", "Probe", PartRole.Phrase, "Probe", 1, new("probe", PatternKind.Fixed, notes))]);
    var rig = new Rig(Rig.CurrentSchemaVersion, [new(outputId, SendClock: false)], [new("Probe", outputId)]);
    var plan = RouteResolver.Resolve(PlanCompiler.Compile(section), rig);
    host.Start(plan, [output], 120, false).Wait();
    while (host.Status.Running) Thread.Sleep(50);
    Thread.Sleep(500);
    var capture = host.Capture();
    var sent = capture.Entries.Where(e => !e.Input && (e.Status & 0xF0) == 0x90).ToArray();
    var received = arrivals.ToArray();
    var latencies = new List<double>();
    var next = 0;
    foreach (var s in sent)
    {
        var due = capture.Origin + s.Seconds;
        while (next < received.Length && received[next].Seconds < due - .05) next++;
        if (next < received.Length && received[next].Data1 == s.Data1) latencies.Add((received[next++].Seconds - due) * 1000);
    }
    latencies.Sort();
    double Q(double q) => latencies.Count == 0 ? double.NaN : latencies[(int)Math.Min(latencies.Count - 1, Math.Round(q * (latencies.Count - 1)))];
    var status = host.Status;
    Console.WriteLine(FormattableString.Invariant(
        $"sent {sent.Length}  matched {latencies.Count}  lost {sent.Length - latencies.Count}  late {status.LateNoteOns}  dropped inputs {status.DroppedInputs}"));
    Console.WriteLine(FormattableString.Invariant(
        $"latency ms  median {Q(.5):F2}  p95 {Q(.95):F2}  p99 {Q(.99):F2}  max {Q(1):F2}  min {Q(0):F2}"));
    if (status.Error is not null) Console.Error.WriteLine(status.Error);
    return latencies.Count == sent.Length && status.Error is null ? 0 : 2;
}
