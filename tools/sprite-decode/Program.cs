using OpenSwos.Tools.SpriteDecode;

string? input = null;
string output = "./out";
int width = 16;
int height = 16;
int planes = 5;
int skip = 0;
int? count = null;
int cols = 8;
int? asciiSprite = null;
var layout = BitplaneLayout.Interleaved;
string paletteName = "swos";
int? gridSize = null;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "-h" || a == "--help") { PrintUsage(); return 0; }
    else if ((a == "-o" || a == "--output") && i + 1 < args.Length) output = args[++i];
    else if ((a == "-w" || a == "--width") && i + 1 < args.Length) width = int.Parse(args[++i]);
    else if ((a == "-H" || a == "--height") && i + 1 < args.Length) height = int.Parse(args[++i]);
    else if ((a == "-p" || a == "--planes") && i + 1 < args.Length) planes = int.Parse(args[++i]);
    else if ((a == "-s" || a == "--skip") && i + 1 < args.Length) skip = int.Parse(args[++i]);
    else if ((a == "-n" || a == "--count") && i + 1 < args.Length) count = int.Parse(args[++i]);
    else if ((a == "-c" || a == "--cols") && i + 1 < args.Length) cols = int.Parse(args[++i]);
    else if (a == "--seq") layout = BitplaneLayout.Sequential;
    else if (a == "--ilv") layout = BitplaneLayout.Interleaved;
    else if ((a == "--ascii") && i + 1 < args.Length) asciiSprite = int.Parse(args[++i]);
    else if ((a == "--palette") && i + 1 < args.Length) paletteName = args[++i];
    else if ((a == "--grid") && i + 1 < args.Length) gridSize = int.Parse(args[++i]);
    else if (!a.StartsWith('-') && input is null) input = a;
    else { Console.Error.WriteLine($"unknown argument: {a}"); PrintUsage(); return 1; }
}

if (input is null) { PrintUsage(); return 1; }
if (!File.Exists(input)) { Console.Error.WriteLine($"file not found: {input}"); return 1; }

byte[] data = File.ReadAllBytes(input);
int bytesPerSprite = Bitplane.BytesPerSprite(width, height, planes);
int available = data.Length - skip;
int maxSprites = available / bytesPerSprite;
int spriteCount = Math.Min(count ?? maxSprites, maxSprites);

Console.WriteLine($"input    : {input} ({data.Length} bytes)");
Console.WriteLine($"layout   : {layout}, {width}x{height}x{planes} bitplanes -> {bytesPerSprite} bytes/sprite");
Console.WriteLine($"skip     : {skip} bytes header");
Console.WriteLine($"sprites  : {spriteCount} (of {maxSprites} max fit, trailing {available - spriteCount * bytesPerSprite} bytes unused)");

var sprites = new byte[spriteCount][,];
for (int i = 0; i < spriteCount; i++)
{
    int off = skip + i * bytesPerSprite;
    sprites[i] = Bitplane.Decode(data, off, width, height, planes, layout);
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
string baseOut = output.EndsWith(".ppm") || output.EndsWith(".bmp")
    ? output[..^4]
    : output;

byte[] palette = paletteName switch
{
    "swos"  => Palette.SwosAmigaDefault(),
    "debug" => Palette.Debug32(),
    _ => throw new ArgumentException($"unknown palette: {paletteName}. Try 'swos' or 'debug'."),
};

Ppm.WriteGrid(baseOut + ".ppm", sprites, palette, cols);
Bmp.WriteGrid24(baseOut + ".bmp", sprites, palette, cols);

Console.WriteLine($"palette  : {paletteName} ({palette.Length / 3} entries)");
Console.WriteLine($"wrote    : {baseOut}.ppm");
Console.WriteLine($"wrote    : {baseOut}.bmp");

if (gridSize is int cs && spriteCount == 1)
{
    // gridSize only meaningful for a single-sprite render — overlays cell boundaries.
    Bmp.WriteWithGrid(baseOut + "_grid.bmp", sprites[0], palette, cs, gridIndex: 11);
    Console.WriteLine($"wrote    : {baseOut}_grid.bmp  (with {cs}px grid)");

    // 4× nearest-neighbour upscale + grid overlay — easy to read individual cells.
    Bmp.WriteScaledWithGrid(baseOut + "_4x_grid.bmp", sprites[0], palette, 4, cs, gridIndex: 11);
    Console.WriteLine($"wrote    : {baseOut}_4x_grid.bmp  (4x scale, {cs}px source grid)");
}

if (asciiSprite is int idx)
{
    if (idx < 0 || idx >= sprites.Length)
    {
        Console.Error.WriteLine($"ascii index {idx} out of range [0..{sprites.Length - 1}]");
        return 1;
    }
    Console.WriteLine();
    Console.WriteLine($"--- ASCII sprite #{idx} ---");
    var s = sprites[idx];
    // glyphs sorted from "dim" to "bright" — lets brightest indices read as filled
    const string glyphs = ".:-=+*#%@$&0123456789ABCDEFGHIJKLM";
    for (int y = 0; y < s.GetLength(0); y++)
    {
        for (int x = 0; x < s.GetLength(1); x++)
        {
            byte v = s[y, x];
            char g = v < glyphs.Length ? glyphs[v] : '?';
            Console.Write(g);
            Console.Write(' ');
        }
        Console.WriteLine();
    }
}
return 0;

static void PrintUsage()
{
    Console.WriteLine("usage: sprite-decode <input.raw> [-o out.ppm] [-w W] [-H H] [-p P] [-s SKIP] [-n N] [-c COLS] [--seq | --ilv]");
    Console.WriteLine("  -w, --width    sprite width  (default 16)");
    Console.WriteLine("  -H, --height   sprite height (default 16)");
    Console.WriteLine("  -p, --planes   bitplane count (default 5 = 32 colours)");
    Console.WriteLine("  -s, --skip     header bytes to skip (default 0)");
    Console.WriteLine("  -n, --count    sprite count (default: as many as fit)");
    Console.WriteLine("  -c, --cols     columns in output grid (default 8)");
    Console.WriteLine("  --seq          sequential plane layout (default: interleaved)");
    Console.WriteLine("  --ilv          interleaved (per-row) plane layout");
}
