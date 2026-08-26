using System;
using System.Collections.Generic;

namespace OpenSwos.Competition.Career;

// ============================================================================
// SEASON'S TOP SCORER — career depth plan feature #5.
//
// The original ships this in two places, and the strings say so exactly
// (original-amiga-swos.asm, flattened dc.b byte stream):
//
//   asm:283007  "SEASON'S TOP SCORER"      \  the MANAGEMENT RECORD screen: one
//   asm:283027  "SEASON'S TOP SCORERS"      > line per season, plural when the
//   asm:283048  "%a  %0"                   /  club's leading scorers are level
//   asm:283055  "CAREER TOTAL"             -- a manager's running total
//
//   asm:295043  "LEADING COMPETITION GOAL SCORERS"  \ the STATS menu's
//   asm:295076  "TOP GOAL SCORERS"                   > scorer list, plus the
//   asm:295104  "HIGHEST SCORER LIST"                / two aggregate rows
//   asm:295659  "GOALS"  asm:295686 "OWN GOALS"  asm:295696 "EX. PLAYER GOALS"
//
// "EX. PLAYER GOALS" is the tell that the original keeps a CLUB list as well as
// a competition one: goals scored by a player who has since left still belong to
// the club that got the points, so they are folded into one anonymous row rather
// than deleted. We reproduce that at display time (see FoldForClub) instead of
// storing a second copy.
//
// WHERE THE GOALS COME FROM
//   * A fixture the manager PLAYED hands us the real scorers out of sim memory
//     (Sim/Port/Result.cs ScorerInfo -> Main.CaptureMatchOutcome).
//   * Every other fixture is simulated, and a simulated scoreline has no
//     scorers at all. Without attribution a league leaderboard would list only
//     the human's own club, which is worse than not shipping the screen. So the
//     engine attributes each simulated goal to a real member of that club's XI,
//     weighted by position line and finishing ability, drawn from the SAME
//     deterministic competition RNG as the scoreline itself — so a save reloads
//     to the identical scorer table.
//
// The weights are ours, not the original's (the original simulates goals in a
// routine we have not recovered), and they were MEASURED rather than felt:
// --competition-test STEP 06f plays a 16-club season and prints the split. A
// 545-goal season came out at 41 % forwards / 26 % wide / 14 % central midfield
// / 14 % defence / 3 % own goals, against a per-club 4-4-2 the weights predict
// at 46/28/16/10. The picker itself was checked separately over 200 000 draws
// on one club and reproduced that club's predicted split to within 0.5 pt, so
// the gap is one season of variance plus squad shape, not a biased draw.
//
// Two things about the defence share that look wrong and are not: it is spread
// over four times as many players as the forward share (46 different defenders
// scored in that season against 32 forwards), and a 4-4-2 has four defenders to
// two central midfielders — so defence out-scoring central midfield in TOTAL is
// arithmetic, not a fault.
// ============================================================================

/// <summary>One player's goals in the season currently loaded in a competition.</summary>
public sealed class ScorerRow
{
    /// <summary>Index into <c>CompetitionState.Teams</c>.</summary>
    public int Team { get; set; }
    /// <summary>
    /// <c>CareerPlayer.Id</c>. 0 for a non-career competition (no stable ids);
    /// <see cref="ScorerModel.OwnGoalPlayerId"/> for the club own-goals row.
    /// </summary>
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public int Goals { get; set; }
}

/// <summary>
/// Squads for clubs the engine only knows as a master-list index — a plain
/// LEAGUE / CUP / TOURNAMENT has no <see cref="CareerWorld"/> to read. Installed
/// by both front-ends exactly like <c>JobMarketSource</c> / <c>NationalSource</c>,
/// because the Competition layer must not reach up into Menu or Web.
/// </summary>
public sealed class ScorerSource
{
    /// <summary>
    /// The read-only TEAM.* record of the club at this master-list index, or
    /// null. It is needed for two things: the squad of a non-career competition,
    /// and — even in a career — the club's ORIGINAL lineup, which
    /// CareerMatchTeam.BuildOrder projects onto the live squad to work out who
    /// is actually in the XI.
    /// </summary>
    public required Func<int, OpenSwos.Assets.TeamRecord?> Team { get; init; }

    public static ScorerSource FromHost(Func<int, OpenSwos.Assets.TeamRecord?> team)
        => new() { Team = team };
}

