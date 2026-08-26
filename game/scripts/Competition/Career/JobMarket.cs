namespace OpenSwos.Competition.Career;

using System;
using System.Collections.Generic;

// ============================================================================
// Job offers from other clubs — career depth plan feature #3
// (docs/decisions/03-career-depth-plan.md).
//
// FIDELITY NOTE. Every letter below is the ORIGINAL game's text, recovered in
// order from external/original-amiga-swos/original-amiga-swos.asm. The original
// stores the whole flow:
//
//   the letter    'DEAR SIR/MADAM'
//                 'WE HERE AT %a WOULD LIKE TO OFFER YOU THE'
//                 'COACHING JOB AT OUR CLUB FOR NEXT SEASON'
//                 'WE CAN OFFER YOU A VERY COMPETITIVE SALARY AND'
//                 'WE HOPE TO BE ABLE TO MAKE FUNDS AVAILABLE'
//                 'IN THE REGION OF %a FOR IMPROVING THE TEAM'
//                 'WE LOOK FORWARD TO HEARING FROM YOU'
//   accepting     'WE HERE AT %a ARE DELIGHTED THAT YOU'
//                 'HAVE ACCEPTED THE POSITION OF COACH AT OUR CLUB'
//                 'FROM THE START OF NEXT SEASON'
//   leaving       'THANKS FOR ALL YOUR HELP %a, WE ALL WISH'
//                 'YOU WELL IN YOUR NEW JOB'
//   arriving      'WELCOME %a, WE LOOK FORWARD TO MUCH'
//                 'SUCCESS FROM OUR NEW PARTNERSHIP'
//   withdrawn     'JOB OFFER FROM %a WITHDRAWN'
//   screen labels 'JOB OFFERS' 'NO JOB OFFERS' 'JOB OFFERS BEING CONSIDERED' 'NEW JOB'
//
// The original prints its transfer-funds figure with the same %a placeholder as
// the club name; we use %b for the money so one substitution pass cannot eat the
// other. Everything else is verbatim.
//
// WHAT IS OURS. The disassembly carries these strings as raw data with no symbol
// names, so the routine that decides WHO offers and WHEN is not recoverable. The
// reputation model, the club-standing formula, the timing gate and the
// withdrawal rule are ours; they are gathered at the top of this file. There is
// no wall-clock and no System.Random anywhere: the draw is keyed to the career
// itself, so replaying a season produces the same suitors.
//
// The move itself happens at the season rollover (CompetitionEngine
// .AdvanceCareerSeason), which is what 'FOR NEXT SEASON' means. Accepting
// mid-season is allowed and simply books the move — the original's
// 'JOB OFFERS BEING CONSIDERED' state.
// ============================================================================

/// <summary>What the engine needs to know about a club it might offer you.</summary>
public sealed class JobClubInfo
{
    public int MasterIndex { get; init; }
    public ushort GlobalId { get; init; }
    public string Name { get; init; } = "";
    public int Nation { get; init; }
    public string NationName { get; init; } = "";
    public int Division { get; init; }
    public int Strength { get; init; }
}

/// <summary>
/// The master-team-list lookups the job market needs. Supplied by whichever
/// front-end is running (desktop menu or web server) exactly like
/// <see cref="TeamSource"/>: the Competition layer must not reach up into Menu.
/// </summary>
public sealed class JobMarketSource
{
    /// <summary>count deterministic master-list indices to consider as suitors.</summary>
    public required Func<int, List<int>> Sample { get; init; }
    /// <summary>Everything the letter and the next season need about one club.</summary>
    public required Func<int, JobClubInfo> Info { get; init; }

