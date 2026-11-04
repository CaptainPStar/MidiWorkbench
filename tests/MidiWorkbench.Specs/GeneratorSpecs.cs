using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidiWorkbench.Domain;
using MidiWorkbench.Generation;
using MidiWorkbench.Persistence;

public static class GeneratorSpecs
{
    private static readonly Outline Outline = new("Generators", 2, new(4, 4), 60, [0, 2, 4, 5, 7, 9, 11], 1, 1, 0, 42);
    private static readonly PatternGenerator Generator = new();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) { return; }
        throw new Exception("Expected validation failure");
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectStore.JsonOptions);
    private static SongProject Project(Outline outline, Pattern pattern) => new(SongProject.CurrentSchemaVersion, "Generator specs", 120,
        [new("s", outline, [new("p", "Pattern", PartRole.Pulse, "Synth", 1, pattern)])]);

    public static void Run()
    {
        DeterminismAndBounds();
        StepControls();
        ArpeggioMaterial();
        EuclideanRhythm();
        WalkAndDynamics();
        ChordsAndPads();
        ProtectedMaterial();
        RecallAndVersions();
        ScopedParts();
        GroupedMeters();
        StrictValidation();
        VersionedOutput();
    }

    private static void DeterminismAndBounds()
    {
        foreach (var kind in Enum.GetValues<GeneratorKind>())
        foreach (var meter in new[] { new Meter(4, 4), new Meter(7, 8), new Meter(7, 16), new Meter(1, 32), new Meter(12, 8), new Meter(32, 1) })
        foreach (var seed in new uint[] { 0, 1, 42, uint.MaxValue })
        {
            var outline = Outline with { Meter = meter, Seed = seed, Density = .85, Variation = .5 };
            var settings = new GeneratorSettings(kind) { MinPitch = 55, MaxPitch = 78, Probability = .8, Ratchet = 3, Swing = .4, PartSeed = 17 };
            var a = Generator.Generate(outline, settings);
            var b = Generator.Generate(outline, settings);
            Check(Json(a) == Json(b), $"Nondeterministic {kind}/{meter}/{seed}");
            Project(outline, a).Validate();
            Check(a.Notes.All(n => n.Pitch >= 55 && n.Pitch <= 78 && outline.Scale.Contains((n.Pitch - outline.Root + 120) % 12)), $"Scale/range: {kind}");
            Check(a.Notes.Select(n => n.Id).Distinct().Count() == a.Notes.Length, "Duplicate generated IDs");
        }
        var stochastic = new GeneratorSettings(GeneratorKind.Stochastic);
        var original = Generator.Generate(Outline, stochastic);
        Check(Json(original.Notes) != Json(Generator.Generate(Outline with { Seed = 43 }, stochastic).Notes), "Seed has no effect");
        Check(Json(original.Notes) != Json(Generator.Generate(Outline, stochastic with { PartSeed = 1 }).Notes), "Part seed has no effect");
        Check(Json(Generator.Generate(Outline, stochastic with { Seed = 10 })) == Json(Generator.Generate(Outline with { Seed = 1 }, stochastic with { Seed = 10 })), "Seed override ignored");
        Check(Enum.GetValues<GeneratorKind>().All(k => Generator.Generate(Outline with { Density = 0 }, new(k)).Notes.IsEmpty), "Zero density produced notes");
    }

    private static void StepControls()
    {
        var settings = new GeneratorSettings { Steps = 4, StepTicks = 240, StepDegrees = [0, 1, 2, 3], VelocitySpread = 0, Gate = .5 };
        var notes = Generator.Generate(Outline with { Bars = 1 }, settings).Notes;
        Check(notes.Take(4).Select(n => n.Pitch).SequenceEqual([60, 62, 64, 65]), "Step degrees are not diatonic");
        Check(notes.All(n => n.Duration == 120), "Gate ignored");
        var controls = settings with { EnabledSteps = [true, false, true, true], StepProbabilities = [1, 1, 0, 1], StepRatchets = [2, 1, 1, 1], StepVelocities = [50, 60, 70, 100] };
        var controlled = Generator.Generate(Outline with { Bars = 1 }, controls).Notes;
        Check(controlled.Take(3).Select(n => n.Tick).SequenceEqual([0L, 120L, 720L]), "Enabled, probability or ratchet controls ignored");
        Check(controlled.Take(2).All(n => n.Duration == 60 && n.Velocity == 50), "Ratchet gate/velocity wrong");
        var swung = Generator.Generate(Outline, settings with { Swing = .25 }).Notes;
        Check(swung.Take(4).Select(n => n.Tick).SequenceEqual([0L, 300L, 480L, 780L]), "Swing displaced downbeats");
        Check(swung.Zip(swung.Skip(1)).All(pair => pair.First.EndTick <= pair.Second.Tick), "Swing/gate caused overlap");
        Check(Generator.Generate(Outline, settings with { Probability = 0 }).Notes.IsEmpty, "Zero probability emitted notes");
        var partial = Generator.Generate(Outline with { Bars = 1, Meter = new(7, 8) }, settings with { StepTicks = 500, Gate = 1, Ratchet = 3, Swing = .45 });
        Check(partial.Notes.Max(n => n.EndTick) <= 3360, "Partial ratchet exceeds odd bar");
    }

    private static void ArpeggioMaterial()
    {
        var source = ImmutableArray.Create(new MidiNote("e", 0, 960, 64, 80), new MidiNote("c", 0, 960, 60, 80), new MidiNote("g", 0, 960, 67, 80),
            new MidiNote("f", 960, 960, 65, 80), new MidiNote("a", 960, 960, 69, 80));
        var settings = new GeneratorSettings(GeneratorKind.Arpeggiator) { SourceNotes = source, SourceLengthTicks = 1920, StepTicks = 240, VelocitySpread = 0 };
        var up = Generator.Generate(Outline, settings).Notes;
        Check(up.Take(4).Select(n => n.Pitch).SequenceEqual([60, 64, 67, 60]), "Arpeggio lost captured chord");
        Check(up.Where(n => n.Tick >= 960 && n.Tick < 1920).All(n => n.Pitch is 65 or 69), "Arpeggio ignored chord change");
        Check(up.Where(n => n.Tick >= 1920 && n.Tick < 2880).All(n => n.Pitch is 60 or 64 or 67), "Source did not loop");
        Check(Generator.Generate(Outline, settings with { Direction = ArpeggioDirection.Down }).Notes.Take(3).Select(n => n.Pitch).SequenceEqual([67, 64, 60]), "Down arpeggio wrong");
        Check(Generator.Generate(Outline, settings with { Direction = ArpeggioDirection.AsPlayed }).Notes.Take(3).Select(n => n.Pitch).SequenceEqual([64, 60, 67]), "Captured order lost");
        Check(Generator.Generate(Outline, settings with { Direction = ArpeggioDirection.UpDown }).Notes.Take(4).Select(n => n.Pitch).SequenceEqual([60, 64, 67, 64]), "UpDown endpoint duplicated");
        var octaves = Generator.Generate(Outline, settings with { Octaves = 2, SourceNotes = source.Take(3).Select(n => n with { Duration = 3840 }).ToImmutableArray(), SourceLengthTicks = 3840 });
        Check(octaves.Notes.Take(6).Select(n => n.Pitch).SequenceEqual([60, 64, 67, 72, 76, 79]), "Octaves ignored");
        var motif = Generator.Generate(Outline, settings with { SourceNotes = [new("m1", 0, 240, 62, 80), new("m2", 480, 240, 65, 80)], SourceLengthTicks = 960 });
        Check(motif.Notes.Take(4).Select(n => n.Pitch).SequenceEqual([62, 62, 65, 65]), "Motif order or gap sustain wrong");
        var overlapping = Generator.Generate(Outline, settings with { SourceNotes = [new("held", 0, 960, 60, 80), new("later", 240, 480, 67, 80)], SourceLengthTicks = 960 });
        Check(overlapping.Notes.Take(4).Select(n => n.Pitch).SequenceEqual([60, 67, 60, 60]), "Overlapping captured notes or release ignored");
    }

    private static void EuclideanRhythm()
    {
        var settings = new GeneratorSettings(GeneratorKind.Euclidean) { Steps = 16, Pulses = 5, StepTicks = 240, StepDegrees = [0], VelocitySpread = 0 };
        var notes = Generator.Generate(Outline with { Bars = 1 }, settings).Notes;
        Check(notes.Length == 5 && notes[0].Tick == 0, "Euclidean pulse count/phase wrong");
        var slots = notes.Select(n => n.Tick / 240).ToArray();
        var gaps = slots.Zip(slots.Skip(1).Append(slots[0] + 16), (a, b) => b - a).ToArray();
        Check(gaps.Max() - gaps.Min() <= 1, "Euclidean rhythm is uneven");
        var rotated = Generator.Generate(Outline with { Bars = 1 }, settings with { Rotation = 3 }).Notes;
        Check(rotated.Select(n => n.Tick / 240).Order().SequenceEqual(slots.Select(s => (s + 3) % 16).Order()), "Rotation wrong");
        Check(Generator.Generate(Outline, settings with { Pulses = 0 }).Notes.IsEmpty, "Zero-pulse Euclidean rhythm emitted notes");
        Check(Generator.Generate(Outline with { Bars = 1 }, settings with { Pulses = 16 }).Notes.Length == 16, "Full-pulse rhythm skipped notes");
    }

    private static void WalkAndDynamics()
    {
        var settings = new GeneratorSettings(GeneratorKind.RandomWalk) { WalkStep = 1, MinPitch = 60, MaxPitch = 72, VelocitySpread = 0 };
        var notes = Generator.Generate(Outline with { Bars = 8 }, settings).Notes;
        int[] pitches = [60, 62, 64, 65, 67, 69, 71, 72];
        Check(notes.Zip(notes.Skip(1)).All(p => Math.Abs(Array.IndexOf(pitches, p.First.Pitch) - Array.IndexOf(pitches, p.Second.Pitch)) <= 1), "Walk jumped outside step constraint");
        var one = Generator.Generate(Outline, settings with { MinPitch = 60, MaxPitch = 60 });
        Check(one.Notes.All(n => n.Pitch == 60), "Single-note walk failed");
        var quiet = Generator.Generate(Outline with { Intensity = 0 }, settings);
        var loud = Generator.Generate(Outline with { Intensity = 1 }, settings);
        Check(quiet.Notes.Select(n => (n.Tick, n.Pitch)).SequenceEqual(loud.Notes.Select(n => (n.Tick, n.Pitch))), "Intensity changed rhythm/pitch");
        Check(quiet.Notes.Average(n => n.Velocity) < loud.Notes.Average(n => n.Velocity), "Intensity did not affect velocity");
        var dense = Generator.Generate(Outline, settings);
        var sparse = Generator.Generate(Outline with { Density = .1 }, settings);
        Check(sparse.Notes.Length < dense.Notes.Length, "Density did not affect activity");
    }

    private static void ChordsAndPads()
    {
        var settings = new GeneratorSettings(GeneratorKind.Chord) { StepTicks = 960, ChordDegrees = [0, 2, 4], Progression = [0, 3, 4, 0], Gate = 1, VelocitySpread = 0 };
        var chords = Generator.Generate(Outline, settings);
        Check(chords.Notes.Where(n => n.Tick == 0).Select(n => n.Pitch).SequenceEqual([60, 64, 67]), "Chord is not a scale triad");
        Check(chords.Notes.Where(n => n.Tick == 960).Select(n => n.Pitch % 12).Order().SequenceEqual(new[] { 5, 9, 0 }.Order()), "Chord progression ignored");
        Check(chords.Notes.GroupBy(n => n.Tick).All(g => g.Count() == 3 && g.Select(n => n.Pitch).Distinct().Count() == 3), "Duplicate chord voice");
        var pad = Generator.Generate(Outline with { Meter = new(7, 8) }, settings with { Kind = GeneratorKind.Pad });
        Check(pad.Notes.Length == 6 && pad.Notes.All(n => n.Tick % 3360 == 0 && n.Duration == 3360), "Pad did not cover complete odd bars");
        var source = settings with { Kind = GeneratorKind.Pad, SourceNotes = [new("a", 0, 3840, 62, 80), new("b", 0, 3840, 65, 80)] };
        Check(Generator.Generate(Outline, source).Notes.All(n => n.Pitch is 62 or 65), "Pad lost captured harmony");
    }

    private static void ProtectedMaterial()
    {
        var settings = new GeneratorSettings { StepTicks = 240 };
        var fixedPattern = new Pattern("fixed", PatternKind.Fixed, [new("fixed-note", 0, 120, 61, 91)]);
        Check(ReferenceEquals(Generator.Generate(Outline, settings, fixedPattern), fixedPattern), "Fixed pattern changed");
        var old = Generator.Generate(Outline, settings);
        var edit = old.Notes[0] with { Tick = 300, Duration = 300, Pitch = 61, Velocity = 127, Locked = true };
        var previous = old with { Kind = PatternKind.Hybrid, Notes = [edit] };
        var generated = Generator.Generate(Outline with { Seed = 99 }, settings, previous);
        Check(generated.Id == old.Id && generated.Kind == PatternKind.Hybrid && generated.Notes.Contains(edit), "Locked note changed");
        Check(generated.Notes.Count(n => n.Id == edit.Id) == 1 && generated.Notes.Where(n => !n.Locked).All(n => n.EndTick <= edit.Tick || n.Tick >= edit.EndTick), "Lock did not own its range/ID");
        Throws(() => Generator.Generate(Outline with { Bars = 1 }, settings, old with { Notes = [edit with { Tick = 4000 }] }));
        Throws(() => Generator.Generate(Outline with { Bars = 1 }, settings, fixedPattern with { Notes = [new("long", 3000, 1000, 60, 90)] }));
        Check(previous.Notes.Length == 1 && previous.Notes[0] == edit, "Generation mutated accepted notes");
    }

    private static void RecallAndVersions()
    {
        var settings = new GeneratorSettings(GeneratorKind.Arpeggiator) { Seed = null, PartSeed = 81, Direction = ArpeggioDirection.Random, Swing = .2, Ratchet = 2,
            SourceNotes = [new("captured", 0, 960, 65, 100), new("chord", 0, 960, 69, 100)], Grouping = [2, 2, 3] };
        var outline = Outline with { Meter = new(7, 8) };
        var pattern = Generator.Generate(outline, settings);
        var parsed = ProjectStore.Parse(Json(Project(outline, pattern))).Sections[0].Parts[0].Pattern;
        Check(Json(parsed.Recipe!.Settings) == Json(settings), "Recipe did not persist complete settings/material");
        Check(Json(Generator.Regenerate(outline with { Seed = 999 }, parsed)) == Json(pattern), "Recipe recall changed output");
        foreach (var recipe in new[] { parsed.Recipe with { Version = "pattern-999" }, parsed.Recipe with { Random = "unknown" }, parsed.Recipe with { Generator = "unknown" } })
        {
            var unknown = parsed with { Recipe = recipe };
            var loaded = ProjectStore.Parse(Json(Project(outline, unknown))).Sections[0].Parts[0].Pattern;
            Check(Json(loaded.Notes) == Json(pattern.Notes), "Unknown recipe lost accepted notes");
            Throws(() => Generator.Regenerate(outline, loaded));
        }
        Throws(() => Generator.Regenerate(outline, pattern with { Recipe = null }));
        Throws(() => Generator.Regenerate(outline, pattern with { Recipe = pattern.Recipe! with { Settings = null } }));
        var section = new SectionGenerator().Generate(Outline);
        var first = section.Parts[0];
        var unknownDrum = first with { Pattern = first.Pattern with { Recipe = first.Pattern.Recipe! with { Version = "future" } } };
        Throws(() => new SectionGenerator().Generate(Outline, section with { Parts = section.Parts.SetItem(0, unknownDrum) }));
    }

    private static void ScopedParts()
    {
        var generator = new SectionGenerator();
        var section = generator.Generate(Outline);
        var bass = section.Parts.Single(p => p.Id == "bass") with { Muted = true, Solo = true };
        section = section with { Parts = section.Parts.SetItem(1, bass) };
        var selected = new HashSet<string>(StringComparer.Ordinal) { "bass" };
        var settings = new GeneratorSettings(GeneratorKind.RandomWalk) { Seed = 7, PartSeed = 23, MinPitch = 36, MaxPitch = 55 };
        var changed = generator.Generate(Outline, section, generators: new Dictionary<string, GeneratorSettings> { ["bass"] = settings }, partIds: selected);
        Check(changed.Parts.Where(p => p.Id != "bass").SequenceEqual(section.Parts.Where(p => p.Id != "bass")), "Scope changed another part");
        Check(changed.Parts[1].Muted && changed.Parts[1].Solo && changed.Parts[1].Pattern.Recipe!.Settings!.Kind == GeneratorKind.RandomWalk, "Part metadata/settings lost");
        var again = generator.Generate(Outline, changed, partIds: selected);
        Check(Json(changed) == Json(again), "Section did not recall selected generator");
        Throws(() => generator.Generate(Outline, section, partIds: new HashSet<string> { "missing" }));
        var legacy = generator.Generate(Outline, section);
        Check(legacy.Parts[1].Muted && legacy.Parts[1].Solo, "Scaffold regeneration lost mute/solo");
        var phrase = new Part("captured", "Captured", PartRole.Phrase, "Synth", 3, new("capture", PatternKind.Fixed, [new("n", 0, 100, 61, 80)]));
        Check(generator.GeneratePart(Outline, phrase, settings) == phrase, "Fixed captured part changed");
    }

    private static void GroupedMeters()
    {
        Check(MeterGrouping.Resolve(new(7, 8)).SequenceEqual([2, 2, 3]), "7/8 default grouping");
        Check(MeterGrouping.Resolve(new(9, 8)).SequenceEqual([3, 3, 3]), "Compound grouping");
        Check(MeterGrouping.Resolve(new(7, 8), [3, 2, 2]).SequenceEqual([3, 2, 2]), "Explicit grouping ignored");
        Throws(() => MeterGrouping.Resolve(new(7, 8), [2, 2, 2]));
        var options = new DrumOptions(Bars: 2, Complexity: 1, Intensity: 1, Seed: 77, Persona: DrumPersona.Rock) { Grouping = [2, 2, 3] };
        var a = DrumPlanner.Plan(options, new(7, 8));
        var b = DrumPlanner.Plan(options with { Grouping = [3, 2, 2] }, new(7, 8));
        Check(!a.SequenceEqual(b), "Grouping did not affect drums");
        Check(a.Any(h => h.Tick == 2640), "Final 3/8 group was truncated to a quarter note");
        Check(a.Any(h => h.Tick == 3360 && h.Note == 36), "Grouped bar downbeat missing");
        Check(a.All(h => h.Tick >= 0 && h.Tick < 6720), "Grouped hit outside section");
        foreach (var persona in Enum.GetValues<DrumPersona>())
        {
            var hits = DrumPlanner.Plan(options with { Persona = persona }, new(7, 8));
            Check(hits.SequenceEqual(DrumPlanner.Plan(options with { Persona = persona }, new(7, 8))), "Grouped drummer nondeterministic");
            Check(hits.All(h => h.Tick < 6720 && h.Velocity is >= 1 and <= 127), "Grouped persona bounds");
        }
        var drummer = new DrummerSettings(DrumPersona.Rock) { Grouping = [3, 2, 2] };
        var outline = Outline with { Meter = new(7, 8) };
        var section = new SectionGenerator().Generate(outline, drummer: drummer);
        var recipe = section.Parts[0].Pattern.Recipe!;
        Check(recipe.Version == DrumPlanner.GroupedVersion && recipe.Drummer!.Grouping.SequenceEqual([3, 2, 2]), "Grouped drummer recipe missing");
        Check(Json(section) == Json(new SectionGenerator().Generate(outline, section)), "Grouped drummer recall changed");
        var old = section.Parts[0];
        var legacySettings = new DrummerSettings(DrumPersona.Rock, Complexity: 0, Fills: 0);
        old = old with { Pattern = old.Pattern with { Recipe = old.Pattern.Recipe! with { Version = DrumPlanner.Version, Drummer = legacySettings } } };
        var legacy = new SectionGenerator().Generate(outline, section with { Parts = section.Parts.SetItem(0, old) }).Parts[0].Pattern;
        Check(legacy.Recipe!.Version == DrumPlanner.Version && legacy.Notes.Any(n => n.Tick == 2880 && n.Pitch == 42), "Old odd-meter recipe silently upgraded");
        Check(legacy.Notes.All(n => n.Tick != 2640), "Old odd-meter algorithm changed during recall");
        Throws(() => DrumPlanner.Plan(options with { Grouping = [2, 2] }, new(7, 8)));
    }

    private static void VersionedOutput()
    {
        var text = new StringBuilder();
        foreach (var kind in Enum.GetValues<GeneratorKind>())
        foreach (var seed in new uint[] { 0, 42 })
        {
            var outline = Outline with { Meter = new(7, 8), Density = .8, Intensity = .65, Variation = .3, Seed = seed };
            var settings = new GeneratorSettings(kind) { PartSeed = 23, Swing = .2, Gate = .7, Probability = .85, Ratchet = 2,
                Progression = [0, 3, 4], Grouping = [2, 2, 3], Direction = ArpeggioDirection.Random,
                SourceNotes = kind == GeneratorKind.Arpeggiator ? [new("c", 0, 960, 60, 80), new("e", 0, 960, 64, 80), new("f", 960, 960, 65, 80)] : [] };
            text.Append($"{kind}/{seed}:");
            foreach (var note in Generator.Generate(outline, settings).Notes) text.Append($"{note.Tick},{note.Duration},{note.Pitch},{note.Velocity};");
            text.Append('\n');
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        Check(digest == "28BFBA564BE1D038B996EA526C50990DD040144F950DE104F5784C37D0EB19C1", $"{PatternGenerator.Version} output changed: {digest}");
    }

    private static void StrictValidation()
    {
        foreach (var settings in new[]
        {
            new GeneratorSettings((GeneratorKind)99), new() { Direction = (ArpeggioDirection)99 }, new() { Gate = double.NaN }, new() { Gate = 0 },
            new() { Swing = .46 }, new() { Probability = double.PositiveInfinity }, new() { Velocity = 0 }, new() { Ratchet = 9 }, new() { MinPitch = 80, MaxPitch = 60 },
            new() { StepTicks = 0 }, new() { Steps = 0 }, new() { StepDegrees = [] }, new() { StepDegrees = [129] }, new() { StepProbabilities = [double.NaN] },
            new() { StepRatchets = [0] }, new() { ChordDegrees = [] }, new() { EnabledSteps = default }, new() { Grouping = [0] }, new() { SourceLengthTicks = 0 },
            new(GeneratorKind.Euclidean) { Steps = 8, Pulses = 9 }, new(GeneratorKind.Euclidean) { Rotation = 16 },
            new() { SourceNotes = [new("bad", long.MaxValue, 2, 60, 90)] }, new() { SourceNotes = [new("bad", 0, 100, 60, 90)], SourceLengthTicks = 99 }
        }) Throws(() => Generator.Generate(Outline, settings));
        Throws(() => Generator.Generate(Outline, new() { MinPitch = 61, MaxPitch = 61 }));
        Throws(() => Generator.Generate(Outline, new() { Grouping = [2, 2, 3] }));
        Throws(() => Generator.Generate(Outline, new(), new("bad", PatternKind.Hybrid, [new("n", 0, 10, 60, 80), new("n", 10, 10, 60, 80)])));
        try
        {
            Generator.Generate(Outline with { Bars = 256, Meter = new(32, 1) }, new() { StepTicks = 8, Ratchet = 8, VelocitySpread = 0 });
            throw new Exception("Oversized generation silently truncated");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("100000", StringComparison.Ordinal)) { }
    }
}
