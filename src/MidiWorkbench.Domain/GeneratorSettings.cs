using System.Collections.Immutable;

namespace MidiWorkbench.Domain;

public enum GeneratorKind { StepSequencer, Arpeggiator, Euclidean, Stochastic, RandomWalk, Chord, Pad }
public enum ArpeggioDirection { Up, Down, UpDown, AsPlayed, Random }

// Material is copied into the recipe, so recall does not depend on a mutable source part.
public sealed record GeneratorSettings(GeneratorKind Kind = GeneratorKind.StepSequencer)
{
    public uint? Seed { get; init; }
    public uint PartSeed { get; init; }
    public int Steps { get; init; } = 16;
    public long StepTicks { get; init; } = MusicalTime.Ppq / 4;
    public ImmutableArray<int> StepDegrees { get; init; } = [0, 2, 4, 2];
    public ImmutableArray<bool> EnabledSteps { get; init; } = [];
    public ImmutableArray<int> StepVelocities { get; init; } = [];
    public ImmutableArray<double> StepProbabilities { get; init; } = [];
    public ImmutableArray<int> StepRatchets { get; init; } = [];
    public int MinPitch { get; init; } = 36;
    public int MaxPitch { get; init; } = 96;
    public double Gate { get; init; } = .8;
    public int Velocity { get; init; } = 96;
    public int VelocitySpread { get; init; } = 8;
    public double Swing { get; init; }
    public double Probability { get; init; } = 1;
    public int Ratchet { get; init; } = 1;
    public int Pulses { get; init; } = 5;
    public int Rotation { get; init; }
    public int WalkStep { get; init; } = 2;
    public ArpeggioDirection Direction { get; init; } = ArpeggioDirection.Up;
    public int Octaves { get; init; } = 1;
    public ImmutableArray<int> ChordDegrees { get; init; } = [0, 2, 4];
    public ImmutableArray<int> Progression { get; init; } = [0];
    public ImmutableArray<MidiNote> SourceNotes { get; init; } = [];
    public long? SourceLengthTicks { get; init; }
    // Denominator-note groups: 7/8 may be [2, 2, 3] or [3, 2, 2]. Empty uses meter-aware defaults.
    public ImmutableArray<int> Grouping { get; init; } = [];

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Direction)) throw new ArgumentException("Unknown generator or direction.");
        if (Steps is < 1 or > 128 || StepTicks is < 8 or > MusicalTime.Ppq * 128L || MinPitch is < 0 or > 127 || MaxPitch < MinPitch || MaxPitch > 127)
            throw new ArgumentException("Choose 1–128 steps, step ticks 8–122880 and a MIDI pitch range.");
        if (!double.IsFinite(Gate) || Gate is <= 0 or > 1 || !double.IsFinite(Swing) || Swing is < 0 or > .45 ||
            !double.IsFinite(Probability) || Probability is < 0 or > 1 || Velocity is < 1 or > 127 || VelocitySpread is < 0 or > 126 ||
            Ratchet is < 1 or > 8 || WalkStep is < 1 or > 24 || Octaves is < 1 or > 5 || Pulses is < 0 or > 128 || Rotation is < 0 or > 127 ||
            Kind == GeneratorKind.Euclidean && (Pulses > Steps || Rotation >= Steps))
            throw new ArgumentException("Invalid gate, velocity, swing, probability or rhythm.");
        Degrees(StepDegrees, nameof(StepDegrees)); Degrees(ChordDegrees, nameof(ChordDegrees)); Degrees(Progression, nameof(Progression));
        if (EnabledSteps.IsDefault || StepVelocities.IsDefault || StepProbabilities.IsDefault || StepRatchets.IsDefault || SourceNotes.IsDefault || Grouping.IsDefault)
            throw new ArgumentException("Generator arrays must be initialized.");
        if (EnabledSteps.Length > Steps || StepVelocities.Length > Steps || StepProbabilities.Length > Steps || StepRatchets.Length > Steps ||
            StepVelocities.Any(v => v is < 1 or > 127) || StepProbabilities.Any(v => !double.IsFinite(v) || v is < 0 or > 1) ||
            StepRatchets.Any(v => v is < 1 or > 8)) throw new ArgumentException("Invalid step controls.");
        if (Grouping.Length > 32 || Grouping.Any(g => g is < 1 or > 32)) throw new ArgumentException("Invalid meter grouping.");
        if (SourceLengthTicks is <= 0 || SourceNotes.Length > 100000) throw new ArgumentException("Invalid source length or note count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in SourceNotes)
            if (n is null || string.IsNullOrWhiteSpace(n.Id) || !ids.Add(n.Id) || n.Tick < 0 || n.Duration <= 0 || n.Tick > long.MaxValue - n.Duration ||
                n.Pitch is < 0 or > 127 || n.Velocity is < 1 or > 127 || SourceLengthTicks is { } length && n.EndTick > length)
                throw new ArgumentException("Invalid source note.");
    }

    private static void Degrees(ImmutableArray<int> degrees, string name)
    {
        if (degrees.IsDefaultOrEmpty || degrees.Length > 128 || degrees.Any(d => d is < -128 or > 128))
            throw new ArgumentException("Use 1–128 scale degrees between -128 and 128.", name);
    }
}

public static class MeterGrouping
{
    public static ImmutableArray<int> Resolve(Meter meter, ImmutableArray<int> grouping = default)
    {
        ArgumentNullException.ThrowIfNull(meter); meter.Validate();
        if (!grouping.IsDefaultOrEmpty)
        {
            if (grouping.Length > 32 || grouping.Any(g => g < 1 || g > 32) || grouping.Sum() != meter.Numerator)
                throw new ArgumentException("Meter groups must add up to the numerator.", nameof(grouping));
            return grouping;
        }
        if (meter.Denominator >= 8 && meter.Numerator > 3)
        {
            if (meter.Numerator % 3 == 0) return Enumerable.Repeat(3, meter.Numerator / 3).ToImmutableArray();
            var groups = ImmutableArray.CreateBuilder<int>(); var remaining = meter.Numerator;
            while (remaining > 3) { groups.Add(2); remaining -= 2; }
            groups.Add(remaining); return groups.ToImmutable();
        }
        return Enumerable.Repeat(1, meter.Numerator).ToImmutableArray();
    }
}
