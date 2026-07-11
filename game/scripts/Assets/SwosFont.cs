using Godot;
using OpenSwos.Tools.RncDecode;
using OpenSwos.Tools.SpriteDecode;

namespace OpenSwos.Assets;

// Bitmap-font renderer for the SWOS Amiga in-game charset (CHARSET.RAW).
//
// CHARSET.RAW is RNC v2-compressed; decompressed it is a 320x256, 4-bitplane
// row-interleaved Amiga image (same container as every other CJC*.RAW atlas —
// see AmigaSpriteAtlas). It stores TWO proportional fonts laid out as ASCII
// grids, 16 columns wide (16 px column pitch). For a character c:
//
//   index = c - 32;  col = index % 16;  row = index / 16
//   small font: cellX = col*16, cellY =        row*8,  glyph height 6
//   big   font: cellX = col*16, cellY = 112 +  row*8,  glyph height 8
//
// Grid origins, pitches and the 16-wide ASCII arrangement were recovered by
// measuring the decoded atlas (2026-07-03): small-font rows begin at y=0 with
// an 8 px pitch, the big-font block begins at y=112; both use a 16 px column
// pitch, and rows 0..3 hold the printable ASCII range 32..95 (uppercase,
// digits, punctuation) exactly at their ASCII offsets. Verified by rendering
// "BERGKAMP 23" (small) and "ARSENAL" (big) back and comparing to the atlas.
//
// Glyph metrics mirror external/swos-port/src/text/text.cpp:
//   - font heights: text.h kSmallFontHeight=6 / kBigFontHeight=8.
//   - space/tab advances: kSmallFontSpace=3, kBigFontSpace=4, kTabSpace=5.
//   - proportional advance for a real glyph = its bitmap width + 1 trailing
//     blank column (text.cpp advances by getSprite(index).width, which bakes
//     the trailing blank into the stored sprite width; we recover it by
//     scanning the cell's inked columns and adding the 1 px gap).
//
// The atlas font is uppercase-only (charToSprite folds lowercase onto the same
// glyphs; the lowercase cells hold the "undefined" box). We uppercase every
// string, matching SWOS's toUpper HUD convention.
public sealed class SwosFont
{
    public const int SmallHeight = 6;   // text.h kSmallFontHeight
    public const int BigHeight   = 8;   // text.h kBigFontHeight
    private const int SmallSpace = 3;   // text.cpp kSmallFontSpace
    private const int BigSpace   = 4;   // text.cpp kBigFontSpace
    private const int TabSpace   = 5;   // text.cpp kTabSpace
    private const int BigBlockTopY = 112;
    private const int ColPitch = 16;
    private const int RowPitch = 8;

    private const int AtlasWidth = 320;
    private const int AtlasHeight = 256;
    private const int Planes = 4;

    private readonly byte[,] _ink;              // [256,320]: nonzero = glyph pixel
    private readonly int[] _smallInkW = new int[96]; // ASCII 32..127 ink widths
    private readonly int[] _bigInkW   = new int[96];

    // Cache rendered strings so we do not re-bake identical textures every frame.
    private readonly System.Collections.Generic.Dictionary<string, ImageTexture> _cache = new();

    // The charset glyphs carry a BAKED-IN drop shadow: the letter face is one
    // palette index, the shadow pixels another. Rendering every nonzero index
    // in the text colour doubles the glyph (white ghost + shadow shape) — the
    // face index must be recoloured, the rest stays black.
    private readonly byte _smallFaceIdx;
    private readonly byte _bigFaceIdx;

    private SwosFont(byte[,] ink)
    {
        _ink = ink;
        for (int i = 0; i < 96; i++)
        {
            _smallInkW[i] = MeasureCell((char)(32 + i), false);
            _bigInkW[i]   = MeasureCell((char)(32 + i), true);
        }
        // Measured 2026-07-03: glyph cells use exactly two nonzero indices —
        // 2 = letter face, 8 = baked drop shadow (both fonts).
        _smallFaceIdx = DetectFaceIndex(false);
        _bigFaceIdx   = DetectFaceIndex(true);
    }

