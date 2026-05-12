using OpenSwos.Tools.TeamDecode;

namespace OpenSwos.Assets;

public sealed class PcAssetSource : IAssetSource
{
    private readonly string _dataDir;

    public GameVariant Variant => GameVariant.Pc;

    // dataDir = path to SOC/DATA/ containing TEAM.000..TEAM.085, TEAM.CUS, etc.
    public PcAssetSource(string dataDir) => _dataDir = dataDir;

    public IEnumerable<TeamRecord> LoadAllTeams()
    {
        if (!Directory.Exists(_dataDir)) yield break;
        foreach (string file in Directory.EnumerateFiles(_dataDir, "TEAM.*"))
        {
            // TEAM.248 has a different (non-team) layout; TEAM.CUS would need a
            // separate "is_custom" tag — skip both for the baseline source.
            string ext = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
            if (ext == "248" || ext == "CUS") continue;
            foreach (var rec in ParseFile(file)) yield return rec;
        }
    }

    public IEnumerable<TeamRecord> LoadTeamsForNation(int nation)
    {
        string file = Path.Combine(_dataDir, $"TEAM.{nation:D3}");
        if (!File.Exists(file)) yield break;
        foreach (var rec in ParseFile(file)) yield return rec;
    }

    private static IEnumerable<TeamRecord> ParseFile(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch { yield break; }
        List<Team> teams;
        try { teams = TeamFile.Read(data); }
        catch (TeamFileException) { yield break; }
        foreach (var t in teams) yield return AssetMapping.From(t);
    }
}
