# game/

Godot 4.6.2 + C# (.NET 9) project root. `project.godot` and `OpenSwos.csproj` live here.

## Current state

Minimal smoke-test scaffold:
- `project.godot` — GL Compatibility renderer (for weak ARM / handheld targets), 384x272 viewport (Amiga PAL hi-res).
- `OpenSwos.csproj` — targets `net9.0` via `Godot.NET.Sdk/4.6.2`.
- `scenes/Main.tscn` — root `Node2D` with `scripts/Main.cs` attached.
- `scripts/Main.cs` — logs `OpenSWOS booted at …` in `_Ready`.

## Commands (Windows, from repo root)

- editor (GUI): `.tools\godot\Godot_v4.6.2-stable_mono_win64.exe --path game`
- headless import: `.tools\godot\Godot_v4.6.2-stable_mono_win64_console.exe --headless --import --path game`
- C# build: `dotnet build .\game\OpenSwos.csproj`
- headless smoke-test: `.tools\godot\Godot_v4.6.2-stable_mono_win64_console.exe --headless --path game --quit-after 2`

## Conventions (planned, not yet enforced)

- Build scenes **programmatically in C# `_Ready()`** rather than authoring `.tscn` in the editor. Keep `.tscn` files minimal.
- Gameplay data lives in `.tres` resources (text format, agent-friendly), never hardcoded `const`.
- Loaders are **variant-aware** (PC vs Amiga) via a `Variant` boundary at the asset layer.
