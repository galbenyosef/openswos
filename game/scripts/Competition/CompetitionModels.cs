namespace OpenSwos.Competition;

// ============================================================================
// Competition data model — the CONTRACT shared by the engine
// (CompetitionEngine.cs / CompetitionStore.cs) and the menu screens
// (game/scripts/Menu/). Plain serializable POCOs (System.Text.Json), no Godot
// types, so state round-trips to user:// as JSON.
//
// One CompetitionState describes any of: LEAGUE (round-robin), CUP (single-
// elimination), TOURNAMENT (groups -> knockout) or CAREER (a season = league +
// domestic cup interleaved, repeating across seasons). Fixtures carry a Stage
// label ("LEAGUE", "GROUP A", "CUP R1", "QF", "SF", "FINAL") so one fixture
// list serves every format.
// ============================================================================

public enum CompetitionKind { League, Cup, Tournament, Career }

// A participating team. Index/GlobalId refer to the game's master team list
// (Main._allTeams); Strength is the per-stat average skill (1..7) captured at
// creation time and drives AI result simulation.
public sealed class TeamRef
{
    public int MasterIndex { get; set; }        // index into Main's _allTeams
    public ushort GlobalId { get; set; }        // TEAM.* global id (save-stable)
    public string Name { get; set; } = "";
    public int Strength { get; set; }           // avg skill 1..7
}

public sealed class Fixture
{
    public int Round { get; set; }              // 0-based, engine-global ordering
    public string Stage { get; set; } = "";     // "LEAGUE" / "GROUP A" / "CUP R1" / "QF" / "SF" / "FINAL"
    public int HomeTeam { get; set; }           // index into CompetitionState.Teams
    public int AwayTeam { get; set; }
    public bool Played { get; set; }
    public int HomeGoals { get; set; } = -1;
    public int AwayGoals { get; set; } = -1;
    // Knockout only: level after 90' -> decided on penalties (we simulate the
    // shootout; the sim has no extra-time mode yet, and AI games never play out).
    public bool OnPenalties { get; set; }
    public int PenaltyWinner { get; set; } = -1;  // Teams index
}

public sealed class TableRow
{
    public int Team { get; set; }               // Teams index
    public int Played { get; set; }
    public int Won { get; set; }
    public int Drawn { get; set; }
    public int Lost { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int Points { get; set; }
    public int GoalDiff => GoalsFor - GoalsAgainst;
}

// Career-only wrapper data. The engine rebuilds league+cup fixtures each season.
// Manager identity is captured at career creation; Retired flags a career the
// player has ended from the menu (state kept so the history screen still works).
public sealed class CareerState
{
    public int Season { get; set; } = 1;
    public int Nation { get; set; }             // nation index of the player's club
    public int Division { get; set; }           // current division (0 = top flight)
    public string ClubName { get; set; } = "";
    public ushort ClubGlobalId { get; set; }
    public string ManagerName { get; set; } = "";
    public string ManagerTitle { get; set; } = "MR";   // "MR" / "MS"
    public bool Retired { get; set; }
    public System.Collections.Generic.List<string> Trophies { get; set; } = new();
    public System.Collections.Generic.List<string> History { get; set; } = new();  // one line per season
    /// <summary>
    /// The club's leading scorer(s) in each completed season — the original's
    /// MANAGEMENT RECORD line (asm:283007 "SEASON'S TOP SCORER" / asm:283027
    /// plural on a tie). One entry per season, appended as the season closes.
    /// </summary>
    public System.Collections.Generic.List<OpenSwos.Competition.Career.SeasonTopScorer> SeasonTopScorers { get; set; } = new();
    // Persistent evolving squads/finances/staff for the whole world (age,
    // potential, growth, fatigue...). Null on legacy saves -> rebuilt lazily.
    public OpenSwos.Competition.Career.CareerWorld? World { get; set; }

