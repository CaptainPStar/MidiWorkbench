using System.Collections.Immutable;

namespace MidiWorkbench.Domain;

public static class MusicalTime
{
    public const int Ppq = 960;
    public const int MidiClockTicks = Ppq / 24;
}

// Phrase is captured or manual material; generators never create it.
public enum PartRole { Drums, Bass, Pulse, Pad, Phrase }
public enum PatternKind { Fixed, Generative, Hybrid }
public enum DrumPersona { Simple, Rock, Alternative, Doom, Sludge, BlackMetal, DeathMetal, Pop, Jazz }
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
public sealed record DrummerSettings(DrumPersona Persona = DrumPersona.Simple, double Complexity = .3, double Fills = .3,
    double Swing = 0, bool HalfTime = false)
{
    public ImmutableArray<int> Grouping { get; init; } = [];
    public void Validate()
    {
        if (Grouping.IsDefault || Grouping.Length > 32 || Grouping.Any(g => g is < 1 or > 32)) throw new ArgumentException("Invalid meter grouping.");
        if (!Enum.IsDefined(Persona) || !double.IsFinite(Complexity) || Complexity is < 0 or > 1 ||
            !double.IsFinite(Fills) || Fills is < 0 or > 1 || !double.IsFinite(Swing) || Swing is < 0 or > .45)
            throw new ArgumentException("Drummer needs a listed persona, complexity and fills 0–1, swing 0–0.45.");
    }
}
// Generator and Random name algorithms; a seed alone is not reproducible across algorithm changes.
public sealed record Recipe(string Generator, string Version, string Random, uint Seed)
{
    public DrummerSettings? Drummer { get; init; }
    public GeneratorSettings? Settings { get; init; }
}
public sealed record Pattern(string Id, PatternKind Kind, ImmutableArray<MidiNote> Notes)
{
    public Recipe? Recipe { get; init; }
}
public sealed record Part(string Id, string Name, PartRole Role, string Destination, int Channel, Pattern Pattern)
{
    public bool Muted { get; init; }
    public bool Solo { get; init; }
}
public sealed record Section(string Id, Outline Outline, ImmutableArray<Part> Parts);
public sealed record SongProject(int SchemaVersion, string Name, double Bpm, ImmutableArray<Section> Sections)
{
    public const int CurrentSchemaVersion = 3;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ImmutableArray<ArrangementEntry> Arrangement { get; init; } = [];
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported project schema; use a compatible application version.");
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || !double.IsFinite(Bpm) || Bpm is < 20 or > 400)
            throw new ArgumentException("Project needs a name and tempo between 20 and 400 BPM.");
        if (Sections.IsDefaultOrEmpty || Sections.Length > 128) throw new ArgumentException("Project needs 1–128 sections.");
        if (Arrangement.IsDefault || Arrangement.Length > 1024) throw new ArgumentException("Invalid arrangement.");
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
                if (part.Pattern.Recipe is { } recipe && (string.IsNullOrWhiteSpace(recipe.Generator) || string.IsNullOrWhiteSpace(recipe.Version) || string.IsNullOrWhiteSpace(recipe.Random)))
                    throw new ArgumentException("Recipe needs generator, version and random algorithm.");
                part.Pattern.Recipe?.Drummer?.Validate();
                part.Pattern.Recipe?.Settings?.Validate();
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
        foreach (var entry in Arrangement)
            if (entry is null || !ids.Contains(entry.SectionId) || entry.Repeats is < 1 or > 128)
                throw new ArgumentException("Arrangement refers to an unknown section or invalid repeat count.");
    }
}

// Machine-local. Offsets delay an endpoint; compensate a slow device by delaying the others.
public sealed record RigEndpoint(string Id, bool SendClock = true, double OffsetMilliseconds = 0);
public sealed record DeviceRoute(string Destination, string EndpointId, int? Channel = null);
public sealed record Rig(int SchemaVersion, ImmutableArray<RigEndpoint> Endpoints, ImmutableArray<DeviceRoute> Routes)
{
    public const int CurrentSchemaVersion = 1;
    public string? Input { get; init; }
    public static Rig Memory(IEnumerable<string> destinations) => new(CurrentSchemaVersion, [new("memory")],
        destinations.Distinct(StringComparer.Ordinal).Select(d => new DeviceRoute(d, "memory")).ToImmutableArray());
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported rig schema; use a compatible application version.");
        if (Endpoints.IsDefault || Routes.IsDefault || Endpoints.Length > 256 || Routes.Length > 1024) throw new ArgumentException("Invalid rig.");
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Endpoints)
            if (e is null || string.IsNullOrWhiteSpace(e.Id) || !endpoints.Add(e.Id) || !double.IsFinite(e.OffsetMilliseconds) || e.OffsetMilliseconds is < 0 or > 100)
                throw new ArgumentException("Rig endpoints need unique IDs and offsets of 0–100 ms.");
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Routes)
            if (r is null || string.IsNullOrWhiteSpace(r.Destination) || !destinations.Add(r.Destination) || !endpoints.Contains(r.EndpointId) || r.Channel is < 1 or > 16)
                throw new ArgumentException("Each destination needs one route to a listed endpoint and channel 1–16.");
        if (Input is not null && string.IsNullOrWhiteSpace(Input)) throw new ArgumentException("Invalid rig input.");
    }
}

public sealed record CapturedMidi(double Seconds, string Endpoint, string? Destination, byte Status, byte Data1, byte Data2);
public sealed record PerformanceAction(double Seconds, string Kind, string? Value = null);
public sealed record TempoChange(double Seconds, double Bpm);
// Seconds are submission or host-arrival times from transport start, not physical sound onset.
public sealed record PerformanceTake(int SchemaVersion, DateTimeOffset StartedAt, double Bpm, bool Complete,
    ImmutableArray<CapturedMidi> Outputs, ImmutableArray<CapturedMidi> Inputs, ImmutableArray<PerformanceAction> Actions,
    ImmutableArray<TempoChange> TempoChanges)
{
    public const int CurrentSchemaVersion = 2;
    public Meter? Meter { get; init; }
    public string? SourceProjectId { get; init; }
    public string? SourceProjectName { get; init; }
}
