namespace OpenSwos.Assets;

// SWOS kit colours: a 10-entry named palette referenced by name index in the TEAM.*
// kit bytes. Values are approximate sRGB equivalents of the in-game look; can be
// refined later by sampling actual game screenshots.
public static class KitPalette
{
    private static readonly (byte r, byte g, byte b)[] Colours =
    {
        (0x80, 0x80, 0x80), // 0 grey
        (0xFF, 0xFF, 0xFF), // 1 white
        (0x00, 0x00, 0x00), // 2 black
        (0xFF, 0x80, 0x00), // 3 orange
        (0xC0, 0x00, 0x00), // 4 red
        (0x00, 0x40, 0xFF), // 5 blue
        (0x80, 0x40, 0x00), // 6 brown
        (0x80, 0xC0, 0xFF), // 7 light blue
        (0x00, 0xA0, 0x00), // 8 green
        (0xFF, 0xFF, 0x00), // 9 yellow
    };

    public static (byte r, byte g, byte b) Get(byte name) =>
        name < Colours.Length ? Colours[name] : ((byte)0xFF, (byte)0x00, (byte)0xFF); // magenta = unknown

    // Apply a kit onto a 16-entry sprite palette. `kit` is the canonical SWOS
    // team-file layout {shirtType, stripesColor, basicColor, shortsColor,
    // socksColor} (swos.h:245-249). Slot 10 = shirt body, slot 11 = stripes/
    // sleeves accent, 14 = shorts, 15 = socks.
    //
    // Body vs accent per shirt type (derived from swos-port pastePlayerShirtLayer
    // colorizeSprites.cpp:484-508: baseColor=prBasicColor, stripesColor=
    // prStripesColor, blended per-pixel; the sleeve/horizontal sprite thirds
    // carry the body in the stripes channel). Calibrated against the reference
    // team ARSENAL (type=1 sleeves, stripes=4 red, basic=1 white) which must
    // render RED body + WHITE sleeves:
    //   sleeves (1) / horizontal (3): body = stripes, accent = basic
    //   ordinary (0) / vertical (2):  body = basic,   accent = stripes
    // (horizontal is the one type without a verified reference team — the
    // swos-port swap says body=stripes for it; flagged PORT-VISUAL.)
    public static byte[] Apply(byte[] basePaletteRgb, byte[] kit)
    {
        var p = (byte[])basePaletteRgb.Clone();
        if (kit.Length < 2) return p;

        byte type    = kit[0];
        byte stripes = kit.Length > 1 ? kit[1] : (byte)0;
        byte basic   = kit.Length > 2 ? kit[2] : stripes;
        byte shorts  = kit.Length > 3 ? kit[3] : basic;
        byte socks   = kit.Length > 4 ? kit[4] : basic;

        bool bodyIsStripes = (type == 1) || (type == 3);  // sleeves / horizontal
        byte body   = bodyIsStripes ? stripes : basic;
        byte accent = bodyIsStripes ? basic   : stripes;

        SetSlot(p, 10, body);
        SetSlot(p, 11, accent);
        SetSlot(p, 14, shorts);
        SetSlot(p, 15, socks);
        return p;
    }

    // ---- Face (skin + hair) recolouring -------------------------------------
    //
    // Face types (swos-port src/swos/swos.h:436-442, enum FaceTypes):
    //   0 = WHITE, 1 = GINGER, 2 = BLACK.
    public const int FaceCount = 3;

    // Original engine face recolour tables — constants copied from
    // swos-port swos.asm:218199-218205 (whiteGuyConvertTable /
    // gingerGuyConvertTable / blackGuyConvertTable), applied at runtime by
    // FillSkinColorConversionTable (swos.asm:85739-85770). Each pair is
    // (destination palette slot, source palette colour index):
    //
    //   In the player atlas, SKIN pixels use slots 4/5/6 (dark -> light flesh
    //   shades) and HAIR pixels use slots 12/9/13 (dark -> light). Slot 3 is
    //   black. The raw atlas palette holds orange-ish tones in ALL six slots
    //   (hair shades sit next to the flesh ramp), which is exactly why an
    //   un-remapped render makes every player look ginger.
    //
    //   white  (0): skin 6<-6, 5<-5, 4<-4 (unchanged); hair 13<-3, 9<-3, 12<-3
    //               ("keeps the skin color white, the hair colors all go to
    //                black (all 3 shades)" — swos.asm:218201)
    //   ginger (1): skin unchanged; hair 13<-6, 9<-5, 12<-4
    //               ("white skin, ginger hair (notice the hair colors are the
    //                same as skin)" — swos.asm:218204)
    //   black  (2): skin 6<-4, 5<-4, 4<-3 (darkened); hair 13<-3, 9<-3, 12<-3
    //               ("black skin, black hair" — swos.asm:218207)
    //
    // These are the SAME slot conventions on both variants: the PC tables above,
    // and empirically the Amiga CJCTEAM atlas (our renderer's source) draws
    // flesh with 4/5/6 and hair with 9/12/13 — see Palette.SwosAmigaSprite
    // (tools/sprite-decode/Palette.cs:59-73) and the hair-pixel overflow note in
    // AmigaSpriteAtlas.cs (index 13 = hair). Cross-check against the swos-port
    // SDL rewrite (src/game/color.h:31-36): kSkinColor = {252,108,0} white/ginger,
    // {54,18,0} black; kHairColor = {60,60,60} white/black, {180,72,0} ginger —
    // i.e. white/ginger skin ~= game-palette slot 6 {252,100,0} and ginger hair
    // ~= slot 5 {184,68,0} (src/game/color.cpp:15-19), consistent with the remap.
    private static readonly (int slot, int src)[][] FaceRemap =
    {
        new[] { (6, 6), (5, 5), (4, 4), (13, 3), (9, 3), (12, 3) }, // 0 WHITE
        new[] { (6, 6), (5, 5), (4, 4), (13, 6), (9, 5), (12, 4) }, // 1 GINGER
        new[] { (6, 4), (5, 4), (4, 3), (13, 3), (9, 3), (12, 3) }, // 2 BLACK
    };

    // Recolor the skin + hair palette slots for a player's face type.
    // Slots + mappings from the original engine's per-face palette-index remap
    // (swos.asm:218199-218215, see FaceRemap above): instead of flattening the
    // three skin / three hair shades to a single RGB, each destination slot takes
    // the colour of its remapped source slot from the UNMODIFIED input palette —
    // pixel-identical to how SWS.EXE converted the sprites. Face values outside
    // 0..FaceCount-1 clamp to 0 (white), matching the engine's 0..2 cycle
    // (swos.asm:84819-84822).
    public static byte[] ApplyFace(byte[] paletteRgb, int face)
    {
        var p = (byte[])paletteRgb.Clone();
        if (face < 0 || face >= FaceCount) face = 0;

        foreach (var (slot, src) in FaceRemap[face])
        {
            // read from the pristine input so chained remaps (e.g. BLACK's
            // 6<-4 followed by 4<-3) never see already-overwritten slots
            p[slot * 3 + 0] = paletteRgb[src * 3 + 0];
            p[slot * 3 + 1] = paletteRgb[src * 3 + 1];
            p[slot * 3 + 2] = paletteRgb[src * 3 + 2];
        }
        return p;
    }

    private static void SetSlot(byte[] palette, int slot, byte colourName)
    {
        var (r, g, b) = Get(colourName);
        palette[slot * 3 + 0] = r;
        palette[slot * 3 + 1] = g;
        palette[slot * 3 + 2] = b;
    }
}
