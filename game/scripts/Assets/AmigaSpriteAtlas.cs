using Godot;
using OpenSwos.Tools.RncDecode;
using OpenSwos.Tools.SpriteDecode;

namespace OpenSwos.Assets;

// Loads an Amiga player-sprite atlas (CJCTEAM*.RAW, CJCTEAMG.RAW, CJCBENCH.RAW, ...).
// Steps:
//   1. Read the file bytes (RNC v2 compressed, ~6-7 KB).
//   2. Decompress via Rnc.Decode → 40 960-byte raw payload.
//   3. Decode the 320×256 × 4-bitplane interleaved image into a [256, 320] palette-index grid.
//   4. Convert to a Godot Image (RGBA8) using SwosAmigaDefault palette.
public sealed class AmigaSpriteAtlas
{
    public const int AtlasWidth = 320;
    public const int AtlasHeight = 256;
    public const int Planes = 4;

    public byte[,] Indices { get; }
    public byte[] PaletteRgb { get; }

    private AmigaSpriteAtlas(byte[,] indices, byte[] paletteRgb)
    {
        Indices = indices;
        PaletteRgb = paletteRgb;
    }

    public static AmigaSpriteAtlas Load(string path)
    {
        byte[] packed = System.IO.File.ReadAllBytes(path);
        byte[] payload = Rnc.Decode(packed);
        var indices = Bitplane.Decode(payload, 0, AtlasWidth, AtlasHeight, Planes, BitplaneLayout.Interleaved);
        // Use the SPRITE palette so slots 9/12/13 render as hair colours, leaving the
        // pitch palette free to keep those slots as grass/dark-green/pink as the original
        // SWOS Amiga did via mid-scanline palette swap.
        return new AmigaSpriteAtlas(indices, Palette.SwosAmigaSprite());
    }

    // Returns a NEW AmigaSpriteAtlas instance with the kit slots (palette indices 10, 11,
    // 14, 15) overwritten by the team's home (or away) kit colours. The raw bitplane
    // indices are unchanged — only the palette is patched, exactly how the original
    // game does runtime recolouring.
    public AmigaSpriteAtlas WithKitRecolour(byte[] kit) =>
        new(Indices, KitPalette.Apply(PaletteRgb, kit));

    // Returns a NEW atlas with the skin/hair slots remapped for a player FACE
    // type (0=WHITE, 1=GINGER, 2=BLACK) — the original per-face palette-index
    // remap (whiteGuy/gingerGuy/blackGuyConvertTable, swos.asm:218199-218215).
    // Without this pass the raw sprite palette leaves hair on the flesh/orange
    // ramp, which is why every player used to render ginger. Composes with
    // WithKitRecolour in either order (kit slots 10/11/14/15 are disjoint from
    // the face slots 4/5/6/9/12/13).
    public AmigaSpriteAtlas WithFaceRecolour(int face) =>
        new(Indices, KitPalette.ApplyFace(PaletteRgb, face));

    // Returns a Godot Image with the atlas baked at full size. SWOS Amiga player atlases
    // use **palette index 0** as the cell background (transparency marker). Empirically
    // confirmed by visual inspection: every cell's negative space is filled with index 0
    // (dark blue). Index 7 was a PC-era convention we initially mis-extrapolated.
    public Image ToImage()
    {
        var bytes = new byte[AtlasWidth * AtlasHeight * 4];
        for (int y = 0; y < AtlasHeight; y++)
        {
            for (int x = 0; x < AtlasWidth; x++)
            {
                byte idx = Indices[y, x];
                int o = (y * AtlasWidth + x) * 4;
                if (idx == 0)
                {
                    bytes[o + 3] = 0;
                }
                else
                {
                    int p = idx * 3;
                    bytes[o + 0] = PaletteRgb[p + 0];
                    bytes[o + 1] = PaletteRgb[p + 1];
                    bytes[o + 2] = PaletteRgb[p + 2];
                    bytes[o + 3] = 255;
                }
            }
        }
        return Image.CreateFromData(AtlasWidth, AtlasHeight, false, Image.Format.Rgba8, bytes);
    }

