using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class SongIdentitySpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "midi-song-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ProjectRenameHistoryAndPersistence(directory);
            NamedBankSnapshotsAndOrder(directory);
            InvalidNamesAndStoppedEditing();
            PendingRecordingIsCommittedBeforeRename(directory);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectStore.JsonOptions);
    private static void ProjectRenameHistoryAndPersistence(string directory)
    {
        using var session = new WorkbenchSession();
        var original = session.Project; var outline = session.SelectedSection.Outline;
        var candidate = session.PrepareGeneration(outline);
        var revision = session.Revision;
        session.RenameProject("  Untitled  ");
        Check(ReferenceEquals(session.Project, original) && session.Revision == revision && ReferenceEquals(session.Candidate, candidate),
            "An equivalent trimmed project name changed identity, revision or candidate");
        session.RenameProject("  Northern Lights  ");
        var renamed = session.Project;
        Check(renamed.Name == "Northern Lights" && renamed.Id == original.Id && renamed.SchemaVersion == original.SchemaVersion,
            "Project rename lost its normalized name, ID or schema");
        Check(session.Revision == revision + 1 && session.CanUndo && session.Candidate is null,
            "Project rename did not follow normal edit/history/candidate semantics");
        Check(ReferenceEquals(session.SelectedSection.Outline, outline) && renamed.Sections == original.Sections && renamed.Arrangement == original.Arrangement,
            "Renaming the song changed its musical material");
        session.Undo(); Check(ReferenceEquals(session.Project, original) && session.CanRedo, "Project rename could not be undone");
        var undoRevision = session.Revision;
        session.RenameProject("Untitled");
        Check(session.Revision == undoRevision && session.CanRedo && ReferenceEquals(session.Project, original), "No-op rename cleared redo history");
        session.Redo(); Check(ReferenceEquals(session.Project, renamed), "Project rename redo lost its snapshot");
        var path = Path.Combine(directory, "named-project.json"); session.Save(path);
        using var reopened = new WorkbenchSession(); reopened.Load(path);
        Check(Json(reopened.Project) == Json(renamed) && !reopened.IsRunning, "Renamed project did not reopen intact and stopped");
        session.StartSection(true); Thread.Sleep(20); session.Stop();
        var take = session.Take();
        Check(take.SourceProjectName == "Northern Lights" && take.SourceProjectId == original.Id, "Capture did not retain the project's chosen identity");
        session.RenameProject("Later title");
        Check(session.Take().SourceProjectName == "Northern Lights", "Later rename rewrote an earlier take's provenance");
    }
    private static void NamedBankSnapshotsAndOrder(string directory)
    {
        using var session = new WorkbenchSession();
        session.NewBank("Original set"); session.RenameProject("First song");
        var first = session.AddCurrentToBank(); var firstSnapshot = session.Project;
        session.RenameProject("Second song");
        var secondPath = Path.Combine(directory, "second-project.json");
        ProjectStore.Save(secondPath, session.Project with { Id = Guid.NewGuid().ToString("N") });
        session.Load(secondPath); var second = session.AddCurrentToBank();
        session.SetBankOrder([second, first, second]);
        var before = session.Bank!; var project = session.Project; var revision = session.Revision;
        session.RenameBank("  Evening set  ");
        var renamed = session.Bank!;
        Check(!ReferenceEquals(renamed, before) && renamed.Name == "Evening set", "Bank rename did not replace the dirty-tracked snapshot");
        Check(renamed.Songs == before.Songs && renamed.Order == before.Order && ReferenceEquals(session.Project, project) && session.Revision == revision,
            "Bank rename changed song/order identity or project history");
        Check(ReferenceEquals(renamed.Songs.Single(s => s.Id == first).Project, firstSnapshot), "Second project rename mutated the first bank snapshot");
        Check(renamed.Songs.Select(s => s.Project.Id).Distinct().Count() == 2, "Distinct bank projects lost their project identities");
        Check(renamed.Order.Select(id => renamed.Songs.Single(s => s.Id == id).Project.Name).SequenceEqual(["Second song", "First song", "Second song"]),
            "Bank/setlist entries lost their distinct project names");
        session.RenameBank(" Evening set "); Check(ReferenceEquals(session.Bank, renamed), "Equivalent bank rename created a dirty snapshot");
        var path = Path.Combine(directory, "named-bank.json"); session.SaveBank(path);
        using var reopened = new WorkbenchSession(); reopened.LoadBank(path);
        Check(Json(reopened.Bank) == Json(renamed), "Renamed bank and named song order did not save/reopen intact");
        reopened.OpenBankSong(first);
        Check(reopened.Project.Name == "First song" && reopened.Project.Id == firstSnapshot.Id, "Opening a named bank song lost its identity");
    }
    private static void InvalidNamesAndStoppedEditing()
    {
        using var session = new WorkbenchSession();
        Reject<InvalidOperationException>(() => session.RenameBank("Missing bank"));
        session.NewBank("Set");
        var project = session.Project; var bank = session.Bank; var revision = session.Revision;
        var candidate = session.PrepareGeneration(session.SelectedSection.Outline);
        foreach (var invalid in new[] { "", " \t\r\n ", null! })
        {
            Reject<ArgumentException>(() => session.RenameProject(invalid));
            Reject<ArgumentException>(() => session.RenameBank(invalid));
        }
        Check(ReferenceEquals(session.Project, project) && ReferenceEquals(session.Bank, bank) && session.Revision == revision
            && ReferenceEquals(session.Candidate, candidate) && !session.CanUndo, "Invalid names mutated content, history, dirty identity or candidate");
        session.DiscardGeneration(); session.StartSection(true);
        Reject<InvalidOperationException>(() => session.RenameProject("Playing rename"));
        Reject<InvalidOperationException>(() => session.RenameProject(project.Name));
        Reject<InvalidOperationException>(() => session.RenameBank("Playing set"));
        Check(session.IsRunning && ReferenceEquals(session.Project, project) && ReferenceEquals(session.Bank, bank), "Rejected live rename stopped or edited playback");
        session.Stop();
    }
    private static void PendingRecordingIsCommittedBeforeRename(string directory)
    {
        var path = Path.Combine(directory, "recording-project.json");
        var section = Build.Section(1, Build.Part("part", "Phrase", 1)) with { Outline = Build.Outline(1, new Meter(1, 4)) };
        ProjectStore.Save(path, new SongProject(SongProject.CurrentSchemaVersion, "Before recording", 400, [section]));
        using var backend = new MemoryBackend();
        using var session = new WorkbenchSession(new SessionOptions(backend));
        session.Load(path); session.SetRig(Rig.Memory(WorkbenchSession.Destinations(session.Project)) with { Input = "loopback" });
        session.Arm(WorkbenchSession.NewPhrase); session.StartSection();
        using var keys = backend.OpenOutput("loopback");
        Thread.Sleep(20); keys.Submit(new(0, 0x90, 72, 100));
        Thread.Sleep(20); keys.Submit(new(0, 0x80, 72, 0));
        Check(SpinWait.SpinUntil(() => !session.IsRunning, TimeSpan.FromSeconds(3)), "Recording failed to finish naturally");
        var beforeCommit = session.Project; var revision = session.Revision;
        Reject<ArgumentException>(() => session.RenameProject(" "));
        session.RenameProject(" Before recording ");
        Check(ReferenceEquals(session.Project, beforeCommit) && session.Revision == revision, "Invalid/no-op rename unexpectedly committed pending input");
        session.RenameProject("After recording");
        Check(session.Project.Name == "After recording" && session.Project.Sections[0].Parts.Any(p => p.Id.StartsWith("phrase-", StringComparison.Ordinal)
            && p.Pattern.Notes is [{ Pitch: 72, Locked: true }]), "Renaming lost a completed but unpolled recording");
        session.Undo();
        Check(session.Project.Name == "Before recording" && session.Project.Sections[0].Parts.Any(p => p.Id.StartsWith("phrase-", StringComparison.Ordinal)),
            "Undoing the title also removed the separately committed phrase");
        session.Undo(); Check(ReferenceEquals(session.Project, beforeCommit), "Pending phrase commit was not separately undoable");
        session.Redo(); session.Redo();
        Check(session.Project.Name == "After recording" && session.Take().SourceProjectName == "Before recording", "Redo or capture provenance failed after pending recording rename");
    }
}
