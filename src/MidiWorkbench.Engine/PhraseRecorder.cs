using System.Collections.Immutable;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Engine;

// Converts captured input to section notes away from the scheduler. Loop passes overdub into one section.
public static class PhraseRecorder
{
    public static ImmutableArray<MidiNote> ToNotes(TransportCapture capture, string idPrefix)
    {
        var map = new TempoMap(capture.Bpm, capture.TempoChanges);
        var end = capture.Actions.LastOrDefault(a => a.Kind is "stop" or "panic" or "end")?.Seconds
            ?? capture.Entries.Select(e => e.Seconds).DefaultIfEmpty(0).Max();
        return ToNotes(capture.Entries.Where(e => e.Input), map, capture.LengthTicks, capture.Loop, map.TickAt(end), idPrefix);
    }

    public static ImmutableArray<MidiNote> ToNotes(IEnumerable<CaptureEntry> inputs, TempoMap map, long lengthTicks, bool loop, double endTick, string idPrefix)
    {
        if (lengthTicks <= 0) return [];
        var open = new Dictionary<(int Channel, int Pitch), (double Tick, int Velocity)>();
        var sustained = new Dictionary<int, List<(int Pitch, double Tick, int Velocity)>>();
        var pedal = new HashSet<int>();
        var notes = new List<MidiNote>();
        void Close(int pitch, double on, int velocity, double off)
        {
            var start = (long)Math.Round(on);
            if (start < 0) return;
            if (loop) start %= lengthTicks; else if (start >= lengthTicks) return;
            var duration = Math.Clamp((long)Math.Round(off - on), 1, lengthTicks - start);
            notes.Add(new($"{idPrefix}-{notes.Count}", start, duration, pitch, velocity, true));
        }
        foreach (var e in inputs.OrderBy(e => e.Seconds))
        {
            var tick = map.TickAt(e.Seconds);
            var kind = e.Status & 0xF0; var channel = e.Status & 15;
            if (kind == 0x90 && e.Data2 > 0)
            {
                if (open.Remove((channel, e.Data1), out var previous)) Close(e.Data1, previous.Tick, previous.Velocity, tick);
                open[(channel, e.Data1)] = (tick, e.Data2);
            }
            else if (kind is 0x80 or 0x90 && open.Remove((channel, e.Data1), out var note))
            {
                if (pedal.Contains(channel))
                {
                    if (!sustained.TryGetValue(channel, out var list)) sustained[channel] = list = new();
                    list.Add((e.Data1, note.Tick, note.Velocity));
                }
                else Close(e.Data1, note.Tick, note.Velocity, tick);
            }
            else if (kind == 0xB0 && e.Data1 == 64)
            {
                if (e.Data2 >= 64) pedal.Add(channel);
                else if (pedal.Remove(channel) && sustained.Remove(channel, out var list))
                    foreach (var s in list) Close(s.Pitch, s.Tick, s.Velocity, tick);
            }
        }
        foreach (var ((_, pitch), note) in open) Close(pitch, note.Tick, note.Velocity, endTick);
        foreach (var list in sustained.Values) foreach (var s in list) Close(s.Pitch, s.Tick, s.Velocity, endTick);
        return notes.OrderBy(n => n.Tick).ThenBy(n => n.Pitch).ToImmutableArray();
    }
}