    // Extract a single 16×16 cell from the atlas as a Godot Image.
    // The atlas is a 20×16 grid (0..19, 0..15) — tileX in [0..19], tileY in [0..15].
    public Image GetTile(int tileX, int tileY, int tileSize = 16)
    {
        if (tileX < 0 || tileY < 0 || tileX * tileSize + tileSize > AtlasWidth
            || tileY * tileSize + tileSize > AtlasHeight)
            throw new System.ArgumentOutOfRangeException(
                $"tile ({tileX}, {tileY}) out of {AtlasWidth / tileSize}x{AtlasHeight / tileSize} grid");

        var bytes = new byte[tileSize * tileSize * 4];
        int x0 = tileX * tileSize;
        int y0 = tileY * tileSize;
        for (int dy = 0; dy < tileSize; dy++)
        {
            for (int dx = 0; dx < tileSize; dx++)
            {
                byte idx = IsNeighbourOverflowPixel(tileX, tileY, dx, dy, tileSize)
                    ? (byte)0
                    : Indices[y0 + dy, x0 + dx];
                int o = (dy * tileSize + dx) * 4;
                if (idx == 0)
                {
                    bytes[o + 3] = 0;
                }
                else
                {
                    int p = idx * 3;
                    bytes[o + 0] = PaletteRgb[p + 0];
                    bytes[o + 1] = PaletteRgb[p + 1];
                    bytes[o + 2] = PaletteRgb[p + 2];
                    bytes[o + 3] = 255;
                }
            }
        }
        return Image.CreateFromData(tileSize, tileSize, false, Image.Format.Rgba8, bytes);
    }

    // Extract an arbitrary pixel rectangle from the atlas as a Godot Image
    // (index 0 → transparent, like GetTile). Used for the non-16×16 frames:
    // the throw-in / cheer bands of CJCTEAM* (16×24 on a 24-px band pitch)
    // and the goalkeeper dive / catch bands of CJCTEAMG (16×20), plus the
    // two goal overlay pieces of CJCBITS — see PlayerFrames.ExtRect and
    // Main.CreateGoalOverlays.
    public Image GetRegion(int x0, int y0, int w, int h)
    {
        if (x0 < 0 || y0 < 0 || w <= 0 || h <= 0 || x0 + w > AtlasWidth || y0 + h > AtlasHeight)
            throw new System.ArgumentOutOfRangeException(
                $"region ({x0},{y0},{w},{h}) outside {AtlasWidth}x{AtlasHeight} atlas");

        var bytes = new byte[w * h * 4];
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                byte idx = Indices[y0 + dy, x0 + dx];
                int o = (dy * w + dx) * 4;
                if (idx == 0)
                {
                    bytes[o + 3] = 0;
                }
                else
                {
                    int p = idx * 3;
                    bytes[o + 0] = PaletteRgb[p + 0];
                    bytes[o + 1] = PaletteRgb[p + 1];
                    bytes[o + 2] = PaletteRgb[p + 2];
                    bytes[o + 3] = 255;
                }
            }
        }
        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
    }

    // Known art-overflow between vertically adjacent cells in the CJCTEAM1/2/3 player
    // atlases: the fallen-W sprite in cell (14,1) has the top of its hair drawn one
    // pixel ABOVE its cell — atlas pixels (x=227..228, y=15), palette index 13 — so a
    // strict 16×16 cut of cell (14,0), the W (left) slide-tackle, shows a stray 2-px
    // dark "line" floating under the sliding player. Those pixels belong to the sprite
    // below, so we drop them when cutting (14,0). Verified identical in all three
    // CJCTEAM atlases via a full-atlas overflow scan (2026-07-02); rows 0 and 1 — the
    // only rows PlayerFrames uses — contain no other instance.
    //
    // The trim is guarded by a content check (art continues into the cell below, and is
    // disconnected from anything above inside this tile), so other atlases routed
    // through this class (CJCBITS ball, CJCTEAMG goalie, CJCBENCH) are unaffected even
    // if this cell is ever requested from them. The complementary loss — the fallen-W
    // tile (14,1) missing its topmost hair row — was already the case before this trim
    // (the strict cut never included atlas row y=15) and is visually negligible.
    private bool IsNeighbourOverflowPixel(int tileX, int tileY, int dx, int dy, int tileSize)
    {
        if (tileSize != 16) return false;
        if (tileX != 14 || tileY != 0 || dy != 15 || (dx != 3 && dx != 4)) return false;
        int x = tileX * 16 + dx;
        int y = tileY * 16 + dy;
        return Indices[y, x] != 0        // pixel present on our bottom row,
            && Indices[y + 1, x] != 0    // continues into the cell below,
            && Indices[y - 1, x] == 0    // and floats free of this tile's own art.
            && Indices[y - 2, x] == 0;
    }
}
