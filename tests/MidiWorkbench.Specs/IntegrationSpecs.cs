using System.Collections.Immutable;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class IntegrationSpecs
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Action)[]
        {
            (nameof(QueueDifferentDestinations), QueueDifferentDestinations),
            (nameof(ArmedPlaybackRespectsMute), ArmedPlaybackRespectsMute),
            (nameof(ArmedPlaybackRespectsSolo), ArmedPlaybackRespectsSolo),
            (nameof(ClockOnlyEndpointTakeReplay), ClockOnlyEndpointTakeReplay),
            (nameof(StopAndPanicDiscardQueuedSection), StopAndPanicDiscardQueuedSection),
            (nameof(RecordSaveAndRecall), RecordSaveAndRecall)
        })
        {
            try { test(); }
            catch (Exception ex) { failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }

    private static SongProject Project(params Section[] sections) =>
        new(SongProject.CurrentSchemaVersion, "Integration", 120, sections.ToImmutableArray());

    private static string SaveProject(SongProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"midi-integration-{Guid.NewGuid():N}.json");
        ProjectStore.Save(path, project);
        return path;
    }

    private static void QueueDifferentDestinations()
    {
        var first = Build.Section(1, Build.Part("one", "A", 1, new MidiNote("a", 0, 960, 60, 100))) with { Id = "first" };
        var second = Build.Section(1, Build.Part("two", "B", 1, new MidiNote("b", 0, 480, 67, 100))) with { Id = "second" };
        var path = SaveProject(Project(first, second));
        try
        {
            var backend = new MemoryBackend();
            using var session = new WorkbenchSession(new SessionOptions(backend));
            session.Load(path);
            session.SetRig(Rig.Memory(["A", "B"]));
            session.StartSection("first", true);
            session.QueueSection("second", 240);
            Thread.Sleep(250);
            session.Stop();
            var take = session.Takes[^1];
            Check(take.Outputs.Any(e => (e.Status & 0xF0) == 0x90 && e.Data1 == 67 && e.Destination == "B"),
                "Queued section did not sound and capture its own destination.");
        }
        finally { File.Delete(path); }
    }

    private static void ArmedPlaybackRespectsMute()
    {
        var muted = Build.Part("muted", "A", 1, new MidiNote("n", 0, 960, 60, 100)) with { Muted = true };
        var target = Build.Part("target", "B", 2);
        var path = SaveProject(Project(Build.Section(1, muted, target)));
        try
        {
            var backend = new MemoryBackend();
            using var session = new WorkbenchSession(new SessionOptions(backend));
            session.Load(path);
            session.SetRig(Rig.Memory(["A", "B"]) with { Input = "loopback" });
            session.Arm("target");
            session.Start();
            Thread.Sleep(80);
            session.Stop();
            Check(!session.Takes[^1].Outputs.Any(e => (e.Status & 0xF0) == 0x90 && e.Data1 == 60 && e.Data2 > 0),
                "A muted part sounded while recording another part.");
        }
        finally { File.Delete(path); }
    }

    private static void ClockOnlyEndpointTakeReplay()
    {
        var path = SaveProject(Project(Build.Section(1, Build.Part("part", "A", 1, new MidiNote("n", 0, 240, 60, 100)))));
        try
        {
            var backend = new MemoryBackend();
            using var session = new WorkbenchSession(new SessionOptions(backend));
            session.Load(path);
            session.SetRig(new Rig(Rig.CurrentSchemaVersion, [new("memory", false), new("loopback")], [new("A", "memory")]));
            session.Start(); Thread.Sleep(100); session.Stop();
            var take = session.Takes[^1];
            Check(take.Outputs.Any(e => e.Endpoint == "loopback" && e.Status == 0xF8), "Clock-only output was not captured.");
            session.ReplayTake(take);
            Thread.Sleep(150); session.Stop();
            Check(session.Takes[^1].Outputs.Any(e => e.Endpoint == "loopback" && e.Status == 0xF8), "Clock-only output was not replayed.");
        }
        finally { File.Delete(path); }
    }

    private static void ArmedPlaybackRespectsSolo()
    {
        var other = Build.Part("other", "A", 1, new MidiNote("n", 0, 960, 60, 100));
        var solo = Build.Part("solo", "B", 2, new MidiNote("s", 0, 960, 67, 100)) with { Solo = true };
        var path = SaveProject(Project(Build.Section(1, other, solo)));
        try
        {
            var backend = new MemoryBackend();
            using var session = new WorkbenchSession(new SessionOptions(backend));
            session.Load(path);
            session.SetRig(Rig.Memory(["A", "B"]) with { Input = "loopback" });
            session.Arm("solo"); session.Start(); Thread.Sleep(80); session.Stop();
            var output = session.Takes[^1].Outputs;
            Check(output.Any(e => (e.Status & 0xF0) == 0x90 && e.Data1 == 67 && e.Data2 > 0), "Solo part did not sound.");
            Check(!output.Any(e => (e.Status & 0xF0) == 0x90 && e.Data1 == 60 && e.Data2 > 0), "A non-solo part sounded while recording.");
        }
        finally { File.Delete(path); }
    }

    private static void StopAndPanicDiscardQueuedSection()
    {
        foreach (var panic in new[] { false, true })
        {
            double now = 0;
            var backend = new FakeBackend(() => now);
            var port = backend.Add("memory", false);
            using var host = new TransportHost(new HostOptions(Background: false));
            var first = Build.Plan(new MidiNote("a", 0, 1920, 60, 100));
            var queued = Build.Plan(new MidiNote("b", 0, 240, 67, 100));
            host.Start(first, [port], 120, true); host.Step(now);
            host.QueuePlan(queued, [port], 960); now = .1; host.Step(now);
            var stopped = panic ? host.Panic() : host.Stop();
            now = .2; host.Step(now); now = .3; host.Step(now);
            Check(stopped.IsCompletedSuccessfully, "Stop or panic did not finish.");
            host.Start(first, [port], 120, true); now = .4; host.Step(now);
            for (var i = 401; i <= 1100; i++) { now = i / 1000d; host.Step(now); }
            Check(!port.Sent.Any(s => (s.Message.Status & 0xF0) == 0x90 && s.Message.Data1 == 67 && s.Message.Data2 > 0),
                "A queued section survived stop or panic into the next take.");
            Check(port.Sent.Any(s => (s.Message.Status & 0xF0) == 0x80 && s.Message.Data1 == 60), "Stop or panic left the sounding note held.");
        }
    }

    private static void RecordSaveAndRecall()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"midi-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new MemoryBackend();
            using var session = new WorkbenchSession(new SessionOptions(backend));
            session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "loopback" });
            session.Arm(WorkbenchSession.NewPhrase);
            session.Start(true);
            using var keyboard = backend.OpenOutput("loopback");
            keyboard.Submit(new(0, 0xB0, 64, 127));
            keyboard.Submit(new(0, 0x90, 73, 100));
            Thread.Sleep(50);
            keyboard.Submit(new(0, 0x80, 73, 0));
            Thread.Sleep(50);
            keyboard.Submit(new(0, 0xB0, 64, 0));
            Thread.Sleep(50);
            session.SetTempo(90);
            session.Stop();
            var phrase = session.SelectedSection.Parts.Single(p => p.Destination == WorkbenchSession.PhraseDestination);
            Check(phrase.Pattern.Notes.Any(n => n.Pitch == 73 && n.Duration > 100), "Sustained recording was not committed.");
            var takePath = Path.Combine(directory, "take.json");
            session.SaveTake(takePath);
            var take = ProjectStore.LoadTake(takePath);
            Check(take.Inputs.Any(e => e.Data1 == 73) && take.Outputs.Any(e => e.Data1 == 73 && e.Destination == WorkbenchSession.PhraseDestination), "Input or thru output lost in saved take.");
            Check(take.TempoChanges.Any(t => t.Bpm == 90), "Tempo change lost in saved take.");
            session.NewBank("Set"); var id = session.AddCurrentToBank();
            var bankPath = Path.Combine(directory, "bank.json"); session.SaveBank(bankPath);
            session.LoadBank(bankPath); session.OpenBankSong(id);
            Check(session.SelectedSection.Parts.Any(p => p.Pattern.Notes.Any(n => n.Pitch == 73)), "Recorded phrase lost on bank recall.");
            session.ReplayTake(take); Thread.Sleep(250); session.Panic();
            var replay = session.Takes[^1];
            Check(replay.Outputs.Any(e => (e.Status & 0xF0) == 0x90 && e.Data1 == 73 && e.Data2 > 0), "Recorded note did not replay.");
            Check(replay.Outputs.Any(e => (e.Status & 0xF0) == 0xB0 && e.Data1 == 64 && e.Data2 >= 64), "Recorded pedal did not replay.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
