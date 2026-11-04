using System.Collections.Immutable;

namespace MidiWorkbench.Domain;

public static class MusicalTime
{
    public const int Ppq = 960;
    public const int MidiClockTicks = Ppq / 24;
}

public enum PartRole { Drums, Bass, Pulse, Pad }
public enum PatternKind { Fixed, Generative, Hybrid }
public sealed record Meter(int Numerator = 4, int Denominator = 4)
{
    public long BarTicks => (long)MusicalTime.Ppq * 4 * Numerator / Denominator;
    public void Validate()
    {
        if (Numerator is < 1 or > 32 || Denominator is not (1 or 2 or 4 or 8 or 16 or 32))
            throw new ArgumentException("Meter must use 1–32 beats and a power-of-two denominator up to 32.");
    }
}
public sealed record Outline(string Name, int Bars, Meter Meter, int Root, ImmutableArray<int> Scale,
    double Density, double Intensity, double Variation, uint Seed)
{
    public long LengthTicks => Bars * Meter.BarTicks;
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Meter);
        Meter.Validate();
        if (string.IsNullOrWhiteSpace(Name) || Bars is < 1 or > 256 || Root is < 24 or > 84)
            throw new ArgumentException("Choose a section name, 1–256 bars and root MIDI note 24–84.");
        if (Scale.IsDefaultOrEmpty || Scale.Any(n => n is < 0 or > 11) || Scale.Distinct().Count() != Scale.Length)
            throw new ArgumentException("Scale needs distinct pitch classes from 0 to 11.");
        foreach (var value in new[] { Density, Intensity, Variation })
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentException("Musical amounts must be between 0 and 1.");
    }
}
public sealed record MidiNote(string Id, long Tick, long Duration, int Pitch, int Velocity, bool Locked = false)
{
    public long EndTick => checked(Tick + Duration);
}
public sealed record Pattern(string Id, PatternKind Kind, ImmutableArray<MidiNote> Notes)
{
    public string? GeneratorVersion { get; init; }
    public uint? GeneratorSeed { get; init; }
}
public sealed record Part(string Id, string Name, PartRole Role, string Destination, int Channel, Pattern Pattern);
public sealed record Section(string Id, Outline Outline, ImmutableArray<Part> Parts);
public sealed record SongProject(int SchemaVersion, string Name, double Bpm, ImmutableArray<Section> Sections)
{
    public const int CurrentSchemaVersion = 1;
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported project schema; use a compatible application version.");
        if (string.IsNullOrWhiteSpace(Name) || !double.IsFinite(Bpm) || Bpm is < 20 or > 400)
            throw new ArgumentException("Project needs a name and tempo between 20 and 400 BPM.");
        if (Sections.IsDefaultOrEmpty || Sections.Length > 128) throw new ArgumentException("Project needs 1–128 sections.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in Sections)
        {
            ArgumentNullException.ThrowIfNull(section);
            ArgumentNullException.ThrowIfNull(section.Outline);
            if (string.IsNullOrWhiteSpace(section.Id) || !ids.Add(section.Id)) throw new ArgumentException("Section IDs must be unique.");
            section.Outline.Validate();
            if (section.Parts.IsDefaultOrEmpty || section.Parts.Length > 64) throw new ArgumentException("Section needs 1–64 parts.");
            var parts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in section.Parts)
            {
                ArgumentNullException.ThrowIfNull(part);
                ArgumentNullException.ThrowIfNull(part.Pattern);
                if (string.IsNullOrWhiteSpace(part.Id) || !parts.Add(part.Id) || string.IsNullOrWhiteSpace(part.Destination) || part.Channel is < 1 or > 16)
                    throw new ArgumentException("Part needs a unique ID, destination and MIDI channel 1–16.");
                if (!Enum.IsDefined(part.Role) || !Enum.IsDefined(part.Pattern.Kind) || part.Pattern.Notes.IsDefault || part.Pattern.Notes.Length > 100000)
                    throw new ArgumentException("Invalid pattern.");
                var noteIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in part.Pattern.Notes)
                {
                    ArgumentNullException.ThrowIfNull(n);
                    if (string.IsNullOrWhiteSpace(n.Id) || !noteIds.Add(n.Id) || n.Tick < 0 || n.Duration <= 0 || n.Tick > section.Outline.LengthTicks ||
                        n.Duration > section.Outline.LengthTicks - n.Tick || n.Pitch is < 0 or > 127 || n.Velocity is < 1 or > 127)
                        throw new ArgumentException("Note exceeds section bounds or MIDI range.");
                }
            }
        }
    }
}
public sealed record DeviceRoute(string Destination, string EndpointId, int Channel, double OffsetMilliseconds = 0);
public sealed record Rig(int SchemaVersion, ImmutableArray<DeviceRoute> Routes);
public sealed record CapturedMidi(double Seconds, string Destination, byte Status, byte Data1, byte Data2);
public sealed record PerformanceTake(int SchemaVersion, DateTimeOffset StartedAt, double Bpm,
    bool Complete, ImmutableArray<CapturedMidi> Events)
{
    public ImmutableArray<TempoChange> TempoChanges { get; init; } = [];
}
public sealed record TempoChange(double Seconds, double Bpm);
