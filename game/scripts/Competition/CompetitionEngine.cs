using System;
using System.Collections.Generic;
using OpenSwos.Menu;

namespace OpenSwos.Competition;

// ============================================================================
// CompetitionEngine — pure, static, deterministic competition logic for
// league / cup / tournament / career play. No Godot types, no System.Random:
// every random decision (draws, AI scores, penalty shootouts) flows through
// the xorshift32 state persisted in CompetitionState.RngState so a saved
// competition replays identically after load.
//
// Contract: see the comment block at the bottom of CompetitionModels.cs.
// ============================================================================

public static class CompetitionEngine
{
    // ------------------------------------------------------------------
    // Creation
    // ------------------------------------------------------------------

    /// Round-robin league via the Berger/circle method. Odd team counts get a
    /// bye (the padded slot simply produces no fixture). doubleRoundRobin
    /// appends the mirrored second half with continuing round numbers.
    public static CompetitionState CreateLeague(
        string name, List<TeamRef> teams, int playerTeam, bool doubleRoundRobin, int seed)
    {
        var s = NewState(CompetitionKind.League, name, teams, playerTeam, seed);
        s.DoubleRoundRobin = doubleRoundRobin;

        List<List<(int Home, int Away)>> rounds = RoundRobin(teams.Count);
        int half = rounds.Count;
        for (int r = 0; r < half; r++)
            foreach (var (h, a) in rounds[r])
                s.Fixtures.Add(new Fixture { Round = r, Stage = "LEAGUE", HomeTeam = h, AwayTeam = a });
        if (doubleRoundRobin)
            for (int r = 0; r < half; r++)
                foreach (var (h, a) in rounds[r])
                    s.Fixtures.Add(new Fixture { Round = half + r, Stage = "LEAGUE", HomeTeam = a, AwayTeam = h });

        s.TotalRounds = doubleRoundRobin ? half * 2 : half;
        return s;
    }

    /// Single-elimination cup. Round 1 is drawn at random (deterministic RNG);
    /// later rounds are created by RecordResult as each round completes.
    public static CompetitionState CreateCup(string name, List<TeamRef> teams, int playerTeam, int seed)
    {
        int c = teams?.Count ?? 0;
        if (c != 4 && c != 8 && c != 16 && c != 32)
            throw new ArgumentException("CUP NEEDS 4, 8, 16 OR 32 TEAMS", nameof(teams));

        var s = NewState(CompetitionKind.Cup, name, teams!, playerTeam, seed);
        var order = new List<int>(c);
        for (int i = 0; i < c; i++) order.Add(i);
        Shuffle(s, order);

        string stage = KnockoutStageLabel(c);
        for (int i = 0; i + 1 < c; i += 2)
            s.Fixtures.Add(new Fixture { Round = 0, Stage = stage, HomeTeam = order[i], AwayTeam = order[i + 1] });

        s.TotalRounds = Log2(c);
        return s;
    }

    /// Groups-of-4 tournament: random group draw, single round-robin inside
    /// each group (rounds 0..2), then a knockout built from the group tables
    /// once every group game is played (top 2 advance, cross-paired).
    public static CompetitionState CreateTournament(
        string name, List<TeamRef> teams, int playerTeam, int groupCount, int seed)
    {
        if (groupCount < 2 || groupCount > 8 || (groupCount & (groupCount - 1)) != 0)
            throw new ArgumentException("GROUP COUNT MUST BE 2, 4 OR 8", nameof(groupCount));
        if (teams is null || teams.Count != groupCount * 4)
            throw new ArgumentException("TOURNAMENT NEEDS EXACTLY 4 TEAMS PER GROUP", nameof(teams));

        var s = NewState(CompetitionKind.Tournament, name, teams, playerTeam, seed);
        s.GroupCount = groupCount;

        // Random draw: shuffled team indices, 4 consecutive per group.
        var order = new List<int>(teams.Count);
        for (int i = 0; i < teams.Count; i++) order.Add(i);
        Shuffle(s, order);
        for (int g = 0; g < groupCount; g++)
            for (int m = 0; m < 4; m++)
                s.GroupOf[order[g * 4 + m]] = g;

        // Single round-robin inside each group; all groups share rounds 0..2.
        List<List<(int Home, int Away)>> rr = RoundRobin(4);
        for (int r = 0; r < rr.Count; r++)
            for (int g = 0; g < groupCount; g++)
            {
                string stage = "GROUP " + (char)('A' + g);
                foreach (var (h, a) in rr[r])
                    s.Fixtures.Add(new Fixture
                    {
                        Round = r,
                        Stage = stage,
                        HomeTeam = order[g * 4 + h],
                        AwayTeam = order[g * 4 + a],
                    });
            }

        s.TotalRounds = GroupStageRounds + Log2(groupCount * 2);
        return s;
    }

    /// Career season 1: double round-robin league plus a domestic cup drawn
    /// from cupTeams (which must include the player's club). Cup rounds are
    /// interleaved into the league calendar (one cup round after every
    /// ~quarter of the league schedule, the cup final after the last league
    /// round). playerTeam indexes leagueTeams.
    public static CompetitionState CreateCareer(
        string name, List<TeamRef> leagueTeams, List<TeamRef> cupTeams,
        int playerTeam, int nation, int division, int seed)
    {
        if (leagueTeams is null || leagueTeams.Count < 2)
            throw new ArgumentException("CAREER LEAGUE NEEDS AT LEAST 2 TEAMS", nameof(leagueTeams));
        if (playerTeam < 0 || playerTeam >= leagueTeams.Count)
            throw new ArgumentOutOfRangeException(nameof(playerTeam));

        var s = new CompetitionState
        {
            Kind = CompetitionKind.Career,
            Name = name ?? "",
            RngState = SeedToRng(seed),
            Career = new CareerState
            {
                Season = 1,
                Nation = nation,
                Division = division,
                ClubName = leagueTeams[playerTeam].Name,
                ClubGlobalId = leagueTeams[playerTeam].GlobalId,
            },
        };
        BuildCareerSeason(s, leagueTeams, cupTeams, playerTeam);
        return s;
    }

    // ------------------------------------------------------------------
    // Fixture navigation
    // ------------------------------------------------------------------

    /// First unplayed fixture involving PlayerTeam (by Round, then list order).
    public static Fixture? NextPlayerFixture(CompetitionState state)
    {
        if (state.PlayerTeam < 0) return null;
        Fixture? best = null;
        foreach (var f in state.Fixtures)
            if (!f.Played && Involves(f, state.PlayerTeam) && (best is null || f.Round < best.Round))
                best = f;
        return best;
    }

    /// First unplayed fixture of any team (by Round, then list order).
    public static Fixture? NextFixture(CompetitionState state)
    {
        Fixture? best = null;
        foreach (var f in state.Fixtures)
            if (!f.Played && (best is null || f.Round < best.Round))
                best = f;
        return best;
    }

    // ------------------------------------------------------------------
    // Result recording and progression
    // ------------------------------------------------------------------

