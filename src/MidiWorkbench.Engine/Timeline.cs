using MidiWorkbench.Domain;
using System.Collections.Immutable;

namespace MidiWorkbench.Engine;

public sealed class TempoClock
{
    public double Bpm { get; private set; } = 120;
    public double OriginSeconds { get; private set; }
    public double OriginTick { get; private set; }
    public double TickAt(double seconds) => OriginTick + (seconds - OriginSeconds) * Bpm * MusicalTime.Ppq / 60;
    public double SecondsAt(double tick) => OriginSeconds + (tick - OriginTick) * 60 / (Bpm * MusicalTime.Ppq);
    public void SetTempo(double seconds, double bpm)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(bpm) || bpm is < 20 or > 400) throw new ArgumentOutOfRangeException(nameof(bpm));
        OriginTick = TickAt(seconds); OriginSeconds = seconds; Bpm = bpm;
    }
    public void Start(double seconds, double bpm)
    {
        SetTempo(seconds, bpm); OriginSeconds = seconds; OriginTick = 0;
    }
}

public sealed record PlannedMessage(long Tick, string Owner, string Destination, byte Status, byte Data1, byte Data2);
public sealed record PlaybackPlan(long LengthTicks, ImmutableArray<PlannedMessage> Events, ImmutableArray<string> Destinations);

public static class PlanCompiler
{
    public static PlaybackPlan Compile(Section section)
    {
        new SongProject(1, "Validation", 120, [section]).Validate();
        var messages = new List<PlannedMessage>();
        foreach (var part in section.Parts)
        foreach (var note in part.Pattern.Notes)
        {
            var channel = part.Channel - 1;
            messages.Add(new(note.Tick, part.Id + "/" + note.Id, part.Destination, (byte)(0x90 | channel), (byte)note.Pitch, (byte)note.Velocity));
            messages.Add(new(note.EndTick, part.Id + "/" + note.Id, part.Destination, (byte)(0x80 | channel), (byte)note.Pitch, 0));
        }
        // Off before on preserves repeated notes at a shared boundary.
        return new(section.Outline.LengthTicks, messages.OrderBy(m => m.Tick).ThenBy(m => m.Status & 0xF0).ToImmutableArray(),
            section.Parts.Select(p => p.Destination).Distinct(StringComparer.Ordinal).ToImmutableArray());
    }
}
