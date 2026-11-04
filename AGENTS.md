# Project rules

Read README.md, docs/architecture.md, docs/status.md and docs/product.md before changes.

- Target .NET 10. Frontend: Avalonia + Dock. Keep engine free of UI references.
- MIDI only; no synthesis, audio capture, plug-in hosting or required sound-state recall.
- Preserve both outline-led and performance-led workflows in one project model.
- Never perform generation, storage or UI operations on the scheduler path.
- The current memory transport is a simulation foundation, not hardware timing certification.
- Do not claim physical MIDI, drummer migration or piano-roll editing exists until implemented.
- Preserve locked notes and fixed patterns. Regeneration is explicit and scoped.
- UI copy: short labels; occasional explanation in tooltips; no ornamental text.
- Keep project files independent of endpoints and dock layout. Routes belong to a rig.
- Use versioned project/recipe schemas. Unsupported schemas must fail visibly.
- Run scripts/verify.ps1 or scripts/verify.sh. Console specifications return a failing exit code; dotnet test does not run them.
- Every hardware adapter requires disconnect, stop, backlog, timestamp and note-ownership tests.
- Do not add CI, telemetry, network services or audio dependencies without a concrete need.
