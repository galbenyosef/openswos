using OpenSwos.Sim;

namespace OpenSwos.Assets;

// CJCTEAM1.RAW 320×256 atlas cell layout — rows 0 and 1, verified by visual inspection
// on 2026-05-12. Each direction occupies 3 horizontally-adjacent cells:
//
//   CARDINALS (row 0): standing = FIRST cell of triplet, run1 + run2 follow.
//     cols 0..2  = N   (standing 0, run 1, run 2)
//     cols 3..5  = S
//     cols 6..8  = E
//     cols 9..11 = W
//     cols 12..19 = 8 slide-tackle directions (one cell each: N, S, W, E, SW, SE, NW, NE)
//
//   DIAGONALS (row 1): standing = MIDDLE cell of triplet — different convention!
//     cols 0..2   = SW  (run 0, standing 1, run 2)
//     cols 3..5   = SE
//     cols 6..8   = NW
//     cols 9..11  = NE
//     cols 12..19 = 8 fallen poses (one cell each: N, S, W, E, SW, SE, NW, NE)
//
// Some sprites visually extend 1-2 pixels into the cell below (artistic intent, not a
// decoder bug). For MVP we render strictly the 16×16 cell — any overflow is clipped.
// One case goes the other way: the fallen-W sprite (14,1) draws its hair-top 1 px ABOVE
// its cell, leaking a stray 2-px mark into the bottom row of the W slide-tackle (14,0).
// AmigaSpriteAtlas.GetTile trims those pixels (see IsNeighbourOverflowPixel there).
//
// Slide direction order (cols 12..19 = N,S,W,E,SW,SE,NW,NE) is confirmed against the
// original: swos.asm plTacklingAnimTable (line 218989) maps direction 0..7
// (N,NE,E,SE,S,SW,W,NW) to sprites 395,402,398,400,396,399,397,401 — i.e. ordinals
// 395..402 are laid out N,S,W,E,SW,SE,NW,NE, matching Slide() below (395+k → col 12+k).

public static class PlayerFrames
{
    public const int CellSize = 16;

    // Standing-pose cell (col, row in the 20×16 atlas grid).
    public static (int Col, int Row) Standing(Direction d) => d switch
    {
        Direction.North     => (0, 0),
        Direction.South     => (3, 0),
        Direction.East      => (6, 0),
        Direction.West      => (9, 0),
        Direction.SouthWest => (1, 1),
        Direction.SouthEast => (4, 1),
        Direction.NorthWest => (7, 1),
        Direction.NorthEast => (10, 1),
        _ => (3, 0),
    };

    // Run-cycle frames per direction. Three cells per direction in the order they
    // appear in the atlas (left-to-right). Use phase 0..2 to index.
    public static (int Col, int Row) RunFrame(Direction d, int phase)
    {
        phase = ((phase % 3) + 3) % 3;
        return d switch
        {
            Direction.North     => (phase + 0, 0),
            Direction.South     => (phase + 3, 0),
            Direction.East      => (phase + 6, 0),
            Direction.West      => (phase + 9, 0),
            Direction.SouthWest => (phase + 0, 1),
            Direction.SouthEast => (phase + 3, 1),
            Direction.NorthWest => (phase + 6, 1),
            Direction.NorthEast => (phase + 9, 1),
            _ => (phase + 3, 0),
        };
    }

    // Slide-tackle pose per direction (single cell, no animation).
    // Atlas cols 12..19 of row 0; order is N, S, W, E, SW, SE, NW, NE.
    public static (int Col, int Row) Slide(Direction d) => d switch
    {
        Direction.North     => (12, 0),
        Direction.South     => (13, 0),
        Direction.West      => (14, 0),
        Direction.East      => (15, 0),
        Direction.SouthWest => (16, 0),
        Direction.SouthEast => (17, 0),
        Direction.NorthWest => (18, 0),
        Direction.NorthEast => (19, 0),
        _ => (13, 0),
    };

    // Fallen (knocked-down / injured) pose per direction. Row 1 cols 12..19.
    public static (int Col, int Row) Fallen(Direction d) => d switch
    {
        Direction.North     => (12, 1),
        Direction.South     => (13, 1),
        Direction.West      => (14, 1),
        Direction.East      => (15, 1),
        Direction.SouthWest => (16, 1),
        Direction.SouthEast => (17, 1),
        Direction.NorthWest => (18, 1),
        Direction.NorthEast => (19, 1),
        _ => (13, 1),
    };