    /// <summary>
    /// Builds the source from a host's master-list accessors. Both front-ends
    /// call this instead of assembling a JobClubInfo themselves: the desktop
    /// menu and the web server hold their team lookups in different shapes, but
    /// a second copy of "trim it, upper-case it, carry the division" would drift
    /// the moment either side was touched.
    /// </summary>
    public static JobMarketSource FromHost(
        Func<int, int, List<int>> randomTeams,
        Func<int, string> teamName,
        Func<int, ushort> teamGlobalId,
        Func<int, int> teamNation,
        Func<int, int> teamDivision,
        Func<int, int> teamStrength,
        Func<int, string> nationName) => new()
    {
        // randomTeams is deterministic in (count, mustInclude), so the same
        // season always considers the same field of clubs.
        Sample = count => randomTeams(count, -1),
        Info = master => new JobClubInfo
        {
            MasterIndex = master,
            GlobalId = teamGlobalId(master),
            Name = (teamName(master) ?? "").Trim().ToUpperInvariant(),
            Nation = teamNation(master),
            NationName = (nationName(teamNation(master)) ?? "").Trim(),
            Division = teamDivision(master),
            Strength = teamStrength(master),
        },
    };
}

/// <summary>One club's approach for the manager, valid for the next season.</summary>
public sealed class JobOffer
{
    public int Id { get; set; }
    public ushort ClubGlobalId { get; set; }
    public int ClubMasterIndex { get; set; }
    public string ClubName { get; set; } = "";
    public int Nation { get; set; }
    public string NationName { get; set; } = "";
    public int Division { get; set; }
    public int Strength { get; set; }
    /// <summary>'IN THE REGION OF %b FOR IMPROVING THE TEAM'.</summary>
    public long TransferFunds { get; set; }
    /// <summary>The season during which the letter arrived; the job starts the next one.</summary>
    public int Season { get; set; }
    /// <summary>Player fixtures the club will wait. 0 = the offer lapses.</summary>
    public int MatchesLeft { get; set; }
    public bool Accepted { get; set; }
    public bool Withdrawn { get; set; }
    /// <summary>False until the manager has opened the letter (flashes the entry).</summary>
    public bool Seen { get; set; }
}

public static class JobMarket
{
    /// <summary>
    /// Master-list lookups, installed once by the front-end (MenuClient and
    /// CareerWeb both do it in their constructor). Null — as in a headless test
    /// that never installs one — simply means no club ever comes calling, so
    /// every existing code path behaves exactly as it did before this feature.
    /// </summary>
    public static JobMarketSource? Source { get; set; }

    // ---- the parts that are OURS (see the fidelity note) -------------------

    /// <summary>Clubs sampled from the master list when drawing suitors.</summary>
    private const int SampleSize = 96;

    /// <summary>League programme completed, in percent, before clubs approach you.</summary>
    private const int ApproachGatePercent = 60;

    /// <summary>Shortlist the draw picks from, after sorting by fit.</summary>
    private const int ShortlistSize = 12;

    /// <summary>Reputation floor and ceiling. 5 = nobody returns your calls.</summary>
    public const int MinReputation = 5;
    public const int MaxReputation = 100;

    /// <summary>
    /// How a club ranks in the game's eyes: squad strength first, division
    /// second. 8 (weak, fourth tier) .. 80 (strongest, top flight). Compared
    /// against the manager's reputation to decide who would have him.
    /// </summary>
    public static int ClubStanding(int strength, int division)
        => Math.Clamp(strength, 1, 7) * 8 + (3 - Math.Clamp(division, 0, 3)) * 4;

    /// <summary>
    /// Reputation a manager starts with: the club that hired him IS his
    /// reputation at the outset, discounted — an unproven name at a big club is
    /// not yet a big name.
    /// </summary>
    public static int StartingReputation(int clubStrength, int division)
        => Math.Clamp(ClubStanding(clubStrength, division) * 3 / 4, 12, 60);

