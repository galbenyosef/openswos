namespace OpenSwos.Competition.Career;

using System;
using System.Collections.Generic;

// ============================================================================
// The national-team job — career depth plan feature #4
// (docs/decisions/03-career-depth-plan.md).
//
// FIDELITY NOTE. Every letter and every screen label below is the ORIGINAL
// game's text, recovered in order from
// external/original-amiga-swos/original-amiga-swos.asm:
//
//   the letter   'THE MEMBERS OF THE FOOTBALLING COMMITEE'
//                'OF %a WOULD LIKE TO OFFER YOU THE JOB'
//                'AS COACH OF THE NATIONAL TEAM'
//                'WE CAN PROMISE YOU AN EXCELLENT SALARY AND'
//                'AN ANNUALLY REVIEWABLE CONTRACT'
//                'WE LOOK FORWARD TO HEARING FROM YOU'
//   accepting    'THE MEMBERS OF THE FOOTBALLING COMMITEE'
//                'OF %a ARE DELIGHTED THAT YOU HAVE'
//                'ACCEPTED THE POSITION AS COACH OF THE NATIONAL TEAM'
//   labels       'INTERNATIONAL JOB OFFER' 'INTERNATIONAL JOB OFFER FROM %a'
//                'ADD INTERNATIONAL PLAYER (HOME)'
//                'ADD INTERNATIONAL PLAYER (ABROAD)'
//                'SELECT 1 MORE INTERNATIONAL SQUAD PLAYER'
//                'SELECT %0 MORE INTERNATIONAL SQUAD PLAYERS'
//                '%a SQUAD'  'NATIONAL TEAMS'
//
// The original's split of the pool into HOME (playing club football in the
// country) and ABROAD is reproduced, because it is the one thing that makes
// selection feel like international management rather than a second squad list.
//
// WHAT IS OURS. The strings are raw data with no symbols, so nothing about WHO
// is offered the job, WHEN, or what the national side then plays is
// recoverable. The reputation gate, the squad size, the tournament format and
// the annual review are ours and are gathered at the top of this file. The job
// is held ALONGSIDE a club — 'AN ANNUALLY REVIEWABLE CONTRACT' is a side job,
// not a career change, and the original kept the club menus alive throughout.
//
// Determinism: no wall-clock, no System.Random. The draw and the tournament are
// keyed to the career, so replaying a season produces the same tournament.
// ============================================================================

/// <summary>A federation's approach, and the job once it is accepted.</summary>
public sealed class NationalJobOffer
{
    /// <summary>TEAM.* master index of the national side.</summary>
    public int MasterIndex { get; set; }
    public ushort GlobalId { get; set; }
    /// <summary>The country, as the roster spells it ("POLAND").</summary>
    public string Country { get; set; } = "";
    /// <summary>TEAM.* nation byte of the national side's file (80..85).</summary>
    public int TeamNation { get; set; }
    /// <summary>Continent code, 0 EUROPE .. 5 OCEANIA.</summary>
    public int Continent { get; set; }
    /// <summary>Player-record nationality byte for this country, or -1.</summary>
    public int PlayerNationality { get; set; } = -1;
    public int Season { get; set; }
    public bool Seen { get; set; }
}

/// <summary>One eligible international, with the HOME/ABROAD split.</summary>
public sealed class NationalCandidate
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public int Age { get; set; }
    public int Overall { get; set; }
    public int Skill { get; set; }
    public ushort ClubId { get; set; }
    /// <summary>True when the player's club is in the country he plays for.</summary>
    public bool Home { get; set; }
    public bool Selected { get; set; }
}