    // ============================================================================
    // Extended (non-16×16) frames — the atlas bands BELOW rows 0/1.
    //
    // CJCTEAM1/2/3 (outfielders), verified by pixel-occupancy scan of the
    // decoded atlas (2026-07-03): below row 1 the sheet switches to eight
    // 24-px-tall horizontal bands at y = 32 + 24·k. Each band belongs to one
    // direction (band order N, S, E, W, SW, SE, NW, NE — same convention as
    // rows 0/1) and holds, per band: kick frames at cols 0-2, the THROW-IN
    // trio at cols 4-6, and (bands 1/2 only) the arms-up CHEER trio at
    // cols 9-11. Sprite ordinals (from the anim streams in
    // AnimationTablesData / swos.asm:218418-218449):
    //   365-370  cheer   (341 + 24 offset, SetNextPlayerFrame asm:102959)
    //   371-394  throw-in — 3 per direction, groups of 3 from 371 in the
    //            SAME direction order as the bands (N,S,E,W,SW,SE,NW,NE)
    // Team2 mirrors at 668-673 / 674-697 (rel identical after -644).
    //
    // CJCTEAMG (goalkeepers): six bands — catch at y=32/56 (3 frames each)
    // and four 7-slot dive bands at y=77/103/125/151 (E-dive/W-dive for the
    // top-goal keeper, then E/W for the bottom-goal keeper; the 7th slot of
    // each dive band is an unused white-kit variant). Ordinals (from the
    // goalie jumping/catch streams, swos.asm:218568-218590):
    //   971-998   dives  (4 bands × 7)
    //   999-1004  catch  (2 bands × 3)
    // Team2 mirrors at 1087+; the reserve-keeper block repeats every 58
    // ordinals (kNumGoalkeeperSprites — swos-port gameSprites.cpp:231-240),
    // callers reduce rel modulo 58 before calling GoalieExtCell.
    //
    // Cells are keyed into the existing (col,row) texture dictionaries with
    // SYNTHETIC row numbers >= ExtThrowRowBase so they can't collide with the
    // real 20×16 grid; ExtRect() decodes a synthetic cell back to the pixel
    // rectangle to cut from the atlas.
    // ============================================================================

    public const int ExtThrowRowBase  = 100;  // rows 100..107 → CJCTEAM* band y = 32 + 24·(row-100)
    public const int ExtWritheRowBase = 108;  // row 108 → CJCTEAM* injured-writhe tiles at (col,32), 16×16 (cols 7-10)
    public const int ExtGoalieRowBase = 120;  // rows 120..125 → CJCTEAMG bands y = {32,56,77,103,125,151}

    private static readonly int[] GoalieBandY = { 32, 56, 77, 103, 125, 151 };

    // rel = sprite ordinal - team outfield base (341 / 644).
    public static (int Col, int Row)? OutfieldExtCell(int rel)
    {
        if (rel >= 24 && rel <= 29)          // cheer 365-370: N trio then S trio, cols 9-11
            return (9 + (rel - 24) % 3, ExtThrowRowBase + (rel <= 26 ? 1 : 2));
        if (rel >= 30 && rel <= 53)          // throw-in 371-394: cols 4-6 of direction band
        {
            int o = rel - 30;
            return (4 + o % 3, ExtThrowRowBase + o / 3);
        }
        return null;
    }

    // rel = sprite ordinal - team goalie base (947 / 1063), already reduced
    // modulo 58 (main vs reserve keeper block).
    public static (int Col, int Row)? GoalieExtCell(int rel)
    {
        if (rel >= 24 && rel <= 51)          // dives 971-998: 4 bands × 7 frames
            return ((rel - 24) % 7, ExtGoalieRowBase + 2 + (rel - 24) / 7);
        if (rel >= 52 && rel <= 57)          // catch 999-1004: 2 bands × 3 frames
            return ((rel - 52) % 3, ExtGoalieRowBase + (rel - 52) / 3);
        return null;
    }