    /// <summary>How many clubs will approach a manager of this standing.</summary>
    private static int OfferSlots(int reputation, bool unemployed)
    {
        int slots = reputation >= 70 ? 3 : reputation >= 45 ? 2 : reputation >= 28 ? 1 : 0;
        // A sacked manager is looking, so somebody smaller takes a chance —
        // but only if he is worth a chance at all.
        if (unemployed) slots = reputation >= 15 ? Math.Clamp(slots, 1, 2) : 0;
        return slots;
    }

    /// <summary>The band of clubs that would have this manager, and that he would have.</summary>
    private static (int Lo, int Hi) StandingBand(int reputation, bool unemployed)
        => unemployed ? (reputation - 40, reputation + 4)
                      : (reputation - 22, reputation + 14);

    // ---- letter text (ORIGINAL wording) ------------------------------------

    private static readonly string[] OfferLines =
    {
        "DEAR SIR/MADAM",
        "WE HERE AT %a WOULD LIKE TO OFFER YOU THE",
        "COACHING JOB AT OUR CLUB FOR NEXT SEASON",
        "WE CAN OFFER YOU A VERY COMPETITIVE SALARY AND",
        "WE HOPE TO BE ABLE TO MAKE FUNDS AVAILABLE",
        "IN THE REGION OF %b FOR IMPROVING THE TEAM",
        "WE LOOK FORWARD TO HEARING FROM YOU",
    };
    private static readonly string[] AcceptedLines =
    {
        "WE HERE AT %a ARE DELIGHTED THAT YOU",
        "HAVE ACCEPTED THE POSITION OF COACH AT OUR CLUB",
        "FROM THE START OF NEXT SEASON",
    };
    private static readonly string[] FarewellLines =
    {
        "THANKS FOR ALL YOUR HELP %a, WE ALL WISH",
        "YOU WELL IN YOUR NEW JOB",
    };
    private static readonly string[] WelcomeLines =
    {
        "WELCOME %a, WE LOOK FORWARD TO MUCH",
        "SUCCESS FROM OUR NEW PARTNERSHIP",
    };
    private static readonly string[] WithdrawnLines = { "JOB OFFER FROM %a WITHDRAWN" };

    /// <summary>Header the original prints above the offer letter.</summary>
    public const string LetterHeader = "JOB OFFER FROM %a";

    /// <summary>
    /// The letter's lines with their placeholders intact (%a = club, %b = the
    /// transfer funds). BOTH clients walk these and substitute themselves, so
    /// each prints the figure in its own money format (the desktop's compact
    /// 5.3M, the browser's 5,300,000) and the desktop can translate line by line
    /// (job.letter.1, .2 ...), exactly as it does with the chairman's memos.
    /// Handing out a pre-substituted letter instead put two different formats
    /// for the same number on the same card.
    /// </summary>
    public static IReadOnlyList<string> OfferLetterLines => OfferLines;

    // ---- queries -------------------------------------------------------------

    /// <summary>Offers still open for an answer, newest first.</summary>
    public static List<JobOffer> LiveOffers(CareerState? career)
    {
        var list = new List<JobOffer>();
        if (career?.JobOffers is null) return list;
        for (int i = career.JobOffers.Count - 1; i >= 0; i--)
        {
            var o = career.JobOffers[i];
            if (o is not null && !o.Withdrawn) list.Add(o);
        }
        return list;
    }

    /// <summary>The offer the manager has accepted, or null.</summary>
    public static JobOffer? AcceptedOffer(CareerState? career)
    {
        if (career?.JobOffers is null) return null;
        foreach (var o in career.JobOffers)
            if (o is not null && o.Accepted && !o.Withdrawn) return o;
        return null;
    }

    /// <summary>Unopened letters, for the "!" flash on the menu entry.</summary>
    public static int UnseenOffers(CareerState? career)
    {
        int n = 0;
        if (career?.JobOffers is null) return 0;
        foreach (var o in career.JobOffers)
            if (o is not null && !o.Withdrawn && !o.Seen) n++;
        return n;
    }