    /// Writes a result and drives all competition progression: penalty
    /// shootout on level knockout ties, next knockout round when a round
    /// completes, Finished/Champion, CurrentRound advance, and career
    /// season close-out. A second call on an already-played fixture is a
    /// no-op; a fixture that is not part of state throws.
    /// <param name="credits">
    /// The REAL scorers of a match the manager played, from
    /// <c>Main.CaptureMatchOutcome</c>. Null (every simulated fixture) makes the
    /// engine attribute the goals itself — see
    /// <see cref="OpenSwos.Competition.Career.ScorerModel"/>.
    /// </param>
    public static void RecordResult(CompetitionState state, Fixture fixture, int homeGoals, int awayGoals,
        IReadOnlyList<OpenSwos.Competition.Career.GoalCredit>? credits = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (fixture is null) throw new ArgumentNullException(nameof(fixture));
        if (!ContainsFixture(state, fixture))
            throw new ArgumentException("FIXTURE IS NOT PART OF THIS COMPETITION", nameof(fixture));
        if (fixture.Played) return;   // ignore double-record

        fixture.HomeGoals = Math.Max(0, homeGoals);
        fixture.AwayGoals = Math.Max(0, awayGoals);
        fixture.Played = true;

        // Career depth plan feature #5: every goal gets a name against it.
        CreditFixtureScorers(state, fixture, credits);
        // Feature #8: and every player who was in the XI gets an appearance.
        // Both clubs, every fixture, played or simulated — a counter built only
        // from matches the manager watched would describe one club.
        OpenSwos.Competition.Career.CareerRecords.CreditFixture(state, fixture);
        // Feature #7: and the diary gets the results worth remembering.
        if (state.Kind == CompetitionKind.Career)
            OpenSwos.Competition.Career.CareerRecords.NoteFixture(state, fixture);

        // Knockout ties never end level — resolve on simulated penalties.
        if (IsKnockoutStage(fixture.Stage) && fixture.HomeGoals == fixture.AwayGoals)
        {
            fixture.OnPenalties = true;
            fixture.PenaltyWinner = SimulateShootout(state, fixture);
        }

        if (RoundIsComplete(state, fixture.Round))
            OnRoundComplete(state, fixture.Round);

        AdvanceCurrentRound(state);

        if (state.Kind == CompetitionKind.Career)
        {
            // Career depth plan feature #2: the chairman reads the table after
            // every match the managed club plays. Runs BEFORE the season is
            // closed so a mid-season dismissal is possible, and only while the
            // league is still running.
            if (Involves(fixture, state.PlayerTeam))
                RunChairmanAfterFixture(state, fixture);
            MaybeCloseCareerSeason(state);
        }
    }

    /// <summary>
    /// Puts a name on every goal in a fixture. A played match hands us its real
    /// scorers; anything they do not cover (all of a simulated scoreline, or a
    /// goal the sim could not attribute) is shared out by
    /// <see cref="OpenSwos.Competition.Career.ScorerModel"/> from the
    /// competition's own RNG, so reloading a save rebuilds the same table.
    /// </summary>
    private static void CreditFixtureScorers(CompetitionState s, Fixture f,
        IReadOnlyList<OpenSwos.Competition.Career.GoalCredit>? credits)
    {
        var world = s.Career?.World;
        int homeDone = 0, awayDone = 0;
        if (credits is not null)
        {
            foreach (var g in credits)
            {
                if (g.Goals <= 0) continue;
                if (g.Team != f.HomeTeam && g.Team != f.AwayTeam) continue;
                // Never credit more than the scoreline: a mismatch would show up
                // as a striker on 40 goals, and the fixture's score is the truth.
                bool home = g.Team == f.HomeTeam;
                int room = home ? f.HomeGoals - homeDone : f.AwayGoals - awayDone;
                int n = Math.Min(g.Goals, room);
                if (n <= 0) continue;
                if (g.OwnGoal)
                    OpenSwos.Competition.Career.ScorerModel.CreditOwnGoal(s, g.Team, n);
                else
                    OpenSwos.Competition.Career.ScorerModel.Credit(s, g.Team, g.PlayerId, g.Name, n, world);
                if (home) homeDone += n; else awayDone += n;
            }
        }
        OpenSwos.Competition.Career.ScorerModel.AttributeSimulated(
            s, f.HomeTeam, f.HomeGoals - homeDone, n => NextInt(s, n));
        OpenSwos.Competition.Career.ScorerModel.AttributeSimulated(
            s, f.AwayTeam, f.AwayGoals - awayDone, n => NextInt(s, n));
    }

    /// Feeds the chairman the state of the table and the bank after one of the
    /// managed club's fixtures. Engine-side, so the desktop menu and the browser
    /// client get identical memos without either implementing a rule.
    private static void RunChairmanAfterFixture(CompetitionState s, Fixture fixture)
    {
        var career = s.Career;
        if (career is null || career.Retired || career.Sacked) return;

        bool isLeague = fixture.Stage == "LEAGUE";

        int leaguePlayed = 0, leagueTotal = 0;
        foreach (var f in s.Fixtures)
        {
            if (f.Stage != "LEAGUE" || !Involves(f, s.PlayerTeam)) continue;
            leagueTotal++;
            if (f.Played) leaguePlayed++;
        }
        // Season already complete -> the end-of-season verdict handles it.
        if (leagueTotal > 0 && leaguePlayed >= leagueTotal) return;
        int percent = leagueTotal > 0 ? leaguePlayed * 100 / leagueTotal : 0;

        var table = Table(s, "LEAGUE");
        int pos = 0;
        for (int i = 0; i < table.Count; i++)
            if (table[i].Team == s.PlayerTeam) { pos = i + 1; break; }

        int winner = WinnerOf(fixture);
        bool won = winner == s.PlayerTeam;

        long budget = 0L;
        if (career.World is not null
            && career.World.Clubs.TryGetValue(career.ClubGlobalId, out var club))
            budget = club.Budget;

        // Feature #3 needs the manager's standing to exist before anything reads
        // it; a career created before job offers shipped has none.
        int myStrength = s.PlayerTeam >= 0 && s.PlayerTeam < s.Teams.Count
            ? s.Teams[s.PlayerTeam].Strength : 3;
        OpenSwos.Competition.Career.JobMarket.EnsureSeeded(career, myStrength);

        // Fix what the board expects of this squad, once, for the whole season.
        EnsureSeasonExpectation(s);

        bool sacked = OpenSwos.Competition.Career.ChairmanModel.AfterPlayerFixture(
            career,
            OpenSwos.Competition.Career.ChairmanModel.Patience,
            pos, table.Count, percent, isLeague, won, budget);

        // Career depth plan feature #3: the job market runs on the same beat as
        // the chairman. A dismissed manager goes looking for work at once, so
        // the offers are on the table while the career screen still is;
        // otherwise clubs approach late in the season, and lose interest the
        // moment his own board turns on him.
        if (sacked)
            OpenSwos.Competition.Career.JobMarket.AfterSacking(career, career.World);
        else
            OpenSwos.Competition.Career.JobMarket.AfterPlayerFixture(
                career, career.World, leaguePlayed, leagueTotal);
    }

    /// Simulates and records every unplayed current-round fixture that does
    /// not involve the player.
    public static void SimulateAiRound(CompetitionState state)
    {
        int round = state.CurrentRound;
        // RecordResult may append next-round fixtures; those have a higher
        // Round so the index loop with a round filter stays correct.
        for (int i = 0; i < state.Fixtures.Count; i++)
        {
            var f = state.Fixtures[i];
            if (f.Round != round || f.Played) continue;
            if (Involves(f, state.PlayerTeam)) continue;
            var (h, a) = SimulateResult(state, f);
            RecordResult(state, f, h, a);
        }
    }

    /// Simulates fixtures in order until the next one involves the player or
    /// the competition is finished.
    public static void FastForwardAiOnly(CompetitionState state)
    {
        while (!state.Finished)
        {
            var f = NextFixture(state);
            if (f is null) break;
            if (Involves(f, state.PlayerTeam)) break;
            var (h, a) = SimulateResult(state, f);
            RecordResult(state, f, h, a);
        }
    }

    /// <summary>
    /// How good a club is RIGHT NOW, on the same 1..7 scale as
    /// <see cref="TeamRef.Strength"/> but as a fraction, read from the career
    /// world when there is one.
    ///
    /// TeamRef.Strength is a snapshot taken from the read-only TEAM.* roster
    /// when the season's entrant list was built, so on its own it describes
    /// every club as it was in 1996 forever. Found by PLAYING a career
    /// (2026-08-23): season 2 at WIDZEW LODZ played out to an identical table
    /// whether or not a 9.4M striker had been signed — transfers, ageing,
    /// growth, retirements and youth intakes were all invisible to the
    /// simulated results, which is most of what a career is.
    ///
    /// Falls back to the snapshot for a non-career competition, or a club the
    /// world does not know.
    /// </summary>
    public static double LiveStrength(CompetitionState state, int team)
    {
        if (team < 0 || team >= state.Teams.Count) return 3.0;
        var world = state.Career?.World;
        if (world?.Clubs is not null)
        {
            ushort gid = state.Teams[team].GlobalId;
            if (world.Clubs.TryGetValue(gid, out var club)
                && club?.Squad is not null && club.Squad.Count > 0)
            {
                int total = 0, n = 0;
                foreach (var p in club.Squad)
                {
                    if (p is null || p.Retired) continue;
                    total += p.EffectiveOverall();
                    n++;
                }
                if (n > 0) return (double)total / n;
            }
        }
        return state.Teams[team].Strength;
    }

