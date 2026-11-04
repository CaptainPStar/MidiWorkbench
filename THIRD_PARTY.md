# Third-party dependencies

Avalonia: https://github.com/AvaloniaUI/Avalonia (MIT).
Dock: https://github.com/wieslawsoltes/Dock (MIT).
.NET runtime/SDK: https://github.com/dotnet/runtime and https://github.com/dotnet/sdk.

Dependencies are referenced through NuGet, not vendored. Preserve upstream notices when distributing binaries. Exact restored transitive dependencies and license notices must be reviewed after restore. No license has yet been selected for this new project.

The deterministic drummer and personas derive from CaptainPStar/MusicSessionTrace (`DrumPatternPlanner.cs`, `PersonaDrumPlanner.cs`, `DrummerPersonas.cs` and option records), inspected at `f9d8e5a`. This port converts sample positions to musical ticks and adds versioned meter grouping. No upstream license was present; no new license grant is implied.
