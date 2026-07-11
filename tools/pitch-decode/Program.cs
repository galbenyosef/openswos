using OpenSwos.Tools.PitchDecode;
using OpenSwos.Tools.RncDecode;
using OpenSwos.Tools.SpriteDecode;

string? input = null;
string output = "./pitch";

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "-h" || a == "--help") { PrintUsage(); return 0; }
    else if ((a == "-o" || a == "--output") && i + 1 < args.Length) output = args[++i];
    else if (!a.StartsWith('-') && input is null) input = a;
    else { Console.Error.WriteLine($"unknown argument: {a}"); PrintUsage(); return 1; }
}

if (input is null) { PrintUsage(); return 1; }
if (!File.Exists(input)) { Console.Error.WriteLine($"file not found: {input}"); return 1; }

byte[] raw = File.ReadAllBytes(input);
byte[] decompressed;
if (raw.Length >= 4 && raw[0] == 'R' && raw[1] == 'N' && raw[2] == 'C')
{
    Console.WriteLine($"input  : {input} ({raw.Length} bytes, RNC v{raw[3]})");
    try { decompressed = Rnc.Decode(raw); }
    catch (Exception ex) { Console.Error.WriteLine($"RNC decode failed: {ex.Message}"); return 2; }
}
else
{
    Console.WriteLine($"input  : {input} ({raw.Length} bytes, raw)");
    decompressed = raw;
}

Console.WriteLine($"decomp : {decompressed.Length} bytes");
int tileCount = PitchFile.TileCount(decompressed.Length);
Console.WriteLine($"tiles  : {tileCount}  (header {PitchFile.HeaderSize} + {tileCount}×{PitchFile.BytesPerTile})");

byte[,] pixels;
try { pixels = PitchFile.Render(decompressed); }
catch (PitchFileException ex) { Console.Error.WriteLine($"pitch decode failed: {ex.Message}"); return 3; }

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
string baseOut = output.EndsWith(".ppm") || output.EndsWith(".bmp") ? output[..^4] : output;

byte[] palette = Palette.SwosAmigaDefault();
Ppm.Write(baseOut + ".ppm", pixels, palette);
Bmp.Write24(baseOut + ".bmp", pixels, palette);
Console.WriteLine($"wrote  : {baseOut}.ppm");
Console.WriteLine($"wrote  : {baseOut}.bmp  ({PitchFile.PixelWidth}×{PitchFile.PixelHeight})");
return 0;

static void PrintUsage()
{
    Console.WriteLine("usage: pitch-decode <input.MAP> [-o <output>]");
    Console.WriteLine("  Accepts either a raw decompressed pitch (9240 + N×128 bytes) or");
    Console.WriteLine("  the original RNC-packed file (auto-detects via 'RNC' magic).");
}
