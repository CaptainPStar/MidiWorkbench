using MidiWorkbench.Domain;

namespace MidiWorkbench.Persistence;

public sealed record SmfEvent(long Tick, byte Status, byte Data1, byte Data2);
public sealed record SmfTrack(string Name, IReadOnlyList<SmfEvent> Events);

// Standard MIDI File type 1 at the project resolution. Realtime and system messages are not written.
public static class MidiFile
{
    public static byte[] Build(Meter meter, IEnumerable<(long Tick, double Bpm)> tempo, IReadOnlyList<SmfTrack> tracks)
        => Build([(0L, meter)], tempo, tracks);

    public static byte[] Build(IEnumerable<(long Tick, Meter Meter)> meters, IEnumerable<(long Tick, double Bpm)> tempo, IReadOnlyList<SmfTrack> tracks)
    {
        if (tracks.Count > 1000) throw new ArgumentException("Too many tracks.");
        using var file = new MemoryStream();
        file.Write("MThd"u8); BigEndian(file, 6, 4); BigEndian(file, 1, 2); BigEndian(file, tracks.Count + 1, 2); BigEndian(file, MusicalTime.Ppq, 2);
        var conductor = new List<(long Tick, byte[] Bytes)>();
        foreach (var (tick, meter) in meters)
        {
            meter.Validate();
            conductor.Add((Math.Max(0, tick), [0xFF, 0x58, 4, (byte)meter.Numerator, (byte)Math.Log2(meter.Denominator), 24, 8]));
        }
        foreach (var (tick, bpm) in tempo)
        {
            var micros = (int)Math.Round(60_000_000 / bpm);
            conductor.Add((Math.Max(0, tick), [0xFF, 0x51, 3, (byte)(micros >> 16), (byte)(micros >> 8), (byte)micros]));
        }
        Track(file, conductor);
        foreach (var track in tracks)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(track.Name);
            var events = new List<(long, byte[])> { (0, [0xFF, 0x03, .. VariableLength(name.Length), .. name]) };
            foreach (var e in track.Events.Where(e => e.Status is >= 0x80 and < 0xF0))
                events.Add((Math.Max(0, e.Tick), (e.Status & 0xF0) is 0xC0 or 0xD0 ? [e.Status, e.Data1] : [e.Status, e.Data1, e.Data2]));
            Track(file, events);
        }
        return file.ToArray();
    }

    public static byte[] FromSection(Section section, double bpm)
    {
        var tracks = section.Parts.Select(p => new SmfTrack(p.Name, p.Pattern.Notes
            .SelectMany(n => new[] { new SmfEvent(n.Tick, (byte)(0x90 | p.Channel - 1), (byte)n.Pitch, (byte)n.Velocity),
                new SmfEvent(n.EndTick, (byte)(0x80 | p.Channel - 1), (byte)n.Pitch, 0) })
            .OrderBy(e => e.Tick).ThenBy(e => e.Status & 0xF0).ToArray())).ToArray();
        return Build(section.Outline.Meter, [(0, bpm)], tracks);
    }

    // Converts submission times to ticks through the recorded tempo map; the file does not claim sound onset.
    public static byte[] FromTake(PerformanceTake take, Meter meter, Func<double, double> tickAt, IEnumerable<(long Tick, double Bpm)> tempo)
    {
        SmfEvent Convert(CapturedMidi m) => new((long)Math.Round(tickAt(m.Seconds)), m.Status, m.Data1, m.Data2);
        var tracks = take.Outputs.Where(m => m.Status < 0xF0).GroupBy(m => m.Destination ?? m.Endpoint)
            .Select(g => new SmfTrack(g.Key, g.Select(Convert).ToArray())).ToList();
        if (take.Inputs.Length > 0) tracks.Add(new("Input", take.Inputs.Select(Convert).ToArray()));
        return Build(meter, tempo, tracks);
    }

    private static void Track(Stream file, List<(long Tick, byte[] Bytes)> events)
    {
        using var body = new MemoryStream();
        long last = 0;
        foreach (var (tick, bytes) in events.OrderBy(e => e.Tick))
        {
            body.Write(VariableLength(tick - last)); body.Write(bytes); last = tick;
        }
        body.Write([0, 0xFF, 0x2F, 0]);
        file.Write("MTrk"u8); BigEndian(file, body.Length, 4); body.Position = 0; body.CopyTo(file);
    }
    private static byte[] VariableLength(long value)
    {
        if (value is < 0 or > 0x0FFFFFFF) throw new ArgumentOutOfRangeException(nameof(value), "MIDI file delta exceeds the format limit.");
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        while ((value >>= 7) > 0) bytes.Insert(0, (byte)(value & 0x7F | 0x80));
        return bytes.ToArray();
    }
    private static void BigEndian(Stream s, long value, int count)
    {
        for (var i = count - 1; i >= 0; i--) s.WriteByte((byte)(value >> (8 * i)));
    }
}
