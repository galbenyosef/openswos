namespace OpenSwos.Tools.SpriteDecode;

public static class Palette
{
    // SWOS Amiga 16-colour default palette, sourced from `base_palette.dat` in
    // starwindz/bmp-to-raw-for-amiga-swos — that file is the canonical palette the
    // BMP→RAW converter assumes when packing Amiga sprite atlases. Values are stored
    // there as BMP-format BGRA quads (one per palette entry):
    //
    //   00 66 33 00  00 99 99 99  00 FF FF FF  00 00 00 00
    //   00 11 22 77  00 00 44 AA  00 11 77 FF  00 00 55 22
    //   00 00 33 00  00 00 77 33  00 00 00 FF  00 00 FF 00
    //   00 00 22 00  00 77 00 FF  00 88 88 00  00 00 88 33
    //
    // Decoded to (R, G, B) and annotated with the SWOS slot meaning:
    //
    //   0  dark blue  (#003366)  outline / text dark
    //   1  grey       (#999999)  outline / text mid
    //   2  white      (#FFFFFF)  outline highlight
    //   3  black      (#000000)  outline
    //   4  skin dark  (#772211)
    //   5  skin mid   (#AA4400)
    //   6  skin light (#FF7711)
    //   7  transparent (#225500) — green-marker in BMP, treat as alpha=0 in renders
    //   8  outline2   (#003300)
    //   9  hair brown (#337700)
    //   10 SHIRT body (#FF0000)  — KIT SLOT (remapped per team)
    //   11 SHIRT stripes (#00FF00) — KIT SLOT
    //   12 hair dark  (#002200)
    //   13 hair red   (#FF0077)
    //   14 SHORTS     (#008888)  — KIT SLOT
    //   15 SOCKS      (#338800)  — KIT SLOT
    // Canonical SWOS Amiga 16-colour palette as used by the PITCH (grass, lines, crowd,
    // stands). Slot 9 = medium green for grass — sharing this slot with player sprites
    // was an Amiga-era trick (palette swap mid-scanline). On modern 24-bit displays we
    // use TWO palettes: this one for pitch, SwosAmigaSprite() for player atlases.
    public static byte[] SwosAmigaDefault()
    {
        var p = new byte[16 * 3];
        ReadOnlySpan<int> rgb24 = new[]
        {
            0x003366, 0x999999, 0xFFFFFF, 0x000000,
            0x772211, 0xAA4400, 0xFF7711, 0x225500,
            0x003300, 0x337700, 0xFF0000, 0x00FF00,
            0x002200, 0xFF0077, 0x008888, 0x338800,
        };
        for (int i = 0; i < 16; i++)
        {
            p[i * 3 + 0] = (byte)((rgb24[i] >> 16) & 0xFF);
            p[i * 3 + 1] = (byte)((rgb24[i] >>  8) & 0xFF);
            p[i * 3 + 2] = (byte)( rgb24[i]        & 0xFF);
        }
        return p;
    }

    // Index 7 is the transparency marker in SWOS atlases.
    public const byte SwosTransparentIndex = 7;

    // Sprite-specific palette: same as SwosAmigaDefault for slots 0..8, 10, 11, 14, 15
    // (outlines, skin, kit slots) but slots 9/12/13 hold actual hair tones rather than
    // the green/dark-green/pink that the shared-palette default has there. This mirrors
    // how original SWOS Amiga palette-swapped between drawing pitch and drawing players.
    public static byte[] SwosAmigaSprite()
    {
        var p = SwosAmigaDefault();
        // slot 9: hair brown
        p[9 * 3 + 0] = 0x88; p[9 * 3 + 1] = 0x44; p[9 * 3 + 2] = 0x11;
        // slot 12: hair dark brown / black
        p[12 * 3 + 0] = 0x22; p[12 * 3 + 1] = 0x11; p[12 * 3 + 2] = 0x00;
        // slot 13: blond / light brown
        p[13 * 3 + 0] = 0xDD; p[13 * 3 + 1] = 0xAA; p[13 * 3 + 2] = 0x77;
        return p;
    }

    // 32-entry debug palette: index 0 = magenta (transparency marker), 1..31 = visually
    // distinct hue ramp so palette indices are eyeball-readable even before we recover
    // the real SWOS palette from the m68k binary.
    public static byte[] Debug32()
    {
        var p = new byte[32 * 3];
        p[0] = 255; p[1] = 0; p[2] = 255; // magenta

        for (int i = 1; i < 32; i++)
        {
            double hue = ((i - 1) * 360.0) / 31.0;
            double sat = 0.85;
            double light = (i < 16) ? 0.45 : 0.65;
            (byte r, byte g, byte b) = HslToRgb(hue, sat, light);
            p[i * 3 + 0] = r;
            p[i * 3 + 1] = g;
            p[i * 3 + 2] = b;
        }
        return p;
    }

    // Decode an Amiga ECS-format palette block: 32 × uint16 BE, each entry RGB444
    // packed as 0x0RGB. Output is 32 × 3 RGB bytes scaled to 0..255.
    public static byte[] FromAmigaEcs(ReadOnlySpan<byte> raw, int offset)
    {
        var p = new byte[32 * 3];
        for (int i = 0; i < 32; i++)
        {
            int b0 = raw[offset + i * 2];
            int b1 = raw[offset + i * 2 + 1];
            int word = (b0 << 8) | b1;
            int r4 = (word >> 8) & 0xF;
            int g4 = (word >> 4) & 0xF;
            int b4 = word & 0xF;
            p[i * 3 + 0] = (byte)((r4 << 4) | r4);
            p[i * 3 + 1] = (byte)((g4 << 4) | g4);
            p[i * 3 + 2] = (byte)((b4 << 4) | b4);
        }
        return p;
    }

    private static (byte r, byte g, byte b) HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hh = h / 60.0;
        double x = c * (1 - Math.Abs(hh % 2 - 1));
        double r1, g1, b1;
        if (hh < 1) { r1 = c; g1 = x; b1 = 0; }
        else if (hh < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hh < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hh < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hh < 5) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        double m = l - c / 2;
        return (
            (byte)Math.Clamp((r1 + m) * 255, 0, 255),
            (byte)Math.Clamp((g1 + m) * 255, 0, 255),
            (byte)Math.Clamp((b1 + m) * 255, 0, 255));
    }
}