    /// Strength-weighted random score: small Poisson (Knuth) around a base
    /// expectation with home advantage. Deterministic via RngState.
    public static (int Home, int Away) SimulateResult(CompetitionState state, Fixture fixture)
    {
        double strH = LiveStrength(state, fixture.HomeTeam);
        double strA = LiveStrength(state, fixture.AwayTeam);
        double baseH = Math.Clamp(1.30 + 0.22 * (strH - strA), 0.15, 4.5);
        double baseA = Math.Clamp(1.05 + 0.22 * (strA - strH), 0.15, 4.5);
        return (Poisson(state, baseH), Poisson(state, baseA));
    }

    // ------------------------------------------------------------------
    // Career season rollover
    // ------------------------------------------------------------------

    /// True when a career season has fully concluded (league + cup) and the
    /// next season has not been built yet.
    public static bool PendingSeasonRollover(CompetitionState s)
        => s.Kind == CompetitionKind.Career && s.Career is not null && s.Finished;

    /// Builds the next career season. The caller decides promotion/relegation
    /// and supplies the new league and cup entrant lists; the player's club is
    /// located in newLeagueTeams by GlobalId (name as fallback).
    public static void AdvanceCareerSeason(
        CompetitionState s,
        System.Collections.Generic.List<TeamRef> newLeagueTeams,
        System.Collections.Generic.List<TeamRef> newCupTeams,
        int newDivision)
    {
        if (s.Kind != CompetitionKind.Career || s.Career is null)
            throw new InvalidOperationException("NOT A CAREER COMPETITION");
        if (newLeagueTeams is null || newLeagueTeams.Count < 2)
            throw new ArgumentException("NEW LEAGUE NEEDS AT LEAST 2 TEAMS", nameof(newLeagueTeams));

        var c = s.Career;
        // Career depth plan feature #3: when the manager has accepted a job
        // elsewhere, the club he must be found in is the NEW one — the caller
        // has built next season's pools around it.
        var pendingMove = OpenSwos.Competition.Career.JobMarket.AcceptedOffer(c);
        ushort wantId = pendingMove?.ClubGlobalId ?? c.ClubGlobalId;
        string wantName = pendingMove?.ClubName ?? c.ClubName;

        int idx = -1;
        if (wantId != 0)
            for (int i = 0; i < newLeagueTeams.Count; i++)
                if (newLeagueTeams[i].GlobalId == wantId) { idx = i; break; }
        if (idx < 0)
            for (int i = 0; i < newLeagueTeams.Count; i++)
                if (string.Equals(newLeagueTeams[i].Name, wantName, StringComparison.OrdinalIgnoreCase))
                { idx = i; break; }
        if (idx < 0)
            throw new ArgumentException("PLAYER CLUB NOT FOUND IN NEW LEAGUE TEAMS", nameof(newLeagueTeams));

        // The books are closed exactly once per season. A dismissed manager who
        // then finds a new job re-enters this method to take it, and must not
        // age the world, pay the wages or draw up the accounts a second time.
        if (c.World is not null && !c.SeasonBooksClosed)
        {
            // Career depth plan feature #1: the finished season's table and cup
            // run become prize money. Built BEFORE the fixtures are rebuilt —
            // BuildCareerSeason below wipes s.Fixtures.
            var seasonResults = BuildSeasonResults(s, c.Division, newDivision);

            // Feature #8: fold the season's counters into the club's permanent
            // legends list BEFORE anybody retires or is sold, so the record a
            // player built here survives him leaving.
            OpenSwos.Competition.Career.CareerRecords.UpdateLegends(c);

            OpenSwos.Competition.Career.SeasonProgression.AgeAndRetire(c.World);
            // Feature #6: the academy has always produced players every summer;
            // what was missing was the MOMENT. Snapshot the managed club's squad
            // so the intake screen can name exactly who walked in.
            var beforeIntake = SnapshotSquadIds(c);
            OpenSwos.Competition.Career.RegenModel.RunRegen(c.World);
            RecordYouthIntake(c, beforeIntake);
            OpenSwos.Competition.Career.StaffModel.RunClubStaffAI(c.World);
            OpenSwos.Competition.Career.Scouting.RunScoutingAI(c.World);
            OpenSwos.Competition.Career.GrowthModel.ApplySeasonGrowth(c.World);
            // Career depth plan feature #2: the chairman's end-of-season verdict.
            // Judged BEFORE the world rolls forward, from the season that was
            // actually played. A dismissal ends the career, so the next season
            // is never built (see the early return below).
            bool dismissed = RunChairmanSeasonVerdict(s, c.Division, newDivision, seasonResults);
            // Feature #7: the board's verdict is the loudest thing that happens
            // all summer, so the chronicle carries it too.
            if (dismissed)
                OpenSwos.Competition.Career.Chronicle.Add(c, "chron.sacked",
                    "SACKED BY %a", "board", 2, a: c.ClubName);
            else if (c.LastVerdict >= 0)
                OpenSwos.Competition.Career.Chronicle.Add(c, "chron.verdict",
                    "THE BOARD RATED THE SEASON %a", "board", 1,
                    a: VerdictWord(c.LastVerdict));

            var account = OpenSwos.Competition.Career.Finance.ApplySeasonFinances(
                c.World, seasonResults, c.Season, c.ClubGlobalId);
            if (account is not null)
            {
                c.LastAccount = account;
                c.AccountHistory.Add(account);
                // Keep the history bounded — a 20-season career is the original's
                // limit and nothing reads further back than that.
                while (c.AccountHistory.Count > 20) c.AccountHistory.RemoveAt(0);
            }
            // Feature #4: the national side plays its tournament for the season
            // that has just ended, and the federation then reviews the annually
            // reviewable contract. Runs AFTER the verdict so a manager sacked by
            // his club still finishes the international season he was appointed
            // for, and BEFORE the offer below so he is never offered the job he
            // has just lost.
            OpenSwos.Competition.Career.NationalJob.RunSeason(c, c.World);
            OpenSwos.Competition.Career.NationalJob.MaybeOffer(c, c.World, c.Nation);

            // Transfer market resets each season: offers/list cleared, negotiation
            // budget refilled to 6, sell/buy quotas zeroed (swos.asm:127226).
            OpenSwos.Competition.Career.TransferOffers.ResetForNewSeason(c);

            c.SeasonBooksClosed = true;

            // Sacked: the books are closed and the record is written. Feature #3
            // gives him one more thing to do — go and find another club. If
            // somebody wants him he can accept and call back in to take the job;
            // if nobody does, the competition stays Finished, which is what both
            // clients already render as "career over".
            if (dismissed)
            {
                OpenSwos.Competition.Career.JobMarket.AfterSacking(c, c.World);
                pendingMove = OpenSwos.Competition.Career.JobMarket.AcceptedOffer(c);
                if (pendingMove is null) return;
            }
        }
        else if (c.Sacked && pendingMove is null)
        {
            // Out of work with nothing on the table: nothing to advance into.
            return;
        }

        c.Season++;
        if (c.World is not null)
        {
            c.World.Season = c.Season;
            // Feature #8 / training: per-season counters and pre-season fitness.
            OpenSwos.Competition.Career.CareerRecords.StartNewSeason(c.World);
            OpenSwos.Competition.Career.TrainingModel.StartNewSeason(c.World);
        }
        // Training is scheduled per round, and the rounds start again.
        c.TrainingLastRound = -1;
        c.TrainingLastSeason = -1;
        c.TrainingReport = new System.Collections.Generic.List<
            OpenSwos.Competition.Career.TrainingResultRow>();
        // Feature #2: clear last season's warning ladders and file the
        // chairman's new-season note, now that the counter reads the new season.
        OpenSwos.Competition.Career.ChairmanModel.StartNewSeason(c);
        // Feature #3: last season's letters were offers to coach the season that
        // has just started, so they lapse here. Done BEFORE the move so an
        // accepted offer is still on the pile when ApplyPendingMove reads it.
        if (!OpenSwos.Competition.Career.JobMarket.HasAcceptedOffer(c))
            OpenSwos.Competition.Career.JobMarket.StartNewSeason(c);
        c.Division = newDivision;
        // A new season, a new squad, a new league: the board's expectation is
        // taken again when the first match of it is played.
        c.SeasonExpectedPosition = 0;
        c.SeasonLeagueTeams = 0;
        // Feature #3: the move happens here, which is what the original's
        // 'FROM THE START OF NEXT SEASON' means. It rewrites the club, the
        // nation and the division, files the farewell and welcome letters, and
        // puts a sacked manager back in work.
        var moved = OpenSwos.Competition.Career.JobMarket.ApplyPendingMove(c);
        if (moved is null) c.SeasonsAtClub++;
        c.SeasonBooksClosed = false;
        c.ClubName = newLeagueTeams[idx].Name;
        c.ClubGlobalId = newLeagueTeams[idx].GlobalId;
        BuildCareerSeason(s, newLeagueTeams, newCupTeams, idx);
    }