/// <summary>What the national side did in one tournament.</summary>
public sealed class NationalSeasonResult
{
    public int Season { get; set; }
    public string Tournament { get; set; } = "";
    /// <summary>"WINNERS" / "RUNNERS UP" / "OUT IN SEMI FINAL" / "OUT IN ROUND 1".</summary>
    public string Result { get; set; } = "";
    public bool Won { get; set; }
    /// <summary>One line per match: "POLAND 2-1 SPAIN".</summary>
    public List<string> Matches { get; set; } = new();
    /// <summary>Rounds the national side won.</summary>
    public int RoundsWon { get; set; }
    public int Squad { get; set; }
    public int SquadStrength { get; set; }
}

/// <summary>Master-list lookups the national job needs, supplied by the front-end.</summary>
public sealed class NationalSource
{
    /// <summary>Master indices of the teams in a TEAM.* nation file.</summary>
    public required Func<int, List<int>> TeamsInNation { get; init; }
    /// <summary>Name / GlobalId / nation / strength for one master index.</summary>
    public required Func<int, JobClubInfo> Info { get; init; }

    /// <summary>Builds it from a host's accessors, as JobMarketSource does.</summary>
    public static NationalSource FromHost(
        Func<int, int, int, List<int>> teamsByNationDivision,
        Func<int, string> teamName,
        Func<int, ushort> teamGlobalId,
        Func<int, int> teamNation,
        Func<int, int> teamDivision,
        Func<int, int> teamStrength) => new()
    {
        TeamsInNation = nation => teamsByNationDivision(nation, -1, 128),
        Info = master => new JobClubInfo
        {
            MasterIndex = master,
            GlobalId = teamGlobalId(master),
            Name = (teamName(master) ?? "").Trim().ToUpperInvariant(),
            Nation = teamNation(master),
            Division = teamDivision(master),
            Strength = teamStrength(master),
        },
    };
}

public static class NationalJob
{
    /// <summary>Master-list lookups, installed once by each front-end.</summary>
    public static NationalSource? Source { get; set; }

    // ---- the parts that are OURS -------------------------------------------

    /// <summary>
    /// Reputation a federation wants before it will call — the WELL REGARDED
    /// band. MEASURED, not guessed: six seasons at JUVENTUS with two trophies
    /// (2026-08-23) ran between 40 and 48, so a higher bar would have made this
    /// whole feature unreachable content.
    /// </summary>
    public const int ReputationGate = 48;

    /// <summary>Trophies a committee wants to see before it hires a club coach.</summary>
    public const int TrophyGate = 1;

    /// <summary>Reputation below which a federation ends the contract.</summary>
    public const int ReputationFloor = 36;

    /// <summary>Internationals in a squad. SWOS national sides carry 16.</summary>
    public const int SquadSize = 16;

    /// <summary>Teams in the tournament the national side plays each season.</summary>
    public const int TournamentSize = 8;

    /// <summary>TEAM.* nation files that hold national sides, by continent code.</summary>
    private static readonly int[] ContinentFile = { 80, 81, 82, 83, 84, 85 };

    /// <summary>The continental tournament, in the original's preset names.</summary>
    public static string TournamentName(int continent) => continent switch
    {
        0 => "EUROPEAN CHAMPIONSHIP",
        2 => "COPA AMERICA",
        1 => "AFRICAN NATIONS CUP",
        3 => "GOLD CUP",
        4 => "ASIAN CUP",
        _ => "OCEANIA CUP",
    };

    // ---- letter text (ORIGINAL wording) ------------------------------------

    private static readonly string[] OfferLines =
    {
        "THE MEMBERS OF THE FOOTBALLING COMMITEE",
        "OF %a WOULD LIKE TO OFFER YOU THE JOB",
        "AS COACH OF THE NATIONAL TEAM",
        "WE CAN PROMISE YOU AN EXCELLENT SALARY AND",
        "AN ANNUALLY REVIEWABLE CONTRACT",
        "WE LOOK FORWARD TO HEARING FROM YOU",
    };
    private static readonly string[] AcceptedLines =
    {
        "THE MEMBERS OF THE FOOTBALLING COMMITEE",
        "OF %a ARE DELIGHTED THAT YOU HAVE",
        "ACCEPTED THE POSITION AS COACH OF THE NATIONAL TEAM",
    };
    private static readonly string[] EndedLines =
    {
        "THE MEMBERS OF THE FOOTBALLING COMMITEE",
        "OF %a HAVE DECIDED NOT TO RENEW YOUR",
        "ANNUALLY REVIEWABLE CONTRACT",
    };