    public static bool IsExtCell((int Col, int Row) cell) => cell.Row >= ExtThrowRowBase;

    // ============================================================================
    // Authentic per-frame anchors for the extended (16×20 goalie, 16×24 outfield)
    // cells — REPLACES the old hand-tuned global (0,4) ext offset (bug #162: the
    // ball floated a few px off the keeper's hands during a dive/catch because the
    // renderer anchored every ext frame on the outfield STANDING foot-anchor
    // instead of the frame's own centre).
    //
    // Each value is the pixel inside the ext tile that must land on the player's
    // world (logical-origin) position — i.e. draw pos = (worldX - Cx, worldY - Cy).
    // The sim pins the ball to the keeper's logical origin via the claim-height
    // tables (dseg_17DEF4 / kGoalKeeperClaimingBallHeight), so putting the sprite
    // anchor ON that origin makes the hands meet the ball. For a full-stretch dive
    // Cx runs out to 14 (the reaching hand) vs the old ~2 body-centre value — that
    // horizontal miss was the visible gap.
    //
    // Derivation (offline, 2026-07-03) — PORT-VISUAL, combines authentic PC sprite
    // centres with the Amiga tile's measured figure placement:
    //   Cx = amigaFigureBBoxLeft + pc_centerX
    //   Cy = amigaFigureBBoxTop  + pc_centerY
    // where pc_center* are the SWOS SpriteGraphics center_x/center_y fields read
    // from Swos9697_PC/SensiWs9/SOC/GOAL1.DAT (goalie, ordinals 971-1004 via
    // tools/sprite-anchors-extract -o 947) and TEAM1.DAT (outfield throw-in/cheer,
    // ordinals 365-394), and amigaFigureBBox* is the top-left of the non-background
    // pixels in the corresponding CJCTEAMG / CJCTEAM1 tile (the Amiga art aligns
    // the figure differently per band — goalie dives sit bottom-of-cell, throw-ins
    // top-of-cell — so the PC centre alone can't be used; the bbox term rebases it
    // into the actual Amiga tile). Each entry cites its ordinal + raw pc(cx,cy).
    private static readonly System.Collections.Generic.Dictionary<(int Col, int Row), (int Cx, int Cy)> _extAnchors = new()
    {
        // --- CJCTEAMG goalie CATCH (rows 120-121, cols 0-2) ---
        {(0,120),(5,15)}, {(1,120),(5,16)}, {(2,120),(3,18)},   // ord 999-1001
        {(0,121),(5,15)}, {(1,121),(5,16)}, {(2,121),(3,18)},   // ord 1002-1004
        // --- CJCTEAMG goalie DIVE (rows 122-125, cols 0-6) ---
        {(0,122),(6,17)},{(1,122),(11,17)},{(2,122),(12,17)},{(3,122),(12,17)},{(4,122),(13,17)},{(5,122),(14,17)},{(6,122),(9,17)},   // ord 971-977
        {(0,123),(-1,17)},{(1,123),(1,17)},{(2,123),(2,17)},{(3,123),(1,17)},{(4,123),(1,17)},{(5,123),(4,17)},{(6,123),(3,16)},       // ord 978-984
        {(0,124),(6,17)},{(1,124),(11,17)},{(2,124),(12,17)},{(3,124),(12,17)},{(4,124),(13,17)},{(5,124),(14,17)},{(6,124),(9,18)},   // ord 985-991
        {(0,125),(-1,17)},{(1,125),(1,17)},{(2,125),(2,17)},{(3,125),(1,17)},{(4,125),(1,17)},{(5,125),(4,17)},{(6,125),(3,17)},       // ord 992-998
        // --- CJCTEAM1 outfield THROW-IN (rows 100-107, cols 4-6; band order N,S,E,W,SW,SE,NW,NE) ---
        {(4,100),(4,17)},{(5,100),(4,18)},{(6,100),(4,14)},   // ord 371-373 N
        {(4,101),(4,17)},{(5,101),(4,18)},{(6,101),(4,13)},   // ord 374-376 S
        {(4,102),(4,16)},{(5,102),(3,17)},{(6,102),(3,13)},   // ord 377-379 E
        {(4,103),(2,16)},{(5,103),(3,17)},{(6,103),(5,13)},   // ord 380-382 W
        {(4,104),(2,16)},{(5,104),(3,17)},{(6,104),(5,13)},   // ord 383-385 SW
        {(4,105),(4,16)},{(5,105),(3,17)},{(6,105),(2,13)},   // ord 386-388 SE
        {(4,106),(2,16)},{(5,106),(3,17)},{(6,106),(5,13)},   // ord 389-391 NW
        {(4,107),(4,16)},{(5,107),(3,17)},{(6,107),(2,13)},   // ord 392-394 NE
        // --- CJCTEAM1 outfield CHEER (rows 101-102, cols 9-11) ---
        {(9,101),(5,21)},{(10,101),(5,21)},{(11,101),(5,21)},   // ord 365-367
        {(9,102),(5,13)},{(10,102),(5,13)},{(11,102),(5,13)},   // ord 368-370
        // --- CJCTEAM1 injured WRITHE (row 108, cols 7-10; lying-body, PORT-VISUAL) ---
        // Anchor = torso/hip of the sprawled figure so the body rests on the
        // player's fall position. Within each pair (7/8 TopRight, 9/10 TopLeft)
        // the anchor is IDENTICAL, so the 20-tick flip-flop only kicks the legs
        // and never shifts the torso.
        {(7,108),(5,6)},{(8,108),(5,6)},     // ord 411-412 TopRight (dir 4-7)
        {(9,108),(6,6)},{(10,108),(6,6)},    // ord 413-414 TopLeft  (dir 0-3)
    };