    /// <summary>Marks every open letter read.</summary>
    public static void MarkSeen(CareerState? career)
    {
        if (career?.JobOffers is null) return;
        foreach (var o in career.JobOffers) if (o is not null) o.Seen = true;
    }

    /// <summary>
    /// One line for a dashboard, in the original's words: 'NEW JOB',
    /// 'JOB OFFERS BEING CONSIDERED' or 'NO JOB OFFERS'.
    /// </summary>
    public static string StatusLine(CareerState? career)
    {
        if (career is null) return "";
        var accepted = AcceptedOffer(career);
        if (accepted is not null) return "NEW JOB: " + accepted.ClubName;
        int live = LiveOffers(career).Count;
        if (live > 0) return "JOB OFFERS BEING CONSIDERED";
        return career.Sacked ? "NO JOB OFFERS" : "";
    }

    /// <summary>Reputation as a word, for a screen that has no room for a bar.</summary>
    public static string ReputationLabel(int reputation) => reputation switch
    {
        >= 80 => "WORLD CLASS",
        >= 65 => "HIGHLY RATED",
        >= 48 => "WELL REGARDED",
        >= 32 => "KNOWN",
        >= 18 => "UNPROVEN",
        _ => "UNWANTED",
    };

    // ---- reputation ----------------------------------------------------------

    /// <summary>
    /// Seeds the reputation of a career that has none — a new career, or a save
    /// written before this feature existed. Idempotent.
    /// </summary>
    public static void EnsureSeeded(CareerState? career, int clubStrength)
    {
        if (career is null) return;
        if (career.Reputation <= 0)
            career.Reputation = StartingReputation(clubStrength, career.Division);
    }

    /// <summary>
    /// Moves the manager's reputation after a season, from the same score the
    /// chairman judged him on plus any silverware. Surviving a season is worth a
    /// point on its own: longevity is a reputation.
    /// </summary>
    public static void ApplySeasonReputation(CareerState? career, int seasonScore, int trophiesWon)
    {
        if (career is null) return;
        if (career.Reputation <= 0) career.Reputation = 25;
        int delta = Math.Clamp(seasonScore, -6, 8) + Math.Max(0, trophiesWon) * 4 + 1;
        career.Reputation = Math.Clamp(career.Reputation + delta, MinReputation, MaxReputation);
    }

    // ---- the draw --------------------------------------------------------------

    /// <summary>
    /// Deterministic seed for one career's job market in one season. No
    /// wall-clock, no shared RNG stream: replaying the same season draws the
    /// same suitors.
    /// </summary>
    private static uint SeedFor(CareerState career, int salt)
    {
        uint h = 2166136261u;
        void Mix(uint v) { h ^= v; h *= 16777619u; }
        Mix((uint)career.Season);
        Mix(career.ClubGlobalId);
        Mix((uint)career.Reputation);
        Mix((uint)career.Division);
        foreach (char ch in career.ManagerName ?? "") Mix(ch);
        Mix((uint)salt);
        return h == 0 ? 0x9E3779B9u : h;
    }

