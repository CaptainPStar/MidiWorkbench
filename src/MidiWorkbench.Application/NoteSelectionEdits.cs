using System.Collections.Immutable;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Application;

// Each operation prepares one complete pattern; the session commits it as one undo step.
public static class NoteSelectionEdits
{
    private const int MaxNotes = 100000;

    public static ImmutableArray<MidiNote> Move(IEnumerable<MidiNote> notes, long tickOffset, int pitchOffset, long lengthTicks)
    {
        var source = ReadClipboard(notes);
        var pattern = new Pattern("selection", PatternKind.Fixed, source);
        return Move(pattern, source.Select(n => n.Id), lengthTicks, tickOffset, pitchOffset).Notes;
    }

    public static ImmutableArray<MidiNote> Resize(IEnumerable<MidiNote> notes, long durationOffset, long lengthTicks)
    {
        var source = ReadClipboard(notes);
        var pattern = new Pattern("selection", PatternKind.Fixed, source);
        return Resize(pattern, source.Select(n => n.Id), lengthTicks, durationOffset).Notes;
    }

    public static ImmutableArray<MidiNote> Paste(IEnumerable<MidiNote> clipboard, long atTick, long lengthTicks,
        int pitchOffset = 0, Func<string>? createId = null) =>
        Paste(new Pattern("selection", PatternKind.Fixed, []), clipboard, lengthTicks, atTick, pitchOffset, createId).Notes;

    public static Pattern Apply(Pattern pattern, IEnumerable<MidiNote> upserts, IEnumerable<string> deleteIds, long lengthTicks)
    {
        var deleted = Select(pattern, deleteIds, lengthTicks);
        var edits = ReadClipboard(upserts);
        var byId = new Dictionary<string, MidiNote>(StringComparer.Ordinal);
        foreach (var note in edits)
        {
            if (deleted.Contains(note.Id)) throw new ArgumentException("A note cannot be edited and deleted together.", nameof(upserts));
            if (note.Tick >= lengthTicks || note.Duration > lengthTicks - note.Tick) throw new ArgumentException("Edited note exceeds section bounds.", nameof(upserts));
            byId.Add(note.Id, note);
        }
        var notes = ImmutableArray.CreateBuilder<MidiNote>();
        var changed = deleted.Count > 0;
        foreach (var note in pattern.Notes)
        {
            if (deleted.Contains(note.Id)) continue;
            if (byId.Remove(note.Id, out var updated)) { changed |= updated != note; notes.Add(updated); }
            else notes.Add(note);
        }
        if (byId.Count > MaxNotes - notes.Count) throw new ArgumentException("Pattern exceeds 100000 notes.", nameof(upserts));
        changed |= byId.Count > 0;
        if (!changed) return pattern;
        notes.AddRange(byId.Values);
        return pattern with { Kind = deleted.Count > 0 ? PatternKind.Fixed : EditedKind(pattern), Notes = Ordered(notes) };
    }

    public static Pattern Move(Pattern pattern, IEnumerable<string> ids, long lengthTicks, long tickOffset, int pitchOffset = 0)
    {
        var selected = Select(pattern, ids, lengthTicks);
        if (selected.Count == 0) return pattern;
        var notes = pattern.Notes.Where(n => selected.Contains(n.Id)).ToImmutableArray();
        var offset = ClampOffset(notes, lengthTicks, tickOffset, pitchOffset);
        if (offset == (0L, 0)) return pattern;
        return Update(pattern, selected, n => n with { Tick = n.Tick + offset.Tick, Pitch = n.Pitch + offset.Pitch });
    }

    public static Pattern Resize(Pattern pattern, IEnumerable<string> ids, long lengthTicks, long durationOffset)
    {
        var selected = Select(pattern, ids, lengthTicks);
        if (selected.Count == 0) return pattern;
        var notes = pattern.Notes.Where(n => selected.Contains(n.Id)).ToArray();
        var offset = Math.Clamp(durationOffset, 1 - notes.Min(n => n.Duration), notes.Min(n => lengthTicks - n.Tick - n.Duration));
        return offset == 0 ? pattern : Update(pattern, selected, n => n with { Duration = n.Duration + offset });
    }

