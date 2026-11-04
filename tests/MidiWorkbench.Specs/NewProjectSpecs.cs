using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class NewProjectSpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectStore.JsonOptions);
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "midi-new-project-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            FreshIdentityAndRetainedRigBankHistory(directory);
            EmptyPlaybackAndDevelopment();
            ValidationAndStoppedSafety();
            PendingCaptureBeforeReplacement(directory, false);
            PendingCaptureBeforeReplacement(directory, true);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void FreshIdentityAndRetainedRigBankHistory(string directory)
    {
        using var backend = new MemoryBackend(); using var session = new WorkbenchSession(new SessionOptions(backend));
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "loopback" });
        session.NewBank("Keep this set"); session.AddCurrentToBank(); var bank = session.Bank; var rig = session.Rig;
        session.StartSection(true); Thread.Sleep(20); session.Stop(); var oldTake = session.Takes.Single();
        var second = session.DuplicateSection(session.SelectedSectionId); session.SetTempo(130); session.SetTempo(140); session.Undo();
        session.Arm(WorkbenchSession.NewPhrase); session.PrepareGeneration(session.SelectedSection.Outline);
        Check(session.CanUndo && session.CanRedo && session.Candidate is not null && session.Armed is not null && session.SelectedSectionId == second,
            "New-project reset fixture lacks history/candidate/arm/selection state");
        var before = session.Project; var revision = session.Revision;
        session.NewProject("  Blank canvas  ");
        var project = session.Project; var section = project.Sections.Single();
        Check(project.Name == "Blank canvas" && project.Id != before.Id && !before.Sections.Any(s => s.Id == section.Id), "New project reused a document/section identity or lost its trimmed name");
        Check(project.SchemaVersion == SongProject.CurrentSchemaVersion && project.Bpm == 120 && project.Arrangement.IsEmpty
            && section.Outline is { Bars: 4, Root: 48 } && section.Outline.Meter == new Meter(4, 4)
            && section.Outline.Scale.SequenceEqual([0, 2, 4, 5, 7, 9, 11]), "New project did not use the neutral empty-song defaults");
        Check(section.Parts.Select(p => p.Id).SequenceEqual(["drums", "bass", "pulse", "pad"])
            && section.Parts.Select(p => p.Channel).SequenceEqual([10, 1, 2, 3])
            && section.Parts.All(p => p.Pattern.Kind == PatternKind.Generative && p.Pattern.Notes.IsEmpty && p.Pattern.Recipe is null),
            "New project generated music or omitted its standard editable parts");
        Check(!session.CanUndo && !session.CanRedo && session.Candidate is null && session.GenerationStatus.Phase == GenerationPhase.Idle
            && session.Armed is null && session.SelectedSectionId == section.Id && session.Revision == revision + 1 && !session.IsRunning,
            "New project did not reset project-owned edit/performance state");
        Check(ReferenceEquals(session.Rig, rig) && ReferenceEquals(session.Bank, bank) && session.Takes.Count == 1 && ReferenceEquals(session.Takes[0], oldTake),
            "New project replaced the rig, bank or completed take history");
        var path = Path.Combine(directory, "empty.json"); session.Save(path);
        using var reopened = new WorkbenchSession(); reopened.Load(path);
        Check(Json(reopened.Project) == Json(project), "Saving/reopening the new empty song changed its identity or generated notes");
        session.NewProject();
        Check(session.Project.Name == "Untitled" && session.Project.Id != project.Id && session.SelectedSectionId != section.Id,
            "Repeated New project reused identity instead of making another fresh document");
    }
    private static void EmptyPlaybackAndDevelopment()
    {
        using var backend = new MemoryBackend(); using var session = new WorkbenchSession(new SessionOptions(backend));
        session.NewProject("Sketch"); var empty = session.Project;
        session.StartSection(true); Thread.Sleep(25); session.Stop();
        Check(backend.MemorySnapshot().All(m => (m.Status & 0xF0) is not (0x80 or 0x90)), "Playing an untouched new song emitted note messages");
        Check(session.Take().Complete && ReferenceEquals(session.Project, empty), "Empty playback committed invented material");
        var manual = new MidiNote("manual", 73, 137, 48, 91, true); session.EditNote("bass", manual);
        var candidate = session.PrepareGeneration(session.SelectedSection.Outline with { Density = 1, Variation = 0 }, DrumPersonas.Settings(DrumPersona.Simple));
        Check(candidate.Parts.All(p => !p.Pattern.Notes.IsEmpty) && candidate.Parts.Single(p => p.Id == "bass").Pattern.Notes.Contains(manual),
            "Empty standard parts could not develop through generation around protected manual material");
        Check(session.Project.Sections[0].Parts.Single(p => p.Id == "bass").Pattern.Notes.SequenceEqual([manual]), "Preview changed accepted empty/manual material before acceptance");
        session.AcceptGeneration();
        Check(session.Project.Sections[0].Parts.All(p => !p.Pattern.Notes.IsEmpty), "New song could not accept generated material");
        session.SetRig(session.Rig with { Input = "loopback" }); session.Arm(WorkbenchSession.NewPhrase);
        session.StartSection(true); using var keys = backend.OpenOutput("loopback");
        Thread.Sleep(20); keys.Submit(new(0, 0x90, 72, 100)); Thread.Sleep(20); keys.Submit(new(0, 0x80, 72, 0)); session.Stop();
        Check(session.Project.Sections[0].Parts.Any(p => p.Role == PartRole.Phrase && p.Pattern.Notes is [{ Pitch: 72, Locked: true }])
            && session.Take().SourceProjectId == empty.Id && session.Take().SourceProjectName == "Sketch", "New project could not capture a protected keyboard phrase with its new identity");
    }
    private static void ValidationAndStoppedSafety()
    {
        using var session = new WorkbenchSession(); session.NewBank("Keep");
        var candidate = session.PrepareGeneration(session.SelectedSection.Outline); var project = session.Project; var rig = session.Rig; var bank = session.Bank; var revision = session.Revision;
        foreach (var name in new[] { "", " \t\r\n ", null! }) Reject<ArgumentException>(() => session.NewProject(name));
        Check(ReferenceEquals(session.Project, project) && ReferenceEquals(session.Candidate, candidate) && ReferenceEquals(session.Rig, rig)
            && ReferenceEquals(session.Bank, bank) && session.Revision == revision, "Invalid new-song name changed session state");
        session.DiscardGeneration(); session.StartSection(true);
        Reject<InvalidOperationException>(() => session.NewProject("While playing"));
        Check(session.IsRunning && ReferenceEquals(session.Project, project) && session.Revision == revision, "Rejected live New song stopped playback or replaced the project");
        session.Stop();
    }
    private static void PendingCaptureBeforeReplacement(string directory, bool archiveFailure)
    {
        var archive = Path.Combine(directory, archiveFailure ? "blocked" : "archive");
        if (archiveFailure) File.WriteAllText(archive, "This is a file, not a take directory.");
        var section = new Section("before", Build.Outline(1, new Meter(1, 4)) with { Name = "Before" }, [Build.Part("part", "Phrase", 1)]);
        var path = Path.Combine(directory, archiveFailure ? "failed-old.json" : "old.json");
        ProjectStore.Save(path, new SongProject(SongProject.CurrentSchemaVersion, "Captured old song", 400, [section]));
        using var backend = new MemoryBackend(); using var session = new WorkbenchSession(new SessionOptions(backend, TakeDirectory: archive)); session.Load(path);
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "loopback" }); session.NewBank("Keep");
        var rig = session.Rig; var bank = session.Bank; var oldId = session.Project.Id;
        session.Arm(WorkbenchSession.NewPhrase); session.StartSection(); using var keys = backend.OpenOutput("loopback");
        Thread.Sleep(20); keys.Submit(new(0, 0x90, 72, 100)); Thread.Sleep(20); keys.Submit(new(0, 0x80, 72, 0));
        Check(SpinWait.SpinUntil(() => !session.IsRunning, TimeSpan.FromSeconds(3)), "Pending New song recording failed to finish");
        var before = session.Project; var revision = session.Revision;
        Reject<ArgumentException>(() => session.NewProject(" "));
        Check(ReferenceEquals(session.Project, before) && session.Revision == revision, "Invalid name committed/archived pending capture before rejection");
        if (archiveFailure)
        {
            Reject<IOException>(() => session.NewProject("Next"));
            Check(session.Project.Id == oldId && session.Project.Sections[0].Parts.Any(p => p.Role == PartRole.Phrase && p.Id.StartsWith("phrase-", StringComparison.Ordinal)
                && p.Pattern.Notes is [{ Pitch: 72, Locked: true }]) && session.CanUndo && ReferenceEquals(session.Rig, rig) && ReferenceEquals(session.Bank, bank),
                "Archive failure discarded the old project or its committed pending notes");
            var recovery = Path.Combine(directory, "recovered.json"); session.SaveTake(recovery);
            Check(ProjectStore.LoadTake(recovery).Inputs.Length == 2, "Failed New song could not recover its pending input capture");
        }
        session.NewProject("Next");
        Check(session.Project.Id != oldId && session.Project.Sections[0].Parts.All(p => p.Pattern.Notes.IsEmpty) && !session.CanUndo,
            "Successful New song inherited old recording notes/history");
        var retained = session.Takes.Single();
        Check(retained.Inputs.Length == 2 && retained.SourceProjectId == oldId && retained.SourceProjectName == "Captured old song"
            && ReferenceEquals(session.Rig, rig) && ReferenceEquals(session.Bank, bank), "Replacing the song lost old capture provenance/history or local setup");
        if (!archiveFailure) Check(Directory.GetFiles(archive, "*.json").Length == 1, "New song did not archive the completed capture before replacement");
    }
}