    /// <summary>
    /// Draws this season's suitors. Does nothing without a <see cref="Source"/>,
    /// when the manager has already accepted somewhere, or when nobody of the
    /// right standing would have him.
    /// </summary>
    /// <param name="matchesToDecide">
    /// How many of the club's own fixtures the suitors will wait. Passed in
    /// rather than rolled, because a letter that lapses BEFORE the season ends
    /// takes the decision away from the manager: the season rollover is the
    /// only moment a move can actually happen ('FOR NEXT SEASON'), so an offer
    /// has to survive until then. What kills an offer early is the manager's
    /// own board turning on him, which is exactly what the original's
    /// 'JOB OFFER FROM %a WITHDRAWN' reads like.
    /// </param>
    public static void DrawOffers(
        CareerState? career, CareerWorld? world, bool unemployed, int matchesToDecide)
    {
        if (career is null || Source is null) return;
        if (career.Retired && !career.Sacked) return;      // a retirement is a retirement
        if (AcceptedOffer(career) is not null) return;
        career.JobOffers ??= new List<JobOffer>();

        int slots = OfferSlots(career.Reputation, unemployed);
        if (slots <= 0) return;

        var (lo, hi) = StandingBand(career.Reputation, unemployed);
        var shortlist = new List<(JobClubInfo Club, int Fit)>();
        List<int> sample;
        try { sample = Source.Sample(SampleSize + career.Season * 5 + (unemployed ? 17 : 0)); }
        catch { return; }
        if (sample is null) return;

        foreach (int master in sample)
        {
            JobClubInfo? info;
            try { info = Source.Info(master); } catch { continue; }
            if (info is null || info.GlobalId == 0) continue;
            if (info.GlobalId == career.ClubGlobalId) continue;
            // SWOS reserves nations 80..85 for its national-team files; those
            // sides do not hire club coaches (the international job is its own
            // feature).
            if (info.Nation is >= 80 and <= 85) continue;
            if (world is not null && world.NationalTeamIds.Contains(info.GlobalId)) continue;
            int standing = ClubStanding(info.Strength, info.Division);
            if (standing < lo || standing > hi) continue;
            shortlist.Add((info, Math.Abs(standing - career.Reputation)));
        }
        if (shortlist.Count == 0) return;

        shortlist.Sort((a, b) => a.Fit != b.Fit
            ? a.Fit.CompareTo(b.Fit)
            : string.CompareOrdinal(a.Club.Name, b.Club.Name));
        if (shortlist.Count > ShortlistSize)
            shortlist.RemoveRange(ShortlistSize, shortlist.Count - ShortlistSize);

        var rng = new CareerRng(SeedFor(career, unemployed ? 77 : 11), career.Season);
        var taken = new HashSet<ushort>();
        foreach (var o in career.JobOffers) if (o is not null && !o.Withdrawn) taken.Add(o.ClubGlobalId);

        int drawn = 0, guard = 0;
        while (drawn < slots && shortlist.Count > 0 && guard++ < 64)
        {
            int pick = rng.NextInt(shortlist.Count);
            var club = shortlist[pick].Club;
            shortlist.RemoveAt(pick);
            if (!taken.Add(club.GlobalId)) continue;

            career.JobOffers.Add(new JobOffer
            {
                Id = career.NextJobOfferId++,
                ClubGlobalId = club.GlobalId,
                ClubMasterIndex = club.MasterIndex,
                ClubName = club.Name,
                Nation = club.Nation,
                NationName = club.NationName,
                Division = club.Division,
                Strength = club.Strength,
                TransferFunds = FundsFor(club, world),
                Season = career.Season,
                MatchesLeft = Math.Max(1, matchesToDecide),
            });
            drawn++;
        }
        // Keep the letter pile bounded; the oldest lapse first.
        while (career.JobOffers.Count > 8) career.JobOffers.RemoveAt(0);
    }

    /// <summary>
    /// 'WE HOPE TO BE ABLE TO MAKE FUNDS AVAILABLE IN THE REGION OF %b'. Real
    /// money where we have it — half of what the club actually holds — so the
    /// promise is one the new job can keep.
    /// </summary>
    private static long FundsFor(JobClubInfo club, CareerWorld? world)
    {
        if (world is not null && world.Clubs.TryGetValue(club.GlobalId, out var c)
            && c is not null && c.Budget > 0)
            return c.Budget / 2L;
        return 250_000L * Math.Clamp(club.Strength, 1, 7) * (4 - Math.Clamp(club.Division, 0, 3));
    }

    // ---- the season, one fixture at a time -------------------------------------

