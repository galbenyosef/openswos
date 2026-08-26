namespace OpenSwos.Competition.Career;

using System;
using System.Collections.Generic;

// ============================================================================
// The chairman — career depth plan feature #2
// (docs/decisions/03-career-depth-plan.md).
//
// FIDELITY NOTE. Every memo below is the ORIGINAL game's text, recovered in
// order from external/original-amiga-swos/original-amiga-swos.asm. The original
// runs TWO parallel three-stage escalations — one on league position, one on the
// overdraft — each ending in the same dismissal memo:
//
//   LEAGUE          'PLEASE TAKE NOTE OF OUR CURRENT LEAGUE POSITION AND'
//                   'ENSURE THAT IT IS IMPROVED UPON AS SOON AS POSSIBLE'
//                -> 'REMEMBER, UNLESS OUR LEAGUE PLACING SOON IMPROVES'
//                   'WE WILL HAVE TO RECONSIDER YOUR POSITION AS MANAGER'
//                -> 'FOLLOWING A VOTE OF CONFIDENCE, YOU HAVE NO MORE THAN'
//                   '3 MATCHES TO TURN THIS CLUB AROUND OR YOU WILL BE SACKED'
//
//   OVERDRAFT       'PLEASE TAKE NOTE OF OUR CURRENT BANK BALANCE AND'
//                   'ENSURE THAT WE CLEAR OUR OVERDRAFT AS SOON AS POSSIBLE'
//                -> 'JUST A REMINDER TO DEAL WITH THE OVERDRAFT SITUATION'
//                   'OR WE WILL HAVE TO RECONSIDER YOUR POSITION AS MANAGER'
//                -> 'FOLLOWING A CRISIS BOARD MEETING, WE MUST INSIST YOU'
//                   'CLEAR THE OVERDRAFT IMMEDIATELY OR YOU'RE SACKED'
//
//   BOTH        -> 'IT IS WITH GREAT REGRET THAT THE BOARD AND I HAVE'
//                  'DECIDED TO RELIEVE YOU OF YOUR DUTIES - YOU'RE SACKED'
//
// and a five-grade end-of-season verdict (2697-2706 in the string pool).
//
// The TRIGGER THRESHOLDS are ours — the disassembly carries these strings as
// raw data with no symbol names, so the routine that fires them is not
// identifiable. They are gathered at the top of this file and are pure
// functions of results and money: no RNG anywhere, so the same career always
// produces the same memos.
//
// The original had both a CHAIRMAN and a PRESIDENT variant of the memo header
// ('MEMO FROM THE CLUB CHAIRMAN' / 'MEMO FROM THE PRESIDENT'). We use CHAIRMAN
// throughout; the president wording is available if a nation-dependent title is
// ever wanted.
// ============================================================================

/// <summary>End-of-season verdict, worst-to-best mapped onto the original's five memos.</summary>
public enum ChairmanVerdict
{
    Excellent = 0,
    Good = 1,
    UpAndDown = 2,
    NotVeryGood = 3,
    VeryDisappointing = 4,
}

/// <summary>How quickly the board reaches for the trigger. An OPTIONS setting.</summary>
public enum BoardPatience
{
    Patient = 0,
    Normal = 1,
    Ruthless = 2,
}

/// <summary>One memo from the chairman, ready for either client to render.</summary>
public sealed class ChairmanMemo
{
    /// <summary>Stable i18n key; the desktop menu translates, the browser prints Lines.</summary>
    public string Key { get; set; } = "";
    /// <summary>The original game's English wording, one entry per printed line.</summary>
    public List<string> Lines { get; set; } = new();
    /// <summary>"verdict" | "warning" | "final" | "crisis" | "sacked" | "embargo" | "renewed".</summary>
    public string Kind { get; set; } = "";
    /// <summary>0 = good news, 1 = note, 2 = warning, 3 = dismissal.</summary>
    public int Severity { get; set; }
    public int Season { get; set; }
    /// <summary>Set on the season verdict memo only.</summary>
    public int Verdict { get; set; } = -1;
    /// <summary>
    /// What %a stands for in this memo's ORIGINAL lines. The chairman writes to
    /// the manager, so it is his name; a job-market letter (feature #3) is
    /// written by another club and names the CLUB instead. A client that
    /// translates a line has to re-substitute the placeholder, and without this
    /// it would put the manager's name where the club belongs.
    /// </summary>
    public string Subject { get; set; } = "";
}

