using System.Collections.Immutable;
using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;

public static class NoteSelectionSpecs
{
    private const long Length = 960;
    private static readonly Recipe Recipe = new("test", "1", "none", 1);
    private static Pattern Source => new("p", PatternKind.Generative,
        [new("a", 100, 80, 60, 40), new("b", 300, 120, 67, 100, true), new("c", 800, 160, 72, 80)]) { Recipe = Recipe };
    private static readonly string[] Pair = ["a", "b"];
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; }
        throw new Exception("Expected invalid edit to fail");
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static MidiNote Note(Pattern pattern, string id) => pattern.Notes.Single(n => n.Id == id);
    private static Func<string> Ids(params string[] ids) { var next = 0; return () => ids[next++]; }
    private static void Valid(Pattern pattern, long length = Length)
    {
        Check(pattern.Notes.Length <= 100000 && pattern.Notes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() == pattern.Notes.Length, "Duplicate IDs or excess notes");
        Check(pattern.Notes.All(n => n.Tick >= 0 && n.Duration > 0 && n.Tick < length && n.Duration <= length - n.Tick && n.Pitch is >= 0 and <= 127 && n.Velocity is >= 1 and <= 127), "Edited note outside bounds");
    }

    public static void Run()
    {
        GroupMovement();
        ResizeAndVelocity();
        ClipboardAndDuplication();
        DeleteAndLocks();
        ValidationAndAtomicity();
        OverflowAndRandomizedBounds();
        PreviewOverloads();
        AtomicBatch();
    }

    private static void GroupMovement()
    {
        var source = Source;
        var moved = NoteSelectionEdits.Move(source, Pair, Length, 50, 12);
        Check(Note(moved, "a") is { Tick: 150, Pitch: 72, Duration: 80, Velocity: 40, Locked: false }, "First selected note moved incorrectly");
        Check(Note(moved, "b") is { Tick: 350, Pitch: 79, Duration: 120, Velocity: 100, Locked: true }, "Second selected note moved incorrectly");
        Check(ReferenceEquals(Note(moved, "c"), Note(source, "c")), "Unselected note was replaced");
        Check(moved.Id == source.Id && ReferenceEquals(moved.Recipe, source.Recipe) && moved.Kind == PatternKind.Hybrid, "Pattern identity/recipe/kind changed incorrectly");
        var low = NoteSelectionEdits.Move(source, Pair, Length, long.MinValue, int.MinValue);
        Check(Note(low, "a") is { Tick: 0, Pitch: 0 } && Note(low, "b") is { Tick: 200, Pitch: 7 }, "Lower clamping changed relative timing/pitch");
        var high = NoteSelectionEdits.Move(source, Pair, Length, long.MaxValue, int.MaxValue);
        Check(Note(high, "a") is { Tick: 640, Pitch: 120 } && Note(high, "b") is { Tick: 840, Pitch: 127 }, "Upper clamping changed relative timing/pitch");
        Valid(low); Valid(high);
        Check(ReferenceEquals(NoteSelectionEdits.Move(source, [], Length, 20, 2), source), "Empty selection caused an edit");
        Check(ReferenceEquals(NoteSelectionEdits.Move(source, Pair, Length, 0, 0), source), "No-op move caused an edit");
        var fixedPattern = source with { Kind = PatternKind.Fixed };
        Check(NoteSelectionEdits.Move(fixedPattern, Pair, Length, 10).Kind == PatternKind.Fixed, "Manual move unfroze fixed material");
        Check(Json(NoteSelectionEdits.Move(source, ["a", "b", "a"], Length, 10, 1)) == Json(NoteSelectionEdits.Move(source, Pair, Length, 10, 1)), "Repeated selection IDs changed result");
        Check(Note(source, "a").Tick == 100 && Note(source, "a").Pitch == 60, "Source pattern mutated");
    }

    private static void ResizeAndVelocity()
    {
        var source = Source;
        var shorter = NoteSelectionEdits.Resize(source, Pair, Length, long.MinValue);
        Check(Note(shorter, "a").Duration == 1 && Note(shorter, "b").Duration == 41, "Resize did not clamp one common duration offset");
        var longer = NoteSelectionEdits.Resize(source, Pair, Length, long.MaxValue);
        Check(Note(longer, "a").Duration == 620 && Note(longer, "b").Duration == 660, "Resize did not share available headroom");
        Check(Note(longer, "b").EndTick == Length && Note(longer, "a").Tick == 100 && Note(longer, "b").Pitch == 67, "Resize changed note onset/pitch");
        Check(Note(longer, "c") == Note(source, "c"), "Resize changed unselected note");
        var velocity = NoteSelectionEdits.SetVelocity(source, Pair, Length, 55);
        Check(Note(velocity, "a").Velocity == 55 && Note(velocity, "b").Velocity == 55 && Note(velocity, "c").Velocity == 80, "Velocity edited outside selection");
        Check(Note(velocity, "b").Locked && !Note(velocity, "a").Locked, "Velocity changed locks");
        var louder = NoteSelectionEdits.AdjustVelocity(source, Pair, Length, int.MaxValue);
        Check(Note(louder, "a").Velocity == 67 && Note(louder, "b").Velocity == 127, "Relative velocity did not clamp together");
        var quieter = NoteSelectionEdits.AdjustVelocity(source, Pair, Length, int.MinValue);
        Check(Note(quieter, "a").Velocity == 1 && Note(quieter, "b").Velocity == 61, "Relative velocity lost dynamic difference");
        Check(ReferenceEquals(NoteSelectionEdits.SetVelocity(source, ["a"], Length, 40), source), "Unchanged velocity created an edit");
        Check(ReferenceEquals(NoteSelectionEdits.Resize(source, Pair, Length, 0), source), "Zero resize created an edit");
        Valid(shorter); Valid(longer); Valid(louder); Valid(quieter);
    }

    private static void ClipboardAndDuplication()
    {
        var source = Source;
        var copied = NoteSelectionEdits.Copy(source, ["b", "a"], Length);
        Check(copied.Select(n => n.Tick).SequenceEqual([0L, 200L]) && copied.Select(n => n.Pitch).SequenceEqual([60, 67]), "Clipboard was not normalized/sorted");
        Check(copied[1].Locked && copied[0].Velocity == 40 && copied[1].Duration == 120, "Copy lost note data");
        var pasted = NoteSelectionEdits.Paste(source, copied, Length, 700, 12, Ids("d", "e"));
        Check(Note(pasted, "d") is { Tick: 640, Pitch: 72, Duration: 80 } && Note(pasted, "e") is { Tick: 840, Pitch: 79, Duration: 120, Locked: true }, "Paste clipped individual notes instead of the whole group");
        Check(source.Notes.All(n => ReferenceEquals(Note(pasted, n.Id), n)), "Paste changed target notes");
        Check(pasted.Kind == PatternKind.Hybrid && pasted.Notes.Length == 5, "Paste did not append a complete group");
        var duplicate = NoteSelectionEdits.Duplicate(source, Pair, Length, 400, -12, Ids("d", "e"));
        Check(Note(duplicate, "d") is { Tick: 500, Pitch: 48 } && Note(duplicate, "e") is { Tick: 700, Pitch: 55 }, "Duplicate did not apply a relative offset");
        Check(source.Notes.All(n => Note(duplicate, n.Id) == n), "Duplicate moved originals");
        var noRoom = NoteSelectionEdits.Duplicate(source, ["c"], Length, long.MaxValue, createId: Ids("d"));
        Check(Note(noRoom, "d").Tick == 800 && Note(noRoom, "d").Duration == 160, "End-bound duplicate shortened its note");
        var below = NoteSelectionEdits.Paste(source, copied, Length, long.MinValue, int.MinValue, Ids("d", "e"));
        Check(Note(below, "d") is { Tick: 0, Pitch: 0 } && Note(below, "e") is { Tick: 200, Pitch: 7 }, "Paste lower bound changed group shape");
        var raw = NoteSelectionEdits.Paste(source, source.Notes.Take(2), Length, 0, createId: Ids("d", "e"));
        Check(Note(raw, "d").Tick == 0 && Note(raw, "e").Tick == 200, "Paste requires an already normalized clipboard");
        Check(ReferenceEquals(NoteSelectionEdits.Paste(source, [], Length, 999), source), "Empty paste edited pattern");
        Check(ReferenceEquals(NoteSelectionEdits.Duplicate(source, [], Length, 999), source), "Empty duplicate edited pattern");
        var first = NoteSelectionEdits.Paste(source, copied, Length, 0);
        var second = NoteSelectionEdits.Paste(first, copied, Length, 0);
        Check(second.Notes.Select(n => n.Id).Distinct().Count() == 7, "Repeated pastes reused IDs");
        Valid(pasted); Valid(duplicate); Valid(noRoom); Valid(below); Valid(raw); Valid(second);
    }

    private static void DeleteAndLocks()
    {
        var source = Source;
        var deleted = NoteSelectionEdits.Delete(source, Pair, Length);
        Check(deleted.Kind == PatternKind.Fixed && deleted.Notes.Length == 1 && ReferenceEquals(deleted.Notes[0], Note(source, "c")), "Delete did not retain unselected material/freeze pattern");
        Check(ReferenceEquals(deleted.Recipe, source.Recipe) && deleted.Id == source.Id, "Delete lost recipe or pattern ID");
        var empty = NoteSelectionEdits.Delete(source, source.Notes.Select(n => n.Id), Length);
        Check(empty.Kind == PatternKind.Fixed && empty.Notes.IsEmpty, "Delete all recreated material");
        var locked = NoteSelectionEdits.SetLocked(source, Pair, Length, true);
        Check(Note(locked, "a").Locked && Note(locked, "b").Locked && !Note(locked, "c").Locked, "Lock edited unselected note");
        var unlocked = NoteSelectionEdits.SetLocked(source, Pair, Length, false);
        Check(!Note(unlocked, "a").Locked && !Note(unlocked, "b").Locked && Note(unlocked, "c") == Note(source, "c"), "Unlock edited outside selection");
        Check(ReferenceEquals(NoteSelectionEdits.SetLocked(source, ["b"], Length, true), source), "Unchanged lock caused an edit");
        Check(ReferenceEquals(NoteSelectionEdits.Delete(source, [], Length), source), "Empty delete froze pattern");
    }

    private static void ValidationAndAtomicity()
    {
        var source = Source; var original = Json(source);
        Throws(() => NoteSelectionEdits.Move(source, ["a", "missing"], Length, 10));
        Throws(() => NoteSelectionEdits.Delete(source, ["missing"], Length));
        Throws(() => NoteSelectionEdits.SetLocked(source, ["a", "missing"], Length, true));
        Throws(() => NoteSelectionEdits.Copy(source, [" "], Length));
        Throws(() => NoteSelectionEdits.Move(source, null!, Length, 10));
        Throws(() => NoteSelectionEdits.Move(source, default(ImmutableArray<string>), Length, 10));
        Throws(() => NoteSelectionEdits.SetVelocity(source, Pair, Length, 0));
        Throws(() => NoteSelectionEdits.SetVelocity(source, [], Length, 128));
        Throws(() => NoteSelectionEdits.Move(source, Pair, 0, 0));
        Throws(() => NoteSelectionEdits.Move(source, [], 500, 0));
        Throws(() => NoteSelectionEdits.Paste(source, null!, Length, 0));
        Throws(() => NoteSelectionEdits.Paste(source, default(ImmutableArray<MidiNote>), Length, 0));
        Throws(() => NoteSelectionEdits.Paste(source, [new("long", 0, Length + 1, 60, 90)], Length, 0));
        Throws(() => NoteSelectionEdits.Duplicate(source, Pair, Length, 10, createId: Ids("new", "new")));
        Throws(() => NoteSelectionEdits.Duplicate(source, Pair, Length, 10, createId: Ids("new", "a")));
        Throws(() => NoteSelectionEdits.Duplicate(source, Pair, Length, 10, createId: Ids("new", " ")));
        Throws(() => NoteSelectionEdits.Paste(source, [new("x", long.MaxValue, 1, 60, 90)], Length, 0));
        Throws(() => NoteSelectionEdits.Paste(source, [new("x", 0, 10, 60, 90), new("x", 20, 10, 62, 90)], Length, 0));
        foreach (var invalid in new[]
        {
            new MidiNote("x", -1, 10, 60, 90), new("x", 0, 0, 60, 90), new("x", 0, 10, 128, 90), new("x", 0, 10, 60, 0), null!
        }) Throws(() => NoteSelectionEdits.Paste(source, [invalid], Length, 0));
        Throws(() => NoteSelectionEdits.Move(source with { Notes = [source.Notes[0], source.Notes[0]] }, ["a"], Length, 0));
        Throws(() => NoteSelectionEdits.Delete(source with { Notes = default }, [], Length));
        Check(Json(source) == original, "Failed edit partially changed source");
        var full = new Pattern("full", PatternKind.Fixed, Enumerable.Range(0, 100000).Select(i => new MidiNote($"n{i}", 0, 1, 60, 80)).ToImmutableArray());
        var calls = 0;
        Throws(() => NoteSelectionEdits.Paste(full, [new("extra", 0, 1, 60, 80)], Length, 0, createId: () => { calls++; return "new"; }));
        Check(full.Notes.Length == 100000 && calls == 0, "Note budget failed after allocating partial IDs");
    }

    private static void OverflowAndRandomizedBounds()
    {
        var edge = new Pattern("edge", PatternKind.Fixed, [new("a", long.MaxValue - 20, 5, 0, 1), new("b", long.MaxValue - 10, 10, 127, 127)]);
        var moved = NoteSelectionEdits.Move(edge, Pair, long.MaxValue, long.MinValue, int.MaxValue);
        Check(Note(moved, "a").Tick == 0 && Note(moved, "b").Tick == 10 && Note(moved, "b").Pitch == 127, "Move overflowed at Int64 limit");
        Valid(moved, long.MaxValue);
        var pasted = NoteSelectionEdits.Paste(Source, edge.Notes, Length, long.MaxValue, createId: Ids("d", "e"));
        Check(Note(pasted, "d").Tick == 940 && Note(pasted, "e").EndTick == Length, "Clipboard normalization overflowed at Int64 limit");
        var resized = NoteSelectionEdits.Resize(edge, Pair, long.MaxValue, long.MinValue);
        Check(Note(resized, "a").Duration == 1 && Note(resized, "b").Duration == 6, "Resize overflowed at Int64 limit");
        var random = new Random(1234);
        for (var i = 0; i < 500; i++)
        {
            var source = Source;
            var result = NoteSelectionEdits.Move(source, Pair, Length, random.NextInt64(-100000, 100000), random.Next(-1000, 1000));
            Valid(result);
            Check(Note(result, "b").Tick - Note(result, "a").Tick == 200 && Note(result, "b").Pitch - Note(result, "a").Pitch == 7, "Randomized move changed selection shape");
            Check(Note(result, "a").Duration == 80 && Note(result, "b").Duration == 120 && Note(result, "c") == Note(source, "c"), "Randomized move changed other fields");
        }
    }

    private static void PreviewOverloads()
    {
        var selected = Source.Notes.Take(2).ToImmutableArray();
        var moved = NoteSelectionEdits.Move(selected, long.MaxValue, int.MaxValue, Length);
        Check(moved.Select(n => n.Tick).SequenceEqual([640L, 840L]) && moved.Select(n => n.Pitch).SequenceEqual([120, 127]), "UI preview movement differs from pattern transform");
        var resized = NoteSelectionEdits.Resize(selected, long.MinValue, Length);
        Check(resized.Select(n => n.Duration).SequenceEqual([1L, 41L]), "UI preview resize differs from pattern transform");
        var pasted = NoteSelectionEdits.Paste(selected, long.MaxValue, Length, createId: Ids("new-a", "new-b"));
        Check(pasted.Select(n => n.Tick).SequenceEqual([640L, 840L]) && pasted.All(n => n.Id.StartsWith("new-", StringComparison.Ordinal)), "UI paste differs from pattern transform");
        Check(NoteSelectionEdits.Move([], 10, 1, Length).IsEmpty && NoteSelectionEdits.Resize([], 10, Length).IsEmpty && NoteSelectionEdits.Paste([], 10, Length).IsEmpty, "Empty UI selections failed");
        Throws(() => NoteSelectionEdits.Move(selected, 0, 0, 100));
    }

    private static void AtomicBatch()
    {
        var source = Source; var before = Json(source);
        var edited = NoteSelectionEdits.Apply(source, [Note(source, "a") with { Tick = 200 }, new("d", 500, 100, 64, 90, true)], ["b"], Length);
        Check(edited.Kind == PatternKind.Fixed && edited.Notes.Length == 3 && Note(edited, "a").Tick == 200 && Note(edited, "d").Locked, "Batch did not apply complete edit");
        Check(edited.Notes.All(n => n.Id != "b") && Note(edited, "c") == Note(source, "c") && ReferenceEquals(edited.Recipe, source.Recipe), "Batch changed retained material/metadata");
        Throws(() => NoteSelectionEdits.Apply(source, [Note(source, "a") with { Tick = 0 }, new("invalid", 950, 20, 60, 90)], ["b"], Length));
        Throws(() => NoteSelectionEdits.Apply(source, [Note(source, "a"), Note(source, "a")], [], Length));
        Throws(() => NoteSelectionEdits.Apply(source, [Note(source, "a")], ["a"], Length));
        Throws(() => NoteSelectionEdits.Apply(source, [], ["missing"], Length));
        Check(Json(source) == before, "Invalid batch partially changed accepted notes");
        Check(ReferenceEquals(NoteSelectionEdits.Apply(source, source.Notes, [], Length), source), "Unchanged batch created an edit");
        var empty = NoteSelectionEdits.Apply(source, [], source.Notes.Select(n => n.Id), Length);
        Check(empty.Notes.IsEmpty && empty.Kind == PatternKind.Fixed, "Batch delete all lost fixed state");
        Valid(edited);
    }
}