/// <summary>
/// One entry from a REALLY PLAYED match: what the sim recorded on the result
/// panel, resolved to a club and (in a career) to a stable player id. Handed to
/// <c>CompetitionEngine.RecordResult</c> so a played fixture keeps its true
/// scorers instead of being attributed like a simulated one.
/// </summary>
/// <param name="Team">Index into <c>CompetitionState.Teams</c> — the club that SCORED.</param>
/// <param name="OwnGoal">
/// True for the original's shirt+1000 entries (result.cpp:181): the goal counts
/// for <paramref name="Team"/> but was put in by an opponent, so it lands in the
/// anonymous OWN GOALS row and nobody's tally.
/// </param>
public readonly record struct GoalCredit(
    int Team, int PlayerId, string Name, bool OwnGoal, int Goals);

/// <summary>A player who could be credited with a simulated goal.</summary>
public readonly record struct ScorerCandidate(
    int Id, string Name, string Position, int Shooting, int Finishing, int Heading);

/// <summary>The club leading scorer(s) in one finished season.</summary>
public sealed class SeasonTopScorer
{
    public int Season { get; set; }
    public List<string> Names { get; set; } = new();
    public int Goals { get; set; }
}

public static class ScorerModel
{
    /// <summary>Reserved <see cref="ScorerRow.PlayerId"/> for a club own-goals row.</summary>
    public const int OwnGoalPlayerId = -1;
    /// <summary>Reserved id for the folded "EX. PLAYER GOALS" row (asm:295696).</summary>
    public const int ExPlayerPlayerId = -2;

    /// <summary>Installed by both front-ends; null in a headless engine test.</summary>
    public static ScorerSource? Source { get; set; }

    // Goals by line, before the ability multiplier. See the header for why.
    private const double WeightAttack = 10.0;
    private const double WeightWing   = 6.5;
    private const double WeightMid    = 4.0;
    private const double WeightDefend = 1.1;
    private const double WeightKeeper = 0.05;
    /// <summary>Own goals as a permille of all simulated goals (3 %).</summary>
    private const int OwnGoalPermille = 30;

    /// <summary>
    /// G / D / W / M / A for a SWOS position string. SWOS has exactly seven
    /// outfield positions (RB, LB, D, RW, LW, M, A — TeamFile.DecodePosition);
    /// RW and LW are the WIDE players of a 4-4-2's midfield four, not forwards,
    /// but they get further up the pitch than a central midfielder does, so the
    /// scorer weights give them a line of their own.
    /// </summary>
    public static string LineOf(string? position) => (position ?? "").Trim().ToUpperInvariant() switch
    {
        "G" => "G",
        "D" or "LB" or "RB" => "D",
        "RW" or "LW" => "W",
        "A" => "A",
        _ => "M",
    };