    /// <summary>
    /// Runs the job market after a fixture the managed club played: clubs
    /// approach once the season is far enough along, open letters lapse, and a
    /// manager whose board has lost patience watches his suitors walk away.
    /// </summary>
    /// <param name="leaguePlayed">League fixtures the club has played.</param>
    /// <param name="leagueTotal">League fixtures the club plays in a season.</param>
    public static void AfterPlayerFixture(
        CareerState? career, CareerWorld? world, int leaguePlayed, int leagueTotal)
    {
        if (career is null || career.Retired) return;
        int leagueFixturesPlayedPercent =
            leagueTotal > 0 ? leaguePlayed * 100 / leagueTotal : 0;
        career.JobOffers ??= new List<JobOffer>();

        // A club that sees your own board turn on you loses interest. This is
        // what 'JOB OFFER FROM %a WITHDRAWN' is for.
        bool boardTurned = career.CrisisMatchesLeft > 0 || career.MoneyCrisisMatchesLeft > 0
                           || career.ChairmanWarnLeague >= 2 || career.ChairmanWarnMoney >= 2;

        foreach (var o in career.JobOffers)
        {
            if (o is null || o.Withdrawn || o.Accepted) continue;
            if (boardTurned) { Withdraw(career, o); continue; }
            if (o.MatchesLeft > 0) o.MatchesLeft--;
            if (o.MatchesLeft == 0) Withdraw(career, o);
        }

        if (boardTurned) return;
        if (career.JobOffersDrawnSeason == career.Season) return;
        if (leagueFixturesPlayedPercent < ApproachGatePercent) return;
        career.JobOffersDrawnSeason = career.Season;
        // They wait out the rest of the club's league programme, so the answer
        // can be given at the rollover — one match longer, so the last fixture
        // does not lapse them on the way in.
        DrawOffers(career, world, unemployed: false,
            matchesToDecide: Math.Max(1, leagueTotal - leaguePlayed) + 1);
    }

    /// <summary>
    /// A dismissed manager goes looking for work. Called the moment the sacking
    /// is filed, so the offers are on the table while the career screen still is.
    /// </summary>
    public static void AfterSacking(CareerState? career, CareerWorld? world)
    {
        if (career is null) return;
        career.JobOffers ??= new List<JobOffer>();
        // Whatever was on the table before the sacking is gone with the job.
        foreach (var o in career.JobOffers)
            if (o is not null && !o.Withdrawn && !o.Accepted) Withdraw(career, o);
        // Being sacked costs a manager standing before anyone else calls.
        career.Reputation = Math.Clamp(
            (career.Reputation <= 0 ? 25 : career.Reputation) - 8, MinReputation, MaxReputation);
        career.JobOffersDrawnSeason = career.Season;
        // Out of work there are no more fixtures to tick the wait down, so the
        // letters simply stand until he answers them.
        DrawOffers(career, world, unemployed: true, matchesToDecide: 1);
    }

    /// <summary>
    /// Clears last season's letters at the rollover. They were offers to coach
    /// THIS season, which has now started; leaving them on the pile would let a
    /// manager accept a job a year late. Silent — the board never wrote about
    /// them, and the memo inbox is not the place for bookkeeping.
    /// </summary>
    public static void StartNewSeason(CareerState? career)
    {
        if (career?.JobOffers is null) return;
        career.JobOffers.Clear();
        career.JobOffersDrawnSeason = 0;
    }

    private static void Withdraw(CareerState career, JobOffer offer)
    {
        offer.Withdrawn = true;
        var memo = new ChairmanMemo
        {
            Key = "job.withdrawn",
            Kind = "job_withdrawn",
            Severity = 1,
            Season = career.Season,
            Subject = offer.ClubName,
        };
        memo.Lines.Add(WithdrawnLines[0].Replace("%a", offer.ClubName));
        ChairmanModel.FileMemo(career, memo);
    }

    // ---- answering ---------------------------------------------------------------

