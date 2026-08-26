namespace OpenSwos.Competition;

using System.Collections.Generic;

// ============================================================================
// Career creation, in ONE place.
//
// Building a career means picking a league pool, a cup pool, deriving a
// deterministic seed and materializing the CareerWorld. That logic used to live
// only inside MenuClient as private methods, which meant any second front-end
// (the web career client in scripts/Web/) had to copy it — and a copy of a rule
// like "fewer than 8 same-division clubs available -> fall back to any division"
// silently stops matching the moment either side is touched.
//
// The dependencies are passed as delegates rather than as IMenuHost: this is the
// Competition layer, and it must not reach up into Menu. Both callers supply the
// same three lookups over the master team list.
// ============================================================================

/// <summary>Master-team-list lookups a career needs, independent of any UI layer.</summary>
public sealed class TeamSource
{
    /// <summary>Master-list indices in a nation+division (division -1 = any), capped at max.</summary>
    public required System.Func<int, int, int, List<int>> ByNationDivision { get; init; }
    /// <summary>Arbitrary master-list indices, used to pad a short pool. (count, exclude).</summary>
    public required System.Func<int, int, List<int>> Random { get; init; }
    /// <summary>Builds the competition-level reference for one master index.</summary>
    public required System.Func<int, TeamRef> MakeRef { get; init; }
}

public static class CareerFactory
{
    public const int LeaguePoolMax = 16;
    public const int LeaguePoolMin = 8;
    public const int CupPoolSize = 16;

    /// <summary>
    /// League pool for a career season: same nation+division as the club, up to
    /// 16 including the club; fewer than 8 available -> same nation, any
    /// division. Random-fills to at least 8 and an even count so the
    /// round-robin comes out clean.
    /// </summary>
    public static List<int> BuildLeaguePool(TeamSource src, int you, int nation, int division)
    {
        var pool = new List<int> { you };
        foreach (int idx in src.ByNationDivision(nation, division, 24))
        {
            if (pool.Count >= LeaguePoolMax) break;
            if (idx == you || pool.Contains(idx)) continue;
            pool.Add(idx);
        }
        if (pool.Count < LeaguePoolMin)
        {
            pool = new List<int> { you };
            foreach (int idx in src.ByNationDivision(nation, -1, 24))
            {
                if (pool.Count >= LeaguePoolMax) break;
                if (idx == you || pool.Contains(idx)) continue;
                pool.Add(idx);
            }
        }
        while (pool.Count < LeaguePoolMin || (pool.Count & 1) == 1) AddRandomDistinct(src, pool);
        return pool;
    }

    /// <summary>
    /// Cup pool for a career season: 16 same-nation teams including the club,
    /// random-nation fill when the nation is short.
    /// </summary>
    public static List<int> BuildCupPool(TeamSource src, int you, int nation)
    {
        var pool = new List<int> { you };
        foreach (int idx in src.ByNationDivision(nation, -1, 32))
        {
            if (pool.Count >= CupPoolSize) break;
            if (idx == you || pool.Contains(idx)) continue;
            pool.Add(idx);
        }
        while (pool.Count < CupPoolSize) AddRandomDistinct(src, pool);
        return pool;
    }

    public static void AddRandomDistinct(TeamSource src, List<int> pool)
    {
        foreach (int idx in src.Random(pool.Count + 1, -1))
            if (!pool.Contains(idx)) { pool.Add(idx); return; }
    }

    public static List<TeamRef> MakeTeamRefs(TeamSource src, List<int> masters)
    {
        var list = new List<TeamRef>(masters.Count);
        foreach (int m in masters) list.Add(src.MakeRef(m));
        return list;
    }

    /// <summary>
    /// Deterministic seed from the participant set plus a salt (FNV-1a). Never
    /// wall-clock, so recreating a career with identical picks reproduces the
    /// same draw and the same simulated results.
    /// </summary>
    public static int SeedFrom(List<int> masters, int salt)
    {
        uint h = 2166136261u;
        foreach (int m in masters) { h ^= (uint)m; h *= 16777619u; }
        h ^= (uint)salt; h *= 16777619u;
        if (h == 0) h = 0x9E3779B9u;
        return unchecked((int)h);
    }

    /// <summary>
    /// Creates a career for the club at <paramref name="you"/>: pools, seed,
    /// engine state and manager identity. The club always sits first in the
    /// league pool, so PlayerTeam is 0. The CareerWorld is NOT built here —
    /// callers do that (it needs the full master roster) before the first save.
    /// </summary>
    public static CompetitionState Create(TeamSource src, int you, int nation, int division,
        string managerName, string managerTitle)
    {
        var leaguePool = BuildLeaguePool(src, you, nation, division);
        var cupPool = BuildCupPool(src, you, nation);
        int seed = SeedFrom(leaguePool, 900 + you);
        var comp = CompetitionEngine.CreateCareer("CAREER",
            MakeTeamRefs(src, leaguePool), MakeTeamRefs(src, cupPool), 0, nation, division, seed);
        if (comp.Career is not null)
        {
            comp.Career.ManagerName = managerName;
            comp.Career.ManagerTitle = managerTitle;
            // Career depth plan feature #3: a manager's standing starts as a
            // discounted version of the club that hired him, so the JOB OFFERS
            // screen has something to show from the first match.
            OpenSwos.Competition.Career.JobMarket.EnsureSeeded(
                comp.Career, comp.Teams.Count > 0 ? comp.Teams[0].Strength : 3);
        }
        return comp;
    }
}
