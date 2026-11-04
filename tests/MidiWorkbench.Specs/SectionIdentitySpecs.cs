using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class SectionIdentitySpecs
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
        var directory = Path.Combine(Path.GetTempPath(), "midi-section-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            MetadataRenameKeepsAuthoredMusic(directory);
            ValidationAndStoppedSafety(directory);
            PendingRecordingSurvivesMetadataRename(directory, false);
            PendingRecordingSurvivesMetadataRename(directory, true);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static SongProject Authored()
    {
        var recipe = new Recipe(PatternGenerator.GeneratorId, PatternGenerator.LegacyVersion, PatternGenerator.RandomId, 17)
            { Settings = new() { StepTicks = 480, Seed = 17 } };
        // Deliberately unlocked, off-grid and partly outside the scale. Regenerating
        // the recipe would overwrite this accepted musical material.
        var authored = Build.Part("pulse", "Pulse", 2, new("a", 73, 137, 61, 83), new("b", 2030, 333, 68, 57)) with
            { Solo = true, Pattern = new("authored", PatternKind.Generative, [new("a", 73, 137, 61, 83), new("b", 2030, 333, 68, 57)]) { Recipe = recipe } };
        var fixedPart = Build.Part("fixed", "Fixed", 3, new MidiNote("locked", 960, 480, 79, 94, true)) with { Muted = true };
        var verse = new Section("verse", Build.Outline(2) with { Name = "Verse" }, [authored, fixedPart]);
        var chorus = new Section("chorus", Build.Outline(2, new Meter(7, 8)) with { Name = "Chorus" }, [Build.Part("chord", "Chords", 4, new MidiNote("c", 0, 1920, 67, 100))]);
        return new(SongProject.CurrentSchemaVersion, "Authored song", 120, [verse, chorus])
            { Arrangement = [new("verse", 2), new("chorus"), new("verse")] };
    }
    private static WorkbenchSession Open(SongProject project, string directory, string name)
    {
        var path = Path.Combine(directory, name + ".json"); ProjectStore.Save(path, project);
        var session = new WorkbenchSession(); session.Load(path);
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project))); return session;
    }
    private static void MetadataRenameKeepsAuthoredMusic(string directory)
    {
        using var session = Open(Authored(), directory, "authored");
        var original = session.Project; var verse = original.Sections[0]; var chorus = original.Sections[1];
        var beforeMidi = Path.Combine(directory, "before.mid"); session.ExportArrangementMidi(beforeMidi);
        var candidate = session.PrepareGeneration(session.SelectedSection.Outline); var revision = session.Revision;
        session.RenameSection("verse", "  Verse  ");
        Check(ReferenceEquals(session.Project, original) && session.Revision == revision && ReferenceEquals(session.Candidate, candidate), "No-op section rename changed dirty identity, revision or candidate");
        session.RenameSection("verse", "  Opening verse  ");
        var renamed = session.Project; var changed = renamed.Sections[0];
        Check(changed.Id == verse.Id && changed.Outline.Name == "Opening verse" && session.SelectedSectionId == verse.Id, "Section rename changed its ID/selection or lost the trimmed name");
        Check(changed.Parts == verse.Parts && Json(changed.Outline with { Name = verse.Outline.Name }) == Json(verse.Outline)
            && ReferenceEquals(renamed.Sections[1], chorus) && renamed.Arrangement == original.Arrangement && renamed.Id == original.Id,
            "Metadata rename changed notes, recipes, rhythm, other sections or arrangement identity");
        Check(session.Revision == revision + 1 && session.Candidate is null && session.CanUndo, "Section rename bypassed ordinary history/candidate semantics");
        var afterMidi = Path.Combine(directory, "after.mid"); session.ExportArrangementMidi(afterMidi);
        Check(File.ReadAllBytes(beforeMidi).SequenceEqual(File.ReadAllBytes(afterMidi)), "Renaming an unlocked authored section changed exported music or duration");
        session.Undo(); Check(ReferenceEquals(session.Project, original), "Section rename undo did not restore the exact accepted snapshot");
        var undoneRevision = session.Revision; session.RenameSection("verse", "Verse");
        Check(session.Revision == undoneRevision && session.CanRedo, "No-op section rename cleared redo history");
        session.Redo(); Check(ReferenceEquals(session.Project, renamed), "Section rename redo changed musical identity");
        var path = Path.Combine(directory, "renamed.json"); session.Save(path);
        using var reopened = new WorkbenchSession(); reopened.Load(path);
        Check(Json(reopened.Project) == Json(renamed) && !reopened.IsRunning, "Renamed authored music failed save/reopen");
        var selected = session.SelectedSectionId; var keptVerse = session.Project.Sections[0];
        session.RenameSection("chorus", "Bridge");
        Check(session.SelectedSectionId == selected && ReferenceEquals(session.Project.Sections[0], keptVerse)
            && session.Project.Sections[1].Outline.Name == "Bridge", "Renaming a nonselected section changed the editor target or another section");
    }
    private static void ValidationAndStoppedSafety(string directory)
    {
        using var session = Open(Authored(), directory, "validation");
        var project = session.Project; var revision = session.Revision;
        var candidate = session.PrepareGeneration(session.SelectedSection.Outline);
        foreach (var name in new[] { "", " \t\r\n ", null! }) Reject<ArgumentException>(() => session.RenameSection("verse", name));
        foreach (var id in new[] { "missing", "", null! }) Reject<ArgumentException>(() => session.RenameSection(id, "Renamed"));
        Check(ReferenceEquals(session.Project, project) && session.Revision == revision && ReferenceEquals(session.Candidate, candidate)
            && !session.CanUndo, "Rejected section name/ID mutated music, history or candidate");
        session.DiscardGeneration(); session.StartSection(true);
        Reject<InvalidOperationException>(() => session.RenameSection("verse", "Live rename"));
        Reject<InvalidOperationException>(() => session.RenameSection("verse", "Verse"));
        Check(session.IsRunning && ReferenceEquals(session.Project, project) && session.Revision == revision, "Rejected live section rename stopped or edited playback");
        session.Stop();
    }
    private static void PendingRecordingSurvivesMetadataRename(string directory, bool renameOther)
    {
        var target = new Section("record", Build.Outline(1, new Meter(1, 4)) with { Name = "Recording" }, [Build.Part("part", "Phrase", 1)]);
        var other = new Section("other", Build.Outline(1) with { Name = "Other" }, [Build.Part("other-part", "Other", 2)]);
        var path = Path.Combine(directory, renameOther ? "record-other.json" : "record-self.json");
        ProjectStore.Save(path, new SongProject(SongProject.CurrentSchemaVersion, "Recording song", 400, [target, other]));
        using var backend = new MemoryBackend(); using var session = new WorkbenchSession(new SessionOptions(backend)); session.Load(path);
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "loopback" });
        session.Arm(WorkbenchSession.NewPhrase); session.StartSection(); using var keys = backend.OpenOutput("loopback");
        Thread.Sleep(20); keys.Submit(new(0, 0x90, 72, 100)); Thread.Sleep(20); keys.Submit(new(0, 0x80, 72, 0));
        Check(SpinWait.SpinUntil(() => !session.IsRunning, TimeSpan.FromSeconds(3)), "Recording did not finish naturally");
        var beforeCommit = session.Project; var revision = session.Revision;
        Reject<ArgumentException>(() => session.RenameSection("missing", "Unknown"));
        Reject<ArgumentException>(() => session.RenameSection("record", " "));
        session.RenameSection("record", " Recording ");
        Check(ReferenceEquals(session.Project, beforeCommit) && session.Revision == revision, "Invalid/no-op metadata rename committed pending recording");
        var id = renameOther ? "other" : "record"; session.RenameSection(id, "Renamed section");
        Check(session.Project.Sections.Single(s => s.Id == id).Outline.Name == "Renamed section"
            && session.Project.Sections[0].Parts.Any(p => p.Id.StartsWith("phrase-", StringComparison.Ordinal) && p.Pattern.Notes is [{ Pitch: 72, Locked: true }]),
            "Renaming metadata overwrote newly committed keyboard material");
        session.Undo();
        Check(session.Project.Sections.Single(s => s.Id == id).Outline.Name == (renameOther ? "Other" : "Recording")
            && session.Project.Sections[0].Parts.Any(p => p.Id.StartsWith("phrase-", StringComparison.Ordinal)), "Undo title also discarded the completed recording");
        session.Undo(); Check(ReferenceEquals(session.Project, beforeCommit), "Recording commit was not separately undoable");
        session.Redo(); session.Redo(); Check(session.Project.Sections.Single(s => s.Id == id).Outline.Name == "Renamed section", "Pending recording/title redo failed");
    }
}