    public static Pattern Delete(Pattern pattern, IEnumerable<string> ids, long lengthTicks)
    {
        var selected = Select(pattern, ids, lengthTicks);
        return selected.Count == 0 ? pattern : pattern with
        {
            Kind = PatternKind.Fixed,
            Notes = pattern.Notes.Where(n => !selected.Contains(n.Id)).ToImmutableArray()
        };
    }

    // Clipboard time is relative to the earliest selected onset; all other note data is retained.
    public static ImmutableArray<MidiNote> Copy(Pattern pattern, IEnumerable<string> ids, long lengthTicks)
    {
        var selected = Select(pattern, ids, lengthTicks);
        var notes = pattern.Notes.Where(n => selected.Contains(n.Id)).OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ThenBy(n => n.Id, StringComparer.Ordinal).ToImmutableArray();
        if (notes.IsEmpty) return [];
        var start = notes[0].Tick;
        return notes.Select(n => n with { Tick = n.Tick - start }).ToImmutableArray();
    }

    public static Pattern Paste(Pattern pattern, IEnumerable<MidiNote> clipboard, long lengthTicks, long atTick,
        int pitchOffset = 0, Func<string>? createId = null)
    {
        Validate(pattern, lengthTicks);
        var source = ReadClipboard(clipboard);
        if (source.IsEmpty) return pattern;
        var origin = source.Min(n => n.Tick);
        var relative = source.Select(n => n with { Tick = n.Tick - origin }).ToImmutableArray();
        if (relative.Max(n => n.EndTick) > lengthTicks) throw new ArgumentException("Selection is longer than the section.", nameof(clipboard));
        var offset = ClampOffset(relative, lengthTicks, atTick, pitchOffset);
        return Append(pattern, relative, offset.Tick, offset.Pitch, createId);
    }

    public static Pattern Duplicate(Pattern pattern, IEnumerable<string> ids, long lengthTicks, long tickOffset,
        int pitchOffset = 0, Func<string>? createId = null)
    {
        var selected = Select(pattern, ids, lengthTicks);
        if (selected.Count == 0) return pattern;
        var notes = pattern.Notes.Where(n => selected.Contains(n.Id)).ToImmutableArray();
        var offset = ClampOffset(notes, lengthTicks, tickOffset, pitchOffset);
        return Append(pattern, notes, offset.Tick, offset.Pitch, createId);
    }

    public static Pattern SetLocked(Pattern pattern, IEnumerable<string> ids, long lengthTicks, bool locked) =>
        Update(pattern, Select(pattern, ids, lengthTicks), n => n with { Locked = locked });

    public static Pattern SetVelocity(Pattern pattern, IEnumerable<string> ids, long lengthTicks, int velocity)
    {
        if (velocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(velocity), "Velocity must be 1–127.");
        return Update(pattern, Select(pattern, ids, lengthTicks), n => n with { Velocity = velocity });
    }

    public static Pattern AdjustVelocity(Pattern pattern, IEnumerable<string> ids, long lengthTicks, int offset)
    {
        var selected = Select(pattern, ids, lengthTicks);
        if (selected.Count == 0) return pattern;
        var notes = pattern.Notes.Where(n => selected.Contains(n.Id)).ToArray();
        var delta = Math.Clamp(offset, 1 - notes.Min(n => n.Velocity), 127 - notes.Max(n => n.Velocity));
        return delta == 0 ? pattern : Update(pattern, selected, n => n with { Velocity = n.Velocity + delta });
    }

    private static (long Tick, int Pitch) ClampOffset(ImmutableArray<MidiNote> notes, long lengthTicks, long tickOffset, int pitchOffset) =>
        (Math.Clamp(tickOffset, -notes.Min(n => n.Tick), lengthTicks - notes.Max(n => n.EndTick)),
         Math.Clamp(pitchOffset, -notes.Min(n => n.Pitch), 127 - notes.Max(n => n.Pitch)));

