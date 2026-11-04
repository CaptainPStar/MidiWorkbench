using System.Collections.Immutable;
using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

var tests = new (string Name, Action Run)[]
{
    ("generation is deterministic", () =>
    {
        var o = WorkbenchSession.CreateDemo().Sections[0].Outline;
        var g = new SectionGenerator();
        Check(JsonSerializer.Serialize(g.Generate(o)) == JsonSerializer.Serialize(g.Generate(o)), "Seed changed output");
    }),
    ("generation respects note and section bounds", () =>
    {
        foreach (var meter in new[] { new Meter(4,4), new Meter(7,8), new Meter(3,4) })
        for (uint seed = 0; seed < 50; seed++)
        {
            var p = WorkbenchSession.CreateDemo();
            var o = p.Sections[0].Outline with { Meter = meter, Seed = seed, Bars = 2 };
            (p with { Sections = [new SectionGenerator().Generate(o)] }).Validate();
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
    ("tempo edits preserve position", () =>
    {
        var clock = new TempoClock(); clock.Start(10, 120); var position = clock.TickAt(11);
        clock.SetTempo(11, 60); Check(clock.TickAt(11) == position, "Position jumped");
        Check(clock.TickAt(12) == position + MusicalTime.Ppq, "New tempo not applied");
    }),
    ("7/8 uses seven eighth notes", () => Check(new Meter(7,8).BarTicks == 3360, "Meter length wrong")),
    ("invalid parameters are rejected", () =>
    {
        Throws(() => (WorkbenchSession.CreateDemo() with { Bpm = double.NaN }).Validate());
        Throws(() => new Meter(4,3).Validate());
        Throws(() => (WorkbenchSession.CreateDemo() with { SchemaVersion = 99 }).Validate());
    }),
    ("stop releases sustained notes", () =>
    {
        var sink = new MemoryMidiSink(); var t = new Transport(sink);
        t.Start(Plan(new MidiNote("a",0,1920,60,90)), 0, 120); t.Pump(0); t.Stop(.1);
        Check(sink.Snapshot().Count(e => e.Status == 0x90) == 1, "Missing note-on");
        Check(sink.Snapshot().Count(e => e.Status == 0x80) == 1, "Missing release");
        Check(!t.Running, "Transport still running");
    }),
    ("MIDI clock is 24 PPQN", () =>
    {
        var sink = new MemoryMidiSink(); var t = new Transport(sink); t.Start(Plan(),0,120);
        for (var i = 0; i < 500; i++) t.Pump(i / 1000d);
        Check(sink.Snapshot().Count(e => e.Status == 0xF8) == 24, "Clock pulse count wrong");
    }),
    ("late note-ons are skipped", () =>
    {
        var sink = new MemoryMidiSink(); var t = new Transport(sink);
        t.Start(Plan(new MidiNote("a",0,1920,60,90)),0,120); t.Pump(.1); t.Stop(.2);
        Check(t.LateNoteOns == 1 && !sink.Snapshot().Any(e => (e.Status & 0xF0) == 0x90), "Late attack sent");
    }),
    ("skipped owner cannot release another owner", () =>
    {
        var sink = new MemoryMidiSink(); var t = new Transport(sink);
        t.Start(Plan(new MidiNote("early",0,1000,60,90), new MidiNote("late",100,100,60,90)),0,120);
        t.Pump(0); t.Pump(.2);
        Check(!sink.Snapshot().Any(e => e.Status == 0x80), "Skipped note released sustained note");
        t.Stop(.21); Check(sink.Snapshot().Count(e => e.Status == 0x80) == 1, "Final release missing");
    }),
    ("equal-time release precedes repeat attack", () =>
    {
        var plan = Plan(new MidiNote("a",0,480,60,90), new MidiNote("b",480,480,60,90));
        var pair = plan.Events.Where(e => e.Tick == 480).ToArray();
        Check(pair[0].Status == 0x80 && pair[1].Status == 0x90, "Ordering wrong");
    }),
    ("capture capacity is explicit", () =>
    {
        var sink = new MemoryMidiSink(1); sink.Send(0,"test",0xFA); sink.Send(1,"test",0xFC);
        Check(sink.Count == 1 && sink.Overflowed, "Overflow hidden");
    }),
    ("project roundtrip preserves data", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var p = WorkbenchSession.CreateDemo(); ProjectStore.Save(path,p);
            Check(JsonSerializer.Serialize(p) == JsonSerializer.Serialize(ProjectStore.Load(path)), "Project changed");
        }
        finally { File.Delete(path); }
    }),
    ("simulation ends with stop and balanced notes", () =>
    {
        var plan = PlanCompiler.Compile(WorkbenchSession.CreateDemo().Sections[0]);
        var sink = new MemoryMidiSink(); var t = new Transport(sink); t.Start(plan,0,120);
        for (var i=0;i<=16100;i++) t.Pump(i/1000d);
        var events = sink.Snapshot();
        Check(!t.Running && !sink.Overflowed, "Did not finish");
        Check(events.Count(e => (e.Status & 0xF0)==0x90) == events.Count(e => (e.Status & 0xF0)==0x80), "Unbalanced notes");
    })
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length-failed}/{tests.Length} passed"); return failed == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Throws(Action action)
{
    try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return; }
    throw new Exception("Expected validation failure");
}
static PlaybackPlan Plan(params MidiNote[] notes)
{
    var o = new Outline("Test",1,new Meter(),48,[0,2,4,5,7,9,11],.5,.5,.5,1);
    return PlanCompiler.Compile(new Section("test",o,[new Part("part","Part",PartRole.Pulse,"Test",1,new Pattern("p",PatternKind.Fixed,notes.ToImmutableArray()))]));
}
