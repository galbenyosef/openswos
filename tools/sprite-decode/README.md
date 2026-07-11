# sprite-decode

Generic Amiga bitplane → palette-indexed bitmap decoder. Outputs PPM (P6) for eyeball verification.

## Working configuration for SWOS Amiga player atlases

After `tools/rnc-decode/` unpacks `CJCTEAM[1-3].RAW` / `CJCTEAMG.RAW` (RNC v2 compressed) you get a **40 960-byte payload = 320×256 px image, 4 bitplanes interleaved per row**.

```
dotnet run --project tools/sprite-decode -- \
    assets/extracted/amiga/unpacked/CJCTEAM1.bin \
    -w 320 -H 256 -p 4 -n 1 -c 1 --ilv \
    -o assets/extracted/amiga/sprites/CJCTEAM1_sheet.ppm
```

Output is a 320×256 PPM with a debug rainbow palette. Patterns repeat every 16 pixels — that's the **16×16 sprite-tile grid** (20 cols × 16 rows = 320 cells max, many empty).

## Configurations to expect for other Amiga files

| File family            | Likely dimensions | Notes                                  |
|------------------------|-------------------|----------------------------------------|
| `CJCTEAM*.RAW`         | 320×256 × 4bpp    | Player atlases (3 kit variants)        |
| `CJCTEAMG.RAW`         | 320×256 × 4bpp    | Goalkeeper atlas                       |
| `CJCBENCH.RAW`         | 320×256 × 4bpp    | Bench substitutes                      |
| `CJCBITS.RAW`          | TBD               | Ball + UI bits                         |
| `CJCGRAFS.RAW`         | TBD               | Misc HUD                               |
| `MENUBG*.RAW`          | likely 320×256    | Menu backgrounds (probably 5bpp)       |
| `LOADER*.RAW`          | varies            | Loader-screen still images             |
| `SWCPICH*.MAP`         | varies            | Pitch tile maps — NOT bitplane, raw indices |

## Flags

```
sprite-decode <input.raw> [-o out.ppm] [-w W] [-H H] [-p P] [-s SKIP] [-n N] [-c COLS] [--seq | --ilv] [--ascii N]
```

- `-w/-H` width/height of one "sprite" (default 16×16). For full atlases pass 320×256 with `-n 1`.
- `-p` bitplane count (default 5; use **4** for SWOS Amiga player files).
- `--ilv` interleaved planes per row (default; correct for SWOS Amiga).
- `--seq` sequential planes (all of plane 0, then all of plane 1, …). Not used by SWOS but kept for testing.
- `--ascii N` dump sprite #N as ASCII art (each palette index → distinct glyph). Useful when PPM rendering isn't viewable.

## Still TODO

- Real palette recovery from `SWOS.bin` (Amiga colour signature `$0036 $0999 $0fff $0000` was not found in the decompressed binary — Amiga palette differs from the PC one. Need different search strategy or another agent pass).
- Individual-frame extraction inside the 320×256 atlas (use `frameIndicesTable` from the `starwindz/original-amiga-swos` disassembly).
- Kit-recolour pipeline: remap palette indices 10/11/14/15 to per-team kit colours from `TEAM.*` bytes at runtime.