    /// <summary>The five-grade verdict as one word, for a chronicle line.</summary>
    private static string VerdictWord(int verdict) => verdict switch
    {
        0 => "EXCELLENT",
        1 => "GOOD",
        2 => "UP AND DOWN",
        3 => "NOT VERY GOOD",
        _ => "VERY DISAPPOINTING",
    };

    /// <summary>
    /// Ids currently in the managed club's squad — the "before" half of the
    /// youth-intake diff (feature #6). Diffing is used rather than threading an
    /// out-parameter through RegenModel because the regen also moves surplus
    /// players out to the free-agent pool, and the intake is what ARRIVED.
    /// </summary>
    private static HashSet<int> SnapshotSquadIds(CareerState c)
    {
        var set = new HashSet<int>();
        var world = c.World;
        if (world?.Clubs is not null
            && world.Clubs.TryGetValue(c.ClubGlobalId, out var club) && club?.Squad is not null)
            foreach (var p in club.Squad) if (p is not null) set.Add(p.Id);
        return set;
    }

    private static void RecordYouthIntake(CareerState c, HashSet<int> before)
    {
        var world = c.World;
        var fresh = new System.Collections.Generic.List<int>();
        if (world?.Clubs is not null
            && world.Clubs.TryGetValue(c.ClubGlobalId, out var club) && club?.Squad is not null)
            foreach (var p in club.Squad)
                if (p is not null && p.Generated && !before.Contains(p.Id)) fresh.Add(p.Id);

        c.YouthIntakeIds = fresh;
        c.YouthIntakeSeason = c.Season + 1;   // they belong to the season about to start
        c.YouthIntakeSeen = false;
        if (fresh.Count > 0)
            OpenSwos.Competition.Career.Chronicle.Add(c, "chron.intake",
                "THE ACADEMY PRODUCED %0 PLAYER(S)", "youth", 1, n: fresh.Count);
    }

    // ------------------------------------------------------------------
    // Standings / status queries
    // ------------------------------------------------------------------

    /// League/group standings over played fixtures whose Stage starts with
    /// stagePrefix. Every participating team of the stage gets a row, even
    /// with zero games played. Sort: Pts desc, GD desc, GF desc, Name asc.
    public static List<TableRow> Table(CompetitionState state, string stagePrefix)
    {
        var rows = new Dictionary<int, TableRow>();
        TableRow Row(int team)
        {
            if (!rows.TryGetValue(team, out var row))
            {
                row = new TableRow { Team = team };
                rows[team] = row;
            }
            return row;
        }

        // Group prefixes ("GROUP B") also seed rows from the draw so teams
        // with no fixture yet still appear.
        if (stagePrefix.Length == 7 && stagePrefix.StartsWith("GROUP ", StringComparison.Ordinal))
        {
            int g = stagePrefix[6] - 'A';
            for (int i = 0; i < state.GroupOf.Count && i < state.Teams.Count; i++)
                if (state.GroupOf[i] == g) Row(i);
        }

        foreach (var f in state.Fixtures)
        {
            if (!f.Stage.StartsWith(stagePrefix, StringComparison.Ordinal)) continue;
            var home = Row(f.HomeTeam);
            var away = Row(f.AwayTeam);
            if (!f.Played) continue;

            home.Played++; away.Played++;
            home.GoalsFor += f.HomeGoals; home.GoalsAgainst += f.AwayGoals;
            away.GoalsFor += f.AwayGoals; away.GoalsAgainst += f.HomeGoals;
            if (f.HomeGoals > f.AwayGoals) { home.Won++; away.Lost++; home.Points += 3; }
            else if (f.HomeGoals < f.AwayGoals) { away.Won++; home.Lost++; away.Points += 3; }
            else { home.Drawn++; away.Drawn++; home.Points++; away.Points++; }
        }

        var list = new List<TableRow>(rows.Values);
        list.Sort((x, y) =>
        {
            int cmp = y.Points.CompareTo(x.Points);
            if (cmp != 0) return cmp;
            cmp = y.GoalDiff.CompareTo(x.GoalDiff);
            if (cmp != 0) return cmp;
            cmp = y.GoalsFor.CompareTo(x.GoalsFor);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(state.Teams[x.Team].Name, state.Teams[y.Team].Name);
            if (cmp != 0) return cmp;
            return x.Team.CompareTo(y.Team);   // total order -> deterministic sort
        });
        return list;
    }

    /// Label of the next unplayed fixture's stage with league progress, e.g.
    /// "LEAGUE - ROUND 7/22", "CUP QUARTER FINAL", "GROUP B", "FINAL".
    public static string RoundLabel(CompetitionState state)
    {
        var f = NextFixture(state);
        if (f is null) return state.Finished
            ? Loc.Tr("comp.competition_complete", "COMPETITION COMPLETE")
            : Loc.Tr("comp.no_fixtures", "NO FIXTURES");
        if (f.Stage == "LEAGUE")
        {
            List<int> leagueRounds = DistinctStageRounds(state, "LEAGUE");
            int idx = leagueRounds.IndexOf(f.Round) + 1;
            return string.Format(
                Loc.Tr("comp.league_round", "LEAGUE - ROUND {0}/{1}"), idx, leagueRounds.Count);
        }
        return CompLoc.TrStage(f.Stage);
    }

    /// True while the player still has a current-or-future fixture: an
    /// unplayed fixture now, a live group-stage campaign, or a won knockout
    /// tie whose next round has not been drawn yet.
    public static bool IsPlayerAlive(CompetitionState state)
    {
        int p = state.PlayerTeam;
        if (p < 0 || p >= state.Teams.Count) return false;
        if (state.Finished) return false;

        foreach (var f in state.Fixtures)
            if (!f.Played && Involves(f, p)) return true;

        // Tournament group stage still running: qualification is open until
        // every group game is played (at which point the knockout exists).
        if (state.Kind == CompetitionKind.Tournament && !AllGroupFixturesPlayed(state))
        {
            bool groupDone = true;
            int g = (p < state.GroupOf.Count) ? state.GroupOf[p] : -1;
            foreach (var f in state.Fixtures)
                if (!f.Played && f.Stage.StartsWith("GROUP", StringComparison.Ordinal)
                    && (Involves(f, p) || (g >= 0 && f.Stage.Length == 7 && f.Stage[6] - 'A' == g)))
                    groupDone = false;
            if (!groupDone) return true;
            // Player's group finished early: alive iff currently in the top 2.
            if (g >= 0)
            {
                var table = Table(state, "GROUP " + (char)('A' + g));
                for (int i = 0; i < table.Count && i < 2; i++)
                    if (table[i].Team == p) return true;
                return false;
            }
            return true;
        }

        // Knockout progression pending: alive if the player won the latest
        // knockout tie they played (the next round is drawn on completion).
        Fixture? last = null;
        foreach (var f in state.Fixtures)
            if (f.Played && IsKnockoutStage(f.Stage) && Involves(f, p)
                && (last is null || f.Round > last.Round))
                last = f;
        return last is not null && WinnerOf(last) == p;
    }

