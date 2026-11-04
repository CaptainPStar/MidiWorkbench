using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;
using MidiWorkbench.Specs;

var tests = new (string Name, Action Run)[]
{
    ("bounded take history and archive recovery", TakeHistorySpecs.Run),
    ("scheduler timing regressions", TimingRegressionSpecs.Run),
    ("backend safety suite", BackendSafetySpecs.Run),
    ("persistence workflow regressions", PersistenceWorkflowRegressionSpecs.Run),
    ("transport regression suite", TransportRegressionSpecs.Run),
    ("composition and recording workflow suite", WorkflowSpecs.Run),
    ("configurable generator suite", GeneratorSpecs.Run),
    ("end-to-end integration suite", IntegrationSpecs.Run),
    ("live generation suite", LiveGenerationSpecs.Run),
    ("live transport suite", LiveTransportSpecs.Run),
    ("batch note editing suite", NoteSelectionSpecs.Run),
    ("live workflow integration suite", ContinuationIntegrationSpecs.Run),
    // Generation
    ("generation is deterministic", () =>
    {
        var o = WorkbenchSession.CreateDemo().Sections[0].Outline;
        var g = new SectionGenerator();
        Check(Json(g.Generate(o)) == Json(g.Generate(o)), "Seed changed output");
    }),
    ("generation respects note and section bounds", () =>
    {
        foreach (var meter in new[] { new Meter(4,4), new Meter(7,8), new Meter(3,4), new Meter(5,4), new Meter(7,16), new Meter(12,8), new Meter(1,1) })
        for (uint seed = 0; seed < 30; seed++)
        foreach (var persona in Enum.GetValues<DrumPersona>())
        {
            var p = WorkbenchSession.CreateDemo();
            var o = p.Sections[0].Outline with { Meter = meter, Seed = seed, Bars = 2 };
            (p with { Sections = [new SectionGenerator().Generate(o, drummer: DrumPersonas.Settings(persona))] }).Validate();
        }
    }),
    ("locked edits survive regeneration", () =>
    {
        var section = WorkbenchSession.CreateDemo().Sections[0];
        var first = section.Parts[0]; var note = first.Pattern.Notes[0] with { Pitch = 49, Locked = true };
        first = first with { Pattern = first.Pattern with { Notes = [note] } };
        section = section with { Parts = section.Parts.SetItem(0, first) };
        var changed = new SectionGenerator().Generate(section.Outline with { Seed = 500 }, section);
        Check(changed.Parts[0].Pattern.Notes.Contains(note), "Lost locked edit");
    }),
    ("fixed patterns survive regeneration", () =>
    {
        var section = WorkbenchSession.CreateDemo().Sections[0]; var p = section.Parts[0];
        p = p with { Pattern = p.Pattern with { Kind = PatternKind.Fixed, Notes = [] } };
        section = section with { Parts = section.Parts.SetItem(0, p) };
        Check(new SectionGenerator().Generate(section.Outline, section).Parts[0].Pattern.Notes.Length == 0, "Recreated deleted notes");
    }),
    ("phrase parts are never generated", () =>
    {
        var section = WorkbenchSession.CreateDemo().Sections[0];
        var phrase = Build.Part("phrase-1", "Phrase", 1, new MidiNote("a", 0, 100, 60, 90, true)) ;
        var changed = new SectionGenerator().Generate(section.Outline with { Seed = 9 }, section with { Parts = section.Parts.Add(phrase) });
        Check(changed.Parts.Single(p => p.Id == "phrase-1") == phrase && changed.Parts.Count(p => p.Role == PartRole.Phrase) == 1, "Phrase changed");
    }),
    ("drummer port reproduces the original planner in 4/4, 3/4 and 6/8", () =>
    {
        var meters = new (string Name, Meter Meter)[] { ("FourFour", new(4, 4)), ("ThreeFour", new(3, 4)), ("SixEight", new(6, 8)) };
        var text = new StringBuilder();
        foreach (var persona in Enum.GetValues<DrumPersona>())
        foreach (var (name, meter) in meters)
        foreach (var seed in new[] { 1, 7, 12345 })
        foreach (var tweak in new[] { false, true })
        for (var variant = 0; variant < 3; variant++)
        for (var dynamics = 0; dynamics < 3; dynamics++)
        {
            var o = DrumPersonas.Apply(new DrumOptions(Bars: 4, Seed: seed), persona);
            if (tweak) o = o with { Complexity = .9, FillAmount = .8, VariationAmount = .9, HalfTime = !o.HalfTime };
            text.Append($"{persona}|{name}|{seed}|{tweak}|{variant}|{dynamics}:");
            foreach (var h in DrumPlanner.Plan(o, meter, variant, dynamics)) text.Append($"{h.Tick}:{h.Note}:{h.Velocity};");
            text.Append('\n');
        }
        // SHA-256 of the same listing produced by the original planner (positions × 960, note, velocity).
        Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))) ==
            "F1F4FFE9D5CE230EF016901CBE004C6E4C48153BA8C9C4636D1FEF9176842C50", "Drum output differs from the original planner");
    }),
    ("drummer is deterministic and keeps a downbeat kick in odd meters", () =>
    {
        foreach (var meter in new[] { new Meter(7, 8), new Meter(5, 4), new Meter(7, 16) })
        foreach (var persona in Enum.GetValues<DrumPersona>())
        {
            var o = DrumPersonas.Apply(new DrumOptions(Bars: 3, Seed: 3), persona);
            var a = DrumPlanner.Plan(o, meter); var b = DrumPlanner.Plan(o, meter);
            Check(a.SequenceEqual(b), "Not deterministic");
            Check(a.All(h => h.Tick >= 0 && h.Tick < 3 * meter.BarTicks && h.Velocity is >= 1 and <= 127), "Hit outside section");
            for (var bar = 0; bar < 3; bar++) Check(a.Any(h => h.Tick == bar * meter.BarTicks && h.Note == 36), $"No downbeat kick in {meter}");
        }
    }),
    ("drummer recipe is recorded and kept until changed", () =>
    {
        var g = new SectionGenerator(); var o = WorkbenchSession.CreateDemo().Sections[0].Outline;
        var jazz = g.Generate(o, drummer: DrumPersonas.Settings(DrumPersona.Jazz));
        var drums = jazz.Parts.Single(p => p.Role == PartRole.Drums).Pattern.Recipe!;
        Check(drums.Generator == "drummer" && drums.Drummer!.Persona == DrumPersona.Jazz && drums.Version == DrumPlanner.Version, "Recipe missing");
        var again = g.Generate(o with { Seed = 4 }, jazz).Parts.Single(p => p.Role == PartRole.Drums).Pattern.Recipe!;
        Check(again.Drummer!.Persona == DrumPersona.Jazz, "Persona lost on regeneration");
        var basicSection = g.Generate(o, jazz, basicDrums: true);
        var basic = basicSection.Parts.Single(p => p.Role == PartRole.Drums).Pattern.Recipe!;
        Check(basic.Generator == "scaffold" && basic.Drummer is null, "Basic drums not selected");
        Check(g.Generate(o, basicSection).Parts.Single(p => p.Role == PartRole.Drums).Pattern.Recipe!.Generator == "scaffold", "Generator silently changed");
    }),

    // Time and validation
    ("tempo edits preserve position", () =>
    {
        var clock = new TempoClock(); clock.Start(10, 120); var position = clock.TickAt(11);
        clock.SetTempo(11, 60); Check(clock.TickAt(11) == position, "Position jumped");
        Check(clock.TickAt(12) == position + MusicalTime.Ppq, "New tempo not applied");
        var map = new TempoMap(120, [new(1, 60)]);
        Check(map.TickAt(2) == 2 * MusicalTime.Ppq + MusicalTime.Ppq, "Tempo map wrong");
    }),
    ("7/8 uses seven eighth notes", () => Check(new Meter(7,8).BarTicks == 3360, "Meter length wrong")),
    ("invalid parameters are rejected", () =>
    {
        Throws(() => (WorkbenchSession.CreateDemo() with { Bpm = double.NaN }).Validate());
        Throws(() => new Meter(4,3).Validate());
        Throws(() => (WorkbenchSession.CreateDemo() with { SchemaVersion = 99 }).Validate());
        Throws(() => ProjectStore.Parse("""{"schemaVersion":99}"""));
        Throws(() => (Build.Rig(("A", "x")) with { Routes = [new("A", "missing")] }).Validate());
        Throws(() => (Build.Rig(("A", "x")) with { Endpoints = [new("x", OffsetMilliseconds: -1)] }).Validate());
        Throws(() => new DrummerSettings(Swing: .9).Validate());
    }),
    ("schema 1 projects migrate explicitly", () =>
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(WorkbenchSession.CreateDemo(), ProjectStore.JsonOptions))!.AsObject();
        node["schemaVersion"] = 1;
        foreach (var part in node["sections"]![0]!["parts"]!.AsArray())
        {
            var pattern = part!["pattern"]!.AsObject(); pattern.Remove("recipe");
            pattern["generatorVersion"] = "scaffold-1"; pattern["generatorSeed"] = 1;
        }
        var migrated = ProjectStore.Parse(node.ToJsonString());
        Check(migrated.SchemaVersion == SongProject.CurrentSchemaVersion, "Version not migrated");
        Check(migrated.Sections[0].Parts.All(p => p.Pattern.Recipe is { Generator: "scaffold", Version: "scaffold-1", Seed: 1 }), "Recipe not migrated");
        node["sections"]![0]!["parts"]![0]!["pattern"]!["unexpected"] = 1;
        Throws(() => ProjectStore.Parse(node.ToJsonString()), typeof(JsonException));
    }),
    ("project and rig roundtrip preserve data", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var p = WorkbenchSession.CreateDemo(); ProjectStore.Save(path, p);
            Check(Json(p) == Json(ProjectStore.Load(path)), "Project changed");
            var rig = Build.Rig(("Drums", "a"), ("Bass", "b")) with { Input = "loopback", Routes = [new("Drums", "a", 10), new("Bass", "b")] };
            ProjectStore.SaveRig(path, rig);
            Check(Json(rig) == Json(ProjectStore.LoadRig(path)), "Rig changed");
        }
        finally { File.Delete(path); }
    }),

    // Routing
    ("missing route names the destination", () =>
    {
        var section = Build.Section(1, Build.Part("a", "Lead", 1, new MidiNote("n", 0, 10, 60, 90)));
        try { Build.Plan(section, Build.Rig(("Other", "memory"))); throw new Exception("Resolved without route"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("'Lead'"), "Destination not named"); }
    }),
    ("route channel override applies on the wire", () =>
    {
        var section = Build.Section(1, Build.Part("a", "Lead", 1, new MidiNote("n", 0, 10, 60, 90)));
        var rig = Build.Rig(("Lead", "memory")) with { Routes = [new("Lead", "memory", 5)] };
        Check(Build.Plan(section, rig).Events.All(e => (e.Status & 15) == 4), "Channel not overridden");
    }),
    ("destinations sharing an endpoint get one clock stream", () =>
    {
        var section = Build.Section(1, Build.Part("a", "A", 1, new MidiNote("n", 0, 10, 60, 90)), Build.Part("b", "B", 2, new MidiNote("n", 0, 10, 60, 90)));
        var (t, sent) = Engine(); t.Start(Build.Plan(section), 0, 120, false);
        for (var i = 0; i < 500; i++) t.Pump(i / 1000d, i / 1000d);
        Check(sent.Count(s => s.M.Status == 0xF8) == 24 && sent.Count(s => s.M.Status == 0xFA) == 1, "Clock duplicated per destination");
    }),
    ("shared endpoint channel and pitch release only after the last owner", () =>
    {
        var section = Build.Section(1, Build.Part("a", "A", 1, new MidiNote("n", 0, 960, 60, 90)), Build.Part("b", "B", 1, new MidiNote("n", 480, 1440, 60, 90)));
        var (t, sent) = Engine(); t.Start(Build.Plan(section), 0, 120, false);
        for (var i = 0; i <= 1100; i++) t.Pump(i / 1000d, i / 1000d);
        var notes = sent.Where(s => (s.M.Status & 0xF0) is 0x80 or 0x90).ToArray();
        Check(notes.Length == 2 && notes[0].M.Status == 0x90 && notes[1].M.Status == 0x80 && Math.Abs(notes[1].M.Seconds - 1) < 1e-9, "Owner cut another destination's note");
    }),
    ("endpoint offset delays notes and clock", () =>
    {
        var section = Build.Section(1, Build.Part("a", "A", 1, new MidiNote("n", 480, 10, 60, 90)));
        var rig = Build.Rig(("A", "memory")) with { Endpoints = [new("memory", OffsetMilliseconds: 20)] };
        var (t, sent) = Engine(); t.Start(Build.Plan(section, rig), 0, 120, false);
        for (var i = 0; i <= 300; i++) t.Pump(i / 1000d, i / 1000d);
        Check(Math.Abs(sent.First(s => s.M.Status == 0x90).M.Seconds - .27) < 1e-9, "Note offset missing");
        Check(Math.Abs(sent.First(s => s.M.Status == 0xF8).M.Seconds - .02) < 1e-9, "Clock offset missing");
    }),

    // Transport
    ("stop releases sustained notes", () =>
    {
        var (t, sent) = Engine();
        t.Start(Build.Plan(new MidiNote("a",0,1920,60,90)), 0, 120, false); t.Pump(0, 0); t.Stop(.1);
        Check(sent.Count(e => e.M.Status == 0x90) == 1, "Missing note-on");
        Check(sent.Count(e => e.M.Status == 0x80) == 1 && sent.Last().M.Status == 0xFC, "Missing release or stop");
        Check(!t.Running, "Transport still running");
    }),
    ("stop releases after messages already scheduled", () =>
    {
        var (t, sent) = Engine();
        t.Start(Build.Plan(new MidiNote("a",0,3840,60,90), new MidiNote("b",480,480,62,90)), 0, 120, false); t.Pump(0, .5); t.Stop(.1);
        var last = sent.Where(s => s.M.Status != 0x80 && s.M.Status != 0xFC).Max(s => s.M.Seconds);
        Check(sent.Where(s => s.M.Status is 0x80 or 0xFC).All(s => s.M.Seconds >= last), "Release precedes a scheduled attack");
    }),
    ("MIDI clock is 24 PPQN", () =>
    {
        var (t, sent) = Engine(); t.Start(Build.Plan(),0,120,false);
        for (var i = 0; i < 500; i++) t.Pump(i / 1000d, i / 1000d);
        Check(sent.Count(e => e.M.Status == 0xF8) == 24, "Clock pulse count wrong");
    }),
    ("late note-ons are skipped", () =>
    {
        var (t, sent) = Engine();
        t.Start(Build.Plan(new MidiNote("a",0,1920,60,90)),0,120,false); t.Pump(.1,.1); t.Stop(.2);
        Check(t.LateNoteOns == 1 && !sent.Any(e => (e.M.Status & 0xF0) == 0x90), "Late attack sent");
    }),
    ("skipped owner cannot release another owner", () =>
    {
        var (t, sent) = Engine();
        t.Start(Build.Plan(new MidiNote("early",0,1000,60,90), new MidiNote("late",100,100,60,90)),0,120,false);
        t.Pump(0,0); t.Pump(.2,.2);
        Check(!sent.Any(e => e.M.Status == 0x80), "Skipped note released sustained note");
        t.Stop(.21); Check(sent.Count(e => e.M.Status == 0x80) == 1, "Final release missing");
    }),
    ("equal-time release precedes repeat attack", () =>
    {
        var plan = Build.Plan(new MidiNote("a",0,480,60,90), new MidiNote("b",480,480,60,90));
        var pair = plan.Events.Where(e => e.Tick == 480).ToArray();
        Check(pair[0].Status == 0x80 && pair[1].Status == 0x90, "Ordering wrong");
    }),
    ("a stalled host does not burst a backlog", () =>
    {
        var notes = Enumerable.Range(0, 8).Select(i => new MidiNote($"n{i}", i * 480L, 240, 60, 90)).ToArray();
        var (t, sent) = Engine(); t.Start(Build.Plan(notes), 0, 120, false);
        t.Pump(0, 0); var before = sent.Count; t.Pump(1.5, 1.5);
        var burst = sent.Skip(before).ToArray();
        // Only messages inside the 25 ms late window survive: one attack and two pulses.
        Check(burst.Count(s => s.M.Status == 0x90) == 1 && burst.Count(s => s.M.Status == 0xF8) == 2, "Backlog burst sent");
        Check(t.LateNoteOns == 5 && t.SkippedClocks > 60 && burst.Any(s => s.M.Status == 0x80), "Late policy not applied");
    }),
    ("loop wraps with continuous clock", () =>
    {
        var (t, sent) = Engine(); t.Start(Build.Plan(new MidiNote("a",0,480,60,90)), 0, 120, true);
        for (var i = 0; i <= 3980; i++) t.Pump(i / 1000d, i / 1000d + .01);
        Check(t.Running && !sent.Any(s => s.M.Status == 0xFC), "Loop stopped");
        Check(sent.Count(s => s.M.Status == 0x90) == 2 && sent.Count(s => s.M.Status == 0xF8) == 192, "Loop wrap wrong");
        Check(Math.Abs(sent.Where(s => s.M.Status == 0x90).Last().M.Seconds - 2) < 1e-9, "Second pass not at bar 2");
    }),
    ("panic releases cancelled releases and sends all notes off", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("memory", true);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: 1));
        host.Start(Build.Plan(new MidiNote("a",0,480,60,90), new MidiNote("b",960,480,62,90)), [port], 120, false);
        host.Step(0); now = .1; host.Panic(); host.Step(.1);
        Check(port.Count(0x90) == 1 && port.Sent.Any(s => s.Message is { Status: 0x80, Data1: 60, Seconds: .1 }), "Cancelled release not re-sent");
        Check(port.Sent.Any(s => s.Message is { Status: 0xB0, Data1: 123 }) && !host.Status.Running, "No all-notes-off");
    }),

    ("a completed command is visible in status", () =>
    {
        var backend = new MemoryBackend();
        using var host = new TransportHost(new HostOptions(Background: false));
        var started = host.Start(Build.Plan(new MidiNote("a",0,100,60,90)), [backend.OpenOutput("memory")], 120, false);
        Check(!started.IsCompleted, "Completed before the worker ran");
        host.Step(0);
        Check(started.IsCompletedSuccessfully && host.Status.Running && host.Status.Take == 1, "Status lags the command");
    }),

    // Adapter conformance (FakeBackend stands in for a device)
    ("adapter: disconnect mid-note stops and releases other endpoints", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now);
        var a = backend.Add("A", false); var b = backend.Add("B", false);
        var section = Build.Section(1, Build.Part("a", "A", 1, new MidiNote("n", 0, 3000, 60, 90)), Build.Part("b", "B", 1, new MidiNote("n", 0, 3000, 62, 90)));
        using var host = new TransportHost(new HostOptions(Background: false));
        var plan = Build.Plan(section, Build.Rig(("A", "A"), ("B", "B")));
        host.Start(plan, [a, b], 120, false); host.Step(0);
        a.Disconnect(); now = .1; host.Step(.1); now = .2; host.Step(.2);
        Check(!host.Status.Running && host.Status.Error!.Contains("'A'"), "Fault not reported");
        Check(b.Count(0x80) == 1 && b.Sent.Any(s => s.Message.Status == 0xFC), "Other endpoint not released");
    }),
    ("adapter: stop releases owned notes", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(Build.Plan(new MidiNote("a",0,3000,60,90)), [port], 120, false); host.Step(0);
        host.Stop(); now = .05; host.Step(.05); now = .1; host.Step(.1);
        Check(port.Count(0x90) == 1 && port.Count(0x80) == 1 && port.Sent[^1].Message.Status == 0xFC, "Stop did not release");
    }),
    ("adapter: shutdown flushes queued releases", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .05));
        host.Start(Build.Plan(new MidiNote("a",0,3000,60,90)), [port], 120, false); host.Step(0);
        now = .01; host.Step(.01);
        host.Dispose();
        Check(port.Count(0x90) == 1 && port.Count(0x80) == 1, "Release lost on shutdown");
    }),
    ("adapter: restart flushes releases queued by the previous stop", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .05));
        var plan = Build.Plan(new MidiNote("a",0,3000,60,90));
        host.Start(plan, [port], 120, false); host.Step(0);
        now = .01; host.Step(.01); host.Stop(); host.Start(plan, [port], 120, false); host.Step(.01);
        Check(port.Count(0x80) == 1 && port.Count(0x90) == 2, "Release lost on restart");
    }),
    ("adapter: backlog beyond capacity faults instead of growing", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: 1, PendingCapacity: 4));
        var notes = Enumerable.Range(0, 16).Select(i => new MidiNote($"n{i}", i * 60L, 30, 60 + i, 90)).ToArray();
        host.Start(Build.Plan(notes), [port], 120, false); host.Step(0);
        Check(host.Status.Error?.Contains("backlog") == true && !host.Status.Running, "Backlog not bounded");
    }),
    ("adapter: immediate endpoints receive messages at their deadlines in order", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("memory", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(Build.Plan(new MidiNote("a",0,240,60,90), new MidiNote("b",480,240,62,90), new MidiNote("c",960,240,64,90)), [port], 120, false);
        for (var i = 0; i <= 700; i++) { now = i / 1000d; host.Step(now); }
        Check(port.Sent.Count > 30 && port.Sent.All(s => s.SubmittedAt >= s.Message.Seconds - 1e-9 && s.SubmittedAt - s.Message.Seconds < .0011), "Deadline missed");
        Check(port.Sent.Zip(port.Sent.Skip(1)).All(p => p.First.Message.Seconds <= p.Second.Message.Seconds), "Out of order");
    }),
    ("adapter: scheduled endpoints receive exact timestamps within the lookahead", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("memory", true);
        using var host = new TransportHost(new HostOptions(Background: false, LookaheadSeconds: .01));
        host.Start(Build.Plan(new MidiNote("a",480,240,60,90)), [port], 120, false);
        for (var i = 0; i <= 400; i++) { now = i / 1000d; host.Step(now); }
        var on = port.Sent.Single(s => s.Message.Status == 0x90);
        Check(on.Message.Seconds == .25 && on.SubmittedAt <= .25 && .25 - on.SubmittedAt <= .0101, "Timestamp or lookahead wrong");
    }),
    ("adapter: thru re-channels input and panic releases it", () =>
    {
        var now = 0d; var backend = new FakeBackend(() => now); var port = backend.Add("synth", false);
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Arm(port, 3); host.Step(0);
        host.PostInput(new(0, 0x90, 64, 100)); host.Step(.01);
        host.Panic(); host.Step(.02);
        Check(port.Sent[0].Message is { Status: 0x92, Data1: 64 } && port.Sent.Any(s => s.Message is { Status: 0x82, Data1: 64 }), "Thru wrong");
    }),
    ("adapter: input overflow is counted, never blocks", () =>
    {
        using var host = new TransportHost(new HostOptions(Background: false, InputCapacity: 4));
        for (var i = 0; i < 10; i++) host.PostInput(new(0, 0x90, 60, 90));
        host.Step(0);
        Check(host.Status.DroppedInputs == 6, "Dropped input not reported");
    }),

    // Capture and recording
    ("capture capacity is explicit", () =>
    {
        var backend = new MemoryBackend();
        using var host = new TransportHost(new HostOptions(Background: false, CaptureCapacity: 1));
        host.Start(Build.Plan(new MidiNote("a",0,100,60,90)), [backend.OpenOutput("memory")], 120, false); host.Step(0);
        Check(host.Status.Captured == 1 && !host.Status.CaptureComplete, "Overflow hidden");
    }),
    ("simulation ends with stop and balanced notes", () =>
    {
        var p = WorkbenchSession.CreateDemo();
        var plan = Build.Plan(p.Sections[0], Rig.Memory(WorkbenchSession.Destinations(p)));
        var backend = new MemoryBackend();
        using var host = new TransportHost(new HostOptions(Background: false));
        host.Start(plan, [backend.OpenOutput("memory")], 120, false);
        for (var i = 0; i <= 16100; i++) host.Step(i / 1000d);
        var take = WorkbenchSession.ToTake(host.Capture(), null);
        Check(!host.Status.Running && take.Complete, "Did not finish");
        Check(take.Outputs.Count(e => (e.Status & 0xF0) == 0x90) == take.Outputs.Count(e => (e.Status & 0xF0) == 0x80), "Unbalanced notes");
        Check(take.Outputs.All(e => e.Endpoint == "memory") && take.Actions.Select(a => a.Kind).SequenceEqual(["start", "end"]), "Take metadata wrong");
    }),
    ("recorder handles loop wrap, sustain and held notes", () =>
    {
        CaptureEntry In(double s, byte status, byte d1, byte d2) => new(s, true, -1, -1, status, d1, d2);
        var inputs = new[] { In(.25, 0x90, 60, 80), In(.5, 0x80, 60, 0), In(.6, 0xB0, 64, 127), In(.75, 0x90, 62, 70),
            In(1, 0x80, 62, 0), In(1.25, 0xB0, 64, 0), In(2.1, 0x90, 64, 60), In(2.2, 0x90, 64, 0), In(2.5, 0x90, 65, 50) };
        var notes = PhraseRecorder.ToNotes(inputs, new TempoMap(120, []), 3840, true, MusicalTime.Ppq * 5.5, "r");
        Check(notes.Any(n => n is { Pitch: 60, Tick: 480, Duration: 480, Velocity: 80, Locked: true }), "Plain note wrong");
        Check(notes.Any(n => n is { Pitch: 62, Tick: 1440, Duration: 960 }), "Sustain ignored");
        Check(notes.Any(n => n is { Pitch: 64, Tick: 192, Duration: 192 }), "Loop wrap wrong");
        Check(notes.Any(n => n is { Pitch: 65, Tick: 960, Duration: 480 }), "Held note not closed at end");
    }),
    ("loopback input is recorded against the transport timeline", () =>
    {
        var now = 0d; var backend = new MemoryBackend(() => now);
        using var host = new TransportHost(new HostOptions(Background: false));
        using var input = backend.OpenInput("loopback", host.PostInput);
        var keys = backend.OpenOutput("loopback");
        host.Start(Build.Plan(), [backend.OpenOutput("memory")], 120, false);
        for (var i = 0; i <= 2100; i++)
        {
            now = i / 1000d;
            if (i == 500) keys.Submit(new(now, 0x90, 67, 99));
            if (i == 1000) keys.Submit(new(now, 0x80, 67, 0));
            host.Step(now);
        }
        var capture = host.Capture();
        var notes = PhraseRecorder.ToNotes(capture, "r");
        Check(notes.Length == 1 && notes[0] is { Pitch: 67, Tick: 960, Duration: 960, Velocity: 99 }, "Recorded note wrong");
        Check(WorkbenchSession.ToTake(capture, "loopback").Inputs.Length == 2, "Inputs not separated");
    }),
    ("session records a keyboard phrase into a new part", () =>
    {
        var backend = new MemoryBackend();
        using var session = new WorkbenchSession(new SessionOptions(backend));
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "loopback" });
        session.SetTempo(240);
        session.Generate(session.Project.Sections[0].Outline with { Bars = 1 });
        session.Arm(WorkbenchSession.NewPhrase);
        var keys = backend.OpenOutput("loopback");
        session.Start();
        Thread.Sleep(250); keys.Submit(new(0, 0x90, 72, 90)); Thread.Sleep(250); keys.Submit(new(0, 0x80, 72, 0));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Status().Running && DateTime.UtcNow < deadline) Thread.Sleep(20);
        session.Status();
        var phrase = session.Project.Sections[0].Parts.SingleOrDefault(p => p.Role == PartRole.Phrase);
        Check(phrase is not null && phrase.Pattern.Notes.Length == 1 && phrase.Pattern.Notes[0] is { Pitch: 72, Locked: true }, "Phrase not committed");
        var n = phrase!.Pattern.Notes[0];
        Check(Math.Abs(n.Tick - 960) < 240 && Math.Abs(n.Duration - 960) < 240, $"Phrase timing far off: {n.Tick}/{n.Duration}");
        Check(session.Armed == phrase.Id, "Overdub target not kept");
    }),

    // Files
    ("standard MIDI file export round-trips notes and tempo", () =>
    {
        var p = WorkbenchSession.CreateDemo();
        var bytes = MidiFile.FromSection(p.Sections[0], 90);
        var (tracks, events, tempo) = ReadSmf(bytes);
        Check(tracks == p.Sections[0].Parts.Length + 1, "Track count wrong");
        Check(events.Count(e => (e.Status & 0xF0) == 0x90) == p.Sections[0].Parts.Sum(x => x.Pattern.Notes.Length), "Note count wrong");
        Check(tempo == 666667, "Tempo wrong");
        Check(events.All(e => e.Tick <= p.Sections[0].Outline.LengthTicks), "Event beyond section");
    }),
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length-failed}/{tests.Length} passed"); return failed == 0 ? 0 : 1;

