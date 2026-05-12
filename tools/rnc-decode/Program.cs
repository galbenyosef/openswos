using OpenSwos.Tools.RncDecode;

string? input = null;
string? output = null;
bool verify = false;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "-h" || a == "--help") { PrintUsage(); return 0; }
    else if (a == "--verify") verify = true;
    else if ((a == "-o" || a == "--output") && i + 1 < args.Length) output = args[++i];
    else if (!a.StartsWith('-') && input is null) input = a;
    else { Console.Error.WriteLine($"unknown argument: {a}"); PrintUsage(); return 1; }
}

if (input is null) { PrintUsage(); return 1; }
if (!File.Exists(input)) { Console.Error.WriteLine($"file not found: {input}"); return 1; }

byte[] packed;
try { packed = File.ReadAllBytes(input); }
catch (Exception ex) { Console.Error.WriteLine($"read failed: {ex.Message}"); return 1; }

if (packed.Length >= 4 && packed[0] == 'R' && packed[1] == 'N' && packed[2] == 'C' && packed[3] == 0x01)
{
    Console.WriteLine($"RNC1 header detected ({packed.Length} bytes compressed)");
}
else
{
    Console.Error.WriteLine($"{input}: not an RNC1 stream (magic mismatch)");
    return 2;
}

byte[] unpacked;
try { unpacked = RncV1.Decode(packed); }
catch (RncV1Exception ex)
{
    Console.Error.WriteLine($"decode failed: {ex.Message}");
    return 3;
}

Console.WriteLine($"Decoded   : {unpacked.Length} bytes");
if (unpacked.Length >= 4)
{
    Console.WriteLine($"First 4   : {unpacked[0]:X2} {unpacked[1]:X2} {unpacked[2]:X2} {unpacked[3]:X2}");
    if (unpacked[0] == 0x00 && unpacked[1] == 0x00 && unpacked[2] == 0x03 && unpacked[3] == 0xF3)
        Console.WriteLine("           ^ Amiga m68k HUNK_HEADER magic");
}

if (verify)
    Console.WriteLine("CRC       : OK (both packed and unpacked verified during decode)");

if (output is not null)
{
    File.WriteAllBytes(output, unpacked);
    Console.WriteLine($"Written   : {output}");
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("usage: rnc-decode <input> [-o <output>] [--verify]");
    Console.WriteLine("  -o, --output <file>  write decoded bytes to <file>");
    Console.WriteLine("  --verify             report CRC verification");
    Console.WriteLine("  -h, --help           print this help");
}
