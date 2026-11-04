using System.Collections.Immutable;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Generation;

public sealed class SectionGenerator
{
    public const string Version = "scaffold-1";
    public const string ScaffoldId = "scaffold";
    public const string DrummerId = "drummer";
    public const string RandomId = "xorshift32";
    private static readonly PartRole[] Generated = [PartRole.Drums, PartRole.Bass, PartRole.Pulse, PartRole.Pad];

    // drummer selects the drummer for drums; basicDrums selects the scaffold grid; neither keeps the existing generator.
    public Section Generate(Outline outline, Section? previous = null, DrummerSettings? drummer = null, bool basicDrums = false,
        IReadOnlyDictionary<string, GeneratorSettings>? generators = null, IReadOnlySet<string>? partIds = null)
    {
        outline.Validate(); drummer?.Validate();
        var knownIds = Generated.Select(r => r.ToString().ToLowerInvariant()).Concat(previous?.Parts.Select(p => p.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        if (generators?.Keys.Any(id => !knownIds.Contains(id)) == true || partIds?.Any(id => !knownIds.Contains(id)) == true)
            throw new ArgumentException("Generation scope contains an unknown part.");
        var parts = ImmutableArray.CreateBuilder<Part>();
        foreach (var role in Generated)
        {
            var id = role.ToString().ToLowerInvariant();
            var old = previous?.Parts.FirstOrDefault(p => p.Id == id);
            if (old is not null && (old.Pattern.Kind == PatternKind.Fixed || partIds is not null && !partIds.Contains(id)))
            {
                ValidateProtected(outline, old.Pattern); parts.Add(old); continue;
            }
            if (old is null && partIds is not null && !partIds.Contains(id)) continue;
            if (generators?.TryGetValue(id, out var configured) == true)
            {
                parts.Add(GeneratePart(outline, old ?? DefaultPart(role, id), configured)); continue;
            }
            if (old?.Pattern.Recipe is { } oldRecipe)
            {
                if (oldRecipe.Generator == PatternGenerator.GeneratorId)
                {
                    PatternGenerator.ValidateRecipe(oldRecipe);
                    parts.Add(old with { Pattern = new PatternGenerator().Generate(outline, oldRecipe.Settings!, old.Pattern, oldRecipe.Version) }); continue;
                }
                if (!(role == PartRole.Drums && (basicDrums || drummer is not null))) ValidateRecipe(oldRecipe);
            }
            Recipe recipe;
            List<MidiNote> notes;
            var useDrummer = role == PartRole.Drums && !basicDrums &&
                (drummer is not null || (old?.Pattern.Recipe?.Generator ?? (old is null ? DrummerId : ScaffoldId)) == DrummerId);
            if (useDrummer)
            {
                var settings = drummer ?? old?.Pattern.Recipe?.Drummer ?? DrumPersonas.Settings(DrumPersona.Simple);
                var legacy = drummer is null && old?.Pattern.Recipe?.Version == DrumPlanner.Version && settings.Grouping.IsEmpty;
                recipe = new(DrummerId, legacy ? DrumPlanner.Version : DrumVersion(outline.Meter, settings), RandomId, outline.Seed) { Drummer = settings };
                notes = Drums(outline, settings, legacy);
            }
            else
            {
                recipe = new(ScaffoldId, Version, RandomId, outline.Seed);
                notes = Scaffold(outline, role, id);
            }
            var locked = old?.Pattern.Notes.Where(n => n.Locked).ToArray() ?? [];
            if (locked.Any(n => n.EndTick > outline.LengthTicks))
                throw new InvalidOperationException("Section is shorter than a locked note; unlock or move it first.");
            // Manual locks own their time range; regeneration must not double those attacks.
            notes.RemoveAll(n => locked.Any(l => l.Id == n.Id || l.Tick < n.EndTick && n.Tick < l.EndTick));
            notes.AddRange(locked);
            var pattern = new Pattern(old?.Pattern.Id ?? id, locked.Length > 0 ? PatternKind.Hybrid : PatternKind.Generative,
                notes.OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToImmutableArray()) { Recipe = recipe };
            parts.Add((old ?? DefaultPart(role, id)) with { Pattern = pattern });
        }
        if (previous is not null)
            foreach (var other in previous.Parts.Where(p => !parts.Any(created => created.Id == p.Id)))
            {
                ValidateProtected(outline, other.Pattern);
                if (generators?.TryGetValue(other.Id, out var configured) == true && (partIds is null || partIds.Contains(other.Id)))
                    parts.Add(GeneratePart(outline, other, configured));
                else if (other.Pattern.Kind != PatternKind.Fixed && other.Pattern.Recipe?.Generator == PatternGenerator.GeneratorId &&
                    (partIds is null || partIds.Contains(other.Id)))
                {
                    PatternGenerator.ValidateRecipe(other.Pattern.Recipe);
                    parts.Add(other with { Pattern = new PatternGenerator().Generate(outline, other.Pattern.Recipe.Settings!, other.Pattern, other.Pattern.Recipe.Version) });
                }
                else parts.Add(other);
            }
        return new(previous?.Id ?? "section-1", outline, parts.ToImmutable());
    }

    public Part GeneratePart(Outline outline, Part part, GeneratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part with { Pattern = new PatternGenerator().Generate(outline, settings, part.Pattern) };
    }

    private static Part DefaultPart(PartRole role, string id) => new(id, role.ToString(), role, role.ToString(),
        role == PartRole.Drums ? 10 : (int)role, new Pattern(id, PatternKind.Generative, []));

    private static string DrumVersion(Meter meter, DrummerSettings settings) =>
        settings.Grouping.IsEmpty && (meter is { Numerator: 4, Denominator: 4 } or { Numerator: 3, Denominator: 4 } or { Numerator: 6, Denominator: 8 })
            ? DrumPlanner.Version : DrumPlanner.GroupedVersion;

    private static void ValidateRecipe(Recipe recipe)
    {
        var supported = recipe.Random == RandomId && (recipe.Generator == ScaffoldId && recipe.Version == Version ||
            recipe.Generator == DrummerId && (recipe.Version == DrumPlanner.Version || recipe.Version == DrumPlanner.GroupedVersion));
        if (!supported) throw new InvalidOperationException($"Unsupported generator recipe: {recipe.Generator}/{recipe.Version}/{recipe.Random}.");
    }

    private static void ValidateProtected(Outline outline, Pattern pattern)
    {
        if (pattern.Notes.Any(n => n.Tick < 0 || n.Duration <= 0 || n.Tick > outline.LengthTicks || n.Duration > outline.LengthTicks - n.Tick))
            throw new InvalidOperationException("Section is shorter than retained notes; move them first.");
    }

    private static List<MidiNote> Drums(Outline outline, DrummerSettings s, bool legacy)
    {
        var options = new DrumOptions(outline.Bars, s.Complexity, outline.Intensity, s.Swing, s.Fills, s.HalfTime,
            unchecked((int)outline.Seed), outline.Variation, s.Persona) { Grouping = s.Grouping };
        var notes = new List<MidiNote>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in legacy ? DrumPlanner.PlanLegacy(options, outline.Meter) : DrumPlanner.Plan(options, outline.Meter))
        {
            var id = $"drums-{hit.Tick}-{hit.Note}";
            if (hit.Tick < outline.LengthTicks && ids.Add(id))
                notes.Add(new(id, hit.Tick, Math.Min(DrumPlanner.HitTicks, outline.LengthTicks - hit.Tick), hit.Note, hit.Velocity));
        }
        return notes;
    }

    private static List<MidiNote> Scaffold(Outline outline, PartRole role, string id)
    {
        var random = new SeededRandom(outline.Seed ^ ((uint)role + 1) * 0x9E3779B9u);
        var notes = new List<MidiNote>();
        var step = MusicalTime.Ppq / 2;
        for (long tick = 0; tick < outline.LengthTicks; tick += step)
        {
            var index = tick / step;
            var downbeat = tick % outline.Meter.BarTicks == 0;
            if (role == PartRole.Pad && !downbeat) continue;
            var chance = role == PartRole.Pad ? 1 : downbeat ? .85 : outline.Density;
            if (outline.Density == 0 || random.Unit() > chance) continue;
            var degree = outline.Variation == 0 ? (int)(index % outline.Scale.Length)
                : random.Unit() < outline.Variation ? random.Index(outline.Scale.Length) : (int)(index % outline.Scale.Length);
            var pitch = role switch
            {
                PartRole.Drums => downbeat ? 36 : index % 4 == 2 ? 38 : 42,
                PartRole.Bass => Math.Max(0, outline.Root - 12) + outline.Scale[degree],
                PartRole.Pulse => outline.Root + 12 + outline.Scale[degree],
                _ => outline.Root + outline.Scale[0]
            };
            var duration = role == PartRole.Pad ? outline.Meter.BarTicks : role == PartRole.Drums ? 120 : 360;
            notes.Add(new($"{id}-{tick}", tick, Math.Min(duration, outline.LengthTicks - tick), pitch,
                Math.Clamp((int)(30 + outline.Intensity * 80 + random.Unit() * 10), 1, 127)));
        }
        return notes;
    }
}

internal sealed class SeededRandom(uint seed)
{
    private uint state = seed == 0 ? 0xA341316Cu : seed;
    private uint Next()
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state;
    }
    public double Unit() => Next() / 4294967296.0;
    public int Index(int count) => (int)(Next() % (uint)count);
}
