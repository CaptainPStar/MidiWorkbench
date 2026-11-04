using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class GenerationMusicalSpecs
{
    private static readonly PatternGenerator Generator = new();
    private static Outline Minor(int root = 36) => Build.Outline(2) with { Root = root, Scale = [0, 2, 3, 5, 7, 8, 10], Density = 1, Intensity = 1, Variation = 0 };
    private static GeneratorSettings Harmony(GeneratorKind kind = GeneratorKind.Pad) => new(kind)
        { MinPitch = 48, MaxPitch = 72, ChordDegrees = [0, 2, 4], Progression = [0, 3], Gate = 1, VelocitySpread = 0, StepTicks = 3840 };
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectStore.JsonOptions);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void NeedsWiderRange(Action generate)
    {
        try { generate(); }
        catch (ArgumentException ex) when (ex.Message.Contains("Widen the pitch range", StringComparison.Ordinal)) { return; }
        throw new Exception("Impossible harmonic range did not give an actionable widening error");
    }
    public static void Run()
    {
        MinorTriadsRetainHarmonyAndRegister();
        OctaveVoicesAndInversions();
        ArpeggioAndCapturedMaterial();
        ImpossibleRangesAndProtection();
        StoredVersionPolicy();
        CurrentGoldenOutput();
    }
    private static void MinorTriadsRetainHarmonyAndRegister()
    {
        foreach (var kind in new[] { GeneratorKind.Chord, GeneratorKind.Pad })
        foreach (var root in new[] { 36, 48 })
        {
            var outline = Minor(root); var settings = Harmony(kind);
            var pattern = Generator.Generate(outline, settings);
            Check(pattern.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([48, 51, 55]), $"{kind}/{root} lost its C-minor triad");
            Check(pattern.Notes.Where(n => n.Tick == 3840).Select(n => n.Pitch).SequenceEqual([53, 56, 60]), $"{kind}/{root} lost its F-minor progression");
            Check(pattern.Notes.GroupBy(n => n.Tick).All(g => g.Count() == 3 && g.Select(n => n.Pitch).Distinct().Count() == 3), "Voicing silently lost a harmonic voice");
            Check(pattern.Notes.All(n => n.Pitch is >= 48 and <= 72), "Harmonic placement exceeded its range");
            Check(pattern.Recipe?.Version == PatternGenerator.Version && Json(pattern) == Json(Generator.Regenerate(outline, pattern)), "Current recipe failed exact recall");
        }
        var step = Generator.Generate(Minor(), new() { MinPitch = 48, MaxPitch = 72, StepDegrees = [0, 2, 4], VelocitySpread = 0 });
        Check(step.Notes.Take(3).Select(n => n.Pitch).SequenceEqual([48, 51, 55]), "Range placement changed single-step harmonic degrees");
    }
    private static void OctaveVoicesAndInversions()
    {
        var settings = Harmony() with { ChordDegrees = [0, 7], Progression = [0, 3] };
        var octaves = Generator.Generate(Minor(), settings);
        Check(octaves.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([48, 60]), "Distinct written octave voices collapsed");
        Check(octaves.Notes.Where(n => n.Tick == 3840).Select(n => n.Pitch).SequenceEqual([53, 65]), "Voice leading assigned two octave voices to one pitch");
        var inversion = Generator.Generate(Minor(), Harmony() with { ChordDegrees = [2, 4, 7], Progression = [0] });
        Check(inversion.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([51, 55, 60]), "Initial common-octave placement lost the requested inversion");
        var duplicate = Generator.Generate(Minor(), Harmony() with { ChordDegrees = [0, 0, 2, 4], Progression = [0] });
        Check(duplicate.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([48, 51, 55]), "Identical raw degrees were treated as additional octave voices");
    }
    private static void ArpeggioAndCapturedMaterial()
    {
        var source = ImmutableArray.Create(new MidiNote("eb", 0, 7680, 39, 90), new MidiNote("c", 0, 7680, 36, 90), new MidiNote("g", 0, 7680, 43, 90));
        foreach (var captured in new[] { false, true })
        {
            var settings = Harmony(GeneratorKind.Arpeggiator) with { StepTicks = 240, Octaves = 2, SourceNotes = captured ? source : [], SourceLengthTicks = captured ? 7680 : null };
            var up = Generator.Generate(Minor(), settings);
            Check(up.Notes.Take(6).Select(n => n.Pitch).SequenceEqual([48, 51, 55, 60, 63, 67]), "Arpeggio range clipping collapsed degree/source octave material");
            if (captured)
            {
                var played = Generator.Generate(Minor(), settings with { Direction = ArpeggioDirection.AsPlayed });
                Check(played.Notes.Take(6).Select(n => n.Pitch).SequenceEqual([51, 48, 55, 63, 60, 67]), "Register placement reordered captured AsPlayed material");
                var high = Generator.Generate(Minor(), settings with { SourceNotes = source.Select(n => n with { Pitch = n.Pitch + 48 }).ToImmutableArray() });
                Check(high.Notes.Take(6).Select(n => n.Pitch).SequenceEqual([48, 51, 55, 60, 63, 67]), "Captured high-register transposition changed harmonic classes");
            }
        }
        var chord = Generator.Generate(Minor(), Harmony() with { SourceNotes = [..source, new("octave", 0, 7680, 48, 90)] });
        Check(chord.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([48, 51, 55, 60]), "Captured same-class octave voices collapsed");
        var quantized = Generator.Generate(Minor(), Harmony() with { SourceNotes = [new("c", 0, 7680, 36, 90), new("cs", 0, 7680, 37, 90)] });
        Check(quantized.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([48, 60]), "Source scale quantization silently lost a distinct source voice");
    }
    private static void ImpossibleRangesAndProtection()
    {
        NeedsWiderRange(() => Generator.Generate(Minor(), Harmony() with { MaxPitch = 53 }));
        NeedsWiderRange(() => Generator.Generate(Minor(), Harmony() with { MaxPitch = 59, ChordDegrees = [0, 7], Progression = [0] }));
        NeedsWiderRange(() => Generator.Generate(Minor(), Harmony(GeneratorKind.Arpeggiator) with { MaxPitch = 59, Octaves = 2 }));
        NeedsWiderRange(() => Generator.Generate(Minor(), new() { MinPitch = 48, MaxPitch = 48, StepDegrees = [2] }));
        var original = Generator.Generate(Minor(), Harmony());
        var fixedPattern = original with { Kind = PatternKind.Fixed };
        Check(ReferenceEquals(Generator.Generate(Minor(), Harmony() with { MaxPitch = 53 }, fixedPattern), fixedPattern), "Range correction rewrote fixed material");
        var locked = new MidiNote("manual", 0, 120, 65, 87, true);
        var hybrid = original with { Kind = PatternKind.Hybrid, Notes = [locked, ..original.Notes] };
        var regenerated = Generator.Generate(Minor(), Harmony(), hybrid);
        Check(regenerated.Kind == PatternKind.Hybrid && regenerated.Notes.Single(n => n.Id == "manual") == locked
            && !regenerated.Notes.Any(n => !n.Locked && n.Tick < locked.EndTick), "Harmonic placement altered or doubled protected material");
    }
    private static void StoredVersionPolicy()
    {
        var outline = Minor(); var settings = Harmony();
        var legacy = Generator.Generate(outline, settings, version: PatternGenerator.LegacyVersion);
        Check(legacy.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([48]) && legacy.Recipe?.Version == PatternGenerator.LegacyVersion, "Legacy fixture no longer reproduces the historical collapse");
        Check(Json(legacy) == Json(Generator.Regenerate(outline with { Seed = 999 }, legacy)), "Legacy Regenerate silently upgraded or changed its seed");
        foreach (var id in new[] { "pad", "custom" })
        {
            var section = new Section("section", outline, [new(id, "Harmony", PartRole.Pad, "Pad", 4, legacy)]);
            var regenerated = new SectionGenerator().Generate(outline, section, partIds: new HashSet<string> { id });
            Check(Json(regenerated.Parts.Single().Pattern) == Json(legacy), $"Stored {id} outline generation upgraded its legacy algorithm");
        }
        var directory = Path.Combine(Path.GetTempPath(), "midi-legacy-recipe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "legacy.json");
            ProjectStore.Save(path, new SongProject(SongProject.CurrentSchemaVersion, "Legacy harmony", 120,
                [new("section", outline, [new("pad", "Harmony", PartRole.Pad, "Pad", 4, legacy)])]));
            var bytes = File.ReadAllBytes(path);
            using var session = new WorkbenchSession(); session.Load(path);
            Check(Json(session.Project.Sections[0].Parts.Single().Pattern) == Json(legacy) && File.ReadAllBytes(path).SequenceEqual(bytes), "Opening legacy material rewrote its notes/recipe/file");
            var outlineCandidate = session.PrepareGeneration(outline);
            Check(Json(outlineCandidate.Parts.Single(p => p.Id == "pad").Pattern) == Json(legacy), "Application outline preview upgraded a stored recipe");
            var variation = session.PrepareVariationAsync(123).GetAwaiter().GetResult(); var varied = variation.Parts.Single(p => p.Id == "pad").Pattern;
            Check(varied.Recipe?.Version == PatternGenerator.LegacyVersion && Json(varied) == Json(Generator.Regenerate(variation.Outline, varied)), "Whole-section variation silently changed recipe algorithm");
            var explicitPreview = session.PreparePartGeneration("pad", settings).Parts.Single(p => p.Id == "pad").Pattern;
            Check(explicitPreview.Recipe?.Version == PatternGenerator.Version && explicitPreview.Notes.Count(n => n.Tick == 0) == 3, "Explicit part generation failed to create current harmonic material");
            Check(session.Project.Sections[0].Parts.Single().Pattern.Recipe?.Version == PatternGenerator.LegacyVersion, "Preparing an upgrade changed accepted material before acceptance");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void CurrentGoldenOutput()
    {
        var text = new StringBuilder();
        foreach (var kind in new[] { GeneratorKind.StepSequencer, GeneratorKind.Chord, GeneratorKind.Pad, GeneratorKind.Arpeggiator })
        foreach (var root in new[] { 36, 48, 84 })
        foreach (var seed in new uint[] { 0, 42 })
        {
            var outline = Minor(root) with { Seed = seed, Density = .8, Intensity = .65, Variation = .3 };
            var settings = Harmony(kind) with { MaxPitch = 84, StepTicks = 240, Octaves = 2, Ratchet = 2, Gate = .7, Swing = .2,
                PartSeed = 23, VelocitySpread = 8, Probability = .85, Progression = [0, 3, 4], Direction = ArpeggioDirection.Random };
            var a = Generator.Generate(outline, settings); var b = Generator.Generate(outline, settings);
            Check(Json(a) == Json(b), "Current harmonic output is nondeterministic");
            text.Append($"{kind}/{root}/{seed}:");
            foreach (var note in a.Notes) text.Append($"{note.Tick},{note.Duration},{note.Pitch},{note.Velocity};");
            text.Append('\n');
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        Check(digest == "AFC68EBAD9B6E3772FCEF0BC50A35AA4940BBA1FCFDB20E86D36AD1BB7BC9ACE", $"{PatternGenerator.Version} harmonic golden output changed: {digest}");
    }
}
