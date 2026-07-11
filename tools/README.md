# tools/

CLI utilities for working with original SWOS data. Output of these tools lives in `../assets/extracted/`.

All tools are C# console apps targeting `net9.0` (matches the existing system .NET SDK).

## Implemented

- **`adf-extract/`** — Amiga DD floppy ADF reader (OFS + FFS). Verified against both SWOS Amiga 96/97 disks (125 files total). Output preserves directory structure.
  - list:  `dotnet run --project tools/adf-extract -- "path/to.adf" --list`
  - extract: `dotnet run --project tools/adf-extract -- "path/to.adf" -o "outdir"`
- **`rnc-decode/`** — RNC ProPack v1 decoder. SWOS and SWOS2 m68k binaries both decompress with CRC OK (351 596 / 357 226 bytes output). Eager-refill bit reader (`bitcount < 16`), `p` starts at 0 — see comments in `RncV1.cs`.
  - run: `dotnet run --project tools/rnc-decode -- "path/in.rnc1" -o "path/out.bin" --verify`
- **`team-decode/`** — SWOS PC/Amiga TEAM.\* parser. Coach offset +0x24 (NOT +0x26 as ysoccer claims), 3-bit-nibble skill packing. Sanity-checked against ARSENAL/Wenger/Bergkamp (P=8 S=8 H=6 T=6 C=8 Sp=8 F=8 ✓), ALBPETROL PATOS, SOUTH KOREA.
  - dump: `dotnet run --project tools/team-decode -- "Swos9697_PC/SensiWs9/SOC/DATA/TEAM.008" --team 0`
  - summary: `dotnet run --project tools/team-decode -- "...TEAM.008" --summary`

## Also implemented

- **`sprite-decode/`** — Amiga 4-bitplane interleaved decoder + BMP/PPM writer with SWOS palette. Used by game via `<Compile Include>` (no duplication). Verified against `CJCTEAM*`, `CJCTEAMG`, `CJCBITS`, `CJCGRAFS`, `CJCBENCH`, `SOCCER_S`, `MENUBG`.
- **`pitch-decode/`** — `SWCPICH*.MAP` parser (9240-byte header + N×128-byte tile graphics). All six SWOS pitch variants render to BMP at 672×848. See `../docs/formats/01-player-sprites.md` for the sprite layout + `../memory/reference_swos_pitch_format.md` for the pitch format spec.
- **`sprite-descriptor-extract/`** — PARTIAL. Parses the SWOS Amiga sprite descriptor opcode stream from `SWOS.bin`. Found 7 static streams (charset, cjcgrafs, cjcteamg [goalkeeper, 99 descriptors parsed], cjcbits, cjcbench, menus, menus2). Player atlas (cjcteam1/2/3) descriptors not yet found — likely runtime-generated. See `../memory/reference_swos_descriptor_streams.md` for findings + resumption plan.

## Planned (not implemented)

- **`rjp-decode/`** — Richard Joseph Player audio format used by SWOS Amiga (`disk{1,2}/sound/*.SNG`, `*.INS`, `*.IN1`, `*.IN2` files marked `RJP1`). Separate research epic.
- **`raw-to-wav/`** — converts `HARD/*.RAW` (PC) audio. Likely 8-bit unsigned PCM; sample rate experimentally determined.
- **`adf-write/`** — optional, for repacking modified disks to test in WinUAE.

External format references and community pointers are in `../CLAUDE.md`.