public static class ChairmanModel
{
    /// <summary>
    /// OPTIONS setting: how quickly the board reaches for the trigger. A static
    /// gate, matching the existing PlayerEnergy.EffectEnabled precedent — set
    /// once from the options screen, read by the engine. Default Normal so a
    /// save loaded without touching options behaves the same everywhere.
    /// </summary>
    public static BoardPatience Patience { get; set; } = BoardPatience.Normal;

    // ---- thresholds (OURS — see the fidelity note) -------------------------

    /// <summary>Places above bottom that still count as "in trouble".</summary>
    private const int DangerZoneDepth = 2;

    /// <summary>Fraction of league fixtures played before each league memo may fire, in percent.</summary>
    private static (int Note, int Warn, int Crisis) LeagueGates(BoardPatience p) => p switch
    {
        BoardPatience.Patient => (40, 65, 85),
        BoardPatience.Ruthless => (15, 30, 50),
        _ => (25, 50, 70),
    };

    /// <summary>Matches granted by the vote of confidence.</summary>
    private static int CrisisMatches(BoardPatience p) => p switch
    {
        BoardPatience.Patient => 5,
        BoardPatience.Ruthless => 2,
        _ => 3,   // the original memo says "3 MATCHES"
    };

    /// <summary>Player matches the board tolerates between overdraft memos.</summary>
    private static int OverdraftGrace(BoardPatience p) => p switch
    {
        BoardPatience.Patient => 8,
        BoardPatience.Ruthless => 3,
        _ => 5,
    };

    /// <summary>
    /// Season-verdict score at or below which the manager is dismissed OUTRIGHT,
    /// and the score at or below which a season counts as "bad" and stacks
    /// toward <see cref="BadSeasonsAllowed"/>.
    ///
    /// The outright gate is deliberately CATASTROPHIC-ONLY (a relegated
    /// favourite scores about -22). One poor season must not end a career with
    /// no prior memo: the board's own escalation ladder exists to warn first,
    /// and being sacked out of nowhere reads as a bug rather than a verdict.
    /// Repeated failure is handled by the consecutive-seasons counter instead.
    /// </summary>
    private static (int Sack, int Warn) VerdictGates(BoardPatience p) => p switch
    {
        BoardPatience.Patient => (-16, -5),
        BoardPatience.Ruthless => (-7, -1),
        _ => (-12, -3),
    };

    /// <summary>Consecutive bad seasons tolerated before the contract is torn up.</summary>
    private static int BadSeasonsAllowed(BoardPatience p) => p switch
    {
        BoardPatience.Patient => 4,
        BoardPatience.Ruthless => 2,
        _ => 3,
    };

    // ---- memo text (ORIGINAL wording) --------------------------------------

    private static readonly string[][] VerdictLines =
    {
        new[] { "WELL DONE %a, YOU DID AN EXCELLENT JOB",      "THIS SEASON AND WE ALL APPRECIATE IT" },
        new[] { "A GOOD SEASON %a, WE ARE ALL VERY",           "PLEASED WITH YOU, KEEP UP THE GOOD WORK" },
        new[] { "AN UP AND DOWN SEASON %a, WE ALL HOPE YOU",   "CAN IMPROVE ON THIS IN THE NEAR FUTURE" },
        new[] { "NOT A VERY GOOD SEASON %a, WE HOPE YOU CAN",  "IMPROVE ON OUR PERFORMANCE NEXT SEASON" },
        new[] { "A VERY DISAPPOINTING SEASON %a, WE DEMAND",   "MUCH BETTER FROM YOU NEXT SEASON" },
    };
    private static readonly string[] VerdictKeys =
        { "chair.verdict0", "chair.verdict1", "chair.verdict2", "chair.verdict3", "chair.verdict4" };