    // ---- ORIGINAL SWOS career transfer market (see Career/TransferOffers.cs) --
    // Reconstructed from swos.asm: rival clubs bid for your players (offers with
    // up to two escalations), you list players / release them free, and you must
    // negotiate purchases within a per-season time budget. All deterministic via
    // CareerRng — no System.Random.
    // Incoming bids for the player's squad (cap 5).
    public System.Collections.Generic.List<OpenSwos.Competition.Career.TransferOffer> PendingOffers { get; set; } = new();
    // Player ids the manager has transfer-listed (cap 5) — listed players attract
    // far more offers (swos.asm:78060: chance 11/256 listed vs 1/256 normal).
    public System.Collections.Generic.List<int> TransferListedPlayerIds { get; set; } = new();
    // Purchase negotiation budget, reset to 6 each season (swos.asm:127226).
    public int TimeToNegotiate { get; set; } = 6;
    // Soft quotas: the AI stops generating offers after 6 sells and refuses to
    // sanction further buys after 6 buys, per season.
    public int SellsThisSeason { get; set; }
    public int BuysThisSeason { get; set; }
    // Monotonic salt so every Tick draws a distinct deterministic RNG stream
    // (never advances the competition match/draw RNG).
    public int TransferTicks { get; set; }

    // ---- THE CHAIRMAN (career depth plan feature #2) ------------------------
    // Two parallel escalation ladders straight from the original's memo pool:
    // league position and overdraft, each 0 = quiet, 1 = note, 2 = final
    // warning, then a countdown ("3 MATCHES TO TURN THIS CLUB AROUND").
    // All defaults are the quiet state, so pre-feature saves load calmly.
    public int ChairmanWarnLeague { get; set; }
    public int ChairmanWarnMoney { get; set; }
    public int CrisisMatchesLeft { get; set; }
    public int MoneyCrisisMatchesLeft { get; set; }
    /// <summary>One vote of confidence per season, per ladder.</summary>
    public bool LeagueCrisisUsed { get; set; }
    public bool MoneyCrisisUsed { get; set; }
    /// <summary>Player matches spent in the red since the last overdraft memo.</summary>
    public int OverdraftMatches { get; set; }
    /// <summary>Currently in the drop zone — makes a standing warning live or stale.</summary>
    public bool LeagueInDanger { get; set; }
    /// <summary>Currently in the red.</summary>
    public bool InOverdraft { get; set; }
    /// <summary>Career ended by dismissal rather than retirement ('- SACKED -').</summary>
    public bool Sacked { get; set; }
    /// <summary>Last end-of-season verdict, 0 = excellent .. 4 = very disappointing; -1 = none.</summary>
    public int LastVerdict { get; set; } = -1;
    public int LastSeasonScore { get; set; }
    /// <summary>
    /// Where the board expected this squad to finish, and where it actually did.
    /// Recorded so both clients can print the sentence the verdict is built on
    /// ("EXPECTED 4TH, FINISHED 2ND") — without it the grade looks arbitrary,
    /// and a mis-ranked expectation is invisible until someone plays a season.
    /// </summary>
    public int LastExpectedPosition { get; set; }
    /// <summary>
    /// What the board expects of this squad THIS season, fixed when the season's
    /// first match is played. Frozen on purpose: judged from the end-of-season
    /// squad instead, a signing would raise the bar retroactively and a
    /// last-round fire-sale would lower it. 0 = not taken yet.
    /// </summary>
    public int SeasonExpectedPosition { get; set; }
    public int SeasonLeagueTeams { get; set; }
    public int LastLeaguePosition { get; set; }
    public int LastLeagueTeams { get; set; }
    public int ConsecutiveBadSeasons { get; set; }
    /// <summary>The chairman's memos, oldest first, capped at 12.</summary>
    public System.Collections.Generic.List<OpenSwos.Competition.Career.ChairmanMemo> Memos { get; set; } = new();
    public OpenSwos.Competition.Career.ChairmanMemo? LastMemo { get; set; }
    /// <summary>Unread count, so a client can flash the CHAIRMAN entry.</summary>
    public int UnreadMemos { get; set; }

    // ---- JOB OFFERS (career depth plan feature #3) --------------------------
    // Other clubs come for the manager, not for his players. Rules and text are
    // in Career/JobMarket.cs; only the state lives here so it round-trips
    // through the save. Defaults are the quiet state, so a career written
    // before this feature loads with nobody calling.
    public System.Collections.Generic.List<OpenSwos.Competition.Career.JobOffer> JobOffers { get; set; } = new();
    public int NextJobOfferId { get; set; } = 1;
    /// <summary>Season whose suitors have already been drawn; one draw per season.</summary>
    public int JobOffersDrawnSeason { get; set; }
    /// <summary>Manager standing, 5..100. 0 on legacy saves -> seeded on first use.</summary>
    public int Reputation { get; set; }
    /// <summary>Seasons completed at the current club; reset by a move.</summary>
    public int SeasonsAtClub { get; set; }
    /// <summary>
    /// The finished season's world roll-forward and accounts have already run.
    /// A dismissed manager who then accepts a job re-enters the rollover to take
    /// it; without this he would age the world and pay the wages twice.
    /// </summary>
    public bool SeasonBooksClosed { get; set; }

