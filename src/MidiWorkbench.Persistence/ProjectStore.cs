using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Persistence;

public static class ProjectStore
{
    private const int MaxBytes = 32 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    public static SongProject Load(string path) => Parse(ReadLimited(path));
    public static SongProject Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Project is empty.");
        var version = root["schemaVersion"]?.GetValue<int>() ?? throw new InvalidDataException("Project has no schema version.");
        if (version == 1) { Migrate1To2(root); version = 2; }
        if (version == 2) { root["schemaVersion"] = SongProject.CurrentSchemaVersion; version = SongProject.CurrentSchemaVersion; }
        if (version != SongProject.CurrentSchemaVersion) throw new InvalidDataException($"Project schema {version} is not supported by this version.");
        var project = root.Deserialize<SongProject>(JsonOptions) ?? throw new InvalidDataException("Project is empty.");
        project.Validate(); return project;
    }
    // Schema 1 stored generatorVersion/generatorSeed on patterns; all were scaffold output.
    private static void Migrate1To2(JsonObject root)
    {
        foreach (var section in root["sections"]?.AsArray() ?? [])
        foreach (var part in section?["parts"]?.AsArray() ?? [])
        {
            if (part?["pattern"] is not JsonObject pattern) continue;
            var generator = pattern["generatorVersion"]?.GetValue<string>();
            var seed = pattern["generatorSeed"]?.GetValue<uint>();
            pattern.Remove("generatorVersion"); pattern.Remove("generatorSeed");
            if (generator is not null)
                pattern["recipe"] = new JsonObject { ["generator"] = "scaffold", ["version"] = generator, ["random"] = "xorshift32", ["seed"] = seed ?? 0 };
        }
        root["schemaVersion"] = 2;
    }
    public static void Save(string path, SongProject project)
    {
        project.Validate(); WriteDocument(path, JsonSerializer.Serialize(project, JsonOptions));
    }
    public static void SaveTake(string path, PerformanceTake take)
    {
        ValidateTake(take); WriteDocument(path, JsonSerializer.Serialize(take, JsonOptions));
    }
    public static PerformanceTake LoadTake(string path)
    {
        var take = JsonSerializer.Deserialize<PerformanceTake>(ReadLimited(path), JsonOptions) ?? throw new InvalidDataException("Take is empty.");
        ValidateTake(take); return take;
    }
    public static void ValidateTake(PerformanceTake take)
    {
        if (take.SchemaVersion != PerformanceTake.CurrentSchemaVersion) throw new InvalidDataException("Unsupported take schema.");
        if (!double.IsFinite(take.Bpm) || take.Bpm is < 20 or > 400 || take.Outputs.IsDefault || take.Inputs.IsDefault || take.Actions.IsDefault || take.TempoChanges.IsDefault || take.Outputs.Length + take.Inputs.Length > 1000000) throw new InvalidDataException("Invalid take.");
        take.Meter?.Validate();
        foreach (var e in take.Outputs.Concat(take.Inputs))
            if (e is null || !double.IsFinite(e.Seconds) || e.Seconds < 0 || string.IsNullOrWhiteSpace(e.Endpoint) || e.Data1 > 127 || e.Data2 > 127 || e.Status < 0x80) throw new InvalidDataException("Invalid captured MIDI.");
        foreach (var a in take.Actions) if (a is null || !double.IsFinite(a.Seconds) || a.Seconds < 0 || string.IsNullOrWhiteSpace(a.Kind)) throw new InvalidDataException("Invalid take action.");
        foreach (var t in take.TempoChanges) if (t is null || !double.IsFinite(t.Seconds) || t.Seconds < 0 || !double.IsFinite(t.Bpm) || t.Bpm is < 20 or > 400) throw new InvalidDataException("Invalid take tempo.");
    }
    public static void SaveBank(string path, SongBank bank)
    {
        bank.Validate(); WriteDocument(path, JsonSerializer.Serialize(bank, JsonOptions));
    }
    public static SongBank LoadBank(string path)
    {
        var root = JsonNode.Parse(ReadLimited(path)) as JsonObject ?? throw new InvalidDataException("Bank is empty.");
        if (root["schemaVersion"]?.GetValue<int>() != SongBank.CurrentSchemaVersion) throw new InvalidDataException("Unsupported bank schema.");
        foreach (var song in root["songs"]?.AsArray() ?? [])
        {
            var value = song?["project"] ?? throw new InvalidDataException("Bank song has no project.");
            song!["project"] = JsonSerializer.SerializeToNode(Parse(value.ToJsonString()), JsonOptions);
        }
        var bank = root.Deserialize<SongBank>(JsonOptions) ?? throw new InvalidDataException("Bank is empty.");
        bank.Validate(); return bank;
    }
    public static Rig LoadRig(string path)
    {
        var rig = JsonSerializer.Deserialize<Rig>(ReadLimited(path), JsonOptions) ?? throw new InvalidDataException("Rig is empty.");
        rig.Validate(); return rig;
    }
    public static void SaveRig(string path, Rig rig)
    {
        rig.Validate(); WriteDocument(path, JsonSerializer.Serialize(rig, JsonOptions));
    }
    private static void WriteDocument(string path, string json)
    {
        // Reject before replacing an existing file: every document we save must be readable.
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("File exceeds 32 MiB.");
        WriteAtomic(path, json);
    }
    private static string ReadLimited(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("File exceeds 32 MiB.");
        return File.ReadAllText(path);
    }
    public static void WriteAtomic(string path, string text) => WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(text));
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
