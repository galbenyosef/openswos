# 01 — Player sprites: source files in PC and Amiga

The underlying sprite art is the same on PC and Amiga, but stored very differently. **Neither format is decoded yet.** This document captures what's known so we can build `tools/sprite-decode/` on top of it.

## PC: `SPRITE.DAT`

Master atlas in `Swos9697_PC/SensiWs9/SOC/SPRITE.DAT`. Single binary file containing **all** in-game sprites — players, ball, referee, crowd, hoardings, goal nets, UI elements.

Format (partially understood, real RE still needed):
- Header: sprite count, then a table of `(offset, width, height, flags)` per sprite.
- Pixel data: 8-bit palette-indexed bitmap, but stored in **column-strip order** rather than row-major — a SWOS-specific quirk that has bitten every clean-room implementer.
- Palette lives separately in `PAL.256` / `FLAG.256` / similar 256-byte files.
- **Kit colours are not baked into the sprite data.** Player sprites use dedicated palette index ranges (body, sleeves, shorts, socks) that the engine **remaps at runtime** per team. So extracting the raw atlas gives template-coloured players; the kit-recolour pipeline applies on top using the kit bytes from `TEAM.*`.

Auxiliary sprite files in the same dir:
- `BENCH.DAT` — substitute / bench-area sprites.
- `GOAL1.DAT` — goal celebration animation frames.

Reference material to mine:
- **SWOS Picture Editor** by Zlatko Karakas — `http://zkz.swos.eu/html/swpe.htm`. Loads, displays, and edits the PC sprite files. Source isn't public but the binary is well-tested; DOSBox debugging + reverse-engineering the editor would yield the exact format.
- **AG-SWSEdt** — `github.com/anoxic83/AG_SWSEdt`. Pascal sources may include sprite parsing.
- **SWOS United forum thread #21545** on `SPRITE.DAT`/`BENCH.DAT`/`GOAL.DAT` structure — the canonical community write-up.

## Amiga: `CJCTEAM*.RAW` and friends

Sprite data is split across several files in `disk2/grafs/` (already extracted to `assets/extracted/amiga/disk2/grafs/`):

| File              | Size   | Likely purpose                    |
|-------------------|--------|-----------------------------------|
| `CJCTEAM1.RAW`    | 6691 B | Player sprites, kit variant 1     |
| `CJCTEAM2.RAW`    | 6503 B | Player sprites, kit variant 2     |
| `CJCTEAM3.RAW`    | 6606 B | Player sprites, kit variant 3     |
| `CJCTEAMG.RAW`    | 3996 B | Goalkeeper sprites                |
| `CJCBENCH.RAW`    | 2589 B | Bench sprites                     |
| `CJCBITS.RAW`     |  978 B | Small detail sprites              |
| `CJCGRAFS.RAW`    | 2693 B | Misc in-game graphics             |

- `.RAW` files are **RNC ProPack v2 compressed** (`RNC\x02` magic at offset 0). All 27 graphics files in `grafs/` plus pitch `.MAP` files are RNC v2.
- After RNC v2 decompression: **`CJCTEAM[1-3].RAW` and `CJCTEAMG.RAW` each unpack to 40 960 bytes = 320×256 × 4-bitplane image** (NOT 5-bitplane as Amiga ECS standards suggested, NOT 16×16 individual frames). Player tiles live as 16×16 cells inside the 320×256 atlas (20 cols × 16 rows). Frame-to-cell mapping is implicit, encoded in `frameIndicesTable` inside `SWOS.bin`.
- **Both v1 and v2 RNC decoders are now working** (`tools/rnc-decode/`). All 8 tested Amiga graphics files decode with CRC OK.
- **Bitplane decoder is working** (`tools/sprite-decode/`). Renders 320×256 sheets with a debug palette; player silhouettes visible in the output.
- The `CJC` prefix is internal Sensible Software naming (likely the artist's initials).
- Palette: lives inside the decompressed `SWOS` / `SWOS2` m68k binaries, or in a small `.256`-equivalent we haven't located yet. First job is to recover it.

## Strategy — current state (2026-05-12)

- [x] **Land RNC v2 in `tools/rnc-decode/`** — done.
- [x] **Decode CJCTEAM1.RAW** to 40 960 bytes — done; renders to 320×256 PPM with player silhouettes visible.
- [ ] **Locate the real palette.** Tried searching `SWOS.bin` for the PC palette signature (`$0036 $0999 $0fff $0000`) — **not found**. Amiga palette differs from PC's. Need another search strategy (entropy-based scan for valid Amiga 12-bit colour blocks, or extract from `frameIndicesTable` context in the disassembly).
- [ ] **Identify the kit-remap indices.** Theory: 10 (shirt), 11 (stripes), 14 (shorts), 15 (socks). Verify by diffing `CJCTEAM1` vs `CJCTEAM2` vs `CJCTEAM3` post-decode — colours that vary across the three files are the kit slots.
- [ ] **Identify per-frame tile coords inside the 320×256 atlas.** Encoded in `frameIndicesTable` inside `SWOS.bin`; starwindz's disassembly has it but uncommented.
- [ ] **Then PC `SPRITE.DAT`.** Port format knowledge from Karakas's SPE binary behaviour or the community spec. The art is identical; only the packing differs.
- [ ] **Wire into `game/IAssetSource`.** Add `LoadPlayerSprite(team, direction, frame)` → returns a Godot `Texture2D` with kit recolour applied per `TeamRecord`.

## Side note: audio

Discovered during the same magic-byte sweep — Amiga sound files are **`RJP1` format** (Richard Joseph Player, the in-house audio engine of Sensible Software composer Richard Joseph). Not a standard module format (MED/TFMX/ProTracker). Separate decoder needed when sound work begins. The lone exception is `HERO.INS` which is RNC v1 wrapped around RJP-payload, and `SFX.IN2` which starts `00 00 07 05` (probably truncated or different format — needs investigation).

Hard constraint: keep the kit-recolour pipeline data-driven (palette index → runtime colour from `TEAM.*`), so PC and Amiga teams render with the same `IAssetSource` contract — same way `team-decode` already produces variant-neutral `TeamRecord`.
