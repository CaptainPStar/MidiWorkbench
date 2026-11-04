using MidiWorkbench.Domain;

namespace MidiWorkbench.Generation;

public sealed record DrumOptions(int Bars = 4, double Complexity = .3, double Intensity = .6, double Swing = 0, double FillAmount = 0,
    bool HalfTime = false, int Seed = 1, double VariationAmount = .35, DrumPersona Persona = DrumPersona.Simple)
{
    public void Validate()
    {
        if (Bars is < 1 or > 256 || !Enum.IsDefined(Persona) ||
            !double.IsFinite(Complexity) || Complexity is < 0 or > 1 || !double.IsFinite(Intensity) || Intensity is < 0 or > 1 ||
            !double.IsFinite(Swing) || Swing is < 0 or > .45 || !double.IsFinite(FillAmount) || FillAmount is < 0 or > 1 ||
            !double.IsFinite(VariationAmount) || VariationAmount is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(DrumOptions));
    }
}
public readonly record struct DrumHit(long Tick, int Note, int Velocity);
public sealed record DrumPersonaPreset(DrumPersona Id, string Name, double Complexity, double Intensity, double Fill, double Variation, bool HalfTime, double Swing);

public static class DrumPersonas
{
    public static IReadOnlyList<DrumPersonaPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new DrumPersonaPreset(DrumPersona.Simple, "Simple", .3, .6, .3, .2, false, 0),
        new DrumPersonaPreset(DrumPersona.Rock, "Rock", .5, .75, .45, .35, false, 0),
        new DrumPersonaPreset(DrumPersona.Alternative, "Alternative", .6, .65, .45, .6, false, 0),
        new DrumPersonaPreset(DrumPersona.Doom, "Doom", .25, .85, .3, .2, true, 0),
        new DrumPersonaPreset(DrumPersona.Sludge, "Sludge", .5, .9, .5, .4, true, 0),
        new DrumPersonaPreset(DrumPersona.BlackMetal, "Black Metal", .8, .85, .45, .45, false, 0),
        new DrumPersonaPreset(DrumPersona.DeathMetal, "Death Metal", .8, .85, .65, .55, false, 0),
        new DrumPersonaPreset(DrumPersona.Pop, "Pop", .35, .6, .3, .3, false, 0),
        new DrumPersonaPreset(DrumPersona.Jazz, "Jazz", .55, .45, .4, .6, false, .33)
    });
    public static DrumPersonaPreset Get(DrumPersona persona) => All.Single(p => p.Id == persona);
    public static DrumOptions Apply(DrumOptions current, DrumPersona persona)
    {
        var p = Get(persona);
        return current with { Persona = p.Id, Complexity = p.Complexity, Intensity = p.Intensity, FillAmount = p.Fill,
            VariationAmount = p.Variation, HalfTime = p.HalfTime, Swing = p.Swing };
    }
    public static DrummerSettings Settings(DrumPersona persona)
    {
        var p = Get(persona);
        return new(p.Id, p.Complexity, p.Fill, p.Swing, p.HalfTime);
    }
}

// General MIDI drum patterns. 4/4, 3/4 and 6/8 reproduce the original planner exactly; other meters
// run whole quarter-note beats and drop hits past the bar end.
public static class DrumPlanner
{
    public const string Version = "drummer-1";
    public const int HitTicks = 120;

    public static List<DrumHit> Plan(DrumOptions options, Meter meter, int variant = 0, int dynamics = 1)
    {
        options.Validate(); meter.Validate();
        if (variant is < 0 or > 7 || dynamics is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(variant));
        return options.Persona == DrumPersona.Simple ? Simple(options, meter, variant, dynamics) : Persona(options, meter, variant, dynamics);
    }

    private static int Velocity(DrumOptions o, double accent, int dynamics, double random) =>
        Math.Clamp((int)Math.Round((35 + o.Intensity * 80) * accent * (dynamics == 0 ? .72 : dynamics == 2 ? 1.08 : 1) + (random - .5) * 8), 1, 127);

    private static long Tick(double quarters) => (long)Math.Round(quarters * MusicalTime.Ppq);