    /// One short status line for the player, uppercase ASCII.
    public static string PlayerSummary(CompetitionState state)
    {
        int p = state.PlayerTeam;
        if (p < 0 || p >= state.Teams.Count) return Loc.Tr("comp.no_player_team", "NO PLAYER TEAM");
        if (state.Finished && state.Champion == p) return Loc.Tr("comp.you_champion", "YOU ARE THE CHAMPION");

        if (state.Kind == CompetitionKind.League || state.Kind == CompetitionKind.Career)
        {
            var table = Table(state, "LEAGUE");
            for (int i = 0; i < table.Count; i++)
                if (table[i].Team == p)
                    return string.Format(Loc.Tr("comp.you_are_pos", "YOU ARE {0}"), CompLoc.Ordinal(i + 1));
            return Loc.Tr("comp.you_unplaced", "YOU ARE UNPLACED");
        }

        // Cup / tournament.
        if (!IsPlayerAlive(state)) return Loc.Tr("comp.you_eliminated", "YOU WERE ELIMINATED");
        var next = NextPlayerFixture(state);
        if (next is null) return Loc.Tr("comp.you_through", "YOU ARE THROUGH TO THE NEXT ROUND");
        if (next.Stage.StartsWith("GROUP", StringComparison.Ordinal))
            return string.Format(Loc.Tr("comp.you_in", "YOU ARE IN {0}"), CompLoc.TrStage(next.Stage));
        return string.Format(Loc.Tr("comp.you_in_the", "YOU ARE IN THE {0}"), CompLoc.TrStage(next.Stage));
    }

    // ==================================================================
    // Internals
    // ==================================================================

    private const int GroupStageRounds = 3;   // single round-robin of a 4-team group

    // --- deterministic RNG (xorshift32 over CompetitionState.RngState) ---

    private static uint SeedToRng(int seed)
    {
        uint v = unchecked((uint)seed);
        return v == 0 ? 0x9E3779B9u : v;
    }

    private static uint NextRng(CompetitionState s)
    {
        uint x = s.RngState;
        if (x == 0) x = 0x9E3779B9u;   // xorshift32 must never sit on 0
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        s.RngState = x;
        return x;
    }

    private static int NextInt(CompetitionState s, int maxExclusive)
        => maxExclusive <= 1 ? 0 : (int)(NextRng(s) % (uint)maxExclusive);

    private static double NextDouble(CompetitionState s)
        => (NextRng(s) >> 8) * (1.0 / 16777216.0);   // 24-bit mantissa in [0,1)

