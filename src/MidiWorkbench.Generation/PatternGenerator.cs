using System.Collections.Immutable;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Generation;

public sealed class PatternGenerator
{
    public const string GeneratorId = "pattern";
    public const string Version = "pattern-2";
    public const string LegacyVersion = "pattern-1";
    public const string RandomId = "xorshift32";
    private const int MaxNotes = 100000;

    // Explicit settings create a current recipe. Stored-recipe callers pass its version.
    public Pattern Generate(Outline outline, GeneratorSettings settings, Pattern? previous = null, string? version = null)
    {
        version ??= Version;
        if (version is not (Version or LegacyVersion)) throw new InvalidOperationException($"Unsupported pattern algorithm: {version}.");
        var legacy = version == LegacyVersion;
        ArgumentNullException.ThrowIfNull(outline); ArgumentNullException.ThrowIfNull(settings);
        outline.Validate(); settings.Validate();
        var grouping = MeterGrouping.Resolve(outline.Meter, settings.Grouping);
        ValidatePrevious(outline, previous);
        if (previous?.Kind == PatternKind.Fixed) return previous;
        var pitches = Enumerable.Range(settings.MinPitch, settings.MaxPitch - settings.MinPitch + 1)
            .Where(p => outline.Scale.Contains(Mod(p - outline.Root, 12))).ToArray();
        if (pitches.Length == 0) throw new ArgumentException(legacy ? "Pitch range contains no scale notes." : "Widen the pitch range; it contains no scale notes.");
        var seed = settings.Seed ?? outline.Seed;
        var random = new SeededRandom(MixSeed(seed, settings.PartSeed));
        var notes = new List<MidiNote>();
        var locks = previous?.Notes.Where(n => n.Locked).ToArray() ?? [];
        var reservedIds = locks.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var protectedRanges = new List<(long Start, long End)>();
        foreach (var note in locks.OrderBy(n => n.Tick))
        {
            if (protectedRanges.Count > 0 && protectedRanges[^1].End >= note.Tick)
                protectedRanges[^1] = (protectedRanges[^1].Start, Math.Max(protectedRanges[^1].End, note.EndTick));
            else protectedRanges.Add((note.Tick, note.EndTick));
        }
        bool IsProtected(long tick, long end)
        {
            var low = 0; var high = protectedRanges.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (protectedRanges[middle].Start < end) low = middle + 1; else high = middle;
            }
            return low > 0 && protectedRanges[low - 1].End > tick;
        }
        var accents = new HashSet<long>(); long groupTick = 0;
        foreach (var group in grouping) { accents.Add(groupTick); groupTick += group * MusicalTime.Ppq * 4L / outline.Meter.Denominator; }
        var scale = outline.Scale.Order().ToArray();
        var degreeCache = new Dictionary<int, int>();
        int RawDegree(int degree)
        {
            var octave = (int)Math.Floor(degree / (double)scale.Length);
            return outline.Root + 12 * octave + scale[Mod(degree, scale.Length)];
        }
        int Degree(int degree)
        {
            if (legacy) return Snap(RawDegree(degree));
            if (!degreeCache.TryGetValue(degree, out var pitch))
                degreeCache[degree] = pitch = HarmonicPlacement.Single(RawDegree(degree), settings.MinPitch, settings.MaxPitch);
            return pitch;
        }
        int Snap(int pitch) => pitches.MinBy(p => Math.Abs(p - pitch));
        int SourcePitch(int pitch)
        {
            var octave = (int)Math.Floor((pitch - outline.Root) / 12d);
            return Enumerable.Range(octave - 1, 3).SelectMany(o => scale.Select(s => outline.Root + 12 * o + s))
                .OrderBy(p => Math.Abs(p - pitch)).ThenBy(p => p).First();
        }
        int Value(ImmutableArray<int> values, int index, int fallback) => values.IsEmpty ? fallback : values[index % values.Length];
        double Probability(int index) => settings.Probability * (settings.StepProbabilities.IsEmpty ? 1 : settings.StepProbabilities[index % settings.StepProbabilities.Length]) * outline.Density;
        var walkIndex = Array.IndexOf(pitches, Snap(outline.Root));
        var source = settings.SourceNotes.OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToArray();
        var sourceOrder = settings.SourceNotes.Select((n, i) => (n.Id, Index: i)).ToDictionary(n => n.Id, n => n.Index, StringComparer.Ordinal);
        var sourceLength = settings.SourceLengthTicks ?? (source.Length == 0 ? outline.Meter.BarTicks : source.Max(n => n.EndTick));
        var materialCursor = new MaterialCursor(source, sourceOrder, legacy ? Snap : static p => p, settings.Direction == ArpeggioDirection.AsPlayed);
        int[]? degreeMaterial = null, lastMaterial = null, lastExpanded = null;
        int[] Material(long tick)
        {
            if (source.Length == 0) return legacy ? settings.ChordDegrees.Select(Degree).Distinct().ToArray()
                : degreeMaterial ??= settings.ChordDegrees.Select(RawDegree).Distinct().ToArray();
            return materialCursor.At(tick % sourceLength);
        }
        void Emit(long tick, long cellEnd, int pitch, int velocity, int ratchets, int ordinal, int voice, bool accent)
        {
            var span = cellEnd - tick;
            for (var ratchet = 0; ratchet < ratchets; ratchet++)
            {
                var at = tick + span * ratchet / ratchets;
                var next = tick + span * (ratchet + 1) / ratchets;
                if (next <= at) continue;
                var duration = Math.Max(1, (long)Math.Round((next - at) * settings.Gate));
                var id = $"g-{ordinal}-{voice}-{ratchet}";
                // IDs belong to edits across seeds, including edits moved to another time.
                if (reservedIds.Contains(id) || IsProtected(at, at + duration)) continue;
                var spread = settings.VelocitySpread == 0 ? 0 : random.Index(settings.VelocitySpread * 2 + 1) - settings.VelocitySpread;
                var dynamics = (int)Math.Round(velocity * (.4 + .6 * outline.Intensity) * (accent ? 1 : .9)) + spread;
                if (notes.Count + locks.Length >= MaxNotes) throw new InvalidOperationException("Generated pattern exceeds 100000 notes; use a longer step or fewer ratchets.");
                notes.Add(new(id, at, duration, pitch, Math.Clamp(dynamics, 1, 127)));
            }
        }
        long stepTicks = settings.Kind == GeneratorKind.Pad ? outline.Meter.BarTicks : settings.StepTicks;
        var ordinal = 0;
        int[]? previousChord = null;
        for (long tick = 0; tick < outline.LengthTicks; tick += stepTicks, ordinal++)
        {
            var step = ordinal % settings.Steps;
            if (!settings.EnabledSteps.IsEmpty && !settings.EnabledSteps[step % settings.EnabledSteps.Length]) continue;
            if (settings.Kind == GeneratorKind.Euclidean && Mod(step - settings.Rotation, settings.Steps) * settings.Pulses % settings.Steps >= settings.Pulses) continue;
            var chance = Probability(step);
            if (chance <= 0 || chance < 1 && random.Unit() >= chance) continue;
            var swing = settings.Kind == GeneratorKind.Pad || (ordinal & 1) == 0 ? 0 : (long)Math.Round(stepTicks * settings.Swing);
            var at = tick + swing;
            if (at >= outline.LengthTicks) continue;
            var nextTick = Math.Min(outline.LengthTicks, tick + stepTicks + ((ordinal & 1) == 0 && settings.Kind != GeneratorKind.Pad ? (long)Math.Round(stepTicks * settings.Swing) : 0));
            var velocity = Value(settings.StepVelocities, step, settings.Velocity);
            var ratchets = Value(settings.StepRatchets, step, settings.Ratchet);
            var accent = accents.Contains(tick % outline.Meter.BarTicks);
            int[] voices;
            switch (settings.Kind)
            {
                case GeneratorKind.Arpeggiator:
                    var material = Material(tick);
                    int[] expanded;
                    if (legacy) expanded = Enumerable.Range(0, settings.Octaves).SelectMany(o => material.Select(p => Snap(p + 12 * o))).Distinct().ToArray();
                    else if (ReferenceEquals(material, lastMaterial)) expanded = lastExpanded!;
                    else
                    {
                        var raw = Enumerable.Range(0, settings.Octaves).SelectMany(o => material.Select(p => p + 12 * o)).Distinct().ToArray();
                        expanded = HarmonicPlacement.Voices(source.Length > 0 ? raw.Select(SourcePitch).ToArray() : raw, settings.MinPitch, settings.MaxPitch);
                        lastMaterial = material; lastExpanded = expanded;
                    }
                    if (settings.Direction != ArpeggioDirection.AsPlayed) Array.Sort(expanded);
                    var index = settings.Direction switch
                    {
                        ArpeggioDirection.Down => expanded.Length - 1 - ordinal % expanded.Length,
                        ArpeggioDirection.UpDown when expanded.Length > 1 => expanded.Length - 1 - Math.Abs(ordinal % (2 * expanded.Length - 2) - (expanded.Length - 1)),
                        ArpeggioDirection.Random => random.Index(expanded.Length),
                        _ => ordinal % expanded.Length
                    };
                    voices = [expanded[index]];
                    break;
                case GeneratorKind.Stochastic:
                    voices = [pitches[random.Index(pitches.Length)]];
                    break;
                case GeneratorKind.RandomWalk:
                    if (ordinal > 0)
                    {
                        var delta = random.Index(settings.WalkStep * 2 + 1) - settings.WalkStep;
                        // Reflect at range boundaries instead of accumulating on the end notes.
                        var proposed = walkIndex + delta;
                        if (pitches.Length == 1) walkIndex = 0;
                        else { var period = 2 * (pitches.Length - 1); proposed = Mod(proposed, period); walkIndex = proposed < pitches.Length ? proposed : period - proposed; }
                    }
                    voices = [pitches[walkIndex]];
                    break;
                case GeneratorKind.Chord:
                case GeneratorKind.Pad:
                    var progression = settings.Progression[ordinal % settings.Progression.Length];
                    voices = source.Length > 0 ? Material(tick)
                        : settings.ChordDegrees.Select(d => legacy ? Degree(d + progression) : RawDegree(d + progression)).Distinct().Order().ToArray();
                    if (!legacy)
                    {
                        voices = HarmonicPlacement.Voices(source.Length > 0 ? voices.Select(SourcePitch).ToArray() : voices,
                            settings.MinPitch, settings.MaxPitch, source.Length == 0 ? previousChord : null);
                        Array.Sort(voices);
                    }
                    else if (previousChord is not null && source.Length == 0)
                    {
                        voices = voices.Select((pitch, i) => pitches.Where(p => Mod(p - pitch, 12) == 0)
                            .MinBy(p => Math.Abs(p - previousChord[Math.Min(i, previousChord.Length - 1)]))).Distinct().Order().ToArray();
                    }
                    previousChord = voices;
                    break;
                default:
                    var degree = settings.StepDegrees[step % settings.StepDegrees.Length];
                    if (outline.Variation > 0 && random.Unit() < outline.Variation) degree += random.Index(3) - 1;
                    voices = [Degree(degree)];
                    break;
            }
            for (var voice = 0; voice < voices.Length; voice++) Emit(at, nextTick, voices[voice], velocity, ratchets, ordinal, voice, accent);
        }
        notes.AddRange(locks);
        return new(previous?.Id ?? "generated", locks.Length > 0 ? PatternKind.Hybrid : PatternKind.Generative,
            notes.OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ThenBy(n => n.Id, StringComparer.Ordinal).ToImmutableArray())
        { Recipe = new(GeneratorId, version, RandomId, seed) { Settings = settings } };
    }

    public Pattern Regenerate(Outline outline, Pattern previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.Kind == PatternKind.Fixed)
        {
            outline.Validate(); ValidatePrevious(outline, previous); return previous;
        }
        var recipe = previous.Recipe ?? throw new InvalidOperationException("Pattern has no generation recipe.");
        ValidateRecipe(recipe);
        var settings = recipe.Settings ?? throw new InvalidOperationException("Pattern recipe has no settings.");
        // The recipe seed captures the original outline seed when no per-part override was set.
        return Generate(outline with { Seed = recipe.Seed }, settings, previous, recipe.Version);
    }

    public static void ValidateRecipe(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Generator != GeneratorId || recipe.Version is not (Version or LegacyVersion) || recipe.Random != RandomId)
            throw new InvalidOperationException($"Unsupported generator recipe: {recipe.Generator}/{recipe.Version}/{recipe.Random}.");
        if (recipe.Settings is null) throw new InvalidOperationException("Pattern recipe has no settings.");
        recipe.Settings.Validate();
    }

    private static void ValidatePrevious(Outline outline, Pattern? previous)
    {
        if (previous is null) return;
        if (!Enum.IsDefined(previous.Kind) || previous.Notes.IsDefault || previous.Notes.Length > MaxNotes) throw new ArgumentException("Invalid previous pattern.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in previous.Notes)
        {
            if (n is null || string.IsNullOrWhiteSpace(n.Id) || !ids.Add(n.Id) || n.Tick < 0 || n.Duration <= 0 || n.Tick > long.MaxValue - n.Duration || n.Pitch is < 0 or > 127 || n.Velocity is < 1 or > 127)
                throw new ArgumentException("Invalid previous note.");
            if ((previous.Kind == PatternKind.Fixed || n.Locked) && (n.Tick >= outline.LengthTicks || n.Duration > outline.LengthTicks - n.Tick))
                throw new InvalidOperationException("Section is shorter than protected notes; move them first.");
        }
    }

    private static uint MixSeed(uint seed, uint partSeed)
    {
        if (partSeed == 0) return seed;
        var value = seed ^ partSeed * 0x9E3779B9u;
        value ^= value >> 16; value *= 0x85EBCA6Bu; value ^= value >> 13; value *= 0xC2B2AE35u; return value ^ value >> 16;
    }
    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;

    // Sweep captured events once per source loop; long captures must not be rescanned at every step.
    private sealed class MaterialCursor(MidiNote[] source, Dictionary<string, int> sourceOrder, Func<int, int> snap, bool asPlayed)
    {
        private readonly Dictionary<int, MidiNote> active = [];
        private readonly PriorityQueue<int, long> endings = new();
        private int next;
        private int lastGroup;
        private long lastTick = -1;
        private int[]? cached;

        public int[] At(long tick)
        {
            if (tick < lastTick)
            {
                active.Clear(); endings.Clear(); next = 0; lastGroup = 0; cached = null;
            }
            lastTick = tick;
            while (next < source.Length && source[next].Tick <= tick)
            {
                if (next == 0 || source[next].Tick != source[next - 1].Tick) lastGroup = next;
                active.Add(next, source[next]); endings.Enqueue(next, source[next].EndTick); next++; cached = null;
            }
            while (endings.TryPeek(out var index, out var end) && end <= tick)
            {
                endings.Dequeue(); active.Remove(index); cached = null;
            }
            if (cached is not null) return cached;
            IEnumerable<MidiNote> notes = active.Values;
            if (active.Count == 0)
            {
                var onset = source[lastGroup].Tick;
                notes = source.Skip(lastGroup).TakeWhile(n => n.Tick == onset);
            }
            notes = asPlayed ? notes.OrderBy(n => n.Tick).ThenBy(n => sourceOrder[n.Id]) : notes.OrderBy(n => n.Pitch);
            cached = notes.Select(n => snap(n.Pitch)).Distinct().ToArray();
            return cached;
        }
    }
}