    /// <summary>Strips the original leading "X. " initial so a list reads as surnames.</summary>
    public static string CleanName(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length >= 3 && s[1] == '.' && s[2] == ' ') s = s.Substring(3);
        return s.Trim().ToUpperInvariant();
    }

    // ------------------------------------------------------------------
    // Recording
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="goals"/> to a player row for this season, creating
    /// it if needed, and to his running career total when the world knows him.
    /// </summary>
    public static void Credit(CompetitionState state, int team, int playerId, string name,
                              int goals, CareerWorld? world)
    {
        if (state is null || goals <= 0) return;
        state.Scorers ??= new List<ScorerRow>();
        name = CleanName(name);
        // A row is identified by its player id where there is one; a non-career
        // competition has none, so the name carries the identity there.
        ScorerRow? row = null;
        foreach (var r in state.Scorers)
        {
            if (r.Team != team) continue;
            bool same = playerId != 0 ? r.PlayerId == playerId
                                      : r.PlayerId == 0 && string.Equals(r.Name, name, StringComparison.Ordinal);
            if (same) { row = r; break; }
        }
        if (row is null)
        {
            row = new ScorerRow { Team = team, PlayerId = playerId, Name = name };
            state.Scorers.Add(row);
        }
        row.Goals += goals;
        if (row.Name.Length == 0 && name.Length > 0) row.Name = name;

        if (playerId > 0 && world is not null)
        {
            var p = FindPlayer(world, playerId);
            if (p is not null)
            {
                p.CareerGoals += goals;
                // Feature #8: the club-scoped counter and its milestones live in
                // CareerRecords, so there is exactly one place that knows when a
                // goal is the hundredth for this club.
                CareerRecords.NoteGoal(state, p, goals);
            }
        }
    }

    /// <summary>Credits a club anonymous OWN GOALS row (asm:295686).</summary>
    public static void CreditOwnGoal(CompetitionState state, int team, int goals)
    {
        if (state is null || goals <= 0) return;
        state.Scorers ??= new List<ScorerRow>();
        foreach (var r in state.Scorers)
            if (r.Team == team && r.PlayerId == OwnGoalPlayerId) { r.Goals += goals; return; }
        state.Scorers.Add(new ScorerRow
        {
            Team = team, PlayerId = OwnGoalPlayerId, Name = "", Goals = goals,
        });
    }

    private static CareerPlayer? FindPlayer(CareerWorld world, int playerId)
    {
        if (world.Clubs is not null)
            foreach (var kv in world.Clubs)
            {
                var squad = kv.Value?.Squad;
                if (squad is null) continue;
                foreach (var p in squad) if (p is not null && p.Id == playerId) return p;
            }
        if (world.FreeAgents is not null)
            foreach (var p in world.FreeAgents) if (p is not null && p.Id == playerId) return p;
        return null;
    }

    // ------------------------------------------------------------------
    // Attribution for a SIMULATED scoreline
    // ------------------------------------------------------------------

    /// <summary>
    /// Spreads <paramref name="goals"/> across a club XI. <paramref name="nextInt"/>
    /// is the competition own deterministic RNG (never System.Random), so the
    /// scorer table is a pure function of the save.
    /// </summary>
    public static void AttributeSimulated(CompetitionState state, int team, int goals,
                                          Func<int, int> nextInt)
    {
        if (state is null || goals <= 0 || nextInt is null) return;
        if (team < 0 || team >= state.Teams.Count) return;

        var squad = CandidatesFor(state, team);
        if (squad.Count == 0) return;

        var world = state.Career?.World;
        var weights = new double[squad.Count];
        double total = 0.0;
        for (int i = 0; i < squad.Count; i++)
        {
            weights[i] = Weight(squad[i]);
            total += weights[i];
        }
        if (total <= 0.0) return;

        for (int g = 0; g < goals; g++)
        {
            if (nextInt(1000) < OwnGoalPermille)
            {
                // An own goal is scored FOR this team by an opponent, so it
                // belongs in THIS club's tally (the list has to add up to the
                // goals the club scored) as the anonymous OWN GOALS row —
                // exactly where the original puts it, and where the sim puts it
                // too (Result.RegisterScorer tags it shirt+1000 on the
                // beneficiary's scorer list, result.cpp:181).
                CreditOwnGoal(state, team, 1);
                continue;
            }
            // Weighted pick over a 0..9999 ticket space (integer draw keeps the
            // stream identical across platforms; no float RNG anywhere).
            int ticket = nextInt(10000);
            double acc = 0.0;
            int chosen = squad.Count - 1;
            for (int i = 0; i < squad.Count; i++)
            {
                acc += weights[i] / total * 10000.0;
                if (ticket < acc) { chosen = i; break; }
            }
            var c = squad[chosen];
            Credit(state, team, c.Id, c.Name, 1, world);
        }
    }

    private static double Weight(ScorerCandidate c)
    {
        double bas = LineOf(c.Position) switch
        {
            "A" => WeightAttack,
            "W" => WeightWing,
            "M" => WeightMid,
            "D" => WeightDefend,
            _   => WeightKeeper,
        };
        // Squared, so a club's best finisher pulls clear of his strike partner
        // instead of splitting the goals with him. Measured: with a LINEAR
        // multiplier a 30-game league's leading scorer topped out at 12, which
        // is a chart nobody wins; squared it lands in the high teens, and the
        // top scorer's share of his own club's goals stays around a third.
        double skill = (c.Shooting + c.Finishing + c.Heading) / 3.0;
        double mult = 1.0 + Math.Clamp(skill, 0.0, 7.0) / 7.0;
        return bas * mult * mult;
    }

    /// <summary>
    /// The XI that could score for a club: the live career squad when there is a
    /// world, otherwise the master roster through <see cref="Source"/>.
    /// </summary>
    public static List<ScorerCandidate> CandidatesFor(CompetitionState state, int team)
    {
        var list = new List<ScorerCandidate>();
        if (state is null || team < 0 || team >= state.Teams.Count) return list;

        OpenSwos.Assets.TeamRecord? rec = null;
        if (Source is not null)
            try { rec = Source.Team(state.Teams[team].MasterIndex); } catch { rec = null; }

        var world = state.Career?.World;
        if (world?.Clubs is not null
            && world.Clubs.TryGetValue(state.Teams[team].GlobalId, out var club)
            && club?.Squad is not null && club.Squad.Count > 0)
        {
            // The SAME BuildOrder a real match uses, base record and all — the
            // club has to be simulated with the XI it would actually field, or
            // the leaderboard describes a team nobody ever picks.
            var order = CareerMatchTeam.BuildOrder(club, rec);
            int n = Math.Min(11, order.Count);
            for (int i = 0; i < n; i++)
            {
                var p = order[i];
                if (p is null) continue;
                list.Add(new ScorerCandidate(p.Id, CleanName(p.Name), p.Position,
                    (int)Math.Round(p.Shooting), (int)Math.Round(p.Finishing),
                    (int)Math.Round(p.Heading)));
            }
            if (list.Count > 0) return list;
        }

        // No career world: a plain LEAGUE / CUP / TOURNAMENT of master-roster
        // clubs. LineupOrder is the club's real XI, so honour it.
        if (rec?.Players is not null && rec.Players.Count > 0)
        {
            var order = rec.LineupOrder;
            int n = Math.Min(11, order is not null && order.Length > 0 ? order.Length : rec.Players.Count);
            for (int i = 0; i < n; i++)
            {
                int idx = order is not null && order.Length > i ? order[i] : i;
                if (idx < 0 || idx >= rec.Players.Count) continue;
                var p = rec.Players[idx];
                if (p is null) continue;
                list.Add(new ScorerCandidate(0, CleanName(p.Name), p.Position,
                    p.Shooting, p.Finishing, p.Heading));
            }
        }
        return list;
    }

    // ------------------------------------------------------------------
    // A PLAYED fixture's real scorers
    // ------------------------------------------------------------------

    /// <summary>
    /// Turns what the sim captured — (human's club?, in-game slot, goals, own
    /// goal) — into engine credits. Lives here rather than in either front-end
    /// because the desktop menu and the browser client must resolve a slot to
    /// the same player: through the SAME CareerMatchTeam.BuildOrder the match
    /// was launched with, so a substitution or an injury cannot shift the name.
    /// </summary>
    /// <param name="team">The host's master-roster accessor.</param>
    /// <returns>null when there is nothing to credit, so the caller can pass it
    /// straight to RecordResult and let the engine attribute instead.</returns>
    public static List<GoalCredit>? ResolveCredits(
        CompetitionState state, Fixture fixture,
        IReadOnlyList<(bool playerTeam, int slot, int goals, bool ownGoal)>? raw,
        Func<int, OpenSwos.Assets.TeamRecord?> team)
    {
        if (state is null || fixture is null || raw is null || raw.Count == 0) return null;
        int mine = state.PlayerTeam;
        if (mine < 0 || mine >= state.Teams.Count) return null;
        // The sim always seats the human on the TOP slots whichever way round
        // the fixture is, so "not the player's team" is simply the other side.
        int theirs = fixture.HomeTeam == mine ? fixture.AwayTeam : fixture.HomeTeam;

        var credits = new List<GoalCredit>(raw.Count);
        foreach (var (playerTeam, slot, goals, ownGoal) in raw)
        {
            if (goals <= 0) continue;
            int t = playerTeam ? mine : theirs;
            if (ownGoal) { credits.Add(new GoalCredit(t, 0, "", true, goals)); continue; }
            var (id, name) = ResolveSlot(state, t, slot, team);
            credits.Add(new GoalCredit(t, id, name, false, goals));
        }
        return credits.Count > 0 ? credits : null;
    }

    /// <summary>An in-game slot to (CareerPlayer id, name) for one club.</summary>
    private static (int Id, string Name) ResolveSlot(
        CompetitionState state, int teamIndex, int slot,
        Func<int, OpenSwos.Assets.TeamRecord?> team)
    {
        if (teamIndex < 0 || teamIndex >= state.Teams.Count || slot < 0) return (0, "");
        OpenSwos.Assets.TeamRecord? rec;
        try { rec = team(state.Teams[teamIndex].MasterIndex); } catch { rec = null; }

        var world = state.Career?.World;
        if (world?.Clubs is not null
            && world.Clubs.TryGetValue(state.Teams[teamIndex].GlobalId, out var club) && club is not null)
        {
            // LineupOrder is the identity map (CareerMatchTeam.Build), so the
            // captured PlayerInfo.index IS the BuildOrder slot.
            var order = CareerMatchTeam.BuildOrder(club, rec);
            if (slot < order.Count && order[slot] is not null)
                return (order[slot].Id, order[slot].Name);
        }
        // No career world (a plain LEAGUE / CUP played from the menu): the name
        // is still real, there is just no stable id to hang a career total on.
        if (rec?.Players is not null && slot < rec.Players.Count)
            return (0, rec.Players[slot].Name);
        return (0, "");
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <summary>
    /// The competition-wide leaderboard (asm:295043 "LEADING COMPETITION GOAL
    /// SCORERS"), best first. Own-goal rows never appear — nobody leads a
    /// scorer chart on own goals.
    /// </summary>
    public static List<ScorerRow> Leaderboard(CompetitionState state, int limit = 40)
    {
        var rows = new List<ScorerRow>();
        if (state?.Scorers is null) return rows;
        foreach (var r in state.Scorers)
            if (r is not null && r.PlayerId != OwnGoalPlayerId && r.Goals > 0) rows.Add(r);
        Sort(rows);
        if (limit > 0 && rows.Count > limit) rows.RemoveRange(limit, rows.Count - limit);
        return rows;
    }

    /// <summary>
    /// One club list, with every departed player folded into a single anonymous
    /// row (asm:295696 "EX. PLAYER GOALS") the way the original does. The
    /// own-goals row is kept, last. Rows carrying the reserved ids come back
    /// with an empty Name so the caller supplies the localised label.
    /// </summary>
    public static List<ScorerRow> FoldForClub(CompetitionState state, int team)
    {
        var rows = new List<ScorerRow>();
        if (state?.Scorers is null) return rows;

        HashSet<int>? current = null;
        var world = state.Career?.World;
        if (world?.Clubs is not null && team >= 0 && team < state.Teams.Count
            && world.Clubs.TryGetValue(state.Teams[team].GlobalId, out var club)
            && club?.Squad is not null)
        {
            current = new HashSet<int>();
            foreach (var p in club.Squad) if (p is not null) current.Add(p.Id);
        }

        int exGoals = 0, ownGoals = 0;
        foreach (var r in state.Scorers)
        {
            if (r is null || r.Team != team || r.Goals <= 0) continue;
            if (r.PlayerId == OwnGoalPlayerId) { ownGoals += r.Goals; continue; }
            // Only a career (which has stable ids AND a squad to check against)
            // can tell an ex-player from a current one.
            if (current is not null && r.PlayerId > 0 && !current.Contains(r.PlayerId))
            {
                exGoals += r.Goals;
                continue;
            }
            rows.Add(r);
        }
        Sort(rows);
        if (exGoals > 0)
            rows.Add(new ScorerRow { Team = team, PlayerId = ExPlayerPlayerId, Name = "", Goals = exGoals });
        if (ownGoals > 0)
            rows.Add(new ScorerRow { Team = team, PlayerId = OwnGoalPlayerId, Name = "", Goals = ownGoals });
        return rows;
    }

    private static void Sort(List<ScorerRow> rows)
        => rows.Sort((a, b) =>
        {
            int c = b.Goals.CompareTo(a.Goals);
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            if (c != 0) return c;
            return a.PlayerId.CompareTo(b.PlayerId);
        });

    /// <summary>
    /// The club leading scorer(s) this season — the MANAGEMENT RECORD line
    /// (asm:283007/283027, singular or plural on a tie). Own goals and departed
    /// players are excluded: neither is a top scorer.
    /// </summary>
    public static SeasonTopScorer? SeasonTop(CompetitionState state, int team, int season)
    {
        if (state?.Scorers is null) return null;
        int best = 0;
        foreach (var r in state.Scorers)
            if (r is not null && r.Team == team && r.PlayerId != OwnGoalPlayerId && r.Goals > best)
                best = r.Goals;
        if (best <= 0) return null;

        var names = new List<string>();
        foreach (var r in state.Scorers)
            if (r is not null && r.Team == team && r.PlayerId != OwnGoalPlayerId && r.Goals == best)
                names.Add(r.Name);
        names.Sort(StringComparer.Ordinal);
        return new SeasonTopScorer { Season = season, Goals = best, Names = names };
    }
}