    /// <summary>'INTERNATIONAL JOB OFFER FROM %a'.</summary>
    public const string OfferHeader = "INTERNATIONAL JOB OFFER FROM %a";

    /// <summary>The letter's lines with %a intact, so each client substitutes its own way.</summary>
    public static IReadOnlyList<string> OfferLetterLines => OfferLines;

    // ---- queries -------------------------------------------------------------

    public static bool HasJob(CareerState? career)
        => career is not null && career.NationalTeamId != 0;

    public static bool HasOffer(CareerState? career)
        => career?.NationalOffer is not null;

    /// <summary>'%a SQUAD' — how many more internationals the manager must name.</summary>
    public static int StillToSelect(CareerState? career)
        => career is null ? 0 : Math.Max(0, SquadSize - (career.NationalSquad?.Count ?? 0));

    /// <summary>One line for a dashboard: the job, or the offer waiting on it.</summary>
    public static string StatusLine(CareerState? career)
    {
        if (career is null) return "";
        if (HasOffer(career)) return "INTERNATIONAL JOB OFFER FROM " + career.NationalOffer!.Country;
        if (!HasJob(career)) return "";
        int missing = StillToSelect(career);
        if (missing == 1) return "SELECT 1 MORE INTERNATIONAL SQUAD PLAYER";
        if (missing > 1) return "SELECT " + missing + " MORE INTERNATIONAL SQUAD PLAYERS";
        return career.NationalCountry + " SQUAD";
    }

    // ---- the offer -------------------------------------------------------------

    /// <summary>
    /// A federation approaches the manager at the end of a season. The country
    /// is the one he is working in: a national committee hires the man whose
    /// work it watches every week. Needs a real reputation, and never arrives
    /// while he already holds the job or is out of work.
    /// </summary>
    public static void MaybeOffer(CareerState? career, CareerWorld? world, int clubNation)
    {
        if (career is null || Source is null) return;
        if (career.Retired || career.Sacked) return;
        if (HasJob(career) || HasOffer(career)) return;
        if (career.Reputation < ReputationGate) return;
        // A committee hires a coach who has actually won something.
        if ((career.Trophies?.Count ?? 0) < TrophyGate) return;

        var side = FindNationalSide(clubNation);
        if (side is null) return;

        career.NationalOffer = side;
        side.Season = career.Season;
        var memo = new ChairmanMemo
        {
            Key = "natjob.offer", Kind = "national_offer", Severity = 0,
            Season = career.Season, Subject = side.Country,
        };
        foreach (string l in OfferLines) memo.Lines.Add(l.Replace("%a", side.Country));
        ChairmanModel.FileMemo(career, memo);
    }

    /// <summary>
    /// The national side of a country, found from the club-nation index. The
    /// two numbering systems do not line up, so the country NAME is the bridge:
    /// the club file's country name is matched against the national sides in
    /// the continental files.
    /// </summary>
    public static NationalJobOffer? FindNationalSide(int clubNation)
    {
        if (Source is null) return null;
        string country = OpenSwos.Assets.NationNames.Name(clubNation);
        if (string.IsNullOrWhiteSpace(country) || country.StartsWith("NATION ", StringComparison.Ordinal))
            return null;

        foreach (int file in ContinentFile)
        {
            List<int> teams;
            try { teams = Source.TeamsInNation(file); } catch { continue; }
            if (teams is null) continue;
            foreach (int master in teams)
            {
                JobClubInfo? info;
                try { info = Source.Info(master); } catch { continue; }
                if (info is null) continue;
                if (!SameCountry(info.Name, country)) continue;
                return new NationalJobOffer
                {
                    MasterIndex = master,
                    GlobalId = info.GlobalId,
                    Country = info.Name,
                    TeamNation = info.Nation,
                    Continent = OpenSwos.Assets.NationNames.Continent(clubNation),
                    PlayerNationality = OpenSwos.Assets.PlayerNationNames.IndexOfCountry(info.Name),
                };
            }
        }
        return null;
    }

