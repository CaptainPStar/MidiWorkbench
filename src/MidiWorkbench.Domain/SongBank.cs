using System.Collections.Immutable;

namespace MidiWorkbench.Domain;

public sealed record BankSong(string Id, SongProject Project);
public sealed record SongBank(int SchemaVersion, string Name, ImmutableArray<BankSong> Songs, ImmutableArray<string> Order)
{
    public const int CurrentSchemaVersion = 1;
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported bank schema.");
        if (string.IsNullOrWhiteSpace(Name) || Songs.IsDefault || Order.IsDefault || Songs.Length > 1024 || Order.Length > 10000)
            throw new ArgumentException("Invalid bank.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var song in Songs)
        {
            if (song is null || string.IsNullOrWhiteSpace(song.Id) || !ids.Add(song.Id) || song.Project is null) throw new ArgumentException("Invalid bank song.");
            song.Project.Validate();
        }
        if (Order.Any(id => id is null || !ids.Contains(id))) throw new ArgumentException("Unknown bank song.");
    }
}