    private static readonly string[] SackedLines =
        { "IT IS WITH GREAT REGRET THAT THE BOARD AND I HAVE", "DECIDED TO RELIEVE YOU OF YOUR DUTIES - YOU'RE SACKED" };
    private static readonly string[] NotRenewedLines =
        { "IT IS WITH GREAT REGRET THAT WE HAVE DECIDED NOT TO", "RENEW YOUR CONTRACT FOR NEXT SEASON - GOOD LUCK",
          "AND WE LOOK FORWARD TO SEEING YOU SOON" };
    private static readonly string[] LeagueNoteLines =
        { "PLEASE TAKE NOTE OF OUR CURRENT LEAGUE POSITION AND", "ENSURE THAT IT IS IMPROVED UPON AS SOON AS POSSIBLE" };
    private static readonly string[] LeagueWarnLines =
        { "REMEMBER, UNLESS OUR LEAGUE PLACING SOON IMPROVES", "WE WILL HAVE TO RECONSIDER YOUR POSITION AS MANAGER" };
    private static readonly string[] LeagueCrisisLines =
        { "FOLLOWING A VOTE OF CONFIDENCE, YOU HAVE NO MORE THAN", "3 MATCHES TO TURN THIS CLUB AROUND OR YOU WILL BE SACKED" };
    private static readonly string[] MoneyNoteLines =
        { "PLEASE TAKE NOTE OF OUR CURRENT BANK BALANCE AND", "ENSURE THAT WE CLEAR OUR OVERDRAFT AS SOON AS POSSIBLE" };
    private static readonly string[] MoneyWarnLines =
        { "JUST A REMINDER TO DEAL WITH THE OVERDRAFT SITUATION", "OR WE WILL HAVE TO RECONSIDER YOUR POSITION AS MANAGER" };
    private static readonly string[] MoneyCrisisLines =
        { "FOLLOWING A CRISIS BOARD MEETING, WE MUST INSIST YOU", "CLEAR THE OVERDRAFT IMMEDIATELY OR YOU'RE SACKED" };
    private static readonly string[] NewSeasonLines =
        { "JUST A NOTE TO WISH YOU EVERY SUCCESS IN THE", "NEW SEASON" };

    /// <summary>Header printed above every memo ('MEMO FROM THE CLUB CHAIRMAN').</summary>
    public const string MemoHeader = "MEMO FROM THE CLUB CHAIRMAN";
    /// <summary>Signature printed below every memo ('YOUR CHAIRMAN').</summary>
    public const string MemoSignature = "YOUR CHAIRMAN";

    // ---- memo construction --------------------------------------------------

    private static ChairmanMemo Memo(string key, string[] lines, string kind, int severity, int season, string manager)
    {
        string who = string.IsNullOrWhiteSpace(manager) ? "BOSS" : manager.Trim();
        var m = new ChairmanMemo
        { Key = key, Kind = kind, Severity = severity, Season = season, Subject = who };
        foreach (string line in lines) m.Lines.Add(line.Replace("%a", who));
        return m;
    }

    /// <summary>
    /// Files a memo written by SOMEBODY ELSE'S board — the job market's
    /// acceptance, farewell, welcome and withdrawal letters (feature #3). The
    /// inbox is the one place both clients already render, so those letters
    /// arrive there rather than in a second, parallel screen.
    /// </summary>
    public static void FileMemo(CareerState career, ChairmanMemo memo)
    {
        if (career is null || memo is null) return;
        File(career, memo);
    }

    /// <summary>Files a memo on the career, newest last, and keeps the inbox bounded.</summary>
    private static void File(CareerState career, ChairmanMemo memo)
    {
        career.Memos ??= new List<ChairmanMemo>();
        career.Memos.Add(memo);
        while (career.Memos.Count > 12) career.Memos.RemoveAt(0);
        career.LastMemo = memo;
        career.UnreadMemos++;
    }

    // ======================================================================
    // In-season: called after every fixture the managed club plays.
    // ======================================================================

