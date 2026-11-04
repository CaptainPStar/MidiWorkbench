# ADR 001: Avalonia with Dock

Status: accepted for the foundation, 2026-11-04.

## Decision

Use Avalonia Desktop and Dock with .NET 10. Keep the engine independent so another frontend or a remote controller can be added later.

| Option | Strengths for this app | Costs / limitations |
|---|---|---|
| Avalonia + Dock | Existing interaction familiarity; floating/splitting panes; C# integration; custom drawing for note grids | Desktop rendering, keyboard focus and large-grid performance still need testing |
| TypeScript frontend + local .NET service | Strong web UI ecosystem; future browser/tablet control | Additional process/protocol/schema lifecycle; browser tab is not a clock authority; detached windows require deliberate handling |
| WPF | Mature Windows desktop integration | Windows-only and replaces the established Avalonia approach |
| Blazor Hybrid | Native .NET host with web components | WebView lifetime/interop plus a separate docking choice; no demonstrated benefit for this scope |

The choice is based on workflow and deployment complexity, not a claim that Avalonia improves MIDI timing. All frontends must use the same native scheduling backend. TypeScript remains a valid later remote UI, with no timing-critical responsibilities.

Start with code-first Dock documents and a central session owner. Docked/floating views never own MIDI endpoints. Layout persistence is machine-local and never starts playback. Reset is always available. Prototype view code stays small; split editor view models and custom note canvas as the editor grows.

## Dependency selection

Pin Avalonia.Desktop and Avalonia.Themes.Fluent 12.1.4; Dock packages 12.1.0.9. These versions were read from the current MusicSessionTrace application project. They have not been restored in this environment. Prefer upgrading only after a restore/build/UI smoke test. Generate and commit package lock files after the first successful restore; do not fabricate them.

## Sources

- https://docs.avaloniaui.net/docs/supported-platforms
- https://docs.avaloniaui.net/docs/platform-specific-guides/windows
- https://wieslawsoltes.github.io/Dock/articles/quick-start.html
- https://wieslawsoltes.github.io/Dock/articles/dock-architecture.html
- https://learn.microsoft.com/aspnet/core/blazor/hybrid/
- https://github.com/CaptainPStar/MusicSessionTrace/blob/main/src/MusicSessionTrace.App.Avalonia/MusicSessionTrace.App.Avalonia.csproj

Sources inspected 2026-11-04. Dock source/API usage informed the new shell; no existing application source was copied.