    /// <summary>
    /// Accepts an offer. The move itself happens at the season rollover, which
    /// is what the original's 'FOR NEXT SEASON' means; until then the manager is
    /// still in charge of his current club.
    /// </summary>
    public static bool Accept(CareerState? career, int offerId)
    {
        if (career?.JobOffers is null) return false;
        if (AcceptedOffer(career) is not null) return false;
        JobOffer? target = null;
        foreach (var o in career.JobOffers)
            if (o is not null && o.Id == offerId && !o.Withdrawn) { target = o; break; }
        if (target is null) return false;

        target.Accepted = true;
        target.Seen = true;
        // Every other suitor is told no.
        foreach (var o in career.JobOffers)
            if (o is not null && !ReferenceEquals(o, target) && !o.Withdrawn) o.Withdrawn = true;

        var memo = new ChairmanMemo
        {
            Key = "job.accepted", Kind = "job_accepted", Severity = 0, Season = career.Season,
            Subject = target.ClubName,
        };
        foreach (string l in AcceptedLines) memo.Lines.Add(l.Replace("%a", target.ClubName));
        ChairmanModel.FileMemo(career, memo);
        return true;
    }

    /// <summary>Turns an offer down. It does not come back this season.</summary>
    public static bool Decline(CareerState? career, int offerId)
    {
        if (career?.JobOffers is null) return false;
        foreach (var o in career.JobOffers)
            if (o is not null && o.Id == offerId && !o.Withdrawn && !o.Accepted)
            {
                o.Withdrawn = true;
                o.Seen = true;
                return true;
            }
        return false;
    }

    // ---- the move ------------------------------------------------------------------

    /// <summary>
    /// True when the manager has somewhere else to be next season — the caller
    /// must build the new season's pools around <see cref="AcceptedOffer"/>.
    /// </summary>
    public static bool HasAcceptedOffer(CareerState? career) => AcceptedOffer(career) is not null;

    /// <summary>
    /// Carries out an accepted move at the season rollover: files the two
    /// letters the original prints, writes the record line, and re-points the
    /// career at the new club. A sacked manager is back in work.
    /// </summary>
    /// <returns>The club moved to, or null when there was nothing to do.</returns>
    public static JobOffer? ApplyPendingMove(CareerState? career)
    {
        var offer = AcceptedOffer(career);
        if (career is null || offer is null) return null;

        string oldClub = career.ClubName ?? "";
        string mgr = string.IsNullOrWhiteSpace(career.ManagerName) ? "BOSS" : career.ManagerName.Trim();

        // 'THANKS FOR ALL YOUR HELP %a...' — only if he still had a club to leave.
        if (!career.Sacked && oldClub.Length > 0)
        {
            var bye = new ChairmanMemo
            { Key = "job.farewell", Kind = "job_farewell", Severity = 0, Season = career.Season,
              Subject = mgr };
            foreach (string l in FarewellLines) bye.Lines.Add(l.Replace("%a", mgr));
            ChairmanModel.FileMemo(career, bye);
        }

        career.ClubGlobalId = offer.ClubGlobalId;
        career.ClubName = offer.ClubName;
        career.Nation = offer.Nation;
        career.Division = offer.Division;
        career.SeasonsAtClub = 0;
        career.Sacked = false;
        career.Retired = false;
        career.ConsecutiveBadSeasons = 0;
        career.LastAccount = null;      // the new club's books are its own
        career.JobOffers.Clear();
        career.JobOffersDrawnSeason = 0;

        var hello = new ChairmanMemo
        { Key = "job.welcome", Kind = "job_welcome", Severity = 0, Season = career.Season,
          Subject = mgr };
        foreach (string l in WelcomeLines) hello.Lines.Add(l.Replace("%a", mgr));
        ChairmanModel.FileMemo(career, hello);

        career.History.Add(oldClub.Length > 0
            ? $"S{career.Season}: LEFT {oldClub} FOR {offer.ClubName}"
            : $"S{career.Season}: JOINED {offer.ClubName}");
        return offer;
    }
}
