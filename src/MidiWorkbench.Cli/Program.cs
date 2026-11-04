using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;

try
{
    if (args is ["new", var output])
    {
        ProjectStore.Save(output, WorkbenchSession.CreateDemo()); Console.WriteLine(output); return 0;
    }
    if (args is ["validate", var input])
    {
        var p = ProjectStore.Load(input); Console.WriteLine($"Valid: {p.Sections.Length} sections"); return 0;
    }
    if (args is ["simulate", var source, var target])
    {
        var p = ProjectStore.Load(source); var plan = PlanCompiler.Compile(p.Sections[0]);
        var sink = new MemoryMidiSink(); var transport = new Transport(sink); transport.Start(plan, 0, p.Bpm);
        var duration = plan.LengthTicks * 60d / (p.Bpm * MusicalTime.Ppq);
        for (double time = 0; time <= duration + .01; time += .001) transport.Pump(time);
        transport.Stop(duration + .01);
        ProjectStore.SaveTake(target, new(1, DateTimeOffset.UtcNow, p.Bpm, !sink.Overflowed, sink.Snapshot()));
        Console.WriteLine($"{sink.Count} events; late notes {transport.LateNoteOns}; complete {!sink.Overflowed}"); return sink.Overflowed ? 2 : 0;
    }
    Console.Error.WriteLine("Usage: new PROJECT | validate PROJECT | simulate PROJECT TAKE"); return 1;
}
catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or JsonException)
{ Console.Error.WriteLine(ex.Message); return 2; }