    // ---- THE NATIONAL-TEAM JOB (career depth plan feature #4) ---------------
    // Held ALONGSIDE the club job: the original's 'ANNUALLY REVIEWABLE
    // CONTRACT' is a side job, not a career change. Rules and text are in
    // Career/NationalJob.cs; only the state lives here. Defaults are "no job,
    // nobody has called", so a career written before this feature loads quietly.
    /// <summary>Federation approach waiting for an answer, or null.</summary>
    public OpenSwos.Competition.Career.NationalJobOffer? NationalOffer { get; set; }
    /// <summary>TEAM.* GlobalId of the national side coached; 0 = no job.</summary>
    public ushort NationalTeamId { get; set; }
    public int NationalMasterIndex { get; set; } = -1;
    public string NationalCountry { get; set; } = "";
    /// <summary>0 EUROPE .. 5 OCEANIA — picks the continental tournament.</summary>
    public int NationalContinent { get; set; }
    /// <summary>Player-record nationality byte for the country, or -1.</summary>
    public int NationalPlayerNationality { get; set; } = -1;
    /// <summary>Season the appointment was made.</summary>
    public int NationalSince { get; set; }
    /// <summary>The named internationals, CareerPlayer ids (max 16).</summary>
    public System.Collections.Generic.List<int> NationalSquad { get; set; } = new();
    public System.Collections.Generic.List<OpenSwos.Competition.Career.NationalSeasonResult> NationalHistory { get; set; } = new();
    public OpenSwos.Competition.Career.NationalSeasonResult? LastNationalResult { get; set; }

    // ---- SEASON FINANCES (career depth plan feature #1) ---------------------
    // The club's income/expenditure statement for the season that just ended,
    // in the ORIGINAL game's line items (Career/SeasonFinances.cs). Null until
    // the first season rollover, and on saves written before this existed.
    public OpenSwos.Competition.Career.SeasonAccount? LastAccount { get; set; }
    // Up to the last 20 statements, oldest first — the original career was 20
    // seasons, so nothing needs to look further back.
    public System.Collections.Generic.List<OpenSwos.Competition.Career.SeasonAccount> AccountHistory { get; set; } = new();

    // ---- SEASON CHRONICLE (career depth plan feature #7) --------------------
    // One line per event, read-only, written by the engine. Deliberately NOT an
    // inbox: it is read when the player feels like it and never demands
    // attention (see 03-career-depth-plan.md, "What we deliberately do NOT
    // build"). Bounded so a 20-season career cannot grow the save without end.
    public System.Collections.Generic.List<OpenSwos.Competition.Career.ChronicleEntry> Chronicle { get; set; } = new();

    // ---- YOUTH INTAKE DAY (career depth plan feature #6) --------------------
    // The academy already produced players every season (RegenModel); what was
    // missing was the MOMENT. These are the ids the academy handed the manager
    // at the last rollover, plus the season they arrived, so the intake screen
    // can be opened once and then stays readable all season.
    public System.Collections.Generic.List<int> YouthIntakeIds { get; set; } = new();
    public int YouthIntakeSeason { get; set; }
    public bool YouthIntakeSeen { get; set; }

