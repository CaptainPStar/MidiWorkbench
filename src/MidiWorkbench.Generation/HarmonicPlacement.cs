namespace MidiWorkbench.Generation;

// Octave placement changes register, never a requested harmonic pitch class.
internal static class HarmonicPlacement
{
    private static ArgumentException RangeError() => new("Widen the pitch range to fit every requested harmonic pitch class and octave voice.");
    private static int Mod(int pitch) => (pitch % 12 + 12) % 12;

    public static int Single(int pitch, int minimum, int maximum)
    {
        var best = -1; var distance = int.MaxValue;
        for (var candidate = minimum; candidate <= maximum; candidate++)
            if (Mod(candidate) == Mod(pitch) && Math.Abs(candidate - pitch) < distance)
            { best = candidate; distance = Math.Abs(candidate - pitch); }
        return best < 0 ? throw RangeError() : best;
    }

    public static int[] Voices(int[] desired, int minimum, int maximum, int[]? previous = null)
    {
        if (desired.Length == 0 || desired.Length > maximum - minimum + 1) throw RangeError();
        // Keep the original inversion/register relationship when a common octave shift fits.
        var lowestShift = (int)Math.Ceiling((minimum - desired.Min()) / 12d);
        var highestShift = (int)Math.Floor((maximum - desired.Max()) / 12d);
        var shift = lowestShift <= highestShift ? Math.Clamp(0, lowestShift, highestShift)
            : (int)Math.Round(((minimum + maximum) / 2d - (desired.Min() + desired.Max()) / 2d) / 12d, MidpointRounding.AwayFromZero);
        var targets = desired.Select((pitch, index) => previous is { Length: > 0 }
            ? previous[Math.Min(index, previous.Length - 1)] : pitch + shift * 12).ToArray();
        var result = new int[desired.Length];
        foreach (var group in Enumerable.Range(0, desired.Length).GroupBy(i => Mod(desired[i])))
        {
            var voices = group.OrderBy(i => targets[i]).ThenBy(i => i).ToArray();
            var candidates = Enumerable.Range(minimum, maximum - minimum + 1).Where(p => Mod(p) == group.Key).ToArray();
            if (voices.Length > candidates.Length) throw RangeError();
            // Same-class voices share octave slots. Dynamic programming finds the
            // minimum movement assignment without two voices acquiring one pitch.
            const int unreachable = int.MaxValue / 4;
            var costs = new int[voices.Length + 1, candidates.Length + 1];
            var take = new bool[voices.Length + 1, candidates.Length + 1];
            for (var voice = 1; voice <= voices.Length; voice++)
            {
                costs[voice, 0] = unreachable;
                for (var slot = 1; slot <= candidates.Length; slot++)
                {
                    var use = costs[voice - 1, slot - 1] + Math.Abs(candidates[slot - 1] - targets[voices[voice - 1]]);
                    var skip = costs[voice, slot - 1];
                    take[voice, slot] = use < skip;
                    costs[voice, slot] = Math.Min(use, skip);
                }
            }
            var remaining = voices.Length;
            for (var slot = candidates.Length; remaining > 0 && slot > 0; slot--)
                if (take[remaining, slot]) result[voices[--remaining]] = candidates[slot - 1];
            if (remaining != 0) throw RangeError();
        }
        return result;
    }
}