    /// <summary>
    /// Runs both escalation ladders. Returns true if the manager was sacked.
    /// Pure function of the table and the budget — no RNG, so a replayed season
    /// produces the identical memos.
    /// </summary>
    /// <param name="leaguePosition">1-based; 0 when the league has not started.</param>
    /// <param name="leagueTeams">Teams in the league.</param>
    /// <param name="leagueFixturesPlayedPercent">0-100, how far the club is through its league programme.</param>
    /// <param name="wasLeagueFixture">Crisis countdowns only tick on league matches.</param>
    /// <param name="wonThisMatch">A win is what "turning the club around" means.</param>
    /// <param name="budget">The club's balance right now.</param>
    public static bool AfterPlayerFixture(
        CareerState career,
        BoardPatience patience,
        int leaguePosition,
        int leagueTeams,
        int leagueFixturesPlayedPercent,
        bool wasLeagueFixture,
        bool wonThisMatch,
        long budget)
    {
        if (career is null) throw new ArgumentNullException(nameof(career));
        if (career.Retired || career.Sacked) return false;

        bool inDanger = leagueTeams > 0 && leaguePosition > 0
                        && leaguePosition > leagueTeams - DangerZoneDepth;

        // ---- crisis countdowns first: they can end the career this match ----
        if (career.CrisisMatchesLeft > 0 && wasLeagueFixture)
        {
            // Escaping the drop zone, or simply winning, satisfies the board.
            if (!inDanger || wonThisMatch)
            {
                career.CrisisMatchesLeft = 0;
                career.ChairmanWarnLeague = 2;   // stays on a final warning
            }
            else
            {
                career.CrisisMatchesLeft--;
                if (career.CrisisMatchesLeft == 0)
                {
                    Sack(career, "chair.sacked_league");
                    return true;
                }
            }
        }

        if (career.MoneyCrisisMatchesLeft > 0)
        {
            if (budget >= 0)
            {
                career.MoneyCrisisMatchesLeft = 0;
                career.ChairmanWarnMoney = 2;
            }
            else
            {
                career.MoneyCrisisMatchesLeft--;
                if (career.MoneyCrisisMatchesLeft == 0)
                {
                    Sack(career, "chair.sacked_money");
                    return true;
                }
            }
        }

        // ---- league ladder ---------------------------------------------------
        // The stage only ever CLIMBS within a season. It used to reset the
        // moment the club escaped the drop zone, which meant a side bobbing in
        // and out received the same opening letter four times in one season and
        // never reached the later stages. Escaping now silences the board (no
        // new memos) without rewinding what it has already said; the season
        // rollover clears the ladder.
        career.LeagueInDanger = inDanger;
        var gates = LeagueGates(patience);
        if (inDanger && career.CrisisMatchesLeft == 0)
        {
            if (career.ChairmanWarnLeague == 0 && leagueFixturesPlayedPercent >= gates.Note)
            {
                career.ChairmanWarnLeague = 1;
                File(career, Memo("chair.league_note", LeagueNoteLines, "warning", 1, career.Season, career.ManagerName));
            }
            else if (career.ChairmanWarnLeague == 1 && leagueFixturesPlayedPercent >= gates.Warn)
            {
                career.ChairmanWarnLeague = 2;
                File(career, Memo("chair.league_warn", LeagueWarnLines, "final", 2, career.Season, career.ManagerName));
            }
            else if (career.ChairmanWarnLeague == 2 && leagueFixturesPlayedPercent >= gates.Crisis
                     && !career.LeagueCrisisUsed)
            {
                career.LeagueCrisisUsed = true;   // one vote of confidence per season
                career.CrisisMatchesLeft = CrisisMatches(patience);
                File(career, Memo("chair.league_crisis", LeagueCrisisLines, "crisis", 2, career.Season, career.ManagerName));
            }
        }

        // ---- overdraft ladder -------------------------------------------------
        // Same rule as the league ladder: monotonic within a season. Clearing
        // the overdraft stops the letters and clears the standing order, but
        // does not hand the manager a fresh set of warnings to burn through by
        // dipping back into the red.
        career.InOverdraft = budget < 0;
        if (budget >= 0)
        {
            career.OverdraftMatches = 0;
        }
        else if (career.MoneyCrisisMatchesLeft == 0)
        {
            career.OverdraftMatches++;
            int grace = OverdraftGrace(patience);
            if (career.ChairmanWarnMoney == 0)
            {
                career.ChairmanWarnMoney = 1;
                career.OverdraftMatches = 0;
                File(career, Memo("chair.money_note", MoneyNoteLines, "warning", 1, career.Season, career.ManagerName));
            }
            else if (career.ChairmanWarnMoney == 1 && career.OverdraftMatches >= grace)
            {
                career.ChairmanWarnMoney = 2;
                career.OverdraftMatches = 0;
                File(career, Memo("chair.money_warn", MoneyWarnLines, "final", 2, career.Season, career.ManagerName));
            }
            else if (career.ChairmanWarnMoney == 2 && career.OverdraftMatches >= grace
                     && !career.MoneyCrisisUsed)
            {
                career.MoneyCrisisUsed = true;
                career.MoneyCrisisMatchesLeft = CrisisMatches(patience);
                File(career, Memo("chair.money_crisis", MoneyCrisisLines, "crisis", 2, career.Season, career.ManagerName));
            }
        }

        return false;
    }

