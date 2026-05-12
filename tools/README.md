# tools/

CLI utilities for working with original SWOS data. Output of these tools lives in `../assets/extracted/`.

All tools are C# console apps targeting `net9.0` (matches the existing system .NET SDK).

## Implemented

- **`adf-extract/`** — Amiga DD floppy ADF reader (OFS Standard + FFS Standard). Walks the root block hash table + file header chains, follows extension blocks, strips OFS data-block headers. Verified against both SWOS Amiga 96/97 disks (125 files total, OFS Standard, volumes "SWOS" and "SWOS2"). Output preserves directory structure.
  - build: `dotnet build tools/adf-extract`
  - list:  `dotnet run --project tools/adf-extract -- "path/to.adf" --list`
  - extract: `dotnet run --project tools/adf-extract -- "path/to.adf" -o "outdir"`

## Planned (not implemented yet)

- **`rnc-decode/`** — **immediate blocker.** The Amiga `SWOS` and `SWOS2` executables are RNC ProPack v1 compressed (`RNC\x01` magic). Many `.RAW` graphics may be too. Need a v1 + v2 decompressor before we can disassemble the m68k binary or load most graphics.
- **`team-decode/`** — TEAM.DAT parser. Format is well documented in the community; PC and Amiga share filenames, expect format compatibility.
- **`pitch-decode/`** — `SWCPICH*.MAP` (Amiga) and `PITCH*.BLK`/`PITCH*.DAT` (PC) tile-map parser feeding a renderer.
- **`sprite-decode/`** — `SPRITE.DAT` / `BENCH.DAT` / `GOAL.DAT` (PC). Partially understood format; will require real RE.
- **`raw-to-wav/`** — converts `HARD/*.RAW` (PC) and `sound/*.RAW` (Amiga) audio. Likely 8-bit unsigned PCM; sample rate to determine experimentally.
- **`adf-write/`** — optional, for repacking modified disks to test in WinUAE.

External format references and community pointers are in `../CLAUDE.md`.
