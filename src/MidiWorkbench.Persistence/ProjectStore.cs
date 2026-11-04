using System.Text.Json;
using System.Text.Json.Serialization;
using MidiWorkbench.Domain;

namespace MidiWorkbench.Persistence;

public static class ProjectStore
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    public static SongProject Load(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Project exceeds 32 MiB.");
        var project = JsonSerializer.Deserialize<SongProject>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Project is empty.");
        project.Validate(); return project;
    }
    public static void Save(string path, SongProject project)
    {
        project.Validate(); WriteAtomic(path, JsonSerializer.Serialize(project, JsonOptions));
    }
    public static void SaveTake(string path, PerformanceTake take) => WriteAtomic(path, JsonSerializer.Serialize(take, JsonOptions));
    public static void WriteAtomic(string path, string text)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