    private static void Sack(CareerState career, string key)
    {
        career.Sacked = true;
        career.Retired = true;               // both clients already treat this as "career over"
        career.CrisisMatchesLeft = 0;
        career.MoneyCrisisMatchesLeft = 0;
        File(career, Memo(key, SackedLines, "sacked", 3, career.Season, career.ManagerName));
        career.History.Add($"S{career.Season}: SACKED BY {career.ClubName}");
    }

    // ======================================================================
    // End of season: the verdict.
    // ======================================================================

    /// <summary>
    /// Scores the finished season against what the board expected of this squad.
    /// Positive = overperformed. Deterministic.
    /// </summary>
    /// <param name="expectedPosition">Where the squad's strength ranks it, 1-based.</param>
    public static int SeasonScore(
        int leaguePosition, int leagueTeams, int expectedPosition,
        bool leagueChampion, bool promoted, bool relegated,
        bool cupWinner, bool cupRunnerUp, int cupRoundsWon)
    {
        int score = 0;
        if (leagueTeams > 0 && leaguePosition > 0 && expectedPosition > 0)
        {
            // Finishing above expectation is worth more in a big league, so
            // normalise the gap onto a 16-team scale.
            int gap = expectedPosition - leaguePosition;
            score += gap * 16 / Math.Max(1, leagueTeams);
        }
        if (leagueChampion) score += 6;
        if (promoted) score += 5;
        if (relegated) score -= 8;
        if (cupWinner) score += 5;
        else if (cupRunnerUp) score += 3;
        else score += Math.Min(2, Math.Max(0, cupRoundsWon - 1));
        return score;
    }

    /// <summary>Maps a season score onto the original's five verdict memos.</summary>
    public static ChairmanVerdict VerdictFor(int score) => score switch
    {
        >= 5 => ChairmanVerdict.Excellent,
        >= 1 => ChairmanVerdict.Good,
        >= -2 => ChairmanVerdict.UpAndDown,
        >= -5 => ChairmanVerdict.NotVeryGood,
        _ => ChairmanVerdict.VeryDisappointing,
    };

    /// <summary>
    /// Files the end-of-season verdict and decides whether the contract is
    /// renewed. Returns true if the manager was dismissed.
    /// </summary>
    public static bool SeasonVerdict(
        CareerState career, BoardPatience patience, int score, bool relegated)
    {
        if (career is null) throw new ArgumentNullException(nameof(career));
        if (career.Retired || career.Sacked) return false;

        ChairmanVerdict verdict = VerdictFor(score);
        career.LastVerdict = (int)verdict;
        career.LastSeasonScore = score;

        var memo = Memo(VerdictKeys[(int)verdict], VerdictLines[(int)verdict],
                        "verdict", verdict >= ChairmanVerdict.NotVeryGood ? 2 : 0,
                        career.Season, career.ManagerName);
        memo.Verdict = (int)verdict;
        File(career, memo);

        var gates = VerdictGates(patience);
        // A season that ends ON a final warning counts against the manager: the
        // board already told him his position was under review.
        bool onFinalWarning = career.ChairmanWarnLeague >= 2 || career.ChairmanWarnMoney >= 2;
        int effectiveScore = score - (onFinalWarning ? 2 : 0);

        if (effectiveScore <= gates.Sack || (relegated && patience == BoardPatience.Ruthless))
        {
            career.Sacked = true;
            career.Retired = true;
            File(career, Memo("chair.not_renewed", NotRenewedLines, "sacked", 3, career.Season, career.ManagerName));
            career.History.Add($"S{career.Season}: CONTRACT NOT RENEWED BY {career.ClubName}");
            return true;
        }

        career.ConsecutiveBadSeasons = effectiveScore <= gates.Warn ? career.ConsecutiveBadSeasons + 1 : 0;
        if (career.ConsecutiveBadSeasons >= BadSeasonsAllowed(patience))
        {
            career.Sacked = true;
            career.Retired = true;
            File(career, Memo("chair.not_renewed", NotRenewedLines, "sacked", 3, career.Season, career.ManagerName));
            career.History.Add($"S{career.Season}: CONTRACT NOT RENEWED BY {career.ClubName}");
            return true;
        }

        return false;
    }

