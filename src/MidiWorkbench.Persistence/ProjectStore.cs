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
        if (version == 1) Migrate1To2(root);
        else if (version != SongProject.CurrentSchemaVersion) throw new InvalidDataException($"Project schema {version} is not supported by this version.");
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
        root["schemaVersion"] = SongProject.CurrentSchemaVersion;
    }
    public static void Save(string path, SongProject project)
    {
        project.Validate(); WriteAtomic(path, JsonSerializer.Serialize(project, JsonOptions));
    }
    public static void SaveTake(string path, PerformanceTake take) => WriteAtomic(path, JsonSerializer.Serialize(take, JsonOptions));
    public static Rig LoadRig(string path)
    {
        var rig = JsonSerializer.Deserialize<Rig>(ReadLimited(path), JsonOptions) ?? throw new InvalidDataException("Rig is empty.");
        rig.Validate(); return rig;
    }
    public static void SaveRig(string path, Rig rig)
    {
        rig.Validate(); WriteAtomic(path, JsonSerializer.Serialize(rig, JsonOptions));
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
