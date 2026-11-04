using System.Collections.Immutable;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Generation;

public sealed class SectionGenerator
{
    public const string Version = "scaffold-1";
    public Section Generate(Outline outline, Section? previous = null)
    {
        outline.Validate();
        var parts = ImmutableArray.CreateBuilder<Part>();
        foreach (var role in Enum.GetValues<PartRole>())
        {
            var id = role.ToString().ToLowerInvariant();
            var old = previous?.Parts.FirstOrDefault(p => p.Id == id);
            if (old?.Pattern.Kind == PatternKind.Fixed) { parts.Add(old); continue; }
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
            var locked = old?.Pattern.Notes.Where(n => n.Locked).ToArray() ?? [];
            if (locked.Any(n => n.EndTick > outline.LengthTicks))
                throw new InvalidOperationException("Section is shorter than a locked note; unlock or move it first.");
            // Manual locks own their time range; regeneration must not double those attacks.
            notes.RemoveAll(n => locked.Any(l => l.Id == n.Id || l.Tick < n.EndTick && n.Tick < l.EndTick));
            notes.AddRange(locked);
            var pattern = new Pattern(old?.Pattern.Id ?? id, locked.Length > 0 ? PatternKind.Hybrid : PatternKind.Generative,
                notes.OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToImmutableArray())
                { GeneratorVersion = Version, GeneratorSeed = outline.Seed };
            parts.Add(new(id, old?.Name ?? role.ToString(), role, old?.Destination ?? role.ToString(), old?.Channel ?? (role == PartRole.Drums ? 10 : (int)role), pattern));
        }
        if (previous is not null)
            foreach (var other in previous.Parts.Where(p => !parts.Any(created => created.Id == p.Id))) parts.Add(other);
        return new(previous?.Id ?? "section-1", outline, parts.ToImmutable());
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