    // Authentic anchor (see _extAnchors). Returns the tile pixel that should sit on
    // the player's world position for an extended (goalie dive/catch, outfield
    // throw-in/cheer) frame. Falls back to a bottom-of-cell foot anchor if an
    // unmapped ext cell ever appears.
    public static (int Cx, int Cy) ExtAnchor((int Col, int Row) cell)
    {
        if (_extAnchors.TryGetValue(cell, out var a)) return a;
        // Unmapped ext cell — approximate the old foot anchor (cx=8, cy≈16).
        return (8, 16);
    }

    // Pixel rectangle inside the source atlas for a synthetic ext cell.
    public static (int X, int Y, int W, int H) ExtRect((int Col, int Row) cell)
    {
        if (cell.Row >= ExtGoalieRowBase)
            return (cell.Col * 16, GoalieBandY[cell.Row - ExtGoalieRowBase], 16, 20);
        // Injured-writhe tiles are ordinary 16×16 cells sitting on the y=32 row
        // (grid row 2, cols 7-10); they only ride the ext machinery so they get
        // an absolute, facing-independent anchor like the throw-in/dive frames.
        if (cell.Row >= ExtWritheRowBase)
            return (cell.Col * 16, 32, 16, 16);
        return (cell.Col * 16, 32 + 24 * (cell.Row - ExtThrowRowBase), 16, 24);
    }

    public static System.Collections.Generic.IEnumerable<(int Col, int Row)> AllOutfieldExtCells()
    {
        for (int rel = 24; rel <= 53; rel++)
        {
            var c = OutfieldExtCell(rel);
            if (c.HasValue) yield return c.Value;
        }
    }

    public static System.Collections.Generic.IEnumerable<(int Col, int Row)> AllGoalieExtCells()
    {
        for (int rel = 24; rel <= 57; rel++)
        {
            var c = GoalieExtCell(rel);
            if (c.HasValue) yield return c.Value;
        }
    }

    // The four injured-writhe tiles (CJCTEAM* row 2, cols 7-10 → ordinals
    // 411-414 team1 / 714-717 team2). Baked per-kit/per-face like the other
    // outfield frames so an injured player lies recoloured in his team kit.
    public static System.Collections.Generic.IEnumerable<(int Col, int Row)> AllWritheCells()
    {
        for (int col = 7; col <= 10; col++)
            yield return (col, ExtWritheRowBase);
    }