    private static bool SameCountry(string a, string b)
    {
        static string Canon(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }
        string x = Canon(a ?? ""), y = Canon(b ?? "");
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y
            || (x.Length >= 6 && y.Length >= 6
                && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)));
    }

    /// <summary>Takes the job. The club job continues alongside it.</summary>
    public static bool AcceptOffer(CareerState? career)
    {
        var offer = career?.NationalOffer;
        if (career is null || offer is null) return false;

        career.NationalTeamId = offer.GlobalId;
        career.NationalMasterIndex = offer.MasterIndex;
        career.NationalCountry = offer.Country;
        career.NationalContinent = offer.Continent;
        career.NationalPlayerNationality = offer.PlayerNationality;
        career.NationalSince = career.Season;
        career.NationalSquad ??= new List<int>();
        career.NationalSquad.Clear();
        career.NationalOffer = null;

        var memo = new ChairmanMemo
        {
            Key = "natjob.accepted", Kind = "national_accepted", Severity = 0,
            Season = career.Season, Subject = offer.Country,
        };
        foreach (string l in AcceptedLines) memo.Lines.Add(l.Replace("%a", offer.Country));
        ChairmanModel.FileMemo(career, memo);
        career.History.Add($"S{career.Season}: APPOINTED COACH OF {offer.Country}");
        return true;
    }

    /// <summary>Turns the federation down. It does not ask again this season.</summary>
    public static bool DeclineOffer(CareerState? career)
    {
        if (career?.NationalOffer is null) return false;
        career.NationalOffer = null;
        return true;
    }

    /// <summary>Ends the job — resignation, or the federation not renewing.</summary>
    public static void EndJob(CareerState career, bool byFederation)
    {
        if (career is null || !HasJob(career)) return;
        string country = career.NationalCountry;
        if (byFederation)
        {
            var memo = new ChairmanMemo
            {
                Key = "natjob.ended", Kind = "national_ended", Severity = 2,
                Season = career.Season, Subject = country,
            };
            foreach (string l in EndedLines) memo.Lines.Add(l.Replace("%a", country));
            ChairmanModel.FileMemo(career, memo);
            career.History.Add($"S{career.Season}: LEFT THE {country} JOB");
        }
        career.NationalTeamId = 0;
        career.NationalMasterIndex = -1;
        career.NationalCountry = "";
        career.NationalPlayerNationality = -1;
        career.NationalSquad?.Clear();
    }

    // ---- the squad ---------------------------------------------------------------

    /// <summary>
    /// Everyone eligible for the national side, best first, split into the
    /// original's two pools: HOME (playing club football in the country) and
    /// ABROAD. Eligibility is the player's own nationality byte, so a squad is
    /// drawn from the whole world exactly as the original's two ADD screens are.
    /// </summary>
    public static List<NationalCandidate> Candidates(CareerState? career, CareerWorld? world)
    {
        var list = new List<NationalCandidate>();
        if (career is null || world?.Clubs is null || !HasJob(career)) return list;
        int nat = career.NationalPlayerNationality;
        if (nat < 0) return list;

        var homeClubs = HomeClubIds(career, world);
        var selected = new HashSet<int>(career.NationalSquad ?? new List<int>());

        foreach (var club in world.Clubs.Values)
        {
            if (club?.Squad is null) continue;
            if (world.NationalTeamIds is not null && world.NationalTeamIds.Contains(club.GlobalId)) continue;
            foreach (var p in club.Squad)
            {
                if (p is null || p.Retired || p.Nationality != nat) continue;
                list.Add(new NationalCandidate
                {
                    PlayerId = p.Id,
                    Name = p.Name ?? "",
                    Position = p.Position ?? "",
                    Age = p.Age,
                    Overall = p.EffectiveOverall(),
                    Skill = p.EffectiveSkillSum(),
                    ClubId = club.GlobalId,
                    Home = homeClubs.Contains(club.GlobalId),
                    Selected = selected.Contains(p.Id),
                });
            }
        }
        list.Sort((a, b) =>
        {
            int bySkill = b.Skill.CompareTo(a.Skill);
            if (bySkill != 0) return bySkill;
            int byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : a.PlayerId.CompareTo(b.PlayerId);
        });
        return list;
    }

    /// <summary>Club GlobalIds based in the country the manager coaches.</summary>
    private static HashSet<ushort> HomeClubIds(CareerState career, CareerWorld world)
    {
        var ids = new HashSet<ushort>();
        if (Source is null) return ids;
        // The country's own TEAM.* file is the club nation the manager works in
        // only when he is employed there; the national side's country name is
        // authoritative, so find the club file that carries that name.
        for (int nation = 0; nation < 80; nation++)
        {
            string name = OpenSwos.Assets.NationNames.Name(nation);
            if (!SameCountry(name, career.NationalCountry)) continue;
            List<int> teams;
            try { teams = Source.TeamsInNation(nation); } catch { break; }
            if (teams is null) break;
            foreach (int master in teams)
            {
                try { ids.Add(Source.Info(master).GlobalId); } catch { }
            }
            break;
        }
        return ids;
    }

    /// <summary>Adds or removes an international. Returns false when the squad is full.</summary>
    public static bool ToggleSelection(CareerState? career, int playerId)
    {
        if (career is null || !HasJob(career)) return false;
        career.NationalSquad ??= new List<int>();
        if (career.NationalSquad.Remove(playerId)) return true;
        if (career.NationalSquad.Count >= SquadSize) return false;
        career.NationalSquad.Add(playerId);
        return true;
    }

    /// <summary>Squad shape a federation names: 2 keepers, 5 at the back, 6 in
    /// midfield, 3 up front — SWOS's own 16-man split.</summary>
    private static readonly (string Line, int Count)[] SquadShape =
        { ("G", 2), ("D", 5), ("M", 6), ("A", 3) };

    /// <summary>Which of the four lines a SWOS position belongs to.</summary>
    private static string LineOf(string position) => position switch
    {
        "G" => "G",
        "D" or "LB" or "RB" => "D",
        "A" => "A",
        _ => "M",           // M, LW, RW
    };

    /// <summary>
    /// Names the strongest BALANCED squad. Used by the AUTO PICK button and,
    /// silently, when a tournament starts with an unnamed squad — a federation
    /// fields a team whatever the coach does.
    ///
    /// Balance matters: picking purely by ability named eleven strikers and one
    /// goalkeeper (measured while playing Italy, 2026-08-23), which is not a
    /// squad any committee would announce.
    /// </summary>
    public static void AutoPick(CareerState? career, CareerWorld? world)
    {
        if (career is null || !HasJob(career)) return;
        career.NationalSquad ??= new List<int>();
        career.NationalSquad.Clear();

        var pool = Candidates(career, world);      // already best-first
        foreach (var (line, count) in SquadShape)
        {
            int taken = 0;
            foreach (var c in pool)
            {
                if (taken >= count || career.NationalSquad.Count >= SquadSize) break;
                if (LineOf(c.Position) != line) continue;
                if (career.NationalSquad.Contains(c.PlayerId)) continue;
                career.NationalSquad.Add(c.PlayerId);
                taken++;
            }
        }
        // A thin pool may not fill every line; top up with whoever is left.
        foreach (var c in pool)
        {
            if (career.NationalSquad.Count >= SquadSize) break;
            if (!career.NationalSquad.Contains(c.PlayerId)) career.NationalSquad.Add(c.PlayerId);
        }
    }

    /// <summary>
    /// The selected squad's strength on the 1..7 scale the simulation uses.
    /// This is the whole point of picking a squad: it IS the national side's
    /// rating in the tournament below.
    /// </summary>
    public static int SquadStrength(CareerState? career, CareerWorld? world)
    {
        if (career is null || world?.Clubs is null) return 3;
        var ids = new HashSet<int>(career.NationalSquad ?? new List<int>());
        if (ids.Count == 0) return 3;
        int total = 0, n = 0;
        foreach (var club in world.Clubs.Values)
        {
            if (club?.Squad is null) continue;
            foreach (var p in club.Squad)
                if (p is not null && ids.Contains(p.Id)) { total += p.EffectiveOverall(); n++; }
        }
        return n > 0 ? Math.Clamp((int)Math.Round((double)total / n, MidpointRounding.AwayFromZero), 1, 7) : 3;
    }

    // ---- the tournament ------------------------------------------------------------

    /// <summary>
    /// Plays the national side's tournament for the season that has just ended,
    /// then the federation reviews the contract ('AN ANNUALLY REVIEWABLE
    /// CONTRACT'). Straight knockout between the continent's strongest sides,
    /// simulated with the same engine the career's AI fixtures use — so the
    /// squad the manager named is exactly what decides how far it goes.
    /// </summary>
    public static NationalSeasonResult? RunSeason(CareerState? career, CareerWorld? world)
    {
        if (career is null || Source is null || !HasJob(career)) return null;

        // A federation fields a team whether or not the coach named one.
        if ((career.NationalSquad?.Count ?? 0) == 0) AutoPick(career, world);

        var field = BuildField(career);
        if (field.Count < 2) return null;

        int myStrength = SquadStrength(career, world);
        int me = -1;
        for (int i = 0; i < field.Count; i++)
            if (field[i].GlobalId == career.NationalTeamId)
            {
                field[i].Strength = myStrength;
                me = i;
                break;
            }
        if (me < 0) return null;

        int seed = unchecked((int)Seed(career));
        var cup = CompetitionEngine.CreateCup(
            TournamentName(career.NationalContinent), field, me, seed);

        var result = new NationalSeasonResult
        {
            Season = career.Season,
            Tournament = cup.Name,
            Squad = career.NationalSquad?.Count ?? 0,
            SquadStrength = myStrength,
        };

        for (int guard = 0; guard < 64 && !cup.Finished; guard++)
        {
            var fx = CompetitionEngine.NextFixture(cup);
            if (fx is null) break;
            var (h, a) = CompetitionEngine.SimulateResult(cup, fx);
            CompetitionEngine.RecordResult(cup, fx, h, a);
            if (fx.HomeTeam == me || fx.AwayTeam == me)
            {
                string line = $"{cup.Teams[fx.HomeTeam].Name} {h}-{a} {cup.Teams[fx.AwayTeam].Name}";
                if (fx.OnPenalties)
                    line += " (" + cup.Teams[fx.PenaltyWinner].Name + " ON PENALTIES)";
                result.Matches.Add(line);
                bool through = WonIt(fx, me);
                if (through) result.RoundsWon++;
                else
                {
                    result.Result = "OUT IN " + StageLabel(fx.Stage);
                    break;
                }
            }
        }

        if (cup.Finished && cup.Champion == me)
        {
            result.Result = "WINNERS";
            result.Won = true;
        }
        else if (result.Result.Length == 0)
        {
            result.Result = "OUT IN " + (result.Matches.Count > 0 ? "THE TOURNAMENT" : "ROUND 1");
        }
        if (!result.Won && result.Matches.Count > 0
            && result.Result.EndsWith("FINAL", StringComparison.Ordinal))
            result.Result = "RUNNERS UP";

        career.NationalHistory ??= new List<NationalSeasonResult>();
        career.NationalHistory.Add(result);
        while (career.NationalHistory.Count > 20) career.NationalHistory.RemoveAt(0);
        career.LastNationalResult = result;

        if (result.Won)
        {
            career.Trophies.Add($"SEASON {career.Season} {result.Tournament} WINNERS");
            career.History.Add($"S{career.Season}: {career.NationalCountry} WON THE {result.Tournament}");
            career.Reputation = Math.Clamp(career.Reputation + 6,
                JobMarket.MinReputation, JobMarket.MaxReputation);
        }
        else
        {
            career.History.Add($"S{career.Season}: {career.NationalCountry} {result.Result} - {result.Tournament}");
            if (result.RoundsWon > 0)
                career.Reputation = Math.Clamp(career.Reputation + result.RoundsWon,
                    JobMarket.MinReputation, JobMarket.MaxReputation);
        }

        // 'AN ANNUALLY REVIEWABLE CONTRACT': first-round exits and a manager the
        // game has stopped rating both end it.
        if (result.RoundsWon == 0 || career.Reputation < ReputationFloor)
            EndJob(career, byFederation: true);
        return result;
    }

    private static bool WonIt(Fixture f, int team)
    {
        if (f.OnPenalties) return f.PenaltyWinner == team;
        int winner = f.HomeGoals > f.AwayGoals ? f.HomeTeam
                   : f.AwayGoals > f.HomeGoals ? f.AwayTeam : -1;
        return winner == team;
    }

    private static string StageLabel(string stage)
        => stage.StartsWith("CUP ", StringComparison.Ordinal) ? stage.Substring(4) : stage;

    /// <summary>
    /// The tournament field: the continent's strongest national sides, always
    /// including the manager's own. Deterministic — it is a strength ranking,
    /// not a draw.
    /// </summary>
    private static List<TeamRef> BuildField(CareerState career)
    {
        var field = new List<TeamRef>();
        if (Source is null) return field;
        int file = career.NationalContinent >= 0 && career.NationalContinent < ContinentFile.Length
            ? ContinentFile[career.NationalContinent] : 80;

        List<int> teams;
        try { teams = Source.TeamsInNation(file); } catch { return field; }
        if (teams is null) return field;

        var pool = new List<JobClubInfo>();
        foreach (int master in teams)
        {
            try
            {
                var info = Source.Info(master);
                if (info is not null && info.GlobalId != 0) pool.Add(info);
            }
            catch { }
        }
        pool.Sort((a, b) =>
        {
            int byStrength = b.Strength.CompareTo(a.Strength);
            return byStrength != 0 ? byStrength : string.CompareOrdinal(a.Name, b.Name);
        });

        void Add(JobClubInfo info) => field.Add(new TeamRef
        {
            MasterIndex = info.MasterIndex,
            GlobalId = info.GlobalId,
            Name = info.Name,
            Strength = info.Strength,
        });

        foreach (var info in pool)
        {
            if (field.Count >= TournamentSize) break;
            if (info.GlobalId == career.NationalTeamId) continue;
            Add(info);
        }
        // The manager's own side always takes part, even in a weak continent.
        foreach (var info in pool)
            if (info.GlobalId == career.NationalTeamId)
            {
                if (field.Count >= TournamentSize) field.RemoveAt(field.Count - 1);
                Add(info);
                break;
            }
        // CreateCup wants a power of two.
        while (field.Count > 1 && (field.Count & (field.Count - 1)) != 0)
            field.RemoveAt(field.Count - 2);
        return field;
    }

    private static uint Seed(CareerState career)
    {
        uint h = 2166136261u;
        void Mix(uint v) { h ^= v; h *= 16777619u; }
        Mix((uint)career.Season);
        Mix(career.NationalTeamId);
        Mix((uint)career.NationalSince);
        foreach (char ch in career.ManagerName ?? "") Mix(ch);
        return h == 0 ? 0x9E3779B9u : h;
    }
}
