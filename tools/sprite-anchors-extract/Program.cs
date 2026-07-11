// Extracts per-sprite anchor offsets (centerX, centerY) from a SWOS PC .DAT file
// (e.g. TEAM1.DAT). Each sprite is stored as a 24-byte packed header followed by
// nlines * wquads * 8 bytes of nibble-packed pixel data, sequentially.
//
// Format spec: external/swos-port/docs/SWOS/sprites.txt
// Parser logic paraphrased from external/SwosGfx/SwosGfx/DosSprite.cs
//   (MIT — credit retained; we re-implement the minimum we need for anchor dumping
//   instead of copying the file). We only need geometry + anchor fields here, not
//   the chain/unchain/insert pipeline.
//
// Sprite struct on disk (24 bytes, little-endian):
//    +0   uint32  spr_data   (in-game RAM pointer, always 0 on disk)
//    +4   int16   size       (0 on disk)
//    +6   int16   dat_file   (0 on disk)
//    +8   uint8   changed    (0 on disk)
//    +9   int8    unk1
//   +10   int16   width      (pixels)
//   +12   int16   nlines     (height in lines)
//   +14   int16   wquads     (bytes/8 per line; width = wquads * 16)
//   +16   int16   center_x   (anchor X — range [-8..34])
//   +18   int16   center_y   (anchor Y — range [0..27])
//   +20   uint8   unk4
//   +21   uint8   nlines_div4
//   +22   int16   ordinal    (global sprite index in SPRITE.DAT — first sprite in
//                              TEAM1.DAT is 341; see DatSpecs table in SwosGfx)
//
// TEAM1.DAT contains 303 sprites starting at ordinal 341.

using System;
using System.IO;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

string input = args[0];
int startOrdinal = 341;
int? filterOrdinal = null;
bool annotate = false;
bool tsv = false;
for (int i = 1; i < args.Length; i++)
{
    string a = args[i];
    if ((a == "-o" || a == "--ordinal-start") && i + 1 < args.Length)
        startOrdinal = int.Parse(args[++i]);
    else if ((a == "-f" || a == "--filter") && i + 1 < args.Length)
        filterOrdinal = int.Parse(args[++i]);
    else if (a == "--annotate") annotate = true;
    else if (a == "--tsv") tsv = true;
    else
    {
        Console.Error.WriteLine($"unknown argument: {a}");
        PrintUsage();
        return 1;
    }
}

if (!File.Exists(input))
{
    Console.Error.WriteLine($"file not found: {input}");
    return 1;
}

byte[] data = File.ReadAllBytes(input);
Console.Error.WriteLine($"# input  : {input} ({data.Length} bytes)");
Console.Error.WriteLine($"# start  : ordinal {startOrdinal}");

const int HeaderSize = 24;
int offset = 0;
int ordinal = startOrdinal;
int count = 0;

if (tsv)
    Console.WriteLine("ordinal\twidth\theight\twquads\tcenterX\tcenterY\tnote");
else
    Console.WriteLine($"# ordinal width height (wquads)  cx  cy  note");

while (offset + HeaderSize <= data.Length)
{
    short width   = ReadI16(data, offset + 10);
    short nlines  = ReadI16(data, offset + 12);
    short wquads  = ReadI16(data, offset + 14);
    short centerX = ReadI16(data, offset + 16);
    short centerY = ReadI16(data, offset + 18);
    short hdrOrd  = ReadI16(data, offset + 22);

    if (nlines < 0 || wquads < 0 || nlines > 256 || wquads > 64)
    {
        Console.Error.WriteLine($"# stop at offset {offset}: invalid header (nlines={nlines}, wquads={wquads})");
        break;
    }
    int pixelBytes = nlines * wquads * 8;
    if (offset + HeaderSize + pixelBytes > data.Length)
    {
        Console.Error.WriteLine($"# stop at offset {offset}: pixels overrun file (ord={ordinal}, need {pixelBytes})");
        break;
    }

    if (!filterOrdinal.HasValue || filterOrdinal.Value == ordinal)
    {
        string note = annotate ? AnnotateOrdinal(ordinal) : "";
        if (tsv)
        {
            Console.WriteLine($"{ordinal}\t{width}\t{nlines}\t{wquads}\t{centerX}\t{centerY}\t{note}");
        }
        else
        {
            // Human-friendly columns.
            Console.WriteLine(
                $"{ordinal,7}  {width,3}x{nlines,-3} ({wquads}) " +
                $" cx={centerX,3} cy={centerY,3}" +
                (string.IsNullOrEmpty(note) ? "" : $"   {note}"));
        }
    }

    offset += HeaderSize + pixelBytes;
    ordinal++;
    count++;
}

Console.Error.WriteLine($"# parsed: {count} sprites, ended at offset {offset}/{data.Length}");
return 0;

static short ReadI16(byte[] b, int p) => (short)(b[p] | (b[p + 1] << 8));

// Maps known TEAM1.DAT ordinals to human-readable labels. Source: swos-port
// swos.asm `playerRunningUpTeam1` block and `team1PlayerStandingFacingUpFrames`
// block at line 218368-218398. Used by --annotate so the dump self-documents.
static string AnnotateOrdinal(int ord)
{
    return ord switch
    {
        // Standing poses (one cell each).
        341 => "stand N",
        344 => "stand S",
        347 => "stand E",
        350 => "stand W",
        354 => "stand SW",
        357 => "stand SE",
        360 => "stand NW",
        363 => "stand NE",

        // Cardinal run cycles. Animation order: run0, stand, run1, stand.
        342 => "run N  (phase 0)",
        343 => "run N  (phase 2)",
        345 => "run S  (phase 0)",
        346 => "run S  (phase 2)",
        348 => "run E  (phase 0)",
        349 => "run E  (phase 2)",
        351 => "run W  (phase 0)",
        352 => "run W  (phase 2)",

        // Diagonal run cycles (offsets relative to standing pose).
        353 => "run SW (phase 0)",
        355 => "run SW (phase 2)",
        356 => "run SE (phase 0)",
        358 => "run SE (phase 2)",
        359 => "run NW (phase 0)",
        361 => "run NW (phase 2)",
        362 => "run NE (phase 0)",
        364 => "run NE (phase 2)",

        // Tackling (slide) — frames during the slide motion.
        395 => "tackle N",
        396 => "tackle S",
        397 => "tackle W",
        398 => "tackle E",
        399 => "tackle SW",
        400 => "tackle SE",
        401 => "tackle NW",
        402 => "tackle NE",

        // Fallen (tackled) — recovery / on-the-ground frame.
        403 => "fallen N",
        404 => "fallen S",
        405 => "fallen W",
        406 => "fallen E",
        407 => "fallen SW",
        408 => "fallen SE",
        409 => "fallen NW",
        410 => "fallen NE",

        _ => ""
    };
}

static void PrintUsage()
{
    Console.WriteLine("usage: sprite-anchors-extract <TEAM1.DAT> [options]");
    Console.WriteLine("  -o, --ordinal-start N   first sprite ordinal in this file (default 341 = TEAM1.DAT)");
    Console.WriteLine("  -f, --filter N          print only the row for ordinal N");
    Console.WriteLine("      --annotate          tag rows whose ordinal we've reverse-engineered");
    Console.WriteLine("      --tsv               tab-separated output (for spreadsheet import)");
    Console.WriteLine("  -h, --help              print this help");
}