    // ============================================================================
    // Anchor offsets — fixes the "run-cycle wobble" by snapping the sprite onto
    // the per-frame anchor instead of the geometric cell center.
    //
    // Source: Swos9697_PC/SensiWs9/SOC/TEAM1.DAT sprite headers (extracted by
    //   tools/sprite-anchors-extract). Sprite ordinals from
    //   external/swos-port/swos/swos.asm `team1PlayerStandingFacingUpFrames`
    //   block (line 218368-218410) and `playerRunningUpTeam1` block.
    //
    // The PC values are the SWOS `center_x, center_y` fields (range cx ∈ [-8..34],
    // cy ∈ [0..27]). We use them as DELTAS relative to the direction's standing
    // pose: a non-zero delta shifts the Amiga 16×16 tile in the opposite direction
    // so the sprite's anchor point stays at the player's world position.
    //
    // We borrow PC anchors for the Amiga atlas on the assumption that the same
    // artist designed both versions with identical foot-anchor positions in each
    // pose. Visual smoke-test on the playable build confirms the wobble is much
    // reduced; absolute SWOS-perfect Amiga values are a follow-up (TBD whether
    // CJCBITS/CJCTEAM has its own anchor table — see porting doc).
    //
    // (col, row) is the atlas-grid coordinate (matches Standing/RunFrame/etc).
    // Standing-pose anchor (cx, cy) per direction — sourced from PC TEAM1.DAT sprite
    // headers (see AnchorOffset comments below). Each (cx, cy) is the foot/grass-contact
    // pixel inside the 16×16 sprite tile. The renderer uses this to align the sprite so
    // the foot anchor sits exactly on the PlayerState world position.
    public static (int Cx, int Cy) StandingAnchor(Direction d) => d switch
    {
        Direction.North     => (4, 12),
        Direction.South     => (4, 12),
        Direction.East      => (2, 12),
        Direction.West      => (2, 12),
        Direction.NorthEast => (2, 12),
        Direction.SouthEast => (2, 12),
        Direction.NorthWest => (1, 12),
        Direction.SouthWest => (1, 12),
        _ => (8, 8),
    };

