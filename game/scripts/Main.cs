using Godot;
using OpenSwos.Assets;

namespace OpenSwos;

public partial class Main : Node2D
{
    public override void _Ready()
    {
        GD.Print($"OpenSWOS booted at {Time.GetDatetimeStringFromSystem()}");

        // Smoke-test the variant abstraction by trying each variant against the
        // local-dev reference data dirs. In a shipping build the path resolution
        // would come from a .tres config resource the user points at their install.
        string repoRoot = ProjectSettings.GlobalizePath("res://..");

        TryLoad(new PcAssetSource(Path.Combine(repoRoot, "Swos9697_PC", "SensiWs9", "SOC", "DATA")));
        TryLoad(new AmigaAssetSource(Path.Combine(repoRoot, "assets", "extracted", "amiga", "disk2", "data")));
    }

    private static void TryLoad(IAssetSource source)
    {
        int teamCount = 0;
        int playerCount = 0;
        TeamRecord? first = null;

        foreach (var team in source.LoadAllTeams())
        {
            teamCount++;
            playerCount += team.Players.Count;
            first ??= team;
        }

        if (teamCount == 0)
        {
            GD.Print($"[{source.Variant}] no teams loaded (data dir missing or empty)");
            return;
        }

        GD.Print($"[{source.Variant}] {teamCount} teams, {playerCount} players");
        if (first is not null)
        {
            GD.Print($"  first team: {first.Name}  coach: {first.Coach}  nation: 0x{first.Nation:X2}");
            if (first.Players.Count > 0)
            {
                var p = first.Players[0];
                GD.Print($"  first player: {p.Name} #{p.ShirtNumber} {p.Position}  " +
                    $"P{p.Passing} S{p.Shooting} H{p.Heading} T{p.Tackling} " +
                    $"C{p.Control} Sp{p.Speed} F{p.Finishing}");
            }
        }
    }
}
