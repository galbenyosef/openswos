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
