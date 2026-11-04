using System.Collections.Immutable;
using System.Text;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class MidiExtentSpecs
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        SectionSilence();
        ArrangementRepeatsAndMeters();
        RecordedTakeExtent();
        ExplicitBuilderExtentAndLimits();
    }
    private static Section Section(string id, Meter? meter = null, bool empty = false) => new(id,
        Build.Outline(2, meter) with { Name = id }, [Build.Part(id + "-part", id, 1,
            empty ? [] : [new MidiNote(id + "-note", 0, (meter ?? new()).BarTicks, 60, 100)])]);
    private static void SectionSilence()
    {
        foreach (var empty in new[] { false, true })
        foreach (var meter in new[] { new Meter(4, 4), new Meter(7, 8), new Meter(3, 4) })
        {
            var section = Section("Sparse", meter, empty);
            var parsed = Parse(MidiFile.FromSection(section, 120));
            Check(parsed.Tracks.Count == 2 && parsed.Tracks.All(t => t.End == section.Outline.LengthTicks), "Section conductor/part EOT omitted declared silent time");
            Check(parsed.Tracks.SelectMany(t => t.Events).Count() == (empty ? 0 : 2), "Silence was padded with invented channel messages");
            if (!empty) Check(parsed.Tracks[1].Events.Max(e => e.Tick) == meter.BarTicks, "Export moved the last note to fill silence");
        }
    }
    private static void ArrangementRepeatsAndMeters()
    {
        var directory = Path.Combine(Path.GetTempPath(), "midi-extent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var a = Section("A"); var b = Section("B");
            var project = new SongProject(SongProject.CurrentSchemaVersion, "Silent final bar", 120, [a, b]) { Arrangement = [new("A", 2), new("B")] };
            var parsed = Export(project, directory, "reported-case");
            Check(parsed.Tracks.Count == 3 && parsed.Tracks.All(t => t.End == 23040), "A×2+B arrangement lost its final silent bar");
            Check(parsed.Tracks.SelectMany(t => t.Events).Max(e => e.Tick) == 19200, "Reported sparse song no longer has its original last note");
            Check(parsed.Tracks.SelectMany(t => t.Events).Count() == 6, "Arrangement added notes/controllers to retain duration");
            Check(Duration(parsed) == 12, "Parsed SMF tempo/EOT duration is not the intended 12 seconds");
            var shortMeter = Section("B", new Meter(3, 4));
            parsed = Export(project with { Sections = [a, shortMeter] }, directory, "mixed");
            Check(parsed.Tracks.All(t => t.End == 21120) && parsed.Meters.SequenceEqual([(0L, new Meter()), (15360L, new Meter(3, 4))]), "Mixed-meter repeats lost their change positions or silent extent");
            parsed = Export(project with { Sections = [Section("A", empty: true), Section("B", new Meter(7, 8), true)] }, directory, "empty");
            Check(parsed.Tracks.Count == 1 && parsed.Tracks[0].End == 22080 && parsed.Tracks[0].Events.Count == 0, "Wholly silent arrangement collapsed or fabricated music");
            parsed = Export(project with { Sections = [a, shortMeter], Arrangement = [] }, directory, "default-order");
            Check(parsed.Tracks.All(t => t.End == 13440) && parsed.Meters.SequenceEqual([(0L, new Meter()), (7680L, new Meter(3, 4))]), "Default section order lost its declared extent");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static Parsed Export(SongProject project, string directory, string name)
    {
        var source = Path.Combine(directory, name + ".json"); var target = Path.Combine(directory, name + ".mid");
        ProjectStore.Save(source, project);
        using var session = new WorkbenchSession(); session.Load(source); session.ExportArrangementMidi(target);
        return Parse(File.ReadAllBytes(target));
    }
    private static void RecordedTakeExtent()
    {
        var take = new PerformanceTake(PerformanceTake.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, 120, true,
            [new(0, "memory", "Piano", 0x90, 60, 100), new(.5, "memory", "Piano", 0x80, 60, 0)], [],
            [new(0, "start"), new(2, "stop"), new(99, "arm")], [new(0, 120), new(1, 60)]);
        var map = new TempoMap(take.Bpm, take.TempoChanges);
        var parsed = Parse(MidiFile.FromTake(take, new(), map.TickAt, map.Changes));
        Check(parsed.Tracks.All(t => t.End == 2880), "Take stop/silence did not use its recorded tempo map, or idle arm extended it");
        Check(parsed.Tracks.SelectMany(t => t.Events).Max(e => e.Tick) == 960, "Take note timing changed while preserving its silent tail");
        var unknownDuration = take with { Actions = [], TempoChanges = [] };
        var steady = new TempoMap(120, []);
        parsed = Parse(MidiFile.FromTake(unknownDuration, new(), steady.TickAt, steady.Changes));
        Check(parsed.Tracks.All(t => t.End == 960), "Export fabricated unrecorded duration after the last observed take event");
        var clockOnly = take with { Outputs = [new(3, "clock", null, 0xFC, 0, 0)], Actions = [], TempoChanges = [] };
        parsed = Parse(MidiFile.FromTake(clockOnly, new(), steady.TickAt, steady.Changes));
        Check(parsed.Tracks.Count == 1 && parsed.Tracks[0].End == 5760 && parsed.Tracks[0].Events.Count == 0, "Observed realtime stop extent vanished when SMF omitted realtime messages");
        var empty = clockOnly with { Outputs = [], Actions = [new(4, "end")] };
        parsed = Parse(MidiFile.FromTake(empty, new(), steady.TickAt, steady.Changes));
        Check(parsed.Tracks.Count == 1 && parsed.Tracks[0].End == 7680, "Explicit silent take end action was ignored");
        parsed = Parse(MidiFile.FromTake(empty with { Actions = [] }, new(), steady.TickAt, steady.Changes));
        Check(parsed.Tracks[0].End == 0, "Empty take without duration gained an invented section length");
    }
    private static void ExplicitBuilderExtentAndLimits()
    {
        var parsed = Parse(MidiFile.Build(new Meter(), [(0, 120)], [new("Late", [new(1920, 0xC0, 3, 0)])], 960));
        Check(parsed.Tracks[1].Events.Single().Tick == 1920 && parsed.Tracks[1].End == 1920, "Supplied extent truncated a later event");
        const long maximumDelta = 0x0FFFFFFF;
        parsed = Parse(MidiFile.Build(new Meter(), [(0, 120)], [], maximumDelta));
        Check(parsed.Tracks[0].End == maximumDelta, "Maximum legal silent EOT delta failed");
        try { MidiFile.Build(new Meter(), [(0, 120)], [], maximumDelta + 1); throw new Exception("Oversized silent delta accepted"); }
        catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("format limit", StringComparison.Ordinal)) { }
        try { MidiFile.Build(new Meter(), [(0, 120)], [], -1); throw new Exception("Negative extent accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    private sealed record Parsed(IReadOnlyList<ParsedTrack> Tracks, IReadOnlyList<(long Tick, Meter Meter)> Meters,
        IReadOnlyList<(long Tick, int Micros)> Tempos);
    private sealed record ParsedTrack(long End, IReadOnlyList<SmfEvent> Events);
    private static double Duration(Parsed file)
    {
        var end = file.Tracks.Max(t => t.End); long tick = 0; var micros = 500000; var seconds = 0d;
        foreach (var change in file.Tempos.OrderBy(t => t.Tick))
        {
            if (change.Tick > end) break;
            seconds += (change.Tick - tick) * micros / (960d * 1_000_000); tick = change.Tick; micros = change.Micros;
        }
        return seconds + (end - tick) * micros / (960d * 1_000_000);
    }
    // Reads binary chunks, deltas and EOT independently of the production serializer.
    private static Parsed Parse(byte[] bytes)
    {
        var position = 0;
        long Number(int count) { long value = 0; for (var i = 0; i < count; i++) value = (value << 8) | bytes[position++]; return value; }
        string Tag() { var tag = Encoding.ASCII.GetString(bytes, position, 4); position += 4; return tag; }
        long Variable()
        {
            long value = 0;
            for (var i = 0; i < 4; i++) { var part = bytes[position++]; value = (value << 7) | (uint)(part & 127); if (part < 128) return value; }
            throw new Exception("Malformed overlong SMF variable-length integer");
        }
        Check(Tag() == "MThd" && Number(4) == 6 && Number(2) == 1, "Invalid type-1 SMF header");
        var trackCount = Number(2); Check(Number(2) == 960, "Unexpected MIDI resolution");
        var tracks = new List<ParsedTrack>(); var meters = new List<(long, Meter)>(); var tempos = new List<(long, int)>();
        for (var track = 0; track < trackCount; track++)
        {
            Check(Tag() == "MTrk", "Missing track chunk");
            var length = Number(4); var end = checked(position + (int)length); long tick = 0; long? endTick = null;
            var events = new List<SmfEvent>();
            while (position < end)
            {
                tick += Variable(); var status = bytes[position++];
                if (status == 0xFF)
                {
                    var type = bytes[position++]; var size = checked((int)Variable());
                    if (type == 0x2F) { Check(size == 0 && position == end, "EOT is not the final zero-length meta event"); endTick = tick; }
                    if (type == 0x58) { Check(size == 4, "Bad time signature"); meters.Add((tick, new(bytes[position], 1 << bytes[position + 1]))); }
                    if (type == 0x51)
                    {
                        Check(size == 3, "Bad tempo meta event");
                        tempos.Add((tick, bytes[position] << 16 | bytes[position + 1] << 8 | bytes[position + 2]));
                    }
                    position += size;
                }
                else
                {
                    Check(status is >= 0x80 and < 0xF0, "Unexpected non-channel event");
                    var one = bytes[position++]; var two = (status & 0xF0) is 0xC0 or 0xD0 ? (byte)0 : bytes[position++];
                    Check(one < 128 && two < 128, "Invalid channel data"); events.Add(new(tick, status, one, two));
                }
            }
            Check(position == end && endTick is not null, "Malformed track extent or absent EOT");
            tracks.Add(new(endTick!.Value, events));
        }
        Check(position == bytes.Length, "Unparsed SMF bytes remain"); return new(tracks, meters, tempos);
    }
}
