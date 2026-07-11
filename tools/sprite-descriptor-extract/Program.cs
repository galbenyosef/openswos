using OpenSwos.Tools.SpriteDescriptorExtract;

string? input = null;
int startOffset = 0x18374;  // unk_118374 — player descriptor stream
int? specificIndex = null;
int dumpFirst = 50;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "-h" || a == "--help") { PrintUsage(); return 0; }
    else if ((a == "--offset") && i + 1 < args.Length)
        startOffset = (int)Convert.ToUInt32(args[++i], 16);
    else if ((a == "--picture") && i + 1 < args.Length)
        specificIndex = (int)Convert.ToUInt32(args[++i], 16);
    else if ((a == "--head") && i + 1 < args.Length)
        dumpFirst = int.Parse(args[++i]);
    else if (!a.StartsWith('-') && input is null) input = a;
    else { Console.Error.WriteLine($"unknown argument: {a}"); PrintUsage(); return 1; }
}

if (input is null) { PrintUsage(); return 1; }
if (!File.Exists(input)) { Console.Error.WriteLine($"file not found: {input}"); return 1; }

byte[] swosBin = File.ReadAllBytes(input);
Console.WriteLine($"loaded {input} ({swosBin.Length} bytes)");
Console.WriteLine($"parsing descriptor stream at offset 0x{startOffset:X}");

List<Descriptor> descs;
try
{
    descs = DescriptorStream.Parse(swosBin, startOffset);
}
catch (DescriptorStreamException ex)
{
    Console.Error.WriteLine($"parse failed: {ex.Message}");
    return 2;
}

Console.WriteLine($"parsed {descs.Count} descriptors");
Console.WriteLine();

if (specificIndex is int pi)
{
    var hits = descs.Where(d => d.PictureIndex == pi).ToList();
    Console.WriteLine($"=== picture index 0x{pi:X3} ({pi}): {hits.Count} match(es) ===");
    foreach (var d in hits) PrintDescriptor(d);
    return 0;
}

Console.WriteLine($"=== first {dumpFirst} descriptors ===");
foreach (var d in descs.Take(dumpFirst)) PrintDescriptor(d);

// Highlight key player picture indices we care about.
Console.WriteLine();
Console.WriteLine("=== key player picture indices (standing poses) ===");
int[] standing = { 0xE3, 0xE6, 0xE9, 0xEC, 0xF0, 0xF3, 0xF6, 0xF9 };
foreach (int idx in standing)
{
    var d = descs.FirstOrDefault(x => x.PictureIndex == idx);
    if (d is null) Console.WriteLine($"  0x{idx:X3} ({idx}): not found");
    else PrintDescriptor(d);
}

Console.WriteLine();
Console.WriteLine("=== running-cycle picture indices (down direction = $227..$229) ===");
foreach (int idx in new[] { 0x227, 0x228, 0x229 })
{
    var d = descs.FirstOrDefault(x => x.PictureIndex == idx);
    if (d is null) Console.WriteLine($"  0x{idx:X3} ({idx}): not found");
    else PrintDescriptor(d);
}

return 0;

static void PrintDescriptor(Descriptor d)
{
    string offs = (d.OffsetX != 0 || d.OffsetY != 0) ? $" anchor=({d.OffsetX},{d.OffsetY})" : "";
    string mask = d.HasMask ? " mask" : "";
    string pal = d.Palette != 0 ? $" pal=0x{d.Palette:X}" : "";
    Console.WriteLine(
        $"  0x{d.PictureIndex:X3} cat=0x{d.OpcodeCategory:X4}  cell=({d.CellX,3},{d.CellY,3}) " +
        $"size=({d.CellW},{d.CellH}){offs}{mask}{pal}  atlas={d.AtlasName}");
}

static void PrintUsage()
{
    Console.WriteLine("usage: sprite-descriptor-extract <SWOS.bin> [--offset HEX] [--picture HEX] [--head N]");
    Console.WriteLine("  --offset HEX   stream start offset (default 0x18374 = unk_118374, player sprites)");
    Console.WriteLine("  --picture HEX  show entries matching this picture index only");
    Console.WriteLine("  --head N       how many entries to dump in the head summary (default 50)");
}
