using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Midi.Windows;
using MidiWorkbench.Persistence;

const string usage = """
    Usage:
      new PROJECT
      validate PROJECT
      upgrade PROJECT                rewrite in the current schema
      simulate PROJECT TAKE          synthetic timing, memory output
      simulate-song PROJECT TAKE     play the arranged song in memory
      export PROJECT FILE.mid [--rig RIG.json]       default: stored project channels
      export-song PROJECT FILE.mid [--rig RIG.json]
      sections PROJECT
      generate PROJECT SECTION PART SETTINGS.json OUTPUT
      vary PROJECT SECTION SEED OUTPUT
      validate-take TAKE
      export-take TAKE FILE.mid
      bank-create BANK PROJECT [PROJECT ...]
      bank-list BANK
      bank-export BANK SONG_ID PROJECT
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
        case ["simulate-song", var source, var target]:
            return Simulate(ProjectStore.Load(source), target, true);
        case ["sections", var source]:
            foreach (var section in ProjectStore.Load(source).Sections)
                Console.WriteLine($"{section.Id}\t{section.Outline.Name}\t{section.Outline.Bars} bars\t{section.Outline.Meter.Numerator}/{section.Outline.Meter.Denominator}");
            return 0;
        case ["generate", var source, var sectionId, var partId, var settingsPath, var target]:
            var generatedProject = ProjectStore.Load(source);
            var settings = JsonSerializer.Deserialize<GeneratorSettings>(File.ReadAllText(settingsPath), ProjectStore.JsonOptions)
                ?? throw new ArgumentException("Generator settings are empty.");
            var sectionIndex = Enumerable.Range(0, generatedProject.Sections.Length).FirstOrDefault(i => generatedProject.Sections[i].Id == sectionId, -1);
            if (sectionIndex < 0) throw new ArgumentException("Unknown section.");
            var selected = generatedProject.Sections[sectionIndex];
            var partIndex = Enumerable.Range(0, selected.Parts.Length).FirstOrDefault(i => selected.Parts[i].Id == partId, -1);
            if (partIndex < 0) throw new ArgumentException("Unknown part.");
            var part = selected.Parts[partIndex];
            var pattern = new PatternGenerator().Generate(selected.Outline, settings, part.Pattern);
            selected = selected with { Parts = selected.Parts.SetItem(partIndex, part with { Pattern = pattern }) };
            ProjectStore.Save(target, generatedProject with { Sections = generatedProject.Sections.SetItem(sectionIndex, selected) });
            Console.WriteLine(target); return 0;
        case ["vary", var source, var sectionId, var seedText, var target]:
            if (!uint.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
                throw new ArgumentException("Seed must be an unsigned 32-bit integer.");
            using (var variation = new WorkbenchSession())
            {
                variation.Load(source); variation.SelectSection(sectionId);
                variation.PrepareVariationAsync(seed).GetAwaiter().GetResult();
                variation.AcceptGeneration(); ProjectStore.Save(target, variation.Project);
            }
            Console.WriteLine(target); return 0;
        case ["validate-take", var source]:
            var validatedTake = ProjectStore.LoadTake(source);
            Console.WriteLine($"{validatedTake.Outputs.Length} outputs; {validatedTake.Inputs.Length} inputs; complete {validatedTake.Complete}"); return 0;
        case ["export-take", var source, var target]:
            var exportTake = ProjectStore.LoadTake(source);
            var tempoMap = new TempoMap(exportTake.Bpm, exportTake.TempoChanges);
            ProjectStore.WriteAtomic(target, MidiFile.FromTake(exportTake, exportTake.Meter ?? new Meter(), tempoMap.TickAt, tempoMap.Changes));
            Console.WriteLine(target); return 0;
        case ["export", var source, var target, ..var exportOptions]:
            var sectionRig = ExportRig(exportOptions);
            using (var sectionSession = new WorkbenchSession())
            {
                sectionSession.Load(source);
                sectionSession.ExportMidi(target, sectionRig is null ? ExportChannelMode.ProjectChannels : ExportChannelMode.RoutingOverrides, sectionRig);
            }
            Console.WriteLine(target); return 0;
        case ["export-song", var source, var target, ..var songOptions]:
            var songRig = ExportRig(songOptions);
            using (var session = new WorkbenchSession())
            {
                session.Load(source);
                session.ExportArrangementMidi(target, songRig is null ? ExportChannelMode.ProjectChannels : ExportChannelMode.RoutingOverrides, songRig);
            }
            Console.WriteLine(target); return 0;
        case ["bank-create", var bankPath, ..var sources] when sources.Length > 0:
            var bankSongs = sources.Select(path => new BankSong(Guid.NewGuid().ToString("N"), ProjectStore.Load(path))).ToImmutableArray();
            ProjectStore.SaveBank(bankPath, new SongBank(SongBank.CurrentSchemaVersion, Path.GetFileNameWithoutExtension(bankPath), bankSongs, bankSongs.Select(s => s.Id).ToImmutableArray()));
            Console.WriteLine(bankPath); return 0;
        case ["bank-list", var bankPath]:
            var bank = ProjectStore.LoadBank(bankPath);
            foreach (var id in bank.Order)
            {
                var song = bank.Songs.First(s => s.Id == id);
                Console.WriteLine($"{song.Id}\t{song.Project.Name}");
            }
            return 0;
        case ["bank-export", var bankPath, var songId, var target]:
            var bankSong = ProjectStore.LoadBank(bankPath).Songs.FirstOrDefault(s => s.Id == songId) ?? throw new ArgumentException("Unknown bank song.");
            ProjectStore.Save(target, bankSong.Project); Console.WriteLine(target); return 0;
        case ["devices"]:
            using (var backend = Backend())
                foreach (var e in backend.Endpoints()) Console.WriteLine($"{(e.Output ? "out" : "   ")} {(e.Input ? "in" : "  ")}  {e.Id}");
            return 0;
        case ["probe", var output, var input, ..var rest] when rest.Length <= 1:
            var seconds = rest.Length == 1 ? double.Parse(rest[0], CultureInfo.InvariantCulture) : 10;
            if (!double.IsFinite(seconds) || seconds is < 1 or > 512) throw new ArgumentException("Probe duration must be 1–512 seconds.");
            return Probe(output, input, seconds);
        default:
            Console.Error.WriteLine(usage); return 1;
    }
}
catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or JsonException or MidiPortException or FormatException)
{ Console.Error.WriteLine(ex.Message); return 2; }

static IMidiBackend Backend() => WindowsMidi.CreateBackend() is { } windows ? new CompositeBackend(new MemoryBackend(), windows) : new MemoryBackend();

// Export consumes routing data only. It never installs the rig or opens its input/output devices.
static Rig? ExportRig(string[] options) => options switch
{
    [] => null,
    ["--rig", var path] => ProjectStore.LoadRig(path),
    _ => throw new ArgumentException("Export options: --rig RIG.json (channel overrides only).")
};

static int Simulate(SongProject p, string target, bool song = false)
{
    var plan = RouteResolver.Resolve(song ? ArrangementCompiler.Compile(p) : PlanCompiler.Compile(p.Sections[0]), Rig.Memory(WorkbenchSession.Destinations(p)));
    using var backend = new MemoryBackend();
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
    using var input = backend.OpenInput(inputId, m => { if (m.Status == 0xF8 || (m.Status & 0xF0) == 0x90 && m.Data2 > 0) arrivals.Enqueue(m); });
    using var output = backend.OpenOutput(outputId);
    var bars = Math.Max(1, (int)Math.Ceiling(seconds / 2));
    var notes = Enumerable.Range(0, bars * 8).Select(i => new MidiNote($"p{i}", i * 480L, 240, 60 + i % 12, 100)).ToImmutableArray();
    var outline = new Outline("Probe", bars, new Meter(), 48, [0], 0, 0, 0, 1);
    var section = new Section("probe", outline, [new("probe", "Probe", PartRole.Phrase, "Probe", 1, new("probe", PatternKind.Fixed, notes))]);
    var rig = new Rig(Rig.CurrentSchemaVersion, [new(outputId, SendClock: true)], [new("Probe", outputId)]);
    var plan = RouteResolver.Resolve(PlanCompiler.Compile(section), rig);
    host.Start(plan, [output], 120, false).Wait();
    while (host.Status.Running) Thread.Sleep(50);
    Thread.Sleep(500);
    var capture = host.Capture();
    var sent = capture.Entries.Where(e => !e.Input && (e.Status & 0xF0) == 0x90).ToArray();
    var allReceived = arrivals.ToArray();
    var received = allReceived.Where(m => m.Status != 0xF8).ToArray();
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
        $"planned {notes.Length}  submitted {sent.Length}  matched {latencies.Count}  missing {notes.Length - latencies.Count}  late {status.LateNoteOns}"));
    Console.WriteLine(FormattableString.Invariant(
        $"loopback latency ms  median {Q(.5):F2}  p95 {Q(.95):F2}  p99 {Q(.99):F2}  max {Q(1):F2}  min {Q(0):F2}"));
    var clocks = allReceived.Where(m => m.Status == 0xF8).ToArray();
    var expectedClocks = bars * 4 * 24;
    var spacing = clocks.Zip(clocks.Skip(1), (a, b) => Math.Abs((b.Seconds - a.Seconds - 60d / 120 / 24) * 1000)).Order().ToArray();
    if (spacing.Length > 0)
    {
        var p99 = spacing[(int)Math.Min(spacing.Length - 1, Math.Round((spacing.Length - 1) * .99))];
        var spanError = (clocks[^1].Seconds - clocks[0].Seconds - (clocks.Length - 1) * 60d / 120 / 24) * 1000;
        Console.WriteLine(FormattableString.Invariant($"clock {clocks.Length}/{expectedClocks}  spacing error ms p99 {p99:F2} max {spacing[^1]:F2}  span error ms {spanError:F2}  skipped {status.SkippedClocks}"));
    }
    if (status.Error is not null) Console.Error.WriteLine(status.Error);
    return latencies.Count == notes.Length && clocks.Length == expectedClocks && status.Error is null ? 0 : 2;
}