    // The face index = palette index of the top-left-most lit pixel of 'A'
    // (the baked shadow sits +1,+1 below/right of the face, so the first lit
    // pixel in reading order always belongs to the face).
    private byte DetectFaceIndex(bool big)
    {
        int i = 'A' - 32;
        int col = i % 16, row = i / 16;
        int cx = col * ColPitch;
        int cy = big ? BigBlockTopY + row * RowPitch : row * RowPitch;
        int h = big ? BigHeight : SmallHeight;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < ColPitch; xx++)
                if (_ink[cy + yy, cx + xx] != 0) return _ink[cy + yy, cx + xx];
        return 0;
    }

    public static SwosFont Load(string charsetPath)
    {
        byte[] packed = System.IO.File.ReadAllBytes(charsetPath);
        byte[] payload = Rnc.Decode(packed);
        var indices = Bitplane.Decode(payload, 0, AtlasWidth, AtlasHeight, Planes, BitplaneLayout.Interleaved);
        return new SwosFont(indices);
    }

    public int Height(bool big) => big ? BigHeight : SmallHeight;

    // Advance width (in SWOS pixels) of a single character.
    private int Advance(char c, bool big)
    {
        if (c == ' ') return big ? BigSpace : SmallSpace;
        if (c == '\t') return TabSpace;
        int i = c - 32;
        if (i < 0 || i >= 96) return 0;
        int ink = big ? _bigInkW[i] : _smallInkW[i];
        return ink > 0 ? ink + 1 : 0;
    }

    public int Measure(string text, bool big)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        text = text.ToUpperInvariant();
        int w = 0;
        foreach (char c in text) w += Advance(c, big);
        return w;
    }

    // Scans a glyph cell for its inked-column bounding width (rightmost lit
    // column + 1). Returns 0 for a blank cell.
    private int MeasureCell(char c, bool big)
    {
        int i = c - 32;
        if (i < 0 || i >= 96) return 0;
        int col = i % 16, row = i / 16;
        int cx = col * ColPitch;
        int cy = big ? BigBlockTopY + row * RowPitch : row * RowPitch;
        int h = big ? BigHeight : SmallHeight;
        if (cx + ColPitch > AtlasWidth || cy + h > AtlasHeight) return 0;
        int w = 0;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < ColPitch; xx++)
                if (_ink[cy + yy, cx + xx] != 0) w = System.Math.Max(w, xx + 1);
        return w;
    }

    // Renders a string to a NEAREST-filtered ImageTexture, transparent
    // background, at 1:1 SWOS pixels. The glyph FACE pixels take `color`;
    // the glyphs' baked-in drop-shadow pixels stay BLACK (authentic SWOS
    // look, readable over the pitch). Cached by (text,big,colour).
    public ImageTexture? Render(string text, bool big, Color color)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = text.ToUpperInvariant();
        string key = (big ? "B|" : "S|") + color.ToRgba32().ToString("X8") + "|" + text;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        int w = Measure(text, big);
        int h = big ? BigHeight : SmallHeight;
        if (w <= 0) return null;

        byte r = (byte)color.R8, g = (byte)color.G8, b = (byte)color.B8, a = (byte)color.A8;
        byte faceIdx = big ? _bigFaceIdx : _smallFaceIdx;
        var bytes = new byte[w * h * 4];

        int penX = 0;
        foreach (char c in text)
        {
            if (c == ' ' || c == '\t') { penX += Advance(c, big); continue; }
            int i = c - 32;
            if (i < 0 || i >= 96) continue;
            int inkW = big ? _bigInkW[i] : _smallInkW[i];
            if (inkW <= 0) continue;
            int col = i % 16, row = i / 16;
            int cx = col * ColPitch;
            int cy = big ? BigBlockTopY + row * RowPitch : row * RowPitch;
            for (int yy = 0; yy < h; yy++)
            {
                for (int xx = 0; xx < inkW; xx++)
                {
                    byte idx = _ink[cy + yy, cx + xx];
                    if (idx == 0) continue;
                    int o = (yy * w + (penX + xx)) * 4;
                    if (idx == faceIdx)
                    {
                        bytes[o + 0] = r; bytes[o + 1] = g; bytes[o + 2] = b;
                    }
                    // else: baked shadow pixel — leave RGB black.
                    bytes[o + 3] = a;
                }
            }
            penX += inkW + 1;
        }

        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
        var tex = ImageTexture.CreateFromImage(img);
        // Bound the cache so a long match cannot leak textures indefinitely.
        if (_cache.Count > 512) _cache.Clear();
        _cache[key] = tex;
        return tex;
    }
}