static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectStore.JsonOptions);
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Throws(Action action, Type? type = null)
{
    try { action(); }
    catch (Exception ex) when (type is null ? ex is ArgumentException or InvalidDataException : type.IsInstanceOfType(ex)) { return; }
    throw new Exception("Expected validation failure");
}
static (Transport T, List<(int Port, int Dest, TimedMidi M)> Sent) Engine()
{
    var sent = new List<(int, int, TimedMidi)>();
    return (new Transport((p, d, m) => sent.Add((p, d, m))), sent);
}
static (int Tracks, List<(long Tick, byte Status)> Events, int Tempo) ReadSmf(byte[] b)
{
    int Be(int at, int n) { var v = 0; for (var i = 0; i < n; i++) v = v << 8 | b[at + i]; return v; }
    if (Encoding.ASCII.GetString(b, 0, 4) != "MThd" || Be(8, 2) != 1 || Be(12, 2) != MusicalTime.Ppq) throw new Exception("Bad header");
    var tracks = Be(10, 2); var events = new List<(long, byte)>(); var tempo = 0; var at = 14;
    for (var t = 0; t < tracks; t++)
    {
        if (Encoding.ASCII.GetString(b, at, 4) != "MTrk") throw new Exception("Bad track");
        var end = at + 8 + Be(at + 4, 4); at += 8; long tick = 0;
        long Var() { long v = 0; byte x; do { x = b[at++]; v = v << 7 | (uint)(x & 0x7F); } while ((x & 0x80) != 0); return v; }
        while (at < end)
        {
            tick += Var(); var status = b[at++];
            if (status == 0xFF) { var type = b[at++]; var len = (int)Var(); if (type == 0x51) tempo = Be(at, 3); at += len; }
            else { events.Add((tick, status)); at += (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2; }
        }
    }
    return (tracks, events, tempo);
}
