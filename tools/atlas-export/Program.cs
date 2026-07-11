// atlas-export — decodes every SWOS Amiga graphics atlas the game uses and
// stacks them into ONE editable 24-bit BMP sheet (real SwosAmigaSprite
// palette, 1:1 pixels), so kit/sprite rework can happen in any image editor.
//
// Layout: sections stacked vertically, each 320 px wide, separated by an
// 8-px solid-magenta divider row (RGB 255,0,255 — a colour absent from the
// SWOS palette, easy to target when splitting the sheet back apart). Exact
// row offsets are printed to stdout AND written to a .txt next to the BMP.
//
// Usage:
//   dotnet run --project tools/atlas-export
//     [-i <grafsDir>]   default: assets/extracted/amiga/disk2/grafs
//     [-o <out.bmp>]    default: assets/extracted/graphics/swos_graphics_sheet.bmp
//
// The inputs are RNC v2-compressed 4-bitplane row-interleaved Amiga images
// (see CLAUDE.md "Asset format findings"). 40 960-byte payload = 320x256.
// Payloads of other sizes are decoded with height = size / (40 * planes)
// (320 px wide, 4 planes assumed) — good enough for eyeballing; oddballs
// are reported and skipped rather than crashing the run.

using OpenSwos.Tools.RncDecode;
using OpenSwos.Tools.SpriteDecode;

string grafsDir = Path.Combine("assets", "extracted", "amiga", "disk2", "grafs");
string outPath = Path.Combine("assets", "extracted", "graphics", "swos_graphics_sheet.bmp");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "-i" && i + 1 < args.Length) grafsDir = args[++i];
    else if (args[i] == "-o" && i + 1 < args.Length) outPath = args[++i];
}

// The atlases the game (and menus) actually draw from, in a sensible edit order.
string[] files =
{
    "CJCTEAM1.RAW",  // player atlas, kit variant 1 (the in-match one)
    "CJCTEAM2.RAW",  // player atlas, kit variant 2
    "CJCTEAM3.RAW",  // player atlas, kit variant 3
    "CJCTEAMG.RAW",  // goalkeeper atlas
    "CJCBENCH.RAW",  // bench / substitutes
    "CJCBITS.RAW",   // ball + small bits (HUD digits, mark arrow, flags)
    "CJCGRAFS.RAW",  // misc in-match graphics
    "CHARSET.RAW",   // in-game font
};

const int Width = 320;
const int Planes = 4;
const int SeparatorH = 8;

byte[] palette = Palette.SwosAmigaSprite();

var sections = new List<(string Name, byte[,] Indices)>();
foreach (string name in files)
{
    string path = Path.Combine(grafsDir, name);
    if (!File.Exists(path))
    {
        Console.WriteLine($"  SKIP {name} — not found at {Path.GetFullPath(path)}");
        continue;
    }
    byte[] raw = File.ReadAllBytes(path);
    byte[] payload;
    try
    {
        payload = Rnc.Decode(raw);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  SKIP {name} — RNC decode failed: {ex.Message}");
        continue;
    }
    int bytesPerRow = Width / 8 * Planes;              // 160
    if (payload.Length % bytesPerRow != 0)
    {
        Console.WriteLine($"  SKIP {name} — payload {payload.Length} B not a multiple of {bytesPerRow} (not 320px/4bpp?)");
        continue;
    }
    int height = payload.Length / bytesPerRow;
    byte[,] indices = Bitplane.Decode(payload, 0, Width, height, Planes, BitplaneLayout.Interleaved);
    sections.Add((name, indices));
    Console.WriteLine($"  OK   {name} — {Width}x{height} ({payload.Length} B payload)");
}

if (sections.Count == 0)
{
    Console.Error.WriteLine("No atlases decoded — nothing to write.");
    return 1;
}

// Compose one [totalH, 320] index grid. Palette has no magenta, so the
// separator is painted directly in RGB after the palette pass — use index
// 255 as the separator marker and patch it in the pixel buffer.
int totalH = sections.Sum(s => s.Indices.GetLength(0)) + SeparatorH * (sections.Count - 1);
var sheet = new byte[totalH, Width];
var offsets = new List<string>();
int y0 = 0;
for (int s = 0; s < sections.Count; s++)
{
    var (name, ind) = sections[s];
    int h = ind.GetLength(0);
    offsets.Add($"{name,-14} rows {y0,5}..{y0 + h - 1,5}  ({Width}x{h})");
    for (int y = 0; y < h; y++)
        for (int x = 0; x < Width; x++)
            sheet[y0 + y, x] = ind[y, x];
    y0 += h;
    if (s < sections.Count - 1)
    {
        for (int y = 0; y < SeparatorH; y++)
            for (int x = 0; x < Width; x++)
                sheet[y0 + y, x] = 255;   // separator marker
        y0 += SeparatorH;
    }
}

// Extend the palette so index 255 = magenta separator.
byte[] palExt = new byte[256 * 3];
Array.Copy(palette, palExt, Math.Min(palette.Length, palExt.Length));
palExt[255 * 3 + 0] = 255; palExt[255 * 3 + 1] = 0; palExt[255 * 3 + 2] = 255;

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
Bmp.Write24(outPath, sheet, palExt);

string mapPath = Path.ChangeExtension(outPath, ".offsets.txt");
File.WriteAllLines(mapPath, new[]
{
    "swos_graphics_sheet.bmp — section offsets (320 px wide, 8-px magenta dividers)",
    "Each CJCTEAM*/CJCTEAMG atlas is a 20x16 grid of 16x16 sprite cells.",
    "Kit-recolour palette slots: 10/11 = shirt, 14/15 = shorts/socks (remapped per team at runtime).",
    "Index 0 = transparent (cell background).",
    "",
}.Concat(offsets));

Console.WriteLine();
Console.WriteLine($"Sheet:   {Path.GetFullPath(outPath)}  ({Width}x{totalH})");
Console.WriteLine($"Offsets: {Path.GetFullPath(mapPath)}");
foreach (string line in offsets) Console.WriteLine("  " + line);
return 0;
