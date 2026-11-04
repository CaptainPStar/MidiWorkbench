using System.Text;
using MidiWorkbench.Application;
using MidiWorkbench.Domain;
using MidiWorkbench.Engine;
using MidiWorkbench.Persistence;

namespace MidiWorkbench.Specs;

public static class ExportRoutingSpecs
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "midi-export-routing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { OfflineMapping(directory); LiveAndHistoricalChannels(directory); CompletedRecording(directory); }
        finally { Directory.Delete(directory, true); }
    }
    private static void OfflineMapping(string directory)
    {
        var a = new Section("a", Build.Outline(2), [Build.Part("Phrase", "Phrase", 1, new MidiNote("n", 73, 137, 72, 99, true)),
            Build.Part("Bass", "Bass", 2, new MidiNote("b", 0, 960, 36, 87)), Build.Part("Pad", "Pad", 3, new MidiNote("p", 0, 3840, 60, 80))]);
        var b = new Section("b", Build.Outline(1, new Meter(7, 8)), [Build.Part("phrase2", "Phrase", 6, new MidiNote("n2", 480, 240, 74, 90))]);
        var project = new SongProject(SongProject.CurrentSchemaVersion, "Channel case", 120, [a, b]) { Arrangement = [new("a", 2), new("b")] };
        var source = Path.Combine(directory, "project.json"); ProjectStore.Save(source, project);
        using var backend = new NoDeviceAccess(); using var session = new WorkbenchSession(new SessionOptions(backend)); session.Load(source);
        session.SetRig(Rig.Memory(WorkbenchSession.Destinations(project)) with { Routes = [new("Phrase", "memory", 4)] });
        var offline = new Rig(1, [new("absent-a", true, 75), new("absent-b", true, 100)],
            [new("Phrase", "absent-a", 4), new("Bass", "absent-b")]) { Input = "absent-keyboard" };
        var accepted = session.Project; var rig = session.Rig; var revision = session.Revision;
        var stored = Path.Combine(directory, "stored.mid"); session.ExportMidi(stored);
        Check(File.ReadAllBytes(stored).SequenceEqual(MidiFile.FromSection(a, 120)), "Default section export changed legacy bytes");
        foreach (var arrangement in new[] { false, true })
        {
            void Export(string path, ExportChannelMode mode, Rig? supplied = null)
            { if (arrangement) session.ExportArrangementMidi(path, mode, supplied); else session.ExportMidi(path, mode, supplied); }
            Export(stored, ExportChannelMode.ProjectChannels);
            var routed = Path.Combine(directory, "routed.mid"); Export(routed, ExportChannelMode.RoutingOverrides, offline);
            var before = Parse(File.ReadAllBytes(stored)); var after = Parse(File.ReadAllBytes(routed));
            Check(before.Count == after.Count, "Routing changed destination track count");
            for (var i = 0; i < before.Count; i++)
            {
                var x = before[i]; var y = after[i];
                Check(x.Name == y.Name && x.End == y.End && x.Meta.SequenceEqual(y.Meta), "Routing altered track identity, metadata or extent");
                Check(x.Events.Select(e => (e.Tick, Kind: e.Status & 0xF0, e.Data1, e.Data2))
                    .SequenceEqual(y.Events.Select(e => (e.Tick, Kind: e.Status & 0xF0, e.Data1, e.Data2))), "Routing applied delay or changed notes/ticks");
                var expected = x.Name == "Phrase" ? 4 : x.Name == "Bass" ? 2 : 3;
                Check(y.Events.All(e => (e.Status & 15) + 1 == expected), "Override or missing/null fallback channel is incorrect");
            }
            var phrase = before.Single(t => t.Name == "Phrase");
            Check(phrase.Events.Select(e => (e.Status & 15) + 1).Distinct().SequenceEqual(arrangement ? new[] { 1, 6 } : new[] { 1 }), "Stored channel policy changed saved section channels");
            var current = Path.Combine(directory, "current.mid"); Export(current, ExportChannelMode.RoutingOverrides);
            Check(File.ReadAllBytes(current).SequenceEqual(File.ReadAllBytes(routed)), "Current and equivalent supplied routing differ");
        }
        Check(ReferenceEquals(session.Project, accepted) && ReferenceEquals(session.Rig, rig) && session.Revision == revision && backend.Calls == 0,
            "Offline export changed accepted state or accessed a MIDI device");
        var invalid = Path.Combine(directory, "invalid.mid");
        try { session.ExportMidi(invalid, (ExportChannelMode)99); throw new Exception("Invalid mode accepted"); } catch (ArgumentOutOfRangeException) { }
        Check(!File.Exists(invalid), "Invalid mode published a file");
    }
    private static void LiveAndHistoricalChannels(string directory)
    {
        using var backend = new MemoryBackend(); using var session = new WorkbenchSession(new SessionOptions(backend));
        var section = new Section("live", Build.Outline(), [Build.Part("Phrase", "Phrase", 1, new MidiNote("live-note", 0, 960, 72, 100))]);
        var source = Path.Combine(directory, "live.json"); ProjectStore.Save(source, new SongProject(SongProject.CurrentSchemaVersion, "Live routing", 120, [section])); session.Load(source);
        session.SetRig(new(1, [new("memory")], [new("Phrase", "memory", 4)]));
        session.StartSection(true); Thread.Sleep(50); session.Stop();
        Check(session.Take().Outputs.Any(e => e.Destination == "Phrase" && e.Status == 0x93 && e.Data1 == 72), "Live routing fixture did not send Phrase on channel 4");
        var path = Path.Combine(directory, "live.mid"); session.ExportMidi(path, ExportChannelMode.RoutingOverrides);
        Check(Parse(File.ReadAllBytes(path)).Single(t => t.Name == "Phrase").Events.All(e => (e.Status & 15) == 3), "Export does not match visible/live channel override");
        var take = new PerformanceTake(PerformanceTake.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, 120, true,
            [new(0, "memory", "Phrase", 0x95, 60, 100), new(.5, "memory", "Phrase", 0x85, 60, 0)],
            [new(0, "keys", null, 0x90, 60, 100), new(.5, "keys", null, 0x80, 60, 0)], [new(1, "stop")], []);
        session.SaveTake(path, take); var parsed = Parse(File.ReadAllBytes(path));
        Check(parsed.SelectMany(t => t.Events).Select(e => (e.Status & 15) + 1).Distinct().Order().SequenceEqual([1, 6])
            && parsed.All(t => t.End == 1920), "Take channels or historical timing were remapped by current routing");
    }
    private static void CompletedRecording(string directory)
    {
        foreach (var arrangement in new[] { false, true })
        {
            using var backend = new MemoryBackend(); using var session = new WorkbenchSession(new SessionOptions(backend));
            var source = Path.Combine(directory, "record.json");
            ProjectStore.Save(source, new SongProject(SongProject.CurrentSchemaVersion, "Completed capture", 400,
                [new("short", Build.Outline(1, new Meter(1, 4)), [Build.Part("p", "Phrase", 1)])])); session.Load(source);
            session.SetRig(Rig.Memory(["Phrase"]) with { Input = "loopback" }); session.Arm(WorkbenchSession.NewPhrase); session.StartSection();
            using var keys = backend.OpenOutput("loopback"); Thread.Sleep(20); keys.Submit(new(0, 0x90, 77, 100)); Thread.Sleep(25); keys.Submit(new(0, 0x80, 77));
            Check(SpinWait.SpinUntil(() => !session.IsRunning, TimeSpan.FromSeconds(3)), "Recording did not finish naturally");
            // No Status, Stop or Save call may commit this phrase before the export under test.
            Check(!session.Project.Sections[0].Parts.Any(p => p.Pattern.Notes.Any(n => n.Pitch == 77)), "Capture fixture already committed its phrase");
            var path = Path.Combine(directory, "completed.mid");
            if (arrangement) session.ExportArrangementMidi(path); else session.ExportMidi(path);
            Check(Parse(File.ReadAllBytes(path)).SelectMany(t => t.Events).Any(e => (e.Status & 0xF0) == 0x90 && e.Data1 == 77), "Export omitted a naturally completed pending recording");
        }
    }
    private sealed class NoDeviceAccess : IMidiBackend
    {
        public int Calls;
        public event Action? EndpointsChanged { add { } remove { } }
        public IReadOnlyList<MidiEndpoint> Endpoints() { Calls++; throw new Exception("Unexpected device enumeration"); }
        public IMidiInputPort OpenInput(string id, Action<TimedMidi> callback) { Calls++; throw new Exception("Unexpected input open"); }
        public IMidiOutputPort OpenOutput(string id) { Calls++; throw new Exception("Unexpected output open"); }
        public void Dispose() { }
    }
    private sealed record Track(string Name, long End, List<SmfEvent> Events, List<string> Meta);
    // Parse binary chunks and variable-length deltas independently of the production writer.
    private static List<Track> Parse(byte[] bytes)
    {
        var p = 0;
        long Number(int n) { long v = 0; while (n-- > 0) v = v << 8 | bytes[p++]; return v; }
        string Tag() { var value = Encoding.ASCII.GetString(bytes, p, 4); p += 4; return value; }
        long Variable() { long value = 0; for (var i = 0; i < 4; i++) { var b = bytes[p++]; value = value << 7 | (uint)(b & 127); if (b < 128) return value; } throw new Exception("Bad VLQ"); }
        Check(Tag() == "MThd" && Number(4) == 6 && Number(2) == 1, "Bad SMF header"); var count = Number(2); Check(Number(2) == 960, "PPQ changed");
        var result = new List<Track>();
        for (var i = 0; i < count; i++)
        {
            Check(Tag() == "MTrk", "Missing track"); var size = Number(4); var limit = p + size; long tick = 0; long? eot = null; var name = "";
            var events = new List<SmfEvent>(); var meta = new List<string>();
            while (p < limit)
            {
                tick += Variable(); var status = bytes[p++];
                if (status == 0xFF)
                {
                    var kind = bytes[p++]; var length = checked((int)Variable()); var data = bytes.AsSpan(p, length);
                    meta.Add($"{tick}:{kind}:{Convert.ToHexString(data)}"); if (kind == 3) name = Encoding.UTF8.GetString(data);
                    p += length; if (kind == 0x2F) { Check(length == 0 && p == limit, "Bad EOT"); eot = tick; }
                }
                else
                {
                    Check(status is >= 0x80 and < 0xF0, "Bad channel status"); var one = bytes[p++]; var two = (status & 0xF0) is 0xC0 or 0xD0 ? (byte)0 : bytes[p++]; events.Add(new(tick, status, one, two));
                }
            }
            Check(p == limit && eot is not null, "Incomplete track"); result.Add(new(name, eot!.Value, events, meta));
        }
        Check(p == bytes.Length, "Unparsed bytes"); return result;
    }
}



