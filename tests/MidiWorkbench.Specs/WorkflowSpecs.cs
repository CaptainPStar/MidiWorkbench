using System.Collections.Immutable;
using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;

public static class WorkflowSpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) { return; } throw new Exception("Expected failure"); }
    public static void Run()
    {
        using var session = new WorkbenchSession();
        var original = session.Project.Sections[0];
        var second = session.DuplicateSection(original.Id);
        Check(session.SelectedSectionId == second, "Duplicate selection");
        var outline = session.SelectedSection.Outline with { Name = "Changed", Seed = 42 };
        session.PrepareGeneration(outline);
        Check(session.SelectedSection.Outline.Name != "Changed", "Candidate changed project");
        session.DiscardGeneration();
        Throws(session.AcceptGeneration);
        session.PrepareGeneration(outline); session.AcceptGeneration();
        Check(session.SelectedSection.Outline.Name == "Changed" && session.Project.Sections[0] == original, "Wrong section changed");
        session.Undo(); Check(session.SelectedSection.Outline.Name != "Changed", "Undo");
        session.Redo(); Check(session.SelectedSection.Outline.Name == "Changed", "Redo");
        session.SetArrangement([new(second, 2), new(original.Id)]);
        var plan = ArrangementCompiler.Compile(session.Project);
        Check(plan.LengthTicks == session.SelectedSection.Outline.LengthTicks * 2 + original.Outline.LengthTicks, "Arrangement length");
        Check(plan.Events.Any(e => e.Tick >= session.SelectedSection.Outline.LengthTicks), "Repeat missing");
        var json = JsonSerializer.Serialize(session.Project, ProjectStore.JsonOptions);
        Check(ProjectStore.Parse(json).Arrangement.Length == 2, "Arrangement persistence");
        var old = json.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2");
        Check(ProjectStore.Parse(old).SchemaVersion == SongProject.CurrentSchemaVersion, "Schema 2 migration");
        var muted = session.SelectedSection.Parts[0]; session.SetPartMute(muted.Id, true);
        Check(session.SelectedSection.Parts[0].Muted, "Mute");
        session.RemoveSection(second); Check(session.Project.Arrangement.All(e => e.SectionId != second), "Dangling arrangement");
        session.Undo(); Check(session.Project.Sections.Any(s => s.Id == second), "Undo removal");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var take = new PerformanceTake(PerformanceTake.CurrentSchemaVersion, DateTimeOffset.UtcNow, 120, true,
                [new(0, "old-device", "Bass", 0x90, 60, 100), new(.1, "old-device", "Bass", 0x80, 60, 0)], [], [], []);
            ProjectStore.SaveTake(path, take);
            Check(ProjectStore.LoadTake(path).Outputs.Length == 2, "Take persistence");
            session.LoadTake(path); Check(session.Takes.Count == 1, "Take bank");
            var bank = new SongBank(1, "Set", [new("song", session.Project)], ["song", "song"]);
            ProjectStore.SaveBank(path, bank);
            var loadedBank = ProjectStore.LoadBank(path);
            Check(loadedBank.Order.Length == 2 && loadedBank.Songs[0].Project.Id == session.Project.Id, "Bank roundtrip");
            session.PlayTake(take); Throws(() => session.Arm(null)); session.Stop();
            session.Start(); session.Stop(); session.Start(); session.Stop();
            Check(session.Takes.Count >= 3, "Earlier takes lost");
            Throws(() => ProjectStore.SaveTake(path, take with { Bpm = double.NaN }));
        }
        finally { File.Delete(path); }
        using (var launching = new WorkbenchSession())
        {
            var firstId = launching.SelectedSectionId;
            var secondId = launching.DuplicateSection(firstId);
            launching.SetTempo(240);
            launching.StartSection(firstId, true);
            launching.QueueSection(secondId, 1920);
            Check(launching.PlayingSectionId == firstId, "Queued section appeared before launch");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (launching.PlayingSectionId != secondId && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Check(launching.PlayingSectionId == secondId, "Active section not published after launch");
            launching.QueueSection(firstId, 3840);
            launching.CancelQueuedSection();
            Check(launching.PlayingSectionId == secondId, "Cancel changed active section");
            launching.Stop();
            Check(launching.PlayingSectionId is null, "Stopped section remains active");
        }
        var routePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var template = WorkbenchSession.CreateDemo();
            var firstPart = new Part("part-a", "A", PartRole.Phrase, "A", 1,
                new Pattern("a", PatternKind.Fixed, [new MidiNote("a-note", 0, 240, 60, 100)]));
            var secondPart = firstPart with { Id = "part-b", Destination = "B", Pattern = new Pattern("b", PatternKind.Fixed, [new MidiNote("b-note", 0, 240, 67, 100)]) };
            var first = template.Sections[0] with { Id = "first", Parts = [firstPart] };
            var secondSection = template.Sections[0] with { Id = "second", Parts = [secondPart] };
            ProjectStore.Save(routePath, template with { Sections = [first, secondSection] });
            using var routing = new WorkbenchSession();
            routing.Load(routePath);
            routing.SetRig(new Rig(1, [new("memory", false), new("missing", false)], [new("A", "memory"), new("B", "missing")]));
            routing.StartSection("first", true);
            Check(routing.Status().Running, "Unused unavailable endpoint prevented start");
            Throws(() => routing.QueueSection("second", 240));
            Check(routing.Status().Running && routing.PlayingSectionId == "first", "Failed queue interrupted playback");
            routing.Stop();
            routing.SetRig(new Rig(1, [new("memory", false), new("loopback", false)], [new("A", "memory"), new("B", "loopback")]));
            routing.StartSection("first", true);
            routing.QueueSection("second", 240);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (routing.PlayingSectionId != "second" && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Check(routing.PlayingSectionId == "second", "Queued endpoint was not prepared");
            Thread.Sleep(30); routing.Stop();
            Check(routing.Takes[^1].Outputs.Any(e => e.Endpoint == "loopback" && e.Destination == "B" && e.Data1 == 67), "Queued endpoint output missing");
            var backend = new MidiWorkbench.Specs.FakeBackend(() => HostTime.Now);
            backend.Add("a-output", false);
            var unused = backend.Add("b-output", false);
            using var cancelSession = new WorkbenchSession(new SessionOptions(backend));
            cancelSession.Load(routePath);
            cancelSession.SetRig(new Rig(1, [new("a-output", false), new("b-output", false)],
                [new("A", "a-output"), new("B", "b-output")]));
            cancelSession.StartSection("first", true);
            cancelSession.QueueSection("second", MusicalTime.Ppq * 128L);
            cancelSession.CancelQueuedSection();
            unused.Disconnect();
            Thread.Sleep(150);
            Check(cancelSession.Status().Running && cancelSession.PlayingSectionId == "first", "Cancelled section disconnect stopped active playback");
            cancelSession.Stop();
        }
        finally { File.Delete(routePath); }
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new MemoryBackend();
            using (var record = new WorkbenchSession(new SessionOptions(backend, TakeDirectory: directory)))
            {
                record.SetRig(Rig.Memory(WorkbenchSession.Destinations(record.Project)) with { Input = "loopback" });
                record.Arm(WorkbenchSession.NewPhrase); record.Start();
                using var keys = backend.OpenOutput("loopback");
                keys.Submit(new(0, 0xB0, 64, 127)); Thread.Sleep(30);
                keys.Submit(new(0, 0xB0, 64, 0)); Thread.Sleep(30); record.Stop();
                var captured = record.Takes[^1];
                Check(captured.Meter is not null && captured.SourceProjectId == record.Project.Id, "Take metadata");
                Check(captured.Outputs.Any(e => e.Status == 0xB0 && e.Data1 == 64 && e.Destination == WorkbenchSession.PhraseDestination), "Thru CC destination");
            }
            using var reopened = new WorkbenchSession(new SessionOptions(TakeDirectory: directory));
            Check(reopened.Takes.Count == 1, "Automatic take recovery");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        var playbackBackend = new MemoryBackend();
        using (var playback = new WorkbenchSession(new SessionOptions(playbackBackend)))
        {
            var performed = new PerformanceTake(PerformanceTake.CurrentSchemaVersion, DateTimeOffset.UtcNow, 120, true,
                [new(0, "old", "Bass", 0xFA, 0, 0), new(.01, "old", "Bass", 0xF8, 0, 0),
                 new(.02, "old", "Bass", 0xB0, 64, 127), new(.06, "old", "Bass", 0xF8, 0, 0),
                 new(.08, "old", "Bass", 0xB0, 64, 0), new(.1, "old", "Bass", 0xFC, 0, 0)], [], [],
                [new(0, 120), new(.04, 60)]);
            playback.ReplayTake(performed); Thread.Sleep(250); playback.Stop();
            var output = playbackBackend.MemorySnapshot();
            var clocks = output.Where(m => m.Status == 0xF8).ToArray();
            Check(clocks.Length == 2 && Math.Abs((clocks[1].Seconds - clocks[0].Seconds) - .05) < .005, "Recorded clock timing");
            Check(output.Count(m => (m.Status & 0xF0) == 0xB0 && m.Data1 == 64) == 2, "Take CC replay");
        }
        using (var clockSession = new WorkbenchSession())
        {
            clockSession.SetRig(new Rig(1, [new("memory"), new("loopback")], [new("Bass", "memory")]));
            var clockTake = new PerformanceTake(PerformanceTake.CurrentSchemaVersion, DateTimeOffset.UtcNow, 120, true,
                [new(.01, "removed-clock", null, 0xF8, 0, 0)], [], [], []);
            Throws(() => clockSession.ReplayTake(clockTake));
            clockSession.ReplayTake(clockTake, new Dictionary<string, string>(), new Dictionary<string, string> { ["removed-clock"] = "loopback" });
            Thread.Sleep(50); clockSession.Stop();
            Check(clockSession.Takes[^1].Outputs.Any(e => e.Status == 0xF8 && e.Endpoint == "loopback" && e.Destination is null), "Explicit clock remapping");
        }
        using var feedback = new WorkbenchSession();
        var rig = new Rig(1, [new("loopback")], [new(WorkbenchSession.PhraseDestination, "loopback")]) { Input = "loopback" };
        feedback.SetRig(rig); Throws(() => feedback.Arm(WorkbenchSession.NewPhrase));
    }
}
