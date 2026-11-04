using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;
using MidiWorkbench.Specs;

public static class PersistenceWorkflowRegressionSpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "midi-workbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project.json");
            var template = WorkbenchSession.CreateDemo();
            var section = template.Sections[0] with
            {
                Outline = template.Sections[0].Outline with { Bars = 1, Meter = new Meter(1, 4) },
                Parts = [Build.Part("part", "Phrase", 1)]
            };
            ProjectStore.Save(path, template with { Bpm = 400, Sections = [section] });
            var backend = new MemoryBackend();
            using (var session = new WorkbenchSession(new SessionOptions(backend)))
            {
                session.Load(path);
                session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "loopback" });
                session.Arm(WorkbenchSession.NewPhrase);
                session.StartSection();
                using var keys = backend.OpenOutput("loopback");
                Thread.Sleep(20);
                keys.Submit(new(0, 0x90, 60, 100));
                Thread.Sleep(30);
                keys.Submit(new(0, 0x80, 60, 0));
                // Allow natural completion; deliberately do not poll Status or call Stop.
                Thread.Sleep(500);
                session.Save(path);
                var saved = ProjectStore.Load(path);
                Check(saved.Sections[0].Parts.Any(p => p.Role == PartRole.Phrase && p.Pattern.Notes.Any(n => n.Pitch == 60 && n.Locked)),
                    "Save omitted the completed recording when status was not polled");
                session.Undo();
                Check(!session.Project.Sections[0].Parts.Any(p => p.Id.StartsWith("phrase-", StringComparison.Ordinal)), "Recording commit was not undoable");
            }

            var bankPath = Path.Combine(directory, "bank.json");
            var bank = new SongBank(SongBank.CurrentSchemaVersion, "Set", [], []);
            ProjectStore.SaveBank(bankPath, bank);
            try
            {
                ProjectStore.SaveBank(bankPath, bank with { Name = new string('x', 32 * 1024 * 1024) });
                throw new Exception("Oversized bank save succeeded");
            }
            catch (InvalidDataException) { }
            Check(ProjectStore.LoadBank(bankPath).Name == "Set", "Oversized save replaced the readable bank");

            using (var routing = new WorkbenchSession())
            {
                var original = routing.Rig;
                try { routing.SetRig(original with { Input = "missing-input" }); throw new Exception("Missing input accepted"); }
                catch (MidiPortException) { }
                Check(routing.Rig == original, "Failed input open changed the rig");
                routing.StartSection(true);
                try { routing.SetRig(original); throw new Exception("Rig change during playback accepted"); }
                catch (InvalidOperationException) { }
                Check(routing.Status().Running, "Rig change stopped playback");
                routing.Stop();
            }
            using (var launching = new WorkbenchSession())
            {
                var first = launching.SelectedSectionId;
                var second = launching.DuplicateSection(first);
                launching.SetTempo(400);
                launching.StartSection(first, true);
                launching.QueueSection(second, 3840);
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (launching.Status().QueuedSection?.Id != second && DateTime.UtcNow < deadline) Thread.Sleep(5);
                Check(launching.Status().QueuedSection?.Id == second, "Queued section was not published");
                launching.CancelQueuedSection();
                deadline = DateTime.UtcNow.AddSeconds(1);
                while (launching.Status().QueuedSection is not null && DateTime.UtcNow < deadline) Thread.Sleep(5);
                Check(launching.Status().QueuedSection is null && launching.Status().PlayingSection?.Id == first, "Cancelled section remained queued or launched");
                launching.QueueSection(second, 240);
                deadline = DateTime.UtcNow.AddSeconds(2);
                while (launching.Status().PlayingSection?.Id != second && DateTime.UtcNow < deadline) Thread.Sleep(5);
                Check(launching.Status().PlayingSection?.Id == second && launching.Status().QueuedSection is null, "Queue status did not follow the launch boundary");
                launching.Stop();
            }
            // A directory at the file destination makes the atomic replacement fail.
            using (var routing = new WorkbenchSession(new SessionOptions(RigPath: directory)))
            {
                var original = routing.Rig;
                try { routing.SetRig(original with { Input = "loopback" }); throw new Exception("Rig save unexpectedly succeeded"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Check(routing.Rig == original, "Failed rig save changed the rig");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
