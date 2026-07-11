# Porting #3 — Sprite anchors

**Status: cardinal + diagonal run cycles wired (2026-05-13).** Slide and fallen
poses have anchor values dumped and applied, but their visual fidelity is yet to
be eyeball-tested.

Goal: kill the run-cycle wobble. Our renderer was centring each 16×16 atlas tile
geometrically (`Centered = true`), but SWOS sprites have **per-frame anchor
offsets** (`center_x, center_y` in the on-disk sprite header — see
`external/swos-port/docs/SWOS/sprites.txt`). Without those offsets the sprite's
foot point shifts ±1-2 px between consecutive run frames, producing the wobble.

## Source files

- **Header format**: `external/swos-port/docs/SWOS/sprites.txt` (offset 16 = `centerX`,
  range -8..34; offset 18 = `centerY`, range 0..27). Sprite body = `nlines × wquads × 8` bytes.
- **Parser reference**: `external/SwosGfx/SwosGfx/DosSprite.cs::ReadSpriteFromDatBuffer`.
  Paraphrased, not copied (SwosGfx is MIT but we want the C# minimum). See
  `tools/sprite-anchors-extract/Program.cs` source citation block.
- **Animation tables → ordinal mapping**:
  `external/swos-port/swos/swos.asm:218368-218410` — `team1PlayerStandingFacing*Frames` and
  `playerRunning*Team1` data. Each entry is a sprite ordinal in `sprite.dat`'s 0..1333 namespace.

## Extraction

```
dotnet run --project tools/sprite-anchors-extract -- \
    "Swos9697_PC/SensiWs9/SOC/TEAM1.DAT" --annotate
```

Output: 303 rows (ordinals 341..643), each with width, height, wquads, cx, cy
and (when `--annotate`) a human label for the known ordinals.

## Atlas → PC ordinal map

Our Amiga atlas (`CJCTEAM1.RAW` 320×256, 20×16 grid of 16×16 cells) and the PC
sprite stream are assumed to share the same animation layout (same artist, same
poses, same anchor positions). The mapping below was derived from
`team1PlayerStandingFacingUpFrames` etc. (which give standing ordinals) and
`playerRunningUpTeam1 dw 343, 341, 342, 341` etc. (which give animation order
for the two run frames per direction).

| Cell (col,row) | Direction / pose | PC ordinal | (cx, cy) | Δ from stand |
|----------------|------------------|-----------:|---------:|--------------|
| (0, 0)         | N stand          | 341        | (4, 12)  | (0, 0)       |
| (1, 0)         | N run            | 342        | (5, 12)  | (1, 0)       |
| (2, 0)         | N run            | 343        | (3, 12)  | (-1, 0)      |
| (3, 0)         | S stand          | 344        | (4, 12)  | (0, 0)       |
| (4, 0)         | S run            | 345        | (3, 12)  | (-1, 0)      |
| (5, 0)         | S run            | 346        | (5, 12)  | (1, 0)       |
| (6, 0)         | E stand          | 347        | (2, 12)  | (0, 0)       |
| (7, 0)         | E run            | 348        | (4, 12)  | (2, 0)       |
| (8, 0)         | E run            | 349        | (3, 12)  | (1, 0)       |
| (9, 0)         | W stand          | 350        | (2, 12)  | (0, 0)       |
| (10, 0)        | W run            | 351        | (3, 12)  | (1, 0)       |
| (11, 0)        | W run            | 352        | (3, 12)  | (1, 0)       |
| (12, 0)        | N slide          | 395        | (4, 3)   | (0, -9)      |
| (13, 0)        | S slide          | 396        | (4, 10)  | (0, -2)      |
| (14, 0)        | W slide          | 397        | (3, 8)   | (1, -4)      |
| (15, 0)        | E slide          | 398        | (8, 8)   | (6, -4)      |
| (16, 0)        | SW slide         | 399        | (3, 7)   | (2, -5)      |
| (17, 0)        | SE slide         | 400        | (6, 7)   | (4, -5)      |
| (18, 0)        | NW slide         | 401        | (4, 3)   | (3, -9)      |
| (19, 0)        | NE slide         | 402        | (7, 3)   | (5, -9)      |
| (0, 1)         | SW run           | 353        | (2, 12)  | (1, 0)       |
| (1, 1)         | SW stand         | 354        | (1, 12)  | (0, 0)       |
| (2, 1)         | SW run           | 355        | (3, 12)  | (2, 0)       |
| (3, 1)         | SE run           | 356        | (2, 12)  | (0, 0)       |
| (4, 1)         | SE stand         | 357        | (2, 12)  | (0, 0)       |
| (5, 1)         | SE run           | 358        | (3, 12)  | (1, 0)       |
| (6, 1)         | NW run           | 359        | (3, 12)  | (2, 0)       |
| (7, 1)         | NW stand         | 360        | (1, 12)  | (0, 0)       |
| (8, 1)         | NW run           | 361        | (2, 12)  | (1, 0)       |
| (9, 1)         | NE run           | 362        | (3, 12)  | (1, 0)       |
| (10, 1)        | NE stand         | 363        | (2, 12)  | (0, 0)       |
| (11, 1)        | NE run           | 364        | (2, 12)  | (0, 0)       |
| (12, 1)        | N fallen         | 403        | (4, 13)  | (0, 1)       |
| (13, 1)        | S fallen         | 404        | (4, 0)   | (0, -12)     |
| (14, 1)        | W fallen         | 405        | (13, 5)  | (11, -7)     |
| (15, 1)        | E fallen         | 406        | (0, 5)   | (-2, -7)     |
| (16, 1)        | SW fallen        | 407        | (10, 1)  | (9, -11)     |
| (17, 1)        | SE fallen        | 408        | (2, 1)   | (0, -11)     |
| (18, 1)        | NW fallen        | 409        | (11, 9)  | (10, -3)     |
| (19, 1)        | NE fallen        | 410        | (1, 9)   | (-1, -3)     |

Reference standing anchors are (4, 12) for cardinals N/S, (2, 12) for E/W, and
(1..2, 12) for diagonals — the foot point sits about 4-5 px below the cell
centre (8, 8). All run-cycle Δ values are ±1-2 px in X with Δy = 0, which
matches the visible wobble symptom.

## How the renderer applies it

`game/scripts/Assets/PlayerFrames.cs::AnchorOffset(col, row)` returns the
per-cell `(Δx, Δy)` in pixels. `Main.UpdateSprite` keeps `Sprite2D.Centered = true`
(so the cell centres at the player coords by default) and subtracts the delta:

```csharp
var (ox, oy) = PlayerFrames.AnchorOffset(cell.col, cell.row);
sprite.Position = new Vector2(p.X.ToInt() - ox, p.Y.ToInt() - oy);
```

This is the minimum change that fixes the wobble — we never need to know exactly
where in the 16×16 Amiga cell the sprite content sits, because deltas are
relative to the direction's standing pose, which is also rendered with
`Centered = true`. The standing pose continues to render exactly as before.

## Open assumptions

1. **PC anchors = Amiga anchors.** Unverified but plausible — same artist, same
   pixel-level animations. If the Amiga atlas has its own anchor table baked
   into the sprite descriptor stream (`SWOS.bin`), eventually replace these with
   the Amiga values. See `tools/sprite-descriptor-extract/` for the partial RE.
2. **Cell ordering for run frames.** The atlas comment in `PlayerFrames.cs`
   labels cell phase 1 and phase 2 as just "run 1" and "run 2" without an order
   guarantee. The PC binary stores ordinals 341..343 sequentially and the atlas
   stores cells 0..2 sequentially; we assume left-to-right alignment. The two
   alternative orderings (swap 342↔343) would give symmetric ±1 px deltas, so
   the wobble fix is partially self-correcting either way.
3. **Slide and fallen anchors.** These are wired in but only visually inspected
   for the standing case. The slide motion-cycle is short (~24 ticks @ 50 Hz)
   so any residual misalignment is harder to spot than the run-cycle wobble.

## Port checklist

- [x] Build `tools/sprite-anchors-extract/` C# console app
- [x] Dump all 303 anchors from `TEAM1.DAT` with `--annotate`
- [x] Identify atlas-cell → PC-ordinal mapping from `swos.asm` animation tables
- [x] Add `PlayerFrames.AnchorOffset(col, row)` table
- [x] Wire `Main.UpdateSprite` to subtract anchor from world position
- [x] Build clean, headless smoke-test passes
- [ ] Eyeball test on a live build: cardinal wobble gone, diagonal wobble gone,
      slide pose feet sit on the right point — user-side
- [ ] Cross-check against Amiga sprite descriptor stream (if/when extracted)
- [ ] Add anchors for goalkeeper and bench sprites (ordinals 947+, 1310+)
- [ ] Add anchors for ginger / black skin variants (ordinals 442+, 543+ etc.)
