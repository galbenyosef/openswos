using OpenSwos.Tools.TeamDecode;

string? input = null;
bool summaryOnly = false;
int? teamIndex = null;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "-h" || a == "--help") { PrintUsage(); return 0; }
    else if (a == "-s" || a == "--summary") summaryOnly = true;
    else if ((a == "-t" || a == "--team") && i + 1 < args.Length)
        teamIndex = int.Parse(args[++i]);
    else if (!a.StartsWith('-') && input is null) input = a;
    else { Console.Error.WriteLine($"unknown argument: {a}"); PrintUsage(); return 1; }
}

if (input is null) { PrintUsage(); return 1; }
if (!File.Exists(input)) { Console.Error.WriteLine($"file not found: {input}"); return 1; }

List<Team> teams;
try
{
    teams = TeamFile.Read(File.ReadAllBytes(input));
}
catch (TeamFileException ex)
{
    Console.Error.WriteLine($"parse failed: {ex.Message}");
    return 2;
}

Console.WriteLine($"File: {input}");
Console.WriteLine($"Teams in file: {teams.Count}");
Console.WriteLine();

if (summaryOnly)
{
    foreach (var t in teams)
        Console.WriteLine($"  [{t.IndexInFile,2}] {t.Name,-19}  id=0x{t.GlobalTeamId:X4}  div={TeamFile.DecodeDivision(t.Division)}  tac={TeamFile.DecodeTactics(t.Tactics)}");
    return 0;
}

int start = teamIndex ?? 0;
int end = teamIndex.HasValue ? teamIndex.Value + 1 : teams.Count;
if (start < 0 || start >= teams.Count)
{
    Console.Error.WriteLine($"team index {start} out of range [0..{teams.Count - 1}]");
    return 1;
}

for (int idx = start; idx < end; idx++)
{
    var t = teams[idx];
    Console.WriteLine($"========== Team [{t.IndexInFile}] {t.Name} ==========");
    Console.WriteLine($"  Nation 0x{t.Nation:X2}  GlobalID 0x{t.GlobalTeamId:X4}  TeamSkillTotal {t.TeamSkillTotal}");
    Console.WriteLine($"  Division: {TeamFile.DecodeDivision(t.Division)}");
    Console.WriteLine($"  Tactics : {TeamFile.DecodeTactics(t.Tactics)} (raw 0x{t.Tactics:X2})");
    Console.WriteLine($"  Home kit: {TeamFile.FormatKit(t.HomeKit)}");
    Console.WriteLine($"  Away kit: {TeamFile.FormatKit(t.AwayKit)}");
    Console.WriteLine($"  Coach   : {t.Coach}");
    Console.WriteLine($"  Display order: {string.Join(' ', t.PlayerDisplayOrder)}");
    Console.WriteLine();
    Console.WriteLine($"  {"#",-3} {"Name",-22} {"Pos",-3} {"Nat",-5} {"Pas Sho Hea Tac Con Spe Fin",-30} {"Val"}");
    foreach (var p in t.Players)
    {
        Console.WriteLine(
            $"  {p.ShirtNumber,-3} {p.Name,-22} {TeamFile.DecodePosition(p.PositionByte),-3} 0x{p.Nationality:X2}  " +
            $" {p.Passing}   {p.Shooting}   {p.Heading}   {p.Tackling}   {p.Control}   {p.Speed}   {p.Finishing}   " +
            $" 0x{p.ValueCode:X2}");
    }
    Console.WriteLine();
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("usage: team-decode <TEAM.xxx> [-s | --summary] [-t <index> | --team <index>]");
    Console.WriteLine("  -s, --summary       list all teams in the file (one line each)");
    Console.WriteLine("  -t, --team N        dump only team at index N (otherwise all teams)");
    Console.WriteLine("  -h, --help          print this help");
}