    // ---- TRAINING (user directive 2026-08-26) -------------------------------
    // A weekly session between fixtures: pick a drill, an intensity and the
    // players who do it. Rules in Career/TrainingModel.cs; only the state lives
    // here. Every default is the quiet one, so a career written before training
    // existed loads with nothing scheduled and nothing claimed.
    public int TrainingDrill { get; set; }              // index into TrainingModel.Drills
    public int TrainingIntensity { get; set; } = 1;     // 0 LIGHT, 1 NORMAL, 2 INTENSE
    public System.Collections.Generic.List<int> TrainingGroup { get; set; } = new();
    /// <summary>Round of the last session run; -1 = none yet this career.</summary>
    public int TrainingLastRound { get; set; } = -1;
    public int TrainingLastSeason { get; set; } = -1;
    /// <summary>Sessions run across the whole career (a management-record line).</summary>
    public int TrainingSessionsRun { get; set; }
    /// <summary>The last session's per-player report, for both clients to print.</summary>
    public System.Collections.Generic.List<OpenSwos.Competition.Career.TrainingResultRow> TrainingReport { get; set; } = new();
}

public sealed class CompetitionState
{
    public int FormatVersion { get; set; } = 1;
    public CompetitionKind Kind { get; set; }
    public string Name { get; set; } = "";
    public System.Collections.Generic.List<TeamRef> Teams { get; set; } = new();
    public int PlayerTeam { get; set; } = -1;   // Teams index the human controls
    public System.Collections.Generic.List<Fixture> Fixtures { get; set; } = new();
    public int CurrentRound { get; set; }       // rounds < this are fully played
    public int TotalRounds { get; set; }
    public bool DoubleRoundRobin { get; set; }
    // Tournament: group id per Teams index (-1 = none/knockout only), group count.
    public System.Collections.Generic.List<int> GroupOf { get; set; } = new();
    public int GroupCount { get; set; }
    // Deterministic RNG for draws + AI results (xorshift32 state persisted).
    public uint RngState { get; set; }
    public bool Finished { get; set; }
    public int Champion { get; set; } = -1;     // Teams index once Finished
    public CareerState? Career { get; set; }

    // ---- SEASON'S TOP SCORER (career depth plan feature #5) ---------------
    // Every goal in the CURRENT season, attributed to a player (Career/
    // ScorerModel.cs). Rebuilt from empty each career season, so the list is
    // "this season", exactly like the original's competition scorer list; the
    // per-player running total lives on CareerPlayer.CareerGoals instead.
    // Empty on a pre-feature save, which reads as "no goals recorded yet".
    public System.Collections.Generic.List<OpenSwos.Competition.Career.ScorerRow> Scorers { get; set; } = new();
}

// ============================================================================
// Engine API contract (implemented in CompetitionEngine.cs):
//
//   CreateLeague(name, teams, playerTeam, doubleRR, seed) -> CompetitionState
//   CreateCup(name, teams, playerTeam, seed)              -> CompetitionState
//       (teams.Count must be a power of two: 4/8/16/32)
//   CreateTournament(name, teams, playerTeam, groupCount, seed) -> CompetitionState
//       (groupCount groups of 4, top 2 advance to knockout)
//   CreateCareer(name, leagueTeams, cupTeams, playerTeam, nation, division, seed)
//       -> CompetitionState (league double-RR + cup rounds interleaved)
//
//   NextPlayerFixture(state)  -> Fixture?   (next unplayed fixture involving
//                                            PlayerTeam, or null)
//   NextFixture(state)        -> Fixture?   (next unplayed fixture of any team —
//                                            used to fast-forward AI rounds)
//   RecordResult(state, fixture, homeGoals, awayGoals)
//       — writes the score, updates knockout progression when a round
//         completes (draws in knockout resolve via simulated penalties),
//         advances CurrentRound, sets Finished/Champion, and for Career rolls
//         the season over (promotion/relegation + trophies) when both league
//         and cup have concluded.
//   SimulateAiRound(state)    — plays every unplayed AI-vs-AI fixture of the
//                               current round using SimulateResult.
//   SimulateResult(state, fixture) -> (int home, int away)
//       — strength-weighted random score (deterministic via RngState).
//   Table(state, stagePrefix) -> List<TableRow>
//       — standings over fixtures whose Stage starts with stagePrefix
//         ("LEAGUE", "GROUP A"), sorted: Pts, GD, GF, name.
//   RoundLabel(state)         -> string      ("ROUND 3/14", "QUARTER FINAL"...)
//   IsPlayerAlive(state)      -> bool        (career/cup: player still has
//                                            fixtures to play)
//
// Store API contract (implemented in CompetitionStore.cs):
//   Save(state)  / Load() -> CompetitionState? / Delete() / Exists() -> bool
//   — the AUTOSAVE slot at user://competition.json
//     (Godot ProjectSettings.GlobalizePath("user://competition.json")).
//   SaveAs(state, slot) / LoadSlot(slot) -> CompetitionState? / DeleteSlot(slot)
//   ListSlots() -> List<(slot, label)>
//   — named slots at user://saves/<SLOT>.json; slot "AUTOSAVE" aliases the
//     legacy single-slot API above and always lists first.
// ============================================================================
