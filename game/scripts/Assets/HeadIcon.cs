namespace OpenSwos.Assets;

using Godot;

// ============================================================================
// The 7x7 player bust used next to every player name.
//
// Extracted from MenuClient.Career.cs so the web career client renders exactly
// the same pixels as the in-game rows — a second implementation would be the
// one thing guaranteed to look "almost right" and drift.
//
// Primary source is the real Amiga player atlas (CJCTEAM1.RAW), kit- and
// face-recoloured, cropped to the standing-South bust. When the atlas is not
// present (bring-your-own-assets install) a procedural bust is drawn from the
// EXACT palette entries the recolour would have produced, so the two are
// visually consistent.
// ============================================================================

public static class HeadIcon
{
    public const int W = 7;
    public const int H = 7;

    private static AmigaSpriteAtlas? s_atlas;
    private static bool s_tried;

    /// <summary>Loads the shared player atlas once; null when unavailable.</summary>
    private static AmigaSpriteAtlas? Atlas()
    {
        if (s_tried) return s_atlas;
        s_tried = true;
        try
        {
            string dir = DataPaths.AmigaGrafsDir();
            if (dir.Length > 0)
            {
                // Case-insensitive resolve: case-SENSITIVE Linux/Android/R36S
                // filesystems may hold this as cjcteam1.raw. "" means absent.
                string path = DataPaths.ResolveFile(dir, "CJCTEAM1.RAW");
                if (path.Length > 0) s_atlas = AmigaSpriteAtlas.Load(path);
            }
        }
        catch { s_atlas = null; }
        return s_atlas;
    }

    /// <summary>
    /// The bust for a face type (0 WHITE, 1 GINGER, 2 BLACK) wearing a club kit.
    /// </summary>
    public static Image Build(int face, byte[] kit)
    {
        var atlas = Atlas();
        if (atlas is not null)
        {
            try
            {
                var a = atlas.WithKitRecolour(kit).WithFaceRecolour(face);
                // Standing South cell (col 3, row 0) -> pixel x0 = 48; take cols 1-7.
                return a.GetRegion(48 + 1, 0, W, H);
            }
            catch { /* fall through to procedural */ }
        }
        return Procedural(face, kit);
    }

    /// <summary>
    /// Fallback bust: a flat-shaded 7x7 head using the EXACT palette colours the
    /// atlas recolour would apply — KitPalette.ApplyFace over the sprite palette,
    /// reading the skin ramp (slot 5) and per-face hair ramp (slot 13), with the
    /// club shirt colour on the shoulders.
    /// </summary>
    public static Image Procedural(int face, byte[] kit)
    {
        byte[] pal = KitPalette.ApplyFace(OpenSwos.Tools.SpriteDecode.Palette.SwosAmigaSprite(), face);
        (byte r, byte g, byte b) Slot(int i) => (pal[i * 3], pal[i * 3 + 1], pal[i * 3 + 2]);
        var (sr, sg, sb) = Slot(5);            // mid skin shade
        var (hr, hg, hb) = Slot(13);           // hair (per-face recoloured)
        // Shoulders take the club shirt BODY colour (matching KitPalette.Apply's
        // body-vs-accent rule), so the procedural bust wears the club kit too;
        // free agents (grey kit) still read grey.
        byte shirtType = kit.Length > 0 ? kit[0] : (byte)0;
        byte stripes = kit.Length > 1 ? kit[1] : (byte)0;
        byte basic = kit.Length > 2 ? kit[2] : stripes;
        byte body = (shirtType == 1 || shirtType == 3) ? stripes : basic;
        var (kr, kg, kb) = KitPalette.Get(body);
        // 7x7 mask: 0 transparent, 1 hair, 2 skin, 3 shoulder.
        int[,] m =
        {
            {0,1,1,1,1,1,0},
            {0,1,1,1,1,1,0},
            {0,1,2,2,2,1,0},
            {0,0,2,2,2,0,0},
            {0,3,2,2,2,3,0},
            {3,3,3,3,3,3,3},
            {0,3,3,3,3,3,0},
        };
        var bytes = new byte[W * H * 4];
        for (int yy = 0; yy < H; yy++)
            for (int xx = 0; xx < W; xx++)
            {
                int o = (yy * W + xx) * 4;
                switch (m[yy, xx])
                {
                    case 1: bytes[o] = hr; bytes[o + 1] = hg; bytes[o + 2] = hb; bytes[o + 3] = 255; break;
                    case 2: bytes[o] = sr; bytes[o + 1] = sg; bytes[o + 2] = sb; bytes[o + 3] = 255; break;
                    case 3: bytes[o] = kr; bytes[o + 1] = kg; bytes[o + 2] = kb; bytes[o + 3] = 255; break;
                    default: bytes[o + 3] = 0; break;
                }
            }
        return Image.CreateFromData(W, H, false, Image.Format.Rgba8, bytes);
    }
}
