using System;
using System.Collections.Generic;

namespace OpenSwos.Competition.Career;

// ============================================================================
// PLAYER COUNTERS AND CLUB LEGENDS — career depth plan feature #8.
//
// Two integers per player (appearances, goals) are the cheapest thing in the
// whole plan and among the most visible: they turn an anonymous striker into
// YOUR striker on the screen the manager looks at most, and they make selling
// him hurt. Goals were already tracked (feature #5); appearances were not.
//
// Three rules this file exists to enforce:
//
//  1. An appearance is credited from the SAME XI the match was simulated or
//     played with — CareerMatchTeam.BuildOrder — for BOTH clubs of EVERY
//     fixture. A leaderboard built only from matches the manager watched would
//     describe one club, exactly as the scorer table would have (feature #5).
//  2. Club counters reset LAZILY. ClubStatsClubId is compared against ClubId on
//     every credit, so a transfer needs no hook at any of the four places in
//     TransferModel/RegenModel where a club changes hands, and a save written
//     before this feature starts counting from the player's current club.
//  3. Legends are kept only for clubs the manager has actually worked at. A row
//     for each of ~29 000 players across 1730 clubs would grow every save for a
//     screen nobody can open.
// ============================================================================

public static class CareerRecords
{
    /// <summary>Appearance milestones worth a chronicle line.</summary>
    private static readonly int[] AppearanceMilestones = [50, 100, 150, 200, 250, 300, 400, 500];
    /// <summary>Goal milestones worth a chronicle line.</summary>
    private static readonly int[] GoalMilestones = [25, 50, 100, 150, 200, 250, 300];
    /// <summary>How low inactivity alone can drive sharpness. See CreditTeam.</summary>
    public const int StaleFloor = 25;
    /// <summary>A win or a defeat by this many goals is worth a diary line.</summary>
    private const int NotableMargin = 3;
    /// <summary>A run this long, either way, is worth a diary line.</summary>
    private const int NotableRun = 5;

    /// <summary>
    /// Credits one appearance to each member of both clubs' XI for a fixture
    /// that has just been recorded. Deterministic and RNG-free: the XI is a
    /// pure function of the squad.
    /// </summary>
    public static void CreditFixture(CompetitionState state, Fixture fixture,
                                     Func<int, OpenSwos.Assets.TeamRecord?>? teamRecord = null)
    {
        if (state?.Career?.World is null || fixture is null) return;
        // The base TEAM.* record decides the club's own lineup order, exactly as
        // it does for the scorer table. ScorerModel.Source is the host accessor
        // both front-ends already install, so the engine never reaches into one.
        if (teamRecord is null && ScorerModel.Source is not null)
        {
            var src = ScorerModel.Source;
            teamRecord = i => { try { return src.Team(i); } catch { return null; } };
        }
        CreditTeam(state, fixture.HomeTeam, teamRecord);
        CreditTeam(state, fixture.AwayTeam, teamRecord);
    }

    private static void CreditTeam(CompetitionState state, int team,
                                   Func<int, OpenSwos.Assets.TeamRecord?>? teamRecord)
    {
        if (team < 0 || team >= state.Teams.Count) return;
        var world = state.Career!.World!;
        ushort gid = state.Teams[team].GlobalId;
        if (world.Clubs is null || !world.Clubs.TryGetValue(gid, out var club) || club?.Squad is null)
            return;

        OpenSwos.Assets.TeamRecord? rec = null;
        if (teamRecord is not null)
            try { rec = teamRecord(state.Teams[team].MasterIndex); } catch { rec = null; }

        var order = CareerMatchTeam.BuildOrder(club, rec);
        int n = Math.Min(11, order.Count);
        bool mine = gid == state.Career.ClubGlobalId;
        for (int i = 0; i < n; i++)
        {
            var p = order[i];
            if (p is null) continue;
            EnsureClubStats(p);
            p.Appearances++;
            p.ClubAppearances++;
            p.SeasonAppearances++;
            // Playing is the other way to stay sharp; training is the one the
            // manager controls (TrainingModel).
            p.Sharpness = Math.Clamp(p.Sharpness + 6, 0, 100);
            if (mine) NoteAppearanceMilestone(state, p);
        }

        // Everyone who did NOT play loses a little sharpness. This is what makes
        // a settled XI cost something: the bench goes stale.
        //
        // The floor matters and is not arbitrary. Left to fall to zero, every
        // reserve at all 1730 clubs would eventually carry the -1 skill nudge
        // TrainingModel.SharpnessSkillDelta applies, which is a global change to
        // the AI nobody asked for. StaleFloor is the bottom of the penalty band,
        // so a permanently benched, untrained player settles at exactly one
        // level below his rating and can never spiral past it. Playing him, or
        // putting him in a session, is what lifts him out.
        foreach (var p in club.Squad)
        {
            if (p is null) continue;
            bool played = false;
            for (int i = 0; i < n; i++) if (ReferenceEquals(order[i], p)) { played = true; break; }
            if (!played) p.Sharpness = Math.Max(StaleFloor, p.Sharpness - 3);
        }
    }

