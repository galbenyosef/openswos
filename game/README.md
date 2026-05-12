# game/

Godot 4 + C# project root. `project.godot` and `OpenSwos.csproj` will live here once scaffolded.

This directory will hold:
- `project.godot`, `OpenSwos.csproj`, `.csproj.user`, `icon.svg`
- `scenes/` — minimal `.tscn` files (prefer programmatic scene construction in C#)
- `scripts/` — C# game code (`*.cs`)
- `resources/` — `.tres` data resources (gameplay constants, variant config)
- `addons/` — third-party Godot plugins if any

**Variant-aware:** loaders and gameplay constants must be data-driven so the same code runs against both PC and Amiga assets. See `docs/decisions/` for the rationale.

**Build commands** (to be filled in when scaffolded):
- editor: `godot --editor`
- headless run: `godot --headless`
- export: `godot --headless --export-release "Windows Desktop" ../build/openswos.exe`