    private static List<DrumHit> Simple(DrumOptions options, Meter meter, int variation, int dynamics)
    {
        var quarters = meter.BarTicks / (double)MusicalTime.Ppq;
        var four = meter is { Numerator: 4, Denominator: 4 };
        var three = meter is { Numerator: 3, Denominator: 4 };
        var six = meter is { Numerator: 6, Denominator: 8 };
        var steps = four ? 8 : three || six ? 6 : (int)Math.Ceiling(meter.BarTicks / (MusicalTime.Ppq / 2d));
        var result = new List<DrumHit>(); uint random = unchecked((uint)options.Seed + 1 + (uint)variation * 0x9e3779b9u);
        if (random == 0) random = 0x6d2b79f5;
        double Next() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return random / (double)uint.MaxValue; }
        void Hit(int bar, double step, int note, double accent)
        {
            var local = step * .5 + ((int)step % 2 == 1 ? options.Swing * .5 : 0);
            if (local >= quarters) return;
            result.Add(new(Tick(bar * quarters + local), note, Velocity(options, accent, dynamics, Next())));
        }
        for (var bar = 0; bar < options.Bars; bar++)
        {
            var fillChance = Math.Min(1, options.FillAmount * (dynamics == 2 ? 1.3 : 1));
            var fill = dynamics != 0 && bar == options.Bars - 1 && fillChance > 0 && Next() < fillChance;
            var roll = fill && fillChance >= .45;
            var varied = dynamics != 0 && Next() < options.VariationAmount;
            for (var step = 0; step < steps; step++)
            {
                if ((!roll || step < steps - 2) && (step % 2 == 0 || options.Complexity >= .2))
                    Hit(bar, step, varied && step == steps - 1 ? 46 : 42, step % 2 == 0 ? .8 : .6);
                var snare = four ? options.HalfTime ? step == 4 : step is 2 or 6
                    : three ? step == 2
                    : six ? step == 3
                    : options.HalfTime ? step == steps / 4 * 2 : step % 4 == 2;
                if (snare) Hit(bar, step, 38, 1);
                var extraKick = four ? step == 4 && !options.HalfTime : !three && !six && !options.HalfTime && step > 0 && step % 4 == 0;
                if (step == 0 || extraKick ||
                    (dynamics != 0 && step % 2 == 1 && options.Complexity > .5 && Next() < (options.Complexity - .5) * .5)) Hit(bar, step, 36, 1);
                if (fill && step >= steps - 2)
                {
                    Hit(bar, step, step % 2 == 0 ? 45 : 47, .9);
                    if (roll) Hit(bar, step + .5, step % 2 == 0 ? 38 : 43, .65 + step / (double)steps * .25);
                }
                else if (varied && options.Complexity > .45 && step % 2 == 1 && Next() < options.VariationAmount * .35)
                    Hit(bar, step + .5, 38, .38); // quiet ghost note, not a displaced backbeat
            }
        }
        return result.OrderBy(e => e.Tick).ThenBy(e => e.Note).ToList();
    }

    private static List<DrumHit> Persona(DrumOptions o, Meter meter, int variant, int dynamics)
    {
        var quarters = meter.BarTicks / (double)MusicalTime.Ppq;
        var beats = (int)Math.Ceiling(quarters - 1e-9);
        var hits = new List<DrumHit>();
        var keys = new HashSet<(int Note, long Beat)>();
        uint random = unchecked((uint)o.Seed + 1 + (uint)variant * 0x9e3779b9u); if (random == 0) random = 1;
        double Next() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return random / (double)uint.MaxValue; }
        void Hit(int bar, double beat, int note, double accent = 1)
        {
            if (o.Persona != DrumPersona.Jazz && (int)Math.Floor(beat * 2) % 2 == 1) beat += o.Swing * .5;
            if (beat < 0 || beat >= quarters) return;
            var position = bar * quarters + beat;
            var velocity = Velocity(o, accent, dynamics, Next());
            // No same-key overlapping hits, including a fill that meets an authored snare.
            if (keys.Add((note, (long)Math.Round(position * 1_000_000)))) hits.Add(new(Tick(position), note, velocity));
        }
        for (var bar = 0; bar < o.Bars; bar++)
        {
            var fill = dynamics != 0 && bar == o.Bars - 1 && Next() < Math.Min(1, o.FillAmount * (dynamics == 2 ? 1.3 : 1));
            var varied = dynamics != 0 && Next() < o.VariationAmount;
            var restrained = dynamics == 0;
            for (var beat = 0; beat < beats; beat++)
            {
                var backbeat = o.HalfTime ? beat == beats / 2 : beat % 2 == 1;
                var filling = fill && beat == beats - 1;
                switch (o.Persona)
                {
                    case DrumPersona.Rock:
                        if (!filling) { Hit(bar, beat, 42, .8); Hit(bar, beat + .5, varied && beat == beats - 1 ? 46 : 42, .6); }
                        if (backbeat) Hit(bar, beat, 38);
                        if (beat % 2 == 0) Hit(bar, beat, 36);
                        if (varied && beat == 0 && o.Complexity > .4) Hit(bar, beat + .75, 36, .8);
                        break;
                    case DrumPersona.Alternative:
                        if (!filling) { Hit(bar, beat, varied ? 51 : 42, .7); Hit(bar, beat + .5, 42, .5); }
                        if (backbeat) Hit(bar, beat, 38);
                        if (beat == 0) Hit(bar, beat, 36);
                        if (beat == 1 || beat == beats - 1) Hit(bar, beat + .5, 36, .9);
                        if (varied && !filling && beat == 2) Hit(bar, beat + .75, 38, .35);
                        break;
                    case DrumPersona.Doom:
                        if (!filling) Hit(bar, beat, beat == 0 && bar == 0 ? 49 : 51, .85);
                        if (backbeat) Hit(bar, beat, 38, 1.1);
                        if (beat == 0) Hit(bar, beat, 36, 1.1);
                        if (varied && beat == beats - 1) Hit(bar, beat + .5, 36, .9);
                        break;
                    case DrumPersona.Sludge:
                        if (!filling) Hit(bar, beat, restrained ? 42 : 46, .75);
                        if (backbeat) Hit(bar, beat, 38, 1.1);
                        if (beat == 0 || beat == beats - 1) Hit(bar, beat, 36);
                        if (!restrained && beat == 0) Hit(bar, beat + .75, 36, .85);
                        if (varied && beat == 1) Hit(bar, beat + .5, 43, .75);
                        break;
                    case DrumPersona.BlackMetal:
                        if (!filling) { Hit(bar, beat, 51, .75); if (!restrained) Hit(bar, beat + .5, 51, .65); }
                        Hit(bar, beat, 36);
                        if (restrained) { if (backbeat) Hit(bar, beat, 38); }
                        else
                        {
                            Hit(bar, beat + .5, 38, .9);
                            if (o.Complexity > .65) { Hit(bar, beat + .25, 36, .8); Hit(bar, beat + .75, 38, .8); }
                        }
                        break;
                    case DrumPersona.DeathMetal:
                        if (!filling) { Hit(bar, beat, 42, .75); if (!restrained) Hit(bar, beat + .5, 42, .6); }
                        if (backbeat) Hit(bar, beat, 38);
                        if (beat % 2 == 0)
                        {
                            Hit(bar, beat, 36);
                            if (!restrained) { Hit(bar, beat + .25, 36, .85); Hit(bar, beat + .5, 36, .9); }
                            if (varied && o.Complexity > .7) Hit(bar, beat + .75, 36, .8);
                        }
                        break;
                    case DrumPersona.Pop:
                        if (!filling) { Hit(bar, beat, 42, .65); Hit(bar, beat + .5, 42, .5); }
                        if (backbeat) Hit(bar, beat, varied ? 39 : 38, .9);
                        if (beat % 2 == 0) Hit(bar, beat, 36, .9);
                        if (!restrained && beat == 1) Hit(bar, beat + .75, 36, .7);
                        break;
                    case DrumPersona.Jazz:
                        if (!filling)
                        {
                            Hit(bar, beat, 51, .65);
                            if (beat % 2 == 1 && !restrained) Hit(bar, beat + .5 + o.Swing * .5, 51, .5);
                        }
                        if (beat % 2 == 1) Hit(bar, beat, 44, .5);
                        if (beat % 2 == 0) Hit(bar, beat, 36, .35);
                        if (varied && beat == 1) Hit(bar, beat + .5 + o.Swing * .5, 38, .4);
                        break;
                }
                if (filling)
                {
                    if (o.Persona == DrumPersona.Doom) { Hit(bar, beat, 43, .9); Hit(bar, beat + .5, 45, .8); }
                    else
                    {
                        var roll = o.FillAmount >= .45 && o.Persona != DrumPersona.Jazz;
                        Hit(bar, beat, o.Persona == DrumPersona.Jazz ? 38 : 45, .75);
                        Hit(bar, beat + .5, 47, .8);
                        if (roll) { Hit(bar, beat + .25, 38, .65); Hit(bar, beat + .75, 43, .9); }
                    }
                }
            }
        }
        return hits.OrderBy(h => h.Tick).ThenBy(h => h.Note).ToList();
    }
}