    private static Pattern Append(Pattern pattern, ImmutableArray<MidiNote> source, long tickOffset, int pitchOffset, Func<string>? createId)
    {
        if (source.Length > MaxNotes - pattern.Notes.Length) throw new ArgumentException("Pattern exceeds 100000 notes.");
        var usedIds = pattern.Notes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var notes = ImmutableArray.CreateBuilder<MidiNote>(pattern.Notes.Length + source.Length);
        notes.AddRange(pattern.Notes);
        foreach (var note in source)
        {
            var id = createId is null ? Guid.NewGuid().ToString("N") : createId();
            if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id)) throw new ArgumentException("New note IDs must be unique.", nameof(createId));
            notes.Add(note with { Id = id, Tick = note.Tick + tickOffset, Pitch = note.Pitch + pitchOffset });
        }
        return pattern with { Kind = EditedKind(pattern), Notes = Ordered(notes) };
    }

    private static Pattern Update(Pattern pattern, HashSet<string> selected, Func<MidiNote, MidiNote> change)
    {
        if (selected.Count == 0) return pattern;
        var changed = false;
        var notes = ImmutableArray.CreateBuilder<MidiNote>(pattern.Notes.Length);
        foreach (var note in pattern.Notes)
        {
            var updated = selected.Contains(note.Id) ? change(note) : note;
            changed |= updated != note; notes.Add(updated);
        }
        return changed ? pattern with { Kind = EditedKind(pattern), Notes = Ordered(notes) } : pattern;
    }

    private static PatternKind EditedKind(Pattern pattern) => pattern.Kind == PatternKind.Fixed ? PatternKind.Fixed : PatternKind.Hybrid;
    private static ImmutableArray<MidiNote> Ordered(IEnumerable<MidiNote> notes) =>
        notes.OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ThenBy(n => n.Id, StringComparer.Ordinal).ToImmutableArray();

    private static HashSet<string> Select(Pattern pattern, IEnumerable<string> ids, long lengthTicks)
    {
        Validate(pattern, lengthTicks); ArgumentNullException.ThrowIfNull(ids);
        if (ids is ImmutableArray<string> { IsDefault: true }) throw new ArgumentException("Selection must be initialized.", nameof(ids));
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Select valid note IDs.", nameof(ids));
            if (selected.Add(id) && selected.Count > MaxNotes) throw new ArgumentException("Selection exceeds 100000 notes.", nameof(ids));
        }
        if (!selected.IsSubsetOf(pattern.Notes.Select(n => n.Id))) throw new ArgumentException("Selection contains an unknown note.", nameof(ids));
        return selected;
    }

    private static ImmutableArray<MidiNote> ReadClipboard(IEnumerable<MidiNote> clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard is ImmutableArray<MidiNote> { IsDefault: true }) throw new ArgumentException("Clipboard must be initialized.", nameof(clipboard));
        var notes = ImmutableArray.CreateBuilder<MidiNote>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in clipboard)
        {
            if (notes.Count >= MaxNotes) throw new ArgumentException("Clipboard exceeds 100000 notes.", nameof(clipboard));
            ValidateNote(note, long.MaxValue, ids);
            notes.Add(note);
        }
        return notes.ToImmutable();
    }

    private static void Validate(Pattern pattern, long lengthTicks)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (lengthTicks <= 0) throw new ArgumentOutOfRangeException(nameof(lengthTicks), "Section length must be positive.");
        if (!Enum.IsDefined(pattern.Kind) || pattern.Notes.IsDefault || pattern.Notes.Length > MaxNotes) throw new ArgumentException("Invalid pattern.", nameof(pattern));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in pattern.Notes) ValidateNote(note, lengthTicks, ids);
    }

    private static void ValidateNote(MidiNote note, long lengthTicks, HashSet<string> ids)
    {
        if (note is null || string.IsNullOrWhiteSpace(note.Id) || !ids.Add(note.Id) || note.Tick < 0 || note.Duration <= 0 ||
            note.Tick >= lengthTicks || note.Duration > lengthTicks - note.Tick || note.Pitch is < 0 or > 127 || note.Velocity is < 1 or > 127)
            throw new ArgumentException("Note exceeds section bounds or MIDI range, or has a duplicate ID.");
    }
}