    /// <summary>
    /// Writes the diary's match lines for the manager's own fixture (feature #7).
    /// Deliberately selective: a line per fixture would be a receipt, not a
    /// story. A thrashing either way, and a run of five, are what a supporter
    /// would actually remember.
    /// </summary>
    public static void NoteFixture(CompetitionState state, Fixture f)
    {
        var career = state?.Career;
        if (career is null || f is null || state!.PlayerTeam < 0) return;
        bool home = f.HomeTeam == state.PlayerTeam;
        if (!home && f.AwayTeam != state.PlayerTeam) return;

        int mine = home ? f.HomeGoals : f.AwayGoals;
        int theirs = home ? f.AwayGoals : f.HomeGoals;
        int oppIdx = home ? f.AwayTeam : f.HomeTeam;
        string opponent = oppIdx >= 0 && oppIdx < state.Teams.Count ? state.Teams[oppIdx].Name : "";

        if (mine - theirs >= NotableMargin)
            Chronicle.Add(career, "chron.big_win", "BEAT %a %b", "match", 1,
                a: opponent, b: mine + "-" + theirs, round: f.Round);
        else if (theirs - mine >= NotableMargin)
            Chronicle.Add(career, "chron.big_loss", "BEATEN %b BY %a", "match", 1,
                a: opponent, b: theirs + "-" + mine, round: f.Round);

        // Runs are counted over the club's own PLAYED fixtures in round order,
        // so a cup tie counts exactly as a league game does — which is how a
        // supporter counts them.
        var played = new List<Fixture>();
        foreach (var g in state.Fixtures)
            if (g.Played && (g.HomeTeam == state.PlayerTeam || g.AwayTeam == state.PlayerTeam))
                played.Add(g);
        played.Sort((x, y) => x.Round.CompareTo(y.Round));
        int wins = 0, unbeaten = 0, winless = 0;
        bool winRun = true, unbeatenRun = true, winlessRun = true;
        for (int i = played.Count - 1; i >= 0; i--)
        {
            var g = played[i];
            bool h = g.HomeTeam == state.PlayerTeam;
            int a = h ? g.HomeGoals : g.AwayGoals, b = h ? g.AwayGoals : g.HomeGoals;
            if (winRun && a > b) wins++; else winRun = false;
            if (unbeatenRun && a >= b) unbeaten++; else unbeatenRun = false;
            if (winlessRun && a <= b) winless++; else winlessRun = false;
            if (!winRun && !unbeatenRun && !winlessRun) break;
        }
        if (wins == NotableRun)
            Chronicle.Add(career, "chron.win_run", "%0 WINS IN A ROW", "match", 2,
                n: wins, round: f.Round);
        else if (unbeaten == NotableRun * 2)
            Chronicle.Add(career, "chron.unbeaten", "%0 GAMES UNBEATEN", "match", 2,
                n: unbeaten, round: f.Round);
        else if (winless == NotableRun)
            Chronicle.Add(career, "chron.winless", "%0 GAMES WITHOUT A WIN", "match", 1,
                n: winless, round: f.Round);
    }

    /// <summary>
    /// Called by <see cref="ScorerModel.Credit"/> once a goal has a name against
    /// it, so the club counter and the goal milestones live in one place.
    /// </summary>
    public static void NoteGoal(CompetitionState? state, CareerPlayer? p, int goals)
    {
        if (p is null || goals <= 0) return;
        EnsureClubStats(p);
        p.ClubGoals += goals;
        if (state?.Career is not null && p.ClubId == state.Career.ClubGlobalId)
            NoteGoalMilestone(state, p);
    }