    /// <summary>Clears the season's warning state and files the new-season note.</summary>
    public static void StartNewSeason(CareerState career)
    {
        if (career is null) throw new ArgumentNullException(nameof(career));
        career.ChairmanWarnLeague = 0;
        career.ChairmanWarnMoney = 0;
        career.CrisisMatchesLeft = 0;
        career.MoneyCrisisMatchesLeft = 0;
        career.LeagueCrisisUsed = false;
        career.MoneyCrisisUsed = false;
        career.OverdraftMatches = 0;
        career.LeagueInDanger = false;
        career.InOverdraft = false;
        File(career, Memo("chair.new_season", NewSeasonLines, "renewed", 0, career.Season, career.ManagerName));
    }

    // ======================================================================
    // Board transfer embargo — the memos behind the existing season quotas.
    // ======================================================================

    /// <summary>
    /// 'SORRY %a BUT THE CLUB IS UNWILLING TO MAKE / ANY FURTHER PLAYER
    /// PURCHASES THIS SEASON' — the refusal the existing buy quota already
    /// produces, now in the original's words.
    /// </summary>
    public static ChairmanMemo BuyEmbargo(CareerState career) => Memo(
        "chair.embargo_buy",
        new[] { "SORRY %a BUT THE CLUB IS UNWILLING TO MAKE", "ANY FURTHER PLAYER PURCHASES THIS SEASON" },
        "embargo", 1, career.Season, career.ManagerName);

    /// <summary>
    /// 'PLEASE NOTE %a THAT WE WILL NOT PERMIT / ANY FURTHER PLAYER SALES THIS
    /// SEASON' — the sell-quota refusal.
    /// </summary>
    public static ChairmanMemo SellEmbargo(CareerState career) => Memo(
        "chair.embargo_sell",
        new[] { "PLEASE NOTE %a THAT WE WILL NOT PERMIT", "ANY FURTHER PLAYER SALES THIS SEASON" },
        "embargo", 1, career.Season, career.ManagerName);

    /// <summary>
    /// One-line status for a dashboard: what the board currently thinks. Empty
    /// when there is nothing to say.
    /// </summary>
    public static string StatusLine(CareerState career)
    {
        if (career is null) return "";
        if (career.Sacked) return "SACKED";
        if (career.CrisisMatchesLeft > 0)
            return career.CrisisMatchesLeft + " MATCHES TO SAVE YOUR JOB";
        if (career.MoneyCrisisMatchesLeft > 0)
            return "CLEAR THE OVERDRAFT IN " + career.MoneyCrisisMatchesLeft + " MATCHES";
        // A warning only stands while the problem does. Climbing out of the drop
        // zone, or clearing the overdraft, takes the notice off the header even
        // though the letters stay in the inbox.
        bool leagueLive = career.LeagueInDanger && career.ChairmanWarnLeague > 0;
        bool moneyLive = career.InOverdraft && career.ChairmanWarnMoney > 0;
        if ((leagueLive && career.ChairmanWarnLeague >= 2)
            || (moneyLive && career.ChairmanWarnMoney >= 2))
            return "YOUR POSITION IS UNDER REVIEW";
        if (leagueLive) return "THE BOARD WANTS A BETTER LEAGUE POSITION";
        if (moneyLive) return "THE BOARD WANTS THE OVERDRAFT CLEARED";
        return "";
    }
}
