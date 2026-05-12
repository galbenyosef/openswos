using OpenSwos.Tools.AdfExtract;

string? input = null;
string output = "./out";
bool list = false;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--list" || a == "-l") list = true;
    else if ((a == "-o" || a == "--output") && i + 1 < args.Length) output = args[++i];
    else if (a == "-h" || a == "--help") { PrintUsage(); return 0; }
    else if (!a.StartsWith('-') && input is null) input = a;
    else { Console.Error.WriteLine($"unknown argument: {a}"); PrintUsage(); return 1; }
}

if (input is null) { PrintUsage(); return 1; }
if (!File.Exists(input)) { Console.Error.WriteLine($"file not found: {input}"); return 1; }

AdfDisk disk;
try { disk = AdfDisk.Open(input); }
catch (Exception ex) { Console.Error.WriteLine($"failed to open: {ex.Message}"); return 2; }

Console.WriteLine($"Volume   : {disk.VolumeName}");
Console.WriteLine($"FS       : {disk.FileSystem}");
Console.WriteLine($"Source   : {input}");

int fileCount = 0;
long totalBytes = 0;
foreach (var entry in disk.Walk())
{
    if (entry.IsFile) { fileCount++; totalBytes += entry.Size; }
    if (list)
    {
        string kind = entry.IsDirectory ? "DIR " : "FILE";
        Console.WriteLine($"  {kind}  {entry.Size,9}  {entry.FullPath}");
    }
}
Console.WriteLine($"Total    : {fileCount} files, {totalBytes} bytes");

if (list) return 0;

Directory.CreateDirectory(output);
int extracted = 0;
foreach (var entry in disk.Walk())
{
    string target = Path.Combine(output, entry.FullPath.Replace('/', Path.DirectorySeparatorChar));
    if (entry.IsDirectory)
    {
        Directory.CreateDirectory(target);
        continue;
    }
    if (entry.IsFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, disk.ReadFile(entry));
        extracted++;
    }
}
Console.WriteLine($"Extracted: {extracted} files -> {output}");
return 0;

static void PrintUsage()
{
    Console.WriteLine("usage: adf-extract <input.adf> [-o <output-dir>] [--list]");
    Console.WriteLine("  -o, --output <dir>   destination directory (default: ./out)");
    Console.WriteLine("  -l, --list           list contents only, do not extract");
    Console.WriteLine("  -h, --help           print this help");
}