    /// <summary>
    /// Resets the club-scoped counters when the player has changed hands since
    /// they were last touched. A legacy save (ClubStatsClubId 0) simply starts
    /// counting at the club he is at now.
    /// </summary>
    public static void EnsureClubStats(CareerPlayer p)
    {
        if (p.ClubStatsClubId == p.ClubId) return;
        p.ClubStatsClubId = p.ClubId;
        p.ClubAppearances = 0;
        p.ClubGoals = 0;
    }

    private static void NoteAppearanceMilestone(CompetitionState state, CareerPlayer p)
    {
        foreach (int m in AppearanceMilestones)
        {
            if (p.ClubAppearances != m) continue;
            Chronicle.Add(state.Career, "chron.apps", "%a MAKES APPEARANCE %0 FOR THE CLUB",
                "milestone", 1, a: ScorerModel.CleanName(p.Name), n: m,
                round: state.CurrentRound);
            return;
        }
    }

    private static void NoteGoalMilestone(CompetitionState state, CareerPlayer p)
    {
        foreach (int m in GoalMilestones)
        {
            if (p.ClubGoals != m) continue;
            Chronicle.Add(state.Career, "chron.goals", "%a SCORES GOAL %0 FOR THE CLUB",
                "milestone", 1, a: ScorerModel.CleanName(p.Name), n: m,
                round: state.CurrentRound);
            return;
        }
    }

    // ------------------------------------------------------------------
    // Legends
    // ------------------------------------------------------------------

    /// <summary>
    /// Folds this season's counters into the managed club's permanent legends
    /// list. Run once per season, at the rollover, BEFORE the world ages — a
    /// player who retires this summer keeps the record he built.
    /// </summary>
    public static void UpdateLegends(CareerState? career)
    {
        var world = career?.World;
        if (career is null || world?.Clubs is null) return;
        if (!world.Clubs.TryGetValue(career.ClubGlobalId, out var club) || club?.Squad is null) return;

        club.Legends ??= new List<LegendRow>();
        foreach (var p in club.Squad)
        {
            if (p is null) continue;
            EnsureClubStats(p);
            if (p.ClubAppearances <= 0 && p.ClubGoals <= 0) continue;
            LegendRow? row = null;
            foreach (var r in club.Legends) if (r is not null && r.PlayerId == p.Id) { row = r; break; }
            if (row is null)
            {
                row = new LegendRow { PlayerId = p.Id };
                club.Legends.Add(row);
            }
            row.Name = ScorerModel.CleanName(p.Name);
            row.Position = p.Position ?? "";
            row.Appearances = p.ClubAppearances;
            row.Goals = p.ClubGoals;
            row.LastSeason = career.Season;
        }

        // Keep the list to the 40 biggest records at this club. Anything below
        // that will never appear on a legends screen.
        club.Legends.Sort(CompareLegends);
        while (club.Legends.Count > 40) club.Legends.RemoveAt(club.Legends.Count - 1);
    }

    /// <summary>Zeroes the per-season appearance counter for the whole world.</summary>
    public static void StartNewSeason(CareerWorld? world)
    {
        if (world is null) return;
        if (world.Clubs is not null)
            foreach (var kv in world.Clubs)
            {
                var squad = kv.Value?.Squad;
                if (squad is null) continue;
                foreach (var p in squad) if (p is not null) p.SeasonAppearances = 0;
            }
        if (world.FreeAgents is not null)
            foreach (var p in world.FreeAgents) if (p is not null) p.SeasonAppearances = 0;
    }

    /// <summary>The managed club's legends, best record first.</summary>
    public static List<LegendRow> Legends(CareerState? career, int limit = 40)
    {
        var list = new List<LegendRow>();
        var world = career?.World;
        if (career is null || world?.Clubs is null) return list;
        if (!world.Clubs.TryGetValue(career.ClubGlobalId, out var club) || club?.Legends is null)
            return list;
        foreach (var r in club.Legends) if (r is not null) list.Add(r);
        list.Sort(CompareLegends);
        while (list.Count > limit) list.RemoveAt(list.Count - 1);
        return list;
    }

    // Appearances first — a legends list is about service, and the goal column
    // is right there for anyone who disagrees. Ties broken by goals, then by id
    // so the order is stable across saves.
    private static int CompareLegends(LegendRow a, LegendRow b)
    {
        int byApps = b.Appearances.CompareTo(a.Appearances);
        if (byApps != 0) return byApps;
        int byGoals = b.Goals.CompareTo(a.Goals);
        return byGoals != 0 ? byGoals : a.PlayerId.CompareTo(b.PlayerId);
    }
}
