# tools/

CLI utilities for working with original SWOS data. Output of these tools lives in `../assets/extracted/`.

Planned tools (none implemented yet):

- **adf-extract** — wraps `unADF` to extract `Swos9697_Amiga/*.adf` into `assets/extracted/amiga/`.
- **rnc-decode** — RNC ProPack decompressor for Amiga assets.
- **pitch-decode** — parser for `PITCH*.BLK` + `PITCH*.DAT` + `*.256` palette files.
- **team-decode** — parser for `TEAM.*` files (well-documented format).
- **sprite-decode** — parser for `SPRITE.DAT` / `BENCH.DAT` / `GOAL.DAT` (partial knowledge; RE work needed).
- **raw-to-wav** — converts `HARD/*.RAW` audio (likely 8-bit unsigned PCM) to WAV.

Language: C# (matches the game). Each tool ships as a separate `.csproj` under `tools/<name>/`.

External references and asset format state are documented in `../docs/formats/` and `../CLAUDE.md`.