    public static (int OffsetX, int OffsetY) AnchorOffset(int col, int row)
    {
        // Extended (synthetic) cells — throw-in/cheer (16×24) and goalie
        // dive/catch (16×20) frames. These now carry AUTHENTIC per-frame
        // anchors via ExtAnchor(); the caller (Main.UpdateSprite) reads
        // ExtAnchor and rewrites (ox,oy) accordingly, so this global value is
        // unused for ext cells. Returning (0,0) keeps the raw formula neutral
        // if a caller ever forgets the ExtAnchor override.
        if (row >= ExtThrowRowBase)
            return (0, 0);
        // Per-cell delta in PIXELS to subtract from sprite position to keep the
        // foot/hip anchor steady. Computed from PC sprite headers as follows:
        //   delta = (pc_centerX_of_cell - pc_centerX_of_standing_cell_for_dir,
        //            pc_centerY_of_cell - pc_centerY_of_standing_cell_for_dir)
        //
        // CARDINALS (row 0): standing = first cell of triplet, anchor (cx=4 / 4 / 2 / 2, cy=12).
        //   N (cols 0..2): 341 (4,12) → (5,12) → (3,12)   ⇒ Δ = (0,0) (+1,0) (-1,0)
        //   S (cols 3..5): 344 (4,12) → (3,12) → (5,12)   ⇒ Δ = (0,0) (-1,0) (+1,0)
        //   E (cols 6..8): 347 (2,12) → (4,12) → (3,12)   ⇒ Δ = (0,0) (+2,0) (+1,0)
        //   W (cols 9..11): 350 (2,12) → (3,12) → (3,12)  ⇒ Δ = (0,0) (+1,0) (+1,0)
        //   Slides (cols 12..19): anchored at (cx,cy) wildly different — see big-shifts table.
        //
        // DIAGONALS (row 1): standing = MIDDLE cell of triplet.
        //   SW (cols 0..2): 353 (2,12), 354 (1,12) [stand], 355 (3,12) ⇒ Δ = (+1,0) (0,0) (+2,0)
        //   SE (cols 3..5): 356 (2,12), 357 (2,12) [stand], 358 (3,12) ⇒ Δ = (0,0) (0,0) (+1,0)
        //   NW (cols 6..8): 359 (3,12), 360 (1,12) [stand], 361 (2,12) ⇒ Δ = (+2,0) (0,0) (+1,0)
        //   NE (cols 9..11): 362 (3,12), 363 (2,12) [stand], 364 (2,12) ⇒ Δ = (+1,0) (0,0) (0,0)
        //   Fallen (cols 12..19) — anchored hugely off-cell (legs sprawled). Big-shifts table.

        if (row == 0)
        {
            return col switch
            {
                // North run cycle (standing at col 0).
                0  => ( 0, 0),
                1  => ( 1, 0),
                2  => (-1, 0),
                // South run cycle (standing at col 3).
                3  => ( 0, 0),
                4  => (-1, 0),
                5  => ( 1, 0),
                // East run cycle (standing at col 6).
                6  => ( 0, 0),
                7  => ( 2, 0),
                8  => ( 1, 0),
                // West run cycle (standing at col 9).
                9  => ( 0, 0),
                10 => ( 1, 0),
                11 => ( 1, 0),
                // Slide-tackle poses — order in atlas: N, S, W, E, SW, SE, NW, NE.
                // Reference anchor for slides is the corresponding stand pose for that
                // direction (so slide → stand transitions are smooth). PC TEAM1.DAT:
                //   stand N=(4,12), tackle N=(4,3)      ⇒ Δ = ( 0, -9)
                //   stand S=(4,12), tackle S=(4,10)     ⇒ Δ = ( 0, -2)
                //   stand W=(2,12), tackle W=(3,8)      ⇒ Δ = ( 1, -4)
                //   stand E=(2,12), tackle E=(8,8)      ⇒ Δ = ( 6, -4)
                //   stand SW=(1,12), tackle SW=(3,7)    ⇒ Δ = ( 2, -5)
                //   stand SE=(2,12), tackle SE=(6,7)    ⇒ Δ = ( 4, -5)
                //   stand NW=(1,12), tackle NW=(4,3)    ⇒ Δ = ( 3, -9)
                //   stand NE=(2,12), tackle NE=(7,3)    ⇒ Δ = ( 5, -9)
                12 => ( 0, -9),
                13 => ( 0, -2),
                14 => ( 1, -4),
                15 => ( 6, -4),
                16 => ( 2, -5),
                17 => ( 4, -5),
                18 => ( 3, -9),
                19 => ( 5, -9),
                _  => ( 0, 0),
            };
        }
        if (row == 1)
        {
            return col switch
            {
                // SW run cycle (standing at col 1).
                0  => ( 1, 0),
                1  => ( 0, 0),
                2  => ( 2, 0),
                // SE run cycle (standing at col 4).
                3  => ( 0, 0),
                4  => ( 0, 0),
                5  => ( 1, 0),
                // NW run cycle (standing at col 7).
                6  => ( 2, 0),
                7  => ( 0, 0),
                8  => ( 1, 0),
                // NE run cycle (standing at col 10).
                9  => ( 1, 0),
                10 => ( 0, 0),
                11 => ( 0, 0),
                // Fallen poses — order: N, S, W, E, SW, SE, NW, NE.
                // Reference anchor is the direction's standing pose. PC TEAM1.DAT:
                //   stand N=(4,12), fallen N=(4,13)     ⇒ Δ = ( 0,  1)
                //   stand S=(4,12), fallen S=(4, 0)     ⇒ Δ = ( 0,-12)
                //   stand W=(2,12), fallen W=(13,5)     ⇒ Δ = (11, -7)
                //   stand E=(2,12), fallen E=(0, 5)     ⇒ Δ = (-2, -7)
                //   stand SW=(1,12), fallen SW=(10,1)   ⇒ Δ = ( 9,-11)
                //   stand SE=(2,12), fallen SE=(2, 1)   ⇒ Δ = ( 0,-11)
                //   stand NW=(1,12), fallen NW=(11,9)   ⇒ Δ = (10, -3)
                //   stand NE=(2,12), fallen NE=(1, 9)   ⇒ Δ = (-1, -3)
                12 => ( 0,   1),
                13 => ( 0, -12),
                14 => (11,  -7),
                15 => (-2,  -7),
                16 => ( 9, -11),
                17 => ( 0, -11),
                18 => (10,  -3),
                19 => (-1,  -3),
                _  => ( 0, 0),
            };
        }
        return (0, 0);
    }
}
