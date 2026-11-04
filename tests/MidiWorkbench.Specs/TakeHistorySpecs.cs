using System.Collections.Immutable;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class TakeHistorySpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "midi-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { ArchiveRecovery(root); RecentHistory(root); SelectedTakeSave(root); UnsavedHistory(root); }
        finally { Directory.Delete(root, true); }
    }
    private static PerformanceTake Take(int n, int events = 0) => new(PerformanceTake.CurrentSchemaVersion,
        DateTimeOffset.UnixEpoch.AddSeconds(n), 120, true,
        Enumerable.Range(0, events).Select(i => new CapturedMidi(i * .001, "memory", "Drums", 0xB9, 1, 64)).ToImmutableArray(), [], [], []);

    private static void ArchiveRecovery(string root)
    {
        var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "This is a file, not an archive directory.");
        using var session = new WorkbenchSession(new SessionOptions(TakeDirectory: blocked));
        session.StartSection(true); Thread.Sleep(20);
        try { session.Stop(); throw new Exception("Archive to a file succeeded"); }
        catch (IOException) { }
        var current = session.Take();
        var midi = Path.Combine(root, "output-only.mid"); session.SaveTake(midi);
        try { session.StartSection(true); throw new Exception("MIDI export incorrectly replaced lossless JSON recovery"); }
        catch (IOException) { }
        session.SaveTake(Path.Combine(root, "recovered.json"));
        Check(ProjectStore.LoadTake(Path.Combine(root, "recovered.json")).Outputs.Length == current.Outputs.Length,
            "Recovery save lost captured events");
        session.StartSection(true); Thread.Sleep(20);
        try { session.Stop(); } catch (IOException) { }
        session.SaveTake(Path.Combine(root, "recovered-again.json"));
        Check(session.Takes.Count == 2, "Recovery did not retain both completed takes");
    }
    private static void RecentHistory(string root)
    {
        var folder = Path.Combine(root, "history"); Directory.CreateDirectory(folder);
        for (var i = 0; i < 40; i++) ProjectStore.SaveTake(Path.Combine(folder, $"{i:D3}.json"), Take(i));
        using var session = new WorkbenchSession(new SessionOptions(TakeDirectory: folder));
        Check(session.Takes.Count == WorkbenchSession.RecentTakeLimit && session.HasOlderTakes,
            "Startup loaded the entire archive instead of a bounded recent cache");
        Check(session.Takes[^1].StartedAt == DateTimeOffset.UnixEpoch.AddSeconds(39), "Newest archive missing");
        var old = session.LoadTake(Path.Combine(folder, "000.json"));
        Check(ReferenceEquals(session.Takes[^1], old) && Directory.GetFiles(folder).Length == 40,
            "Older archived take could not be recalled without deleting files");
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(root, $"dense-{i}.json"); ProjectStore.SaveTake(path, Take(i, 100_000)); session.LoadTake(path);
        }
        Check(session.Takes.Sum(t => t.Outputs.Length + t.Inputs.Length) <= WorkbenchSession.RecentTakeEventLimit,
            "Saved take cache ignored its event budget");
    }
    private static void SelectedTakeSave(string root)
    {
        using var session = new WorkbenchSession();
        var first = Take(1, 2); var second = Take(2, 4);
        var path = Path.Combine(root, "selected.json");
        session.SaveTake(Path.Combine(root, "first.json"), first);
        session.SaveTake(Path.Combine(root, "second.json"), second);
        session.SaveTake(path, first);
        var saved = ProjectStore.LoadTake(path);
        Check(saved.StartedAt == first.StartedAt && saved.Outputs.Length == 2, "Selected export saved the current/latest take instead");
    }
    private static void UnsavedHistory(string root)
    {
        using var session = new WorkbenchSession();
        for (var i = 0; i < WorkbenchSession.RecentTakeLimit; i++) { session.StartSection(true); session.Stop(); }
        var oldest = session.Takes[0];
        try { session.StartSection(true); throw new Exception("Unbounded unsaved recording history allowed"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unsaved takes")) { }
        Check(session.Takes.Count == WorkbenchSession.RecentTakeLimit && ReferenceEquals(session.Takes[0], oldest),
            "Unsaved material was evicted");
        session.SaveTake(Path.Combine(root, "saved-oldest.json"), oldest);
        session.StartSection(true); session.Stop();
        Check(session.Takes.Count == WorkbenchSession.RecentTakeLimit && !session.Takes.Any(t => ReferenceEquals(t, oldest)),
            "Saving a selected old take did not release history capacity");
    }
}