    private static void Shuffle(CompetitionState s, List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(s, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static int Poisson(CompetitionState s, double lambda)
    {
        // Knuth: multiply uniforms until below e^-lambda.
        double limit = Math.Exp(-lambda);
        int k = 0;
        double p = 1.0;
        do { k++; p *= NextDouble(s); } while (p > limit && k < 16);
        return k - 1;
    }

    // --- construction helpers ---

    private static CompetitionState NewState(
        CompetitionKind kind, string name, List<TeamRef> teams, int playerTeam, int seed)
    {
        if (teams is null || teams.Count < 2)
            throw new ArgumentException("AT LEAST 2 TEAMS REQUIRED", nameof(teams));
        if (playerTeam < -1 || playerTeam >= teams.Count)
            throw new ArgumentOutOfRangeException(nameof(playerTeam));
        var s = new CompetitionState
        {
            Kind = kind,
            Name = name ?? "",
            Teams = new List<TeamRef>(teams),
            PlayerTeam = playerTeam,
            RngState = SeedToRng(seed),
        };
        for (int i = 0; i < s.Teams.Count; i++) s.GroupOf.Add(-1);
        return s;
    }

    /// Berger/circle round-robin over local indices 0..teamCount-1. Odd counts
    /// get a padded bye slot whose pairings are skipped, so every real team
    /// still meets every other exactly once across teamCount rounds.
    private static List<List<(int Home, int Away)>> RoundRobin(int teamCount)
    {
        int n = (teamCount % 2 == 0) ? teamCount : teamCount + 1;
        var slots = new int[n];
        for (int i = 0; i < n; i++) slots[i] = i;

        var rounds = new List<List<(int, int)>>(n - 1);
        for (int r = 0; r < n - 1; r++)
        {
            var pairs = new List<(int, int)>(n / 2);
            for (int i = 0; i < n / 2; i++)
            {
                int a = slots[i], b = slots[n - 1 - i];
                if (a >= teamCount || b >= teamCount) continue;   // bye
                if ((r + i) % 2 == 1) (a, b) = (b, a);            // rough home/away balance
                pairs.Add((a, b));
            }
            rounds.Add(pairs);
            // Rotate every slot except the fixed slots[0] one step clockwise.
            int lastSlot = slots[n - 1];
            for (int i = n - 1; i >= 2; i--) slots[i] = slots[i - 1];
            slots[1] = lastSlot;
        }
        return rounds;
    }

    /// (Re)builds one career season into s: union team list, double
    /// round-robin league, cup round 1 draw, interleaved global rounds.
    private static void BuildCareerSeason(
        CompetitionState s, List<TeamRef> leagueTeams, List<TeamRef> cupTeams, int playerLeagueIndex)
    {
        int cupCount = cupTeams?.Count ?? 0;
        if (cupCount != 4 && cupCount != 8 && cupCount != 16 && cupCount != 32)
            throw new ArgumentException("CAREER CUP NEEDS 4, 8, 16 OR 32 TEAMS", nameof(cupTeams));

        int playerMaster = leagueTeams[playerLeagueIndex].MasterIndex;
        bool playerInCup = false;
        foreach (var t in cupTeams!)
            if (t.MasterIndex == playerMaster) { playerInCup = true; break; }
        if (!playerInCup)
            throw new ArgumentException("CUP TEAMS MUST INCLUDE THE PLAYER CLUB", nameof(cupTeams));

        // Union of league + cup entrants, deduped by MasterIndex. League teams
        // come first so league fixtures use indices 0..leagueTeams.Count-1.
        s.Teams = new List<TeamRef>();
        var byMaster = new Dictionary<int, int>();
        foreach (var t in leagueTeams)
            if (!byMaster.ContainsKey(t.MasterIndex))
            {
                byMaster[t.MasterIndex] = s.Teams.Count;
                s.Teams.Add(t);
            }
        foreach (var t in cupTeams)
            if (!byMaster.ContainsKey(t.MasterIndex))
            {
                byMaster[t.MasterIndex] = s.Teams.Count;
                s.Teams.Add(t);
            }

        s.PlayerTeam = byMaster[playerMaster];
        s.GroupOf = new List<int>();
        for (int i = 0; i < s.Teams.Count; i++) s.GroupOf.Add(-1);
        s.GroupCount = 0;
        s.DoubleRoundRobin = true;
        s.Fixtures = new List<Fixture>();
        s.CurrentRound = 0;
        s.Finished = false;
        s.Champion = -1;
        // Feature #5: the scorer table is THIS season's, and Teams is rebuilt
        // above, so last season's rows would point at the wrong clubs. Career
        // totals survive on CareerPlayer.CareerGoals.
        s.Scorers = new List<OpenSwos.Competition.Career.ScorerRow>();

        // League: double round-robin over league-local indices (== union
        // indices because league teams were added first).
        List<List<(int Home, int Away)>> rounds = RoundRobin(leagueTeams.Count);
        int half = rounds.Count;
        int leagueRounds = half * 2;
        int cupRounds = Log2(cupCount);

        int LeagueGlobal(int j)
        {
            int offset = 0;
            for (int k = 0; k < cupRounds; k++)
                if (CareerCupCutoff(leagueRounds, cupRounds, k) <= j) offset++;
            return j + offset;
        }

        int Union(int leagueLocal) => byMaster[leagueTeams[leagueLocal].MasterIndex];

        for (int r = 0; r < half; r++)
            foreach (var (h, a) in rounds[r])
                s.Fixtures.Add(new Fixture
                { Round = LeagueGlobal(r), Stage = "LEAGUE", HomeTeam = Union(h), AwayTeam = Union(a) });
        for (int r = 0; r < half; r++)
            foreach (var (h, a) in rounds[r])
                s.Fixtures.Add(new Fixture
                { Round = LeagueGlobal(half + r), Stage = "LEAGUE", HomeTeam = Union(a), AwayTeam = Union(h) });

        // Cup round 1: random draw. Later rounds are created by RecordResult.
        var entrants = new List<int>(cupCount);
        foreach (var t in cupTeams) entrants.Add(byMaster[t.MasterIndex]);
        Shuffle(s, entrants);
        string stage = "CUP " + KnockoutStageLabel(cupCount);
        int cupRound0 = CareerCupCutoff(leagueRounds, cupRounds, 0);
        for (int i = 0; i + 1 < entrants.Count; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = cupRound0, Stage = stage, HomeTeam = entrants[i], AwayTeam = entrants[i + 1] });

        s.TotalRounds = leagueRounds + cupRounds;
    }

    /// How many league rounds precede career cup round k. Cup rounds are
    /// spread evenly across the season (last one after the final league
    /// round), so with 2 cup rounds they land at the halfway point and the
    /// end; with 4 they land after every quarter. Global round of cup round k
    /// is cutoff + k (earlier cup rounds shift the calendar).
    private static int CareerCupCutoff(int leagueRounds, int cupRounds, int k)
    {
        int cutoff = (int)Math.Round((k + 1) * (double)leagueRounds / cupRounds, MidpointRounding.AwayFromZero);
        return Math.Clamp(cutoff, 1, leagueRounds);
    }

    // --- stage / fixture predicates ---

    private static bool Involves(Fixture f, int team) => f.HomeTeam == team || f.AwayTeam == team;

    private static bool IsKnockoutStage(string stage)
        => stage != "LEAGUE" && !stage.StartsWith("GROUP", StringComparison.Ordinal);

    private static string KnockoutStageLabel(int teamsRemaining) => teamsRemaining switch
    {
        2 => "FINAL",
        4 => "SEMI FINAL",
        8 => "QUARTER FINAL",
        16 => "ROUND OF 16",
        32 => "ROUND OF 32",
        _ => "ROUND OF " + teamsRemaining,
    };

    private static int WinnerOf(Fixture f)
    {
        if (f.HomeGoals > f.AwayGoals) return f.HomeTeam;
        if (f.AwayGoals > f.HomeGoals) return f.AwayTeam;
        return f.PenaltyWinner >= 0 ? f.PenaltyWinner : f.HomeTeam;
    }

    private static bool ContainsFixture(CompetitionState s, Fixture f)
    {
        foreach (var x in s.Fixtures) if (ReferenceEquals(x, f)) return true;
        return false;
    }

    private static bool RoundIsComplete(CompetitionState s, int round)
    {
        bool any = false;
        foreach (var f in s.Fixtures)
            if (f.Round == round)
            {
                any = true;
                if (!f.Played) return false;
            }
        return any;
    }

    private static bool AllFixturesPlayed(CompetitionState s)
    {
        foreach (var f in s.Fixtures) if (!f.Played) return false;
        return true;
    }

    private static bool AllGroupFixturesPlayed(CompetitionState s)
    {
        foreach (var f in s.Fixtures)
            if (f.Stage.StartsWith("GROUP", StringComparison.Ordinal) && !f.Played) return false;
        return true;
    }

    private static bool HasKnockoutFixtures(CompetitionState s)
    {
        foreach (var f in s.Fixtures) if (IsKnockoutStage(f.Stage)) return true;
        return false;
    }

    private static List<int> DistinctStageRounds(CompetitionState s, string stagePrefix)
    {
        var set = new SortedSet<int>();
        foreach (var f in s.Fixtures)
            if (f.Stage.StartsWith(stagePrefix, StringComparison.Ordinal)) set.Add(f.Round);
        return new List<int>(set);
    }

    // --- progression ---

    private static void AdvanceCurrentRound(CompetitionState s)
    {
        while (s.CurrentRound < s.TotalRounds)
        {
            bool any = false;
            foreach (var f in s.Fixtures)
                if (f.Round == s.CurrentRound)
                {
                    any = true;
                    if (!f.Played) return;
                }
            if (!any) return;   // round not created yet (pending knockout draw)
            s.CurrentRound++;
        }
    }

    private static void OnRoundComplete(CompetitionState s, int round)
    {
        var roundFixtures = new List<Fixture>();
        foreach (var f in s.Fixtures) if (f.Round == round) roundFixtures.Add(f);
        if (roundFixtures.Count == 0) return;
        string stage = roundFixtures[0].Stage;

        switch (s.Kind)
        {
            case CompetitionKind.League:
                if (AllFixturesPlayed(s))
                {
                    s.Finished = true;
                    var table = Table(s, "LEAGUE");
                    s.Champion = table.Count > 0 ? table[0].Team : -1;
                }
                break;

            case CompetitionKind.Cup:
                AdvanceKnockout(s, roundFixtures, "", round + 1, crownChampion: true);
                break;

            case CompetitionKind.Tournament:
                if (stage.StartsWith("GROUP", StringComparison.Ordinal))
                {
                    if (AllGroupFixturesPlayed(s) && !HasKnockoutFixtures(s))
                        CreateTournamentKnockout(s);
                }
                else
                {
                    AdvanceKnockout(s, roundFixtures, "", round + 1, crownChampion: true);
                }
                break;

            case CompetitionKind.Career:
                // League completion is handled by MaybeCloseCareerSeason.
                if (stage.StartsWith("CUP ", StringComparison.Ordinal))
                    AdvanceCareerCup(s, roundFixtures);
                break;
        }
    }

    /// Pairs the winners of a completed knockout round into the next round
    /// (bracket order: winner of fixture 0 hosts winner of fixture 1, ...).
    /// A single winner means the final was just played.
    private static void AdvanceKnockout(
        CompetitionState s, List<Fixture> completed, string stagePrefix, int nextRound, bool crownChampion)
    {
        var winners = new List<int>(completed.Count);
        foreach (var f in completed) winners.Add(WinnerOf(f));

        if (winners.Count == 1)
        {
            if (crownChampion)
            {
                s.Finished = true;
                s.Champion = winners[0];
            }
            return;
        }

        string stage = stagePrefix + KnockoutStageLabel(winners.Count);
        for (int i = 0; i + 1 < winners.Count; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = nextRound, Stage = stage, HomeTeam = winners[i], AwayTeam = winners[i + 1] });
    }

    /// Group stage done: top 2 of each group advance, cross-paired so group
    /// mates can only re-meet in the final (A1-B2, C1-D2, ..., B1-A2, D1-C2).
    private static void CreateTournamentKnockout(CompetitionState s)
    {
        int g = s.GroupCount;
        var winners = new int[g];
        var runners = new int[g];
        for (int i = 0; i < g; i++)
        {
            var table = Table(s, "GROUP " + (char)('A' + i));
            winners[i] = table[0].Team;
            runners[i] = table[1].Team;
        }

        string stage = KnockoutStageLabel(g * 2);
        for (int i = 0; i + 1 < g; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = GroupStageRounds, Stage = stage, HomeTeam = winners[i], AwayTeam = runners[i + 1] });
        for (int i = 0; i + 1 < g; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = GroupStageRounds, Stage = stage, HomeTeam = winners[i + 1], AwayTeam = runners[i] });
    }

    /// Career cup round completed: draw the next round at its scheduled slot
    /// in the interleaved calendar. The cup final's completion is picked up
    /// by MaybeCloseCareerSeason instead of crowning Champion here.
    private static void AdvanceCareerCup(CompetitionState s, List<Fixture> completed)
    {
        var winners = new List<int>(completed.Count);
        foreach (var f in completed) winners.Add(WinnerOf(f));
        if (winners.Count == 1) return;   // cup final done

        // Reconstruct the season's calendar parameters from the fixtures.
        int leagueRounds = DistinctStageRounds(s, "LEAGUE").Count;
        int firstCupRound = int.MaxValue;
        foreach (var f in s.Fixtures)
            if (f.Stage.StartsWith("CUP ", StringComparison.Ordinal) && f.Round < firstCupRound)
                firstCupRound = f.Round;
        int firstCupFixtures = 0;
        foreach (var f in s.Fixtures)
            if (f.Round == firstCupRound && f.Stage.StartsWith("CUP ", StringComparison.Ordinal))
                firstCupFixtures++;
        int cupRounds = Log2(firstCupFixtures * 2);
        int created = DistinctStageRounds(s, "CUP ").Count;   // includes the round just completed

        int nextGlobal = CareerCupCutoff(leagueRounds, cupRounds, created) + created;
        string stage = "CUP " + KnockoutStageLabel(winners.Count);
        for (int i = 0; i + 1 < winners.Count; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = nextGlobal, Stage = stage, HomeTeam = winners[i], AwayTeam = winners[i + 1] });
    }

    /// When both the league and the cup of a career season have concluded:
    /// Finished + Champion (league leader), one history line, trophies.
    private static void MaybeCloseCareerSeason(CompetitionState s)
    {
        if (s.Finished || s.Career is null) return;

        foreach (var f in s.Fixtures)
            if (f.Stage == "LEAGUE" && !f.Played) return;   // league still running
        Fixture? final = null;
        foreach (var f in s.Fixtures)
            if (f.Stage == "CUP FINAL" && f.Played) final = f;
        if (final is null) return;                          // cup still running

        s.Finished = true;
        var table = Table(s, "LEAGUE");
        s.Champion = table.Count > 0 ? table[0].Team : -1;

        int pos = 0;
        for (int i = 0; i < table.Count; i++)
            if (table[i].Team == s.PlayerTeam) { pos = i + 1; break; }

        int cupWinner = WinnerOf(final);
        string cupPart;
        if (cupWinner == s.PlayerTeam) cupPart = "WINNER";
        else if (Involves(final, s.PlayerTeam)) cupPart = "RUNNER UP";
        else
        {
            Fixture? exit = null;
            foreach (var f in s.Fixtures)
                if (f.Played && f.Stage.StartsWith("CUP ", StringComparison.Ordinal)
                    && Involves(f, s.PlayerTeam) && WinnerOf(f) != s.PlayerTeam
                    && (exit is null || f.Round > exit.Round))
                    exit = f;
            cupPart = exit is not null ? "OUT IN " + exit.Stage.Substring(4) : "OUT";
        }

        var c = s.Career;
        if (s.Champion == s.PlayerTeam) c.Trophies.Add($"SEASON {c.Season} LEAGUE CHAMPIONS");
        if (cupWinner == s.PlayerTeam) c.Trophies.Add($"SEASON {c.Season} CUP WINNERS");
        string posText = pos > 0 ? Ordinal(pos) : "N/A";
        c.History.Add($"S{c.Season}: LEAGUE {posText}, CUP {cupPart}");

        // Career depth plan feature #5: the MANAGEMENT RECORD's per-season
        // "SEASON'S TOP SCORER" line (asm:283007). Taken now, while the season's
        // scorer table is still loaded — the next season rebuilds it from empty.
        var top = OpenSwos.Competition.Career.ScorerModel.SeasonTop(s, s.PlayerTeam, c.Season);
        c.SeasonTopScorers ??= new List<OpenSwos.Competition.Career.SeasonTopScorer>();
        if (top is not null) c.SeasonTopScorers.Add(top);

        // Feature #7: the season's own closing lines. Written here rather than at
        // the rollover because a career that is abandoned before NEXT SEASON is
        // pressed should still have its last season on the record.
        if (s.Champion == s.PlayerTeam)
            OpenSwos.Competition.Career.Chronicle.Add(c, "chron.league_won",
                "%a ARE CHAMPIONS", "season", 2, a: c.ClubName);
        else
            OpenSwos.Competition.Career.Chronicle.Add(c, "chron.league_pos",
                "FINISHED %a IN THE LEAGUE", "season", 1, a: posText);
        if (cupWinner == s.PlayerTeam)
            OpenSwos.Competition.Career.Chronicle.Add(c, "chron.cup_won",
                "%a WIN THE CUP", "season", 2, a: c.ClubName);
        if (top is not null && top.Goals > 0)
            OpenSwos.Competition.Career.Chronicle.Add(c, "chron.topscorer",
                "TOP SCORER: %a WITH %0", "season", 1, a: string.Join(" & ", top.Names), n: top.Goals);
    }

    // ------------------------------------------------------------------
    // Season results -> finance inputs (career depth plan feature #1)
    // ------------------------------------------------------------------

    /// Summarises the just-finished career season per club, keyed by the
    /// save-stable TEAM.* GlobalId. Every club that appears in the competition
    /// gets an entry, so the AI economy reacts to results too and the player is
    /// not the only club whose budget tracks performance.
    ///
    /// <param name="oldDivision">Division the season was played in.</param>
    /// <param name="newDivision">Division for next season; lower = promoted.</param>
    internal static Dictionary<ushort, OpenSwos.Competition.Career.SeasonResultInput>
        BuildSeasonResults(CompetitionState s, int oldDivision, int newDivision)
    {
        var map = new Dictionary<ushort, OpenSwos.Competition.Career.SeasonResultInput>();
        if (s.Teams.Count == 0) return map;

        // League standings of the season that just ended.
        var table = Table(s, "LEAGUE");
        var position = new Dictionary<int, int>();          // Teams index -> 1-based place
        for (int i = 0; i < table.Count; i++) position[table[i].Team] = i + 1;
        int champion = table.Count > 0 ? table[0].Team : -1;

        // Cup final, if the cup ran to completion.
        Fixture? final = null;
        foreach (var f in s.Fixtures)
            if (f.Stage == "CUP FINAL" && f.Played) final = f;
        int cupWinner = final is not null ? WinnerOf(final) : -1;
        int cupRunnerUp = -1;
        if (final is not null && cupWinner >= 0)
            cupRunnerUp = final.HomeTeam == cupWinner ? final.AwayTeam : final.HomeTeam;

        var homeLeague = new int[s.Teams.Count];
        var homeCup = new int[s.Teams.Count];
        var cupWins = new int[s.Teams.Count];
        foreach (var f in s.Fixtures)
        {
            if (!f.Played) continue;
            bool isLeague = f.Stage == "LEAGUE";
            bool isCup = f.Stage.StartsWith("CUP ", StringComparison.Ordinal);
            if (!isLeague && !isCup) continue;
            if (f.HomeTeam >= 0 && f.HomeTeam < s.Teams.Count)
            {
                if (isLeague) homeLeague[f.HomeTeam]++;
                else homeCup[f.HomeTeam]++;
            }
            if (isCup)
            {
                int w = WinnerOf(f);
                if (w >= 0 && w < s.Teams.Count) cupWins[w]++;
            }
        }

        for (int t = 0; t < s.Teams.Count; t++)
        {
            ushort globalId = s.Teams[t].GlobalId;
            if (globalId == 0 || map.ContainsKey(globalId)) continue;

            position.TryGetValue(t, out int place);
            map[globalId] = new OpenSwos.Competition.Career.SeasonResultInput
            {
                LeaguePosition = place,
                LeagueTeams = place > 0 ? table.Count : 0,
                HomeLeagueGames = homeLeague[t],
                HomeCupGames = homeCup[t],
                CupRoundsWon = cupWins[t],
                CupWinner = t == cupWinner,
                CupRunnerUp = t == cupRunnerUp,
                LeagueChampion = t == champion,
                CupResult = CupResultOf(s, t, final, cupWinner),
                Division = oldDivision,
                // Only the managed club's next division is known here, so only it
                // can be credited with a promotion bonus. AI promotion is decided
                // outside the engine and is not visible at this point.
                Promoted = t == s.PlayerTeam && newDivision < oldDivision,
            };
        }
        return map;
    }

    /// <summary>
    /// Where the board ranks the managed club's squad inside its own league,
    /// 1-based. This is what the chairman judges the season against: finish
    /// where your squad belongs and the verdict is neutral, beat it and you are
    /// praised. It keeps a village side promoted into a strong division from
    /// being sacked for finishing last with the weakest squad in the league.
    ///
    /// Ranked by <see cref="LiveStrength"/>, the same measure the simulated
    /// results use, so the board expects exactly what the league is about to
    /// deliver. It must not know which club is the player's: the tie-break used
    /// to fall back to the Teams index, and CareerFactory always puts the
    /// managed club FIRST in the pool, so a manager tied for the strongest
    /// squad was permanently "expected to win the league". Found by playing a
    /// season at WIDZEW LODZ (2026-08-23): 3rd of 16 with a joint-strongest
    /// squad scored -2 and COST reputation, which meant a career in a level
    /// league could never build one at all.
    /// </summary>
    public static int ExpectedLeaguePosition(CompetitionState s)
    {
        var career = s?.Career;
        if (career is null || s!.PlayerTeam < 0) return 0;
        var table = Table(s, "LEAGUE");
        if (table.Count == 0) return 0;

        var ranked = new List<(int Team, double Strength, ushort Gid)>();
        foreach (var row in table)
            ranked.Add((row.Team, LiveStrength(s, row.Team), s.Teams[row.Team].GlobalId));
        ranked.Sort((a, b) =>
        {
            int byStrength = b.Strength.CompareTo(a.Strength);
            return byStrength != 0 ? byStrength : a.Gid.CompareTo(b.Gid);   // club-blind
        });
        for (int i = 0; i < ranked.Count; i++)
            if (ranked[i].Team == s.PlayerTeam) return i + 1;
        return 0;
    }

    /// <summary>
    /// Freezes the board's expectation for the season in progress. Called as the
    /// managed club's first result of the season is recorded, which is the
    /// earliest moment the career world is guaranteed to exist (it is
    /// materialized by the front-end after CreateCareer). Once set it does not
    /// move again until the next season, so a signing does not raise the bar
    /// retroactively and a fire-sale in the last round cannot lower it.
    /// </summary>
    private static void EnsureSeasonExpectation(CompetitionState s)
    {
        var career = s.Career;
        if (career is null || career.SeasonExpectedPosition > 0) return;
        int expected = ExpectedLeaguePosition(s);
        if (expected > 0)
        {
            career.SeasonExpectedPosition = expected;
            career.SeasonLeagueTeams = Table(s, "LEAGUE").Count;
        }
    }

    /// Runs the chairman's end-of-season verdict for the managed club. Returns
    /// true if the manager was dismissed.
    ///
    /// The board's expectation is the club's rank BY SQUAD STRENGTH inside its
    /// own league — finish where your squad belongs and the verdict is neutral;
    /// beat it and you are praised. That keeps a village side promoted into a
    /// strong division from being sacked for finishing last with the weakest
    /// squad in the league.
    private static bool RunChairmanSeasonVerdict(
        CompetitionState s, int oldDivision, int newDivision,
        Dictionary<ushort, OpenSwos.Competition.Career.SeasonResultInput> results)
    {
        var career = s.Career;
        if (career is null || career.Retired || career.Sacked) return false;
        if (!results.TryGetValue(career.ClubGlobalId, out var mine)) return false;

        // A season nobody played cannot be judged. Without this guard an
        // untouched fixture list still yields a full table (every club on zero
        // points, ordered by name), so the strongest club "finishes last" and
        // the chairman sacks a manager who never took charge of a match. Hit
        // immediately by --career-report, which advances seasons as a pure
        // balance soak without playing anything.
        int leaguePlayed = 0;
        foreach (var f in s.Fixtures)
            if (f.Stage == "LEAGUE" && f.Played && Involves(f, s.PlayerTeam)) leaguePlayed++;
        if (leaguePlayed == 0) return false;

        var table = Table(s, "LEAGUE");
        if (table.Count == 0) return false;

        // The board's expectation was FIXED when this season kicked off (see
        // EnsureSeasonExpectation) — judging the end-of-season squad would mean
        // signing a star raised the bar retroactively, and selling one in the
        // last round lowered it. Fall back only for a save that predates it.
        int expected = career.SeasonExpectedPosition > 0
            ? career.SeasonExpectedPosition
            : ExpectedLeaguePosition(s);
        career.LastExpectedPosition = expected;
        career.LastLeaguePosition = mine.LeaguePosition;
        career.LastLeagueTeams = mine.LeagueTeams;

        bool promoted = newDivision < oldDivision;
        bool relegated = newDivision > oldDivision;

        int score = OpenSwos.Competition.Career.ChairmanModel.SeasonScore(
            mine.LeaguePosition, mine.LeagueTeams, expected,
            mine.LeagueChampion, promoted, relegated,
            mine.CupWinner, mine.CupRunnerUp, mine.CupRoundsWon);

        // Feature #3: the same score moves the manager's standing in the game.
        // Silverware counts double — a trophy is what other boards remember.
        int trophiesThisSeason = 0;
        string tag = "SEASON " + career.Season + " ";
        foreach (string t in career.Trophies)
            if (t is not null && t.StartsWith(tag, StringComparison.Ordinal)) trophiesThisSeason++;
        OpenSwos.Competition.Career.JobMarket.ApplySeasonReputation(career, score, trophiesThisSeason);

        // StartNewSeason is deliberately NOT called here: the season counter has
        // not been bumped yet, so its "new season" memo would carry the old
        // number. AdvanceCareerSeason calls it after c.Season++.
        return OpenSwos.Competition.Career.ChairmanModel.SeasonVerdict(
            career, OpenSwos.Competition.Career.ChairmanModel.Patience, score, relegated);
    }

    /// "WINNER" / "RUNNER UP" / "OUT IN <stage>" / "" for one team's cup run.
    private static string CupResultOf(CompetitionState s, int team, Fixture? final, int cupWinner)
    {
        if (final is null) return "";
        if (team == cupWinner) return "WINNER";
        if (Involves(final, team)) return "RUNNER UP";
        Fixture? exit = null;
        foreach (var f in s.Fixtures)
            if (f.Played && f.Stage.StartsWith("CUP ", StringComparison.Ordinal)
                && Involves(f, team) && WinnerOf(f) != team
                && (exit is null || f.Round > exit.Round))
                exit = f;
        return exit is not null ? "OUT IN " + exit.Stage.Substring(4) : "";
    }

    /// Deterministic penalty shootout: best of 5, then sudden death. Slightly
    /// strength-weighted per-kick conversion. Returns the winning Teams index.
    private static int SimulateShootout(CompetitionState s, Fixture f)
    {
        double strH = s.Teams[f.HomeTeam].Strength;
        double strA = s.Teams[f.AwayTeam].Strength;
        double pH = Math.Clamp(0.76 + 0.02 * (strH - strA), 0.55, 0.92);
        double pA = Math.Clamp(0.76 + 0.02 * (strA - strH), 0.55, 0.92);

        int h = 0, a = 0;
        for (int kick = 0; kick < 5; kick++)
        {
            if (NextDouble(s) < pH) h++;
            if (NextDouble(s) < pA) a++;
        }
        while (h == a)   // sudden death, one kick each
        {
            if (NextDouble(s) < pH) h++;
            if (NextDouble(s) < pA) a++;
        }
        return h > a ? f.HomeTeam : f.AwayTeam;
    }

    // --- misc ---

    private static int Log2(int n)
    {
        int r = 0;
        while (n > 1) { n >>= 1; r++; }
        return r;
    }

    private static string Ordinal(int n)
    {
        int rem100 = n % 100;
        string suffix = (rem100 >= 11 && rem100 <= 13)
            ? "TH"
            : (n % 10) switch { 1 => "ST", 2 => "ND", 3 => "RD", _ => "TH" };
        return n + suffix;
    }
}
