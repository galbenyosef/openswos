namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;
using GCE = OpenSwos.Sim.Port.InputControls.GameControlEvents;

// Substitution / bench logic. Mechanical port of
//   external/swos-port/src/game/bench/bench.cpp        (entire file, 144 LOC)
//   external/swos-port/src/game/bench/updateBench.cpp  (entire FSM, 980 LOC)
// plus the substituted-player walk-off/walk-in state machine that the original
// keeps inside the per-player loop of updatePlayers
// (external/swos-port/src/game/updatePlayers/updatePlayers.cpp:9051-9197) —
// hosted HERE because UpdatePlayers.cs carries it as an explicit TODO and is
// owned by another port session (see UpdateSubstitutedPlayerWalk below).
//
// FLOW (how the bench opens and a substitution happens in the original):
//   1. Game loop calls updateBench() every tick (gameLoop.cpp:315).
//   2. While NOT in bench: benchCheckControls (updateBench.cpp:169-192)
//      - benchBlocked (updateBench.cpp:639): ticks down
//        g_waitForPlayerToGoInTimer / g_cameraLeavingSubsTimer, blocks while a
//        substitute walk is in progress / referee active / stats panel up.
//      - benchUnavailable (updateBench.cpp:652): the bench can be summoned only
//        while the game is STOPPED — gameStatePl != kInProgress(100), no card
//        being handed, not during penalties, gameState outside 21..30
//        (starting-game .. game-ended ceremonies).
//      - benchInvoked (updateBench.cpp:762): the HUMAN summons the bench with
//        the secondary fire button (kGameEventBench) or by tapping any one
//        direction three times quickly (kNumTapsForBench).
//      - on invoke: initBenchVars + invokeBench() → g_inSubstitutesMenu = 1.
//        The break-restart ladder (gameLoop.cpp:1484-1489, our GameLoop.Mode4)
//        gates on that flag, so the restart ceremony WAITS while the bench is
//        open.
//   3. While in bench: menu FSM (BenchState) —
//        kInitial          arrow over [coach row + 5 substitutes]
//        kAboutToSubstitute pick the on-pitch player to take off; fire =
//                           initiateSubstitution → old player walks to the
//                           bench (g_substituteInProgress 1 → 2), then
//                           substitutePlayer() swaps the PlayerInfo records +
//                           shirt numbers, teleports the sprite to the entry
//                           point and it walks back in (g_substituteInProgress
//                           -1 → 0). g_waitForPlayerToGoInTimer = 100 blocks
//                           re-entry and the camera keeps bench mode.
//        kFormationMenu    change tactics (changeTactics writes team.tactics)
//        kMarkingPlayers   swap two on-pitch players / mark an opponent
//      LEFT or RIGHT (leavingBenchMotion) leaves the bench: setBenchOff() +
//      switchCameraToLeavingBenchMode + g_cameraLeavingSubsTimer = 55.
//
// Substitution limit — SWOS 96/97 friendly defaults: 2 substitutions from a
// bench of 5 (swos.asm:224560 `minSubstitutes dw 2`, swos.asm:224562
// `maxSubstitutes dw 5`, wired through initializeIngameTeams —
// external/swos-port/src/game/game.cpp:108-111). isPlayerOkToSelect
// (updateBench.cpp:890-905) stops offering subs once team{1,2}NumSubs reaches
// gameMinSubstitutes.
//
// Sources:
//   - external/swos-port/src/game/bench/bench.cpp:9-143
//   - external/swos-port/src/game/bench/updateBench.cpp:12-922
//   - external/swos-port/src/game/bench/updateBench.h:3-10
//   - external/swos-port/src/game/updatePlayers/updatePlayers.cpp:9051-9197
//   - external/swos-port/src/sprites/gameSprites.cpp:113-134
//   - external/swos-port/src/game/game.cpp:1218-1235 (checkIfGoalkeeperClaimedTheBall)
//   - external/swos-port/src/game/team.cpp:26-55      (stopAllPlayers)
//   - external/swos-port/swos/swos.asm:207972-208017  (positionsTable data)
//   - external/swos-port/swos/swos.asm:203792         (kSubstitutedPlayerSpeed dw 1536)
//   - external/swos-port/swos/swos.asm:224560-224562  (min/maxSubstitutes defaults)
public static class Bench
{
    // bench.cpp:9 — `constexpr FixedPoint kBenchX = 27` (also mirrored in
    // Camera.cs as a private const so the camera mode can reach it without
    // depending on this file; declared here for completeness + cross-ref).
    public const int kBenchX = 27;

    // bench.cpp:11-12 — bench Y bands. Top team's bench at y=389; bottom
    // team's bench at y=485 (whole-pixel pitch coordinates).
    public const int kTopBenchY    = 389;
    public const int kBottomBenchY = 485;

    // bench.cpp:14 — training-game uses a single bench location (no
    // top/bottom split because there's only one team on the pitch).
    public const int kTrainingPitchBenchY = 456;

    // bench.cpp:16-17 — the (x, y) point on the pitch where a substituted-in
    // player walks ON from. kPlayerGoingInY = kPitchCenterY = 449
    // (pitchConstants.h:4).
    public const int kPlayerGoingInX = 26;
    public const int kPlayerGoingInY = 449;

    // updateBench.cpp:12-21 — FSM pacing constants.
    public const int kEnterBenchDelay     = 15;   // ticks before the menu accepts input
    public const int kPlayerGoingInDelay  = 100;  // g_waitForPlayerToGoInTimer load
    public const int kNumTapsForBench     = 2;    // tap-counter threshold (≥ → invoke)
    public const int kTapTimeoutTicks     = 15;   // ticks before the tap chain resets
    public const int kLeavingSubsDelay    = 55;   // g_cameraLeavingSubsTimer load
    public const int kSubstituteFireTicks = 8;    // held-fire ticks to mark a player
    public const int kMaxSubstitutes      = 5;    // bench rows (updateBench.cpp:21)

    // bench.h:3 — `constexpr int kNumFormationEntries = 18` (12 built-ins +
    // USER_A..F; the 19th g_tacticsTable slot is the tactics editor scratch).
    public const int kNumFormationEntries = 18;

    // updateBench.cpp:548-549 — initiateSubstitution's touchline point the
    // outgoing player walks to first (kSubstitutedPlayerY = kPitchCenterY).
    public const int kSubstitutedPlayerX = 39;
    public const int kSubstitutedPlayerY = 449;

    // swos.asm:203792 — `kSubstitutedPlayerSpeed dw 1536` (Q8.8 = 6 px/tick).
    // Read by the walk FSM at updatePlayers.cpp:9147/9160/9178.
    public const int kSubstitutedPlayerSpeed = 1536;

    // Substitution allowance — SWOS 96/97 friendly defaults. The original
    // carries these in swos.gameMinSubstitutes / swos.gameMaxSubstitutes,
    // seeded from the friendly-setup menu (swos.asm:224560 `minSubstitutes
    // dw 2`, swos.asm:224562 `maxSubstitutes dw 5`) via initializeIngameTeams
    // (game.cpp:108-111). Memory.cs is owned by another session, so the pair
    // lives here as host-visible statics until slots are allocated.
    // NOTE the counter-intuitive original naming: MIN = how many substitutions
    // may be made; MAX = how many players sit on the bench.
    public static int GameMinSubstitutes = 2;
    public static int GameMaxSubstitutes = 5;

    // ====================================================================
    // positionsTable — swos.asm:207972-208017
    // ====================================================================
    // "every team position ordered, for every tactic". Maps a bench-menu row
    // ordinal (0..10, goalkeeper first) to the 1-based players[] index for
    // the team's current tactics. Row order matches g_tacticsTable
    // (swos.asm:209342 / TacticsLoader.cs:16-31). Slots 12..17 (USER_A..F)
    // fall back to defaultPositions exactly like the original data.
    private static readonly byte[][] kPositionsTable = new byte[][]
    {
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 0  default (4-4-2)
        new byte[] { 1, 2, 3, 4, 7, 5, 6, 8, 10, 9, 11 },   // 1  5-4-1
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 10, 9, 11 },   // 2  4-5-1
        new byte[] { 1, 2, 3, 4, 7, 5, 6, 8, 9, 10, 11 },   // 3  5-3-2
        new byte[] { 1, 2, 3, 5, 6, 4, 7, 8, 9, 10, 11 },   // 4  3-5-2
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 9, 8, 10, 11 },   // 5  4-3-3
        new byte[] { 1, 2, 3, 4, 5, 7, 8, 6, 10, 11, 9 },   // 6  4-2-4
        new byte[] { 1, 2, 3, 5, 6, 4, 7, 9, 8, 10, 11 },   // 7  3-4-3
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 8  sweep
        new byte[] { 1, 2, 3, 4, 7, 5, 8, 10, 6, 11, 9 },   // 9  5-2-3
        new byte[] { 1, 2, 3, 5, 4, 7, 6, 8, 10, 11, 9 },   // 10 attack
        new byte[] { 1, 6, 2, 3, 4, 5, 9, 7, 8, 10, 11 },   // 11 defend
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 12 USER_A → defaultPositions
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 13 USER_B
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 14 USER_C
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 15 USER_D
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 16 USER_E
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },   // 17 USER_F
    };

    // ====================================================================
    // Module state — updateBench.cpp:23-80 file-statics.
    // ====================================================================
    // These are file-static (NOT swos.* globals) in the original, so they
    // live as C# statics here; everything a concurrently-ported module reads
    // (g_inSubstitutesMenu, m_benchState, g_substituteInProgress,
    // g_waitForPlayerToGoInTimer, g_cameraLeavingSubsTimer, m_benchY, ...)
    // stays in Memory.Addr slots. All statics are reset by InitBenchControls
    // (match start) exactly like updateBench.cpp:118-166.

    // updateBench.cpp:23-45 — struct TapCounterState (the "tap a direction
    // three times to call the bench" detector).
    private struct TapCounterState
    {
        public int TapCount;
        public int TapTimeoutCounter;
        public int PreviousDirection;             // GameControlEvents movement bits
        public bool BlockWhileHoldingDirection;

        public void Init()   { Reset(); BlockWhileHoldingDirection = false; }
        public void Reset()
        {
            // updateBench.cpp:33-38 — "don't touch block field".
            TapCount = 0;
            TapTimeoutCounter = 0;
            PreviousDirection = 0;
        }
        public bool GotLastTapDirection()
            => (PreviousDirection & (int)InputControls.kGameEventMovementMask) != 0;
        public bool HoldingSameDirectionAsLastTap(int controls)
            => PreviousDirection == (controls & (int)InputControls.kGameEventMovementMask);
    }

    private static TapCounterState s_pl1TapState;
    private static TapCounterState s_pl2TapState;
    private static int  s_controls;               // m_controls — GameControlEvents bits

    // Port-only diagnostic for the --bench-test harness: snapshot of the two
    // tap-counter states + the last m_controls sample. No original equivalent
    // (the original exposes the same fields through fillBenchData under
    // SWOS_TEST, updateBench.cpp:943-979).
    public static string DebugTapStateString()
        => $"pl1(tap={s_pl1TapState.TapCount},prev={s_pl1TapState.PreviousDirection}," +
           $"block={(s_pl1TapState.BlockWhileHoldingDirection ? 1 : 0)},to={s_pl1TapState.TapTimeoutCounter}) " +
           $"pl2(tap={s_pl2TapState.TapCount},prev={s_pl2TapState.PreviousDirection}," +
           $"block={(s_pl2TapState.BlockWhileHoldingDirection ? 1 : 0)},to={s_pl2TapState.TapTimeoutCounter}) " +
           $"controls={s_controls} lastPoll(gspl={DebugLastPollGameStatePl},blocked={(DebugLastPollBlocked ? 1 : 0)}," +
           $"unavail={(DebugLastPollUnavailable ? 1 : 0)})";

    // Port-only diagnostics: state of the gates the last time the
    // out-of-bench poll ran (used by --bench-test to prove the availability
    // gate is consulted with the value it sees, not the one the harness set).
    public static short DebugLastPollGameStatePl  { get; private set; }
    public static bool  DebugLastPollBlocked      { get; private set; }
    public static bool  DebugLastPollUnavailable  { get; private set; }

    private static int  s_teamBase;               // m_team     — TeamGeneralInfo* (TeamData base addr)
    private static int  s_teamGameBase;           // m_teamGame — TeamGame players[0] addr
    private static int  s_teamNumber;             // m_teamNumber

    // m_state lives in Memory.Addr.m_benchState (see GetBenchState) so other
    // modules (SpinningLogo, drawBench-side UI) can read it.
    private static int  s_goToBenchTimer;         // m_goToBenchTimer

    private static bool s_bench1Called;           // m_bench1Called
    private static bool s_bench2Called;           // m_bench2Called

    private static bool s_blockDirections;        // m_blockDirections
    private static int  s_fireTimer;              // m_fireTimer
    private static bool s_blockFire;              // m_blockFire

    private static int  s_lastDirection;          // m_lastDirection (movement bits)
    private static int  s_movementDelayTimer;     // m_movementDelayTimer

    private static bool s_trainingTopTeam;        // m_trainingTopTeam
    private static bool s_teamsSwapped;           // m_teamsSwapped
    private static int  s_alternateTeamsTimer;    // m_alternateTeamsTimer

    private static int  s_arrowPlayerIndex;       // m_arrowPlayerIndex (0 = coach row, 1..5 = subs)
    private static int  s_selectedMenuPlayerIndex;// m_selectedMenuPlayerIndex
    private static int  s_playerToEnterGameIndex; // m_playerToEnterGameIndex (11..15)

    private static int  s_playerToBeSubstitutedPos; // m_playerToBeSubstitutedPos (players[] index 0..10)
    private static int  s_playerToBeSubstitutedOrd; // m_playerToBeSubstitutedOrd (bench-order 0..10)

    // updateBench.cpp:78 — m_shirtNumberTable[2][kNumPlayersInTeam(16)].
    private static readonly byte[,] s_shirtNumberTable = new byte[2, 16];

    private static int  s_selectedFormationEntry; // m_selectedFormationEntry

    // Walk-FSM globals that have no Memory.Addr slot yet (Memory.cs is owned
    // by another session). Originals: substitutedPlDestX/Y @ g_memByte
    // 523136/523138, plSubstitutedX/Y @ g_memByte 486152/486154.
    private static short s_substitutedPlDestX;
    private static short s_substitutedPlDestY;
    private static short s_plSubstitutedX;
    private static short s_plSubstitutedY;

    // ====================================================================
    // public state queries — bench.cpp:42-65 + updateBench.cpp:194-294
    // ====================================================================

    // bench.cpp:42-45 — `inBench()` returns true while the substitutes menu
    // is being shown. Mirrors the original `swos.g_inSubstitutesMenu != 0`.
    public static bool InBench()
        => Memory.ReadSignedWord(Memory.Addr.g_inSubstitutesMenu) != 0;

    // updateBench.h:3-10 — `enum class BenchState`.
    public const int kBenchStateInitial           = 0;
    public const int kBenchStateAboutToSubstitute = 1;
    public const int kBenchStateFormationMenu     = 2;
    public const int kBenchStateMarkingPlayers    = 3;
    public const int kBenchStateOpponentsBench    = 4;

    // updateBench.cpp:194-197 — `BenchState getBenchState() { return m_state; }`.
    public static int GetBenchState()
        => Memory.ReadSignedWord(Memory.Addr.m_benchState);

    private static void SetBenchState(int state)
        => Memory.WriteWord(Memory.Addr.m_benchState, state);

    // bench.cpp:47-50 — `inBenchMenus()`. The "menu is visible" predicate that
    // the spinning-logo / drawBench / camera-bench-mode all gate on.
    public static bool InBenchMenus()
        => InBench() && GetBenchState() == kBenchStateInitial;

    // bench.cpp:52-55 — current team's bench Y.
    public static int GetBenchY()
        => Memory.ReadSignedWord(Memory.Addr.m_benchY);

    // bench.cpp:57-60 — opponent team's bench Y.
    public static int GetOpponentBenchY()
        => Memory.ReadSignedWord(Memory.Addr.m_opponentBenchY);

    // updateBench.cpp:199-207 — trainingTopTeam / setTrainingTopTeam.
    public static bool TrainingTopTeam() => s_trainingTopTeam;
    public static void SetTrainingTopTeam(bool value) { s_trainingTopTeam = value; }

    // updateBench.cpp:209-217 — requestBench1 / requestBench2. Programmatic
    // bench summon (the original wires these to the pause menu). The request
    // is consumed by benchCheckControls' out-of-bench branch on the next tick
    // that passes benchBlocked/benchUnavailable.
    public static void RequestBench1() { s_bench1Called = true; }
    public static void RequestBench2() { s_bench2Called = true; }

    // updateBench.cpp:219-263 — menu-state getters (consumed by drawBench in
    // the original; by Main.cs's PORT-VISUAL text panel here).
    public static int GetBenchPlayerIndex()        => s_arrowPlayerIndex;
    public static int GetBenchMenuSelectedPlayer() => s_selectedMenuPlayerIndex;
    public static int GetSelectedFormationEntry()  => s_selectedFormationEntry;
    public static int PlayerToEnterGameIndex()     => s_playerToEnterGameIndex;
    public static int PlayerToBeSubstitutedIndex() => s_playerToBeSubstitutedOrd;
    public static int PlayerToBeSubstitutedPos()   => s_playerToBeSubstitutedPos;

    // updateBench.cpp:224-228 — getBenchPlayerShirtNumber.
    public static int GetBenchPlayerShirtNumber(bool topTeam, int index)
        => s_shirtNumberTable[topTeam ? 1 : 0, index];

    // updateBench.cpp:240-249 — inBenchOrGoingTo / goingToBenchDelay.
    public static bool InBenchOrGoingTo()
        => s_goToBenchTimer == 0 && InBench();
    public static bool GoingToBenchDelay()
        => s_goToBenchTimer != 0;

    // updateBench.cpp:266-279 — substitute-in-progress trio, backed by the
    // Memory.Addr.g_substituteInProgress word (signed: 1 walk to touchline,
    // 2 walk to bench, -1 new player walking in, 0 idle).
    public static bool SubstituteInProgress()
        => Memory.ReadSignedWord(Memory.Addr.g_substituteInProgress) != 0;
    public static bool NewPlayerAboutToGoIn()
        => Memory.ReadSignedWord(Memory.Addr.g_substituteInProgress) < 0;
    public static void SetSubstituteInProgress()
        => Memory.WriteWord(Memory.Addr.g_substituteInProgress, 1);

    // updateBench.cpp:281-289 — getBenchTeam. Re-syncs m_team when the
    // team-number fields swapped under us (half-time side swap).
    public static int GetBenchTeamBase()
    {
        if (Memory.ReadSignedWord(Memory.Addr.g_trainingGame) == 0)
        {
            short actualNumber = Memory.ReadSignedWord(s_teamBase + TeamData.OffTeamNumber);
            if (s_teamNumber != actualNumber)
            {
                s_teamBase = s_teamNumber == 1 ? TeamData.BottomBase : TeamData.TopBase;
                s_teamNumber = Memory.ReadSignedWord(s_teamBase + TeamData.OffTeamNumber);
            }
        }
        return s_teamBase;
    }

    // updateBench.cpp:291-294 — getBenchTeamData (TeamGame players[0] addr).
    public static int GetBenchTeamGameBase() => s_teamGameBase;

    // Convenience for the host UI: is the bench team the top team?
    public static bool BenchTeamIsTop() => s_teamBase == TeamData.TopBase;

    // bench.cpp:78-81 — getBenchPlayer: PlayerInfo of the index-th row of the
    // "select player to take off" list. Returns the record's absolute address.
    public static int GetBenchPlayerInfoAddr(int index)
        => s_teamGameBase + GetBenchPlayerPosition(index) * TeamDataLoader.PlayerInfoSize;

    // bench.cpp:83-94 — getBenchPlayerPosition. Maps a menu ordinal (0..10)
    // to the players[] index via positionsTable[tactics].
    // PORT-DIVERGENCE (documented): the original reads the pl1Tactics /
    // pl2Tactics globals; those have no Memory slot, and changeTactics keeps
    // team.tactics (TeamData +28) in lockstep with them (updateBench.cpp:
    // 597-599), so team.tactics is the single source of truth here.
    public static int GetBenchPlayerPosition(int index)
    {
        short tactics = Memory.ReadSignedWord(GetBenchTeamBase() + TeamData.OffTactics);
        if (tactics < 0 || tactics >= kPositionsTable.Length)
            tactics = 0;   // USER slots / corrupt data → defaultPositions (asm fallback rows)
        return kPositionsTable[tactics][index] - 1;
    }

    // ====================================================================
    // initBenchBeforeMatch — bench.cpp:26-33
    // ====================================================================
    // Called once at match start (initGameLoop, gameLoop.cpp:215).
    public static void InitBenchBeforeMatch()
    {
        Memory.WriteWord(Memory.Addr.g_inSubstitutesMenu, 0);
        Memory.WriteWord(Memory.Addr.g_cameraLeavingSubsTimer, 0);
        InitBenchControls();
        // initBenchMenusBeforeMatch (drawBench.cpp) — render-side palette /
        // sprite-frame init. Godot owns rendering; skipped.
        InitBench();
    }

    // ====================================================================
    // initBenchControls — updateBench.cpp:118-166
    // ====================================================================
    public static void InitBenchControls()
    {
        s_teamsSwapped = false;
        s_trainingTopTeam = false;

        s_alternateTeamsTimer = 0;
        s_teamNumber = 0;

        s_pl1TapState.Init();
        s_pl2TapState.Init();

        s_goToBenchTimer = 0;

        s_blockDirections = false;
        s_blockFire = false;
        s_fireTimer = 0;

        s_lastDirection = 0;
        s_movementDelayTimer = 0;

        s_controls = 0;

        SetBenchState(kBenchStateInitial);

        s_arrowPlayerIndex = 0;
        s_playerToEnterGameIndex = 0;
        s_selectedMenuPlayerIndex = 0;

        s_playerToBeSubstitutedPos = 0;
        s_playerToBeSubstitutedOrd = 0;

        s_selectedFormationEntry = 0;

        s_bench1Called = false;
        s_bench2Called = false;

        for (int i = 0; i < 16; i++)
        {
            s_shirtNumberTable[0, i] = (byte)i;
            s_shirtNumberTable[1, i] = (byte)i;
        }

        // updateBench.cpp:159-165 — bench defaults to the team playing up.
        if (Memory.ReadSignedWord(Memory.Addr.teamPlayingUp) == 1)
        {
            s_teamBase = TeamData.TopBase;
            s_teamGameBase = Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
        }
        else
        {
            s_teamBase = TeamData.BottomBase;
            s_teamGameBase = Memory.ReadSignedDword(Memory.Addr.bottomTeamInGame);
        }

        // Walk-FSM globals (no Memory slot — see declaration). The original
        // zeroes their behaviour via g_substituteInProgress = 0 in
        // initGameVariables (game.cpp:1424); mirror the quiescent state here.
        s_substitutedPlDestX = 0;
        s_substitutedPlDestY = 0;
        s_plSubstitutedX = 0;
        s_plSubstitutedY = 0;
    }

    // ====================================================================
    // initBench — bench.cpp:96-118 (static)
    // ====================================================================
    // Runs on every bench invoke AND once at match start. Side effects:
    //   - Clears stateGoal (force-end any active goal cinema).
    //   - Negates resultTimer / statsTimer (snaps the panels off-screen).
    //   - Picks bench Y for the substituting team (training vs not, plus the
    //     bit-2-of-teamName-second-char gate that flips top/bottom in ~50%
    //     of matches — "nice criteria :P", bench.cpp:109).
    //   - Seeds plComingX / plComingY for the substitute-walking sprite.
    private static void InitBench()
    {
        // bench.cpp:98 — swos.stateGoal = 0.
        Memory.WriteWord(Memory.Addr.stateGoal, 0);

        // bench.cpp:99-100 — negate resultTimer + statsTimer.
        int resultTimer = Memory.ReadSignedDword(Memory.Addr.resultTimer);
        Memory.WriteDword(Memory.Addr.resultTimer, -resultTimer);
        short statsTimer = Memory.ReadSignedWord(Memory.Addr.statsTimer);
        Memory.WriteWord(Memory.Addr.statsTimer, -statsTimer);

        // bench.cpp:102-104.
        int benchY    = kTopBenchY;
        int oppBenchY = kBottomBenchY;
        bool topTeam = s_teamGameBase != 0
            && s_teamGameBase == Memory.ReadSignedDword(Memory.Addr.topTeamInGame);

        if (Memory.ReadSignedWord(Memory.Addr.g_trainingGame) != 0)
        {
            // bench.cpp:107 — training game: both benches at training Y.
            benchY = kTrainingPitchBenchY;
            oppBenchY = kTrainingPitchBenchY;
        }
        else
        {
            // bench.cpp:110-111 — bit 2 of the 2nd character of the top team
            // name. topTeamInGame points at players[0]; the TeamGame header
            // (teamName at +22) sits 42 bytes before → name = base - 20.
            int topInGame = Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
            if (topInGame != 0)
            {
                byte ch1 = Memory.ReadByte(topInGame - 20 + 1);
                if ((ch1 & 2) != 0)
                {
                    int t = benchY; benchY = oppBenchY; oppBenchY = t;
                }
            }

            // bench.cpp:112-113 — if topTeam, swap again.
            if (topTeam)
            {
                int t = benchY; benchY = oppBenchY; oppBenchY = t;
            }
        }

        Memory.WriteWord(Memory.Addr.m_benchY, benchY);
        Memory.WriteWord(Memory.Addr.m_opponentBenchY, oppBenchY);

        // bench.cpp:116-117 — plComingX/Y seeded for the new sub to walk in.
        Memory.WriteWord(Memory.Addr.plComingX, kPlayerGoingInX);
        Memory.WriteWord(Memory.Addr.plComingY, kPlayerGoingInY);
    }

    // ====================================================================
    // invokeBench — bench.cpp:121-127 (static)
    // ====================================================================
    // Called by updateBench() when benchCheckControls returned true. Re-runs
    // initBench, raises g_inSubstitutesMenu, then handles throw-in + keeper-
    // ball cleanup so the substitution scene doesn't start mid-action.
    public static void InvokeBench()
    {
        InitBench();
        Memory.WriteWord(Memory.Addr.g_inSubstitutesMenu, 1);
        CheckForThrowInAndKeepersBall();
    }

    // ====================================================================
    // setBenchOff — bench.cpp:67-71
    // ====================================================================
    public static void SetBenchOff()
    {
        CheckIfGoalkeeperClaimedTheBall();
        Memory.WriteWord(Memory.Addr.g_inSubstitutesMenu, 0);
    }

    // ====================================================================
    // swapBenchWithOpponent — bench.cpp:62-65
    // ====================================================================
    public static void SwapBenchWithOpponent()
    {
        int b = Memory.ReadSignedWord(Memory.Addr.m_benchY);
        int o = Memory.ReadSignedWord(Memory.Addr.m_opponentBenchY);
        Memory.WriteWord(Memory.Addr.m_benchY, o);
        Memory.WriteWord(Memory.Addr.m_opponentBenchY, b);
    }

    // ====================================================================
    // updateBench — bench.cpp:36-40 (main entry, gameLoop.cpp:315)
    // ====================================================================
    public static void UpdateBench()
    {
        // PORT-DIVERGENCE (documented, 2026-07-02): the substituted-player
        // walk FSM lives inside updatePlayers' per-player loop in the
        // original (updatePlayers.cpp:9051-9197 l_check_for_substituted_
        // player) and runs BEFORE updateBench in the tick (gameLoop.cpp:301
        // vs :315). UpdatePlayers.cs carries that label as a TODO and is
        // owned by a concurrent port session, so the FSM is hosted here and
        // stepped first to preserve the original walk-then-menu order.
        UpdateSubstitutedPlayerWalk();

        if (BenchCheckControls())
            InvokeBench();
    }

    // ====================================================================
    // benchCheckControls — updateBench.cpp:169-192
    // ====================================================================
    // Returns true if the bench needs to be invoked, false if it's already
    // showing (or can't be invoked this tick).
    public static bool BenchCheckControls()
    {
        if (InBench())
        {
            UpdateBenchControls();
            if (!BumpGoToBenchTimer())
            {
                if (NewPlayerAboutToGoIn())
                    SubstitutePlayer();
                else if (!FilterControls())
                    HandleMenuControls();
            }
        }
        else
        {
            // (debug capture only — no behavioral effect)
            DebugLastPollGameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            bool blocked = BenchBlocked();
            DebugLastPollBlocked = blocked;
            if (!blocked)
            {
                bool unavailable = BenchUnavailable();
                DebugLastPollUnavailable = unavailable;
                if (!unavailable)
                {
                    int teamBase = GetNonBenchControlsTeam();

                    bool benchCalled = s_bench1Called || s_bench2Called;
                    if (benchCalled || (UpdateNonBenchControls(teamBase) && BenchInvoked(teamBase)))
                    {
                        InitBenchVars(teamBase);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // ====================================================================
    // handleMenuControls — updateBench.cpp:296-315
    // ====================================================================
    private static void HandleMenuControls()
    {
        if (SubstituteInProgress())
            return;

        switch (GetBenchState())
        {
            case kBenchStateInitial:
                HandleBenchArrowSelection();
                break;
            case kBenchStateAboutToSubstitute:
                SelectPlayerToSubstituteMenuHandler();
                break;
            case kBenchStateFormationMenu:
                HandleFormationMenuControls();
                break;
            case kBenchStateMarkingPlayers:
                MarkPlayersMenuHandler();
                break;
        }
    }

    // ====================================================================
    // initBenchVars — updateBench.cpp:317-335
    // ====================================================================
    private static void InitBenchVars(int teamBase)
    {
        s_bench1Called = false;
        s_bench2Called = false;

        s_teamBase = teamBase;
        // updateBench.cpp:323 — `m_teamGame = team->teamNumber == 2 ?
        // swos.bottomTeamPtr : swos.topTeamPtr`. PORT-DIVERGENCE (documented):
        // top/bottomTeamPtr (Memory.Addr.topTeamPtr) are only re-pointed by
        // the result-screen scorer flip which our port doesn't perform — the
        // canonical top/bottomTeamInGame pointers reference the same TeamGame
        // records, so we read those.
        short teamNumber = Memory.ReadSignedWord(teamBase + TeamData.OffTeamNumber);
        s_teamGameBase = Memory.ReadSignedDword(teamNumber == 2
            ? Memory.Addr.bottomTeamInGame
            : Memory.Addr.topTeamInGame);
        s_trainingTopTeam = s_teamGameBase != Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
        s_teamNumber = teamNumber;

        s_playerToBeSubstitutedPos = -1;
        s_arrowPlayerIndex = 0;

        SetBenchState(kBenchStateInitial);
        s_goToBenchTimer = kEnterBenchDelay;

        s_blockFire = true;
        s_blockDirections = true;
    }

    // ====================================================================
    // handleBenchArrowSelection — updateBench.cpp:337-372
    // ====================================================================
    private static void HandleBenchArrowSelection()
    {
        bool benchRecalled = s_teamBase == TeamData.TopBase ? s_bench1Called : s_bench2Called;
        int movementFlags = s_controls & (int)InputControls.kGameEventMovementMask;

        if (benchRecalled || LeavingBenchMotion())
        {
            LeaveBench();
        }
        else if (movementFlags == (int)GCE.kGameEventUp || movementFlags == (int)GCE.kGameEventDown)
        {
            bool increaseIndex = true;
            bool allowIfKeeperHolds = false;

            if (s_controls == (int)GCE.kGameEventUp)
            {
                increaseIndex = false;
                if (Memory.ReadSignedWord(Memory.Addr.g_trainingGame) != 0)
                {
                    if (s_trainingTopTeam)
                        increaseIndex = true;
                    else
                        allowIfKeeperHolds = true;
                }
            }
            else if (Memory.ReadSignedWord(Memory.Addr.g_trainingGame) != 0)
            {
                if (s_trainingTopTeam)
                {
                    increaseIndex = false;
                    allowIfKeeperHolds = true;
                }
            }

            // updateBench.cpp:363-364 — GameState::kKeeperHoldsTheBall == 3.
            if (!allowIfKeeperHolds
                && Memory.ReadSignedWord(Memory.Addr.gameState) == kGameStateKeeperHoldsTheBall)
                return;

            s_blockDirections = true;

            if (increaseIndex) IncreasePlayerIndex();
            else               DecreasePlayerIndex(allowIfKeeperHolds);
        }
        else if ((s_controls & (int)GCE.kGameEventKick) != 0)
        {
            FirePressedInSubsMenu();
        }
    }

    // updateBench.cpp:374-380 — selectPlayerToSubstituteMenuHandler.
    private static void SelectPlayerToSubstituteMenuHandler()
    {
        if ((s_controls & (int)GCE.kGameEventKick) != 0)
            InitiateSubstitution();
        else
            UpdateSelectedMenuPlayer();
    }

    // updateBench.cpp:382-397 — handleFormationMenuMovement.
    private static void HandleFormationMenuMovement()
    {
        if (LeavingBenchMotion())
        {
            LeaveBenchFromMenu();
        }
        else if (s_controls == (int)GCE.kGameEventUp)
        {
            s_blockDirections = true;
            if (s_selectedFormationEntry > 0)
                s_selectedFormationEntry--;
        }
        else if (s_controls == (int)GCE.kGameEventDown)
        {
            s_blockDirections = true;
            if (s_selectedFormationEntry < 0)
                s_selectedFormationEntry = 0;
            else if (s_selectedFormationEntry < kNumFormationEntries - 1)
                s_selectedFormationEntry++;
        }
    }

    // updateBench.cpp:399-407 — handleFormationMenuControls.
    private static void HandleFormationMenuControls()
    {
        if ((s_controls & (int)GCE.kGameEventKick) != 0)
            ChangeTactics(s_selectedFormationEntry);
        else
            HandleFormationMenuMovement();
    }

    // updateBench.cpp:409-430 — markPlayersMenuHandler.
    private static void MarkPlayersMenuHandler()
    {
        int playerInfoAddr = 0;
        if (s_playerToBeSubstitutedPos >= 0 && s_playerToBeSubstitutedPos <= 10)
            playerInfoAddr = s_teamGameBase
                + s_playerToBeSubstitutedPos * TeamDataLoader.PlayerInfoSize;

        byte cards = playerInfoAddr != 0
            ? Memory.ReadByte(playerInfoAddr + TeamDataLoader.OffCards)
            : (byte)0;

        if (playerInfoAddr == 0 || cards >= 2 || s_fireTimer != kSubstituteFireTicks)
        {
            if ((s_controls & (int)GCE.kGameEventKick) != 0)
            {
                if (s_playerToBeSubstitutedOrd < 0)
                    ShowFormationMenu();
                else if (s_playerToBeSubstitutedOrd > 0)
                    SelectOrSwapPlayers();
            }
            else
            {
                UpdateSelectedMenuPlayer();
            }
        }
        else
        {
            MarkPlayer();
        }
    }

    // updateBench.cpp:432-457 — updateSelectedMenuPlayer.
    private static void UpdateSelectedMenuPlayer()
    {
        if (LeavingBenchMotion())
        {
            LeaveBenchFromMenu();
        }
        else if (GetBenchState() == kBenchStateMarkingPlayers
                 || s_playerToEnterGameIndex != 11
                 || GameMaxSubstitutes <= 5)
        {
            if (s_controls == (int)GCE.kGameEventUp)
            {
                s_blockDirections = true;
                if (GetBenchState() == kBenchStateMarkingPlayers
                    && (s_playerToBeSubstitutedOrd < 0 || s_playerToBeSubstitutedOrd == 1))
                {
                    // skip goalkeeper when going up, and jump to formation entry
                    s_playerToBeSubstitutedOrd = -1;
                    s_playerToBeSubstitutedPos = -1;
                }
                else if (s_playerToBeSubstitutedOrd != 0)
                {
                    DecreasePlayerToSubstitute();
                }
            }
            else if (s_controls == (int)GCE.kGameEventDown)
            {
                s_blockDirections = true;
                if (GetBenchState() == kBenchStateMarkingPlayers && s_playerToBeSubstitutedOrd < 0)
                {
                    // jump from formation entry to first player (skip goalkeeper)
                    s_playerToBeSubstitutedOrd = 1;
                    UpdatePlayerToBeSubstitutedPosition();
                }
                else if (s_playerToBeSubstitutedOrd != 10)
                {
                    IncreasePlayerToSubstitute();
                }
            }
        }
    }

    // updateBench.cpp:459-464 — showFormationMenu.
    private static void ShowFormationMenu()
    {
        SetBenchState(kBenchStateFormationMenu);
        s_blockFire = true;
        s_selectedFormationEntry = Memory.ReadSignedWord(s_teamBase + TeamData.OffTactics);
    }

    // updateBench.cpp:466-486 — firePressedInSubsMenu.
    private static void FirePressedInSubsMenu()
    {
        if (s_arrowPlayerIndex == 0)
        {
            SetBenchState(kBenchStateMarkingPlayers);
            s_blockFire = true;
            s_playerToBeSubstitutedOrd = -1;
            s_playerToBeSubstitutedPos = -1;
            s_selectedMenuPlayerIndex = -1;
        }
        else
        {
            // updateBench.cpp:475 — players[m_arrowPlayerIndex + 10]:
            // arrow 1..5 → substitutes players[11..15].
            int playerInfoAddr = s_teamGameBase
                + (s_arrowPlayerIndex + 10) * TeamDataLoader.PlayerInfoSize;
            if (!PlayerWasSubstituted(playerInfoAddr))
            {
                SetBenchState(kBenchStateAboutToSubstitute);
                s_blockFire = true;
                s_playerToEnterGameIndex = s_arrowPlayerIndex + 10;
                if (GameMaxSubstitutes <= kMaxSubstitutes || s_playerToEnterGameIndex != 11)
                    FindInitialPlayerToBeSubstituted();
                else
                    s_playerToBeSubstitutedPos = 0;
            }
        }
    }

    // updateBench.cpp:488-499 — maintainMarkedPlayer. TeamGame.markedPlayer
    // is an int16 at struct offset +20; players[] starts at +42, so with
    // s_teamGameBase == &players[0] the field sits at base - 22 (swos.h:296).
    private static void MaintainMarkedPlayer()
    {
        int markedAddr = s_teamGameBase - 22;
        short marked = Memory.ReadSignedWord(markedAddr);

        if (marked == s_playerToBeSubstitutedPos)
        {
            Memory.WriteWord(markedAddr, -1);
            marked = -1;
        }
        if (marked == s_playerToEnterGameIndex)
        {
            Memory.WriteWord(markedAddr, -1);
            if (s_playerToBeSubstitutedPos != 0)
                Memory.WriteWord(markedAddr, s_playerToBeSubstitutedPos);
        }
    }

    // updateBench.cpp:501-508 — swapMarkedPlayer.
    private static void SwapMarkedPlayer()
    {
        int markedAddr = s_teamGameBase - 22;
        short marked = Memory.ReadSignedWord(markedAddr);
        if (marked == s_playerToBeSubstitutedPos)
            Memory.WriteWord(markedAddr, s_selectedMenuPlayerIndex);
        else if (marked == s_selectedMenuPlayerIndex)
            Memory.WriteWord(markedAddr, s_playerToBeSubstitutedPos);
    }

    // updateBench.cpp:510-539 — selectOrSwapPlayers. Fire on a first player
    // selects him; fire on a second player swaps the pair's pitch positions
    // (PlayerInfo records + sprite structs + shirt table), reapplies tactics
    // and leaves the sub-menu.
    private static void SelectOrSwapPlayers()
    {
        if (s_selectedMenuPlayerIndex < 0)
        {
            if (s_fireTimer == 1)
                s_selectedMenuPlayerIndex = s_playerToBeSubstitutedPos;
        }
        else
        {
            if (s_selectedMenuPlayerIndex == s_playerToBeSubstitutedPos)
            {
                if (s_fireTimer == 1)
                    s_selectedMenuPlayerIndex = -1;
            }
            else
            {
                SwapMarkedPlayer();
                SwapPlayerShirtNumbers(s_playerToBeSubstitutedPos, s_selectedMenuPlayerIndex);
                // updateBench.cpp:525 — std::swap(players[posA], players[posB])
                // (the 61-byte PlayerInfo records).
                SwapPlayerInfoRecords(s_playerToBeSubstitutedPos, s_selectedMenuPlayerIndex);
                // updateBench.cpp:526-528 — swap the two SPRITE structs, then
                // swap playerOrdinal back so ordinals stay with their slots.
                SwapSpriteContentsKeepingOrdinals(s_playerToBeSubstitutedPos, s_selectedMenuPlayerIndex);

                InitializePlayerSpriteFrameIndices();
                // updateBench.cpp:531-532 — A4 = m_teamGame; ApplyTeamTactics().
                // PORT-DIVERGENCE (documented): ApplyTeamTactics isn't ported;
                // our per-tick break repositioning (UpdatePlayers.SetPlayer-
                // PositionsForGameBreak → teamTacticsPool) re-derives each
                // sprite's destination from team.tactics every stoppage tick,
                // which reapplies the formation without the one-shot call.

                s_selectedMenuPlayerIndex = -1;

                LeaveBenchFromMenu();
            }
        }
    }

    // updateBench.cpp:541-544 — updatePlayerToBeSubstitutedPosition.
    private static void UpdatePlayerToBeSubstitutedPosition()
    {
        s_playerToBeSubstitutedPos = GetBenchPlayerPosition(s_playerToBeSubstitutedOrd);
    }

    // ====================================================================
    // initiateSubstitution — updateBench.cpp:546-568
    // ====================================================================
    // Fire pressed on the on-pitch player list: start the walk-off. The game
    // waits for the outgoing sprite to reach the bench before swapping data.
    private static void InitiateSubstitution()
    {
        if (s_playerToBeSubstitutedPos < 0 || s_playerToBeSubstitutedPos > 10)
            return;   // original asserts; unreachable with valid menu state

        SetSubstituteInProgress();

        // updateBench.cpp:554-555 — numSubs++ for the substituting team.
        int numSubsAddr = s_teamNumber == 1 ? Memory.Addr.team1NumSubs : Memory.Addr.team2NumSubs;
        Memory.WriteWord(numSubsAddr, Memory.ReadSignedWord(numSubsAddr) + 1);

        s_blockFire = true;
        SetBenchState(kBenchStateInitial);

        // updateBench.cpp:560-561 — must set this sprite since the game waits
        // for him to arrive at the destination before continuing.
        bool top = s_teamBase == TeamData.TopBase;
        int spriteAddr = TeamData.GetTeamSpriteAddr(top, s_playerToBeSubstitutedPos);
        Memory.WriteDword(Memory.Addr.substitutedPlSprite, spriteAddr);
        Memory.WriteDword(Memory.Addr.teamThatSubstitutes, s_teamBase);

        // updateBench.cpp:563-567.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffCards, 0);
        s_substitutedPlDestX = kSubstitutedPlayerX;
        s_substitutedPlDestY = kSubstitutedPlayerY;
        s_plSubstitutedX = kSubstitutedPlayerX;
        s_plSubstitutedY = kSubstitutedPlayerY;
    }

    // ====================================================================
    // substitutePlayer — updateBench.cpp:570-591
    // ====================================================================
    // Old player has left the field, new one is about to go in. Does the
    // actual swap of the player data.
    private static void SubstitutePlayer()
    {
        int spriteAddr = Memory.ReadSignedDword(Memory.Addr.substitutedPlSprite);
        if (spriteAddr != 0)
        {
            Memory.WriteWord(spriteAddr + PlayerSprite.OffInjuryLevel, 0);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffSentAway, 0);
        }

        MaintainMarkedPlayer();
        SwapPlayerShirtNumbers(s_playerToEnterGameIndex, s_playerToBeSubstitutedPos);

        // updateBench.cpp:579 — outgoing record is flagged kSubstituted (-1)
        // BEFORE the swap, so after it the bench slot carries the flag.
        int posInfoAddr = s_teamGameBase
            + s_playerToBeSubstitutedPos * TeamDataLoader.PlayerInfoSize;
        Memory.WriteByte(posInfoAddr + TeamDataLoader.OffPosition, unchecked((byte)(-1)));

        SwapPlayerInfoRecords(s_playerToEnterGameIndex, s_playerToBeSubstitutedPos);

        InitializePlayerSpriteFrameIndices();
        // updateBench.cpp:583-584 — A4 = m_teamGame; ApplyTeamTactics().
        // PORT-DIVERGENCE — see SelectOrSwapPlayers note; per-tick stoppage
        // repositioning reapplies the formation.

        Memory.WriteWord(Memory.Addr.g_waitForPlayerToGoInTimer, kPlayerGoingInDelay);
        s_blockFire = true;

        // updateBench.cpp:589 — enqueueSubstituteSample(). Audio not wired
        // in the port yet (PlayEnqueuedSamples is a stub) — skipped.
        LeaveBench();
    }

    // ====================================================================
    // changeTactics — updateBench.cpp:593-606
    // ====================================================================
    private static void ChangeTactics(int newTactics)
    {
        if (newTactics < 0 || newTactics >= 19)
            return;   // original asserts against g_tacticsTable size

        // updateBench.cpp:597-599 — pl{1,2}Tactics = newTactics AND
        // m_team->tactics = newTactics. PORT-DIVERGENCE (documented): the
        // pl1Tactics/pl2Tactics globals have no Memory slot; team.tactics is
        // the only consumer in this port (tactics positioning + our
        // GetBenchPlayerPosition), so it carries the value alone.
        Memory.WriteWord(s_teamBase + TeamData.OffTactics, newTactics);

        // updateBench.cpp:601-602 — A4 = m_teamGame; ApplyTeamTactics().
        // PORT-DIVERGENCE — see SelectOrSwapPlayers note.

        // updateBench.cpp:604 — enqueueTacticsChangedSample(): audio stub.
        LeaveBenchFromMenu();
    }

    // updateBench.cpp:608-612 — leavingBenchMotion: only pure left/right
    // (no up/down mixed in) counts as "walk away from the bench".
    private static bool LeavingBenchMotion()
        => s_controls == (int)GCE.kGameEventLeft || s_controls == (int)GCE.kGameEventRight;

    // updateBench.cpp:614-630 — leaveBench.
    private static void LeaveBench()
    {
        SetBenchOff();
        Camera.SwitchCameraToLeavingBenchMode();

        Memory.WriteWord(Memory.Addr.g_cameraLeavingSubsTimer, kLeavingSubsDelay);

        s_bench1Called = false;
        s_bench2Called = false;

        s_teamsSwapped = false;
        SetBenchState(kBenchStateInitial);
        s_playerToBeSubstitutedPos = -1;

        s_pl1TapState.Reset();
        s_pl2TapState.Reset();
    }

    // updateBench.cpp:632-637 — leaveBenchFromMenu.
    private static void LeaveBenchFromMenu()
    {
        SetBenchState(kBenchStateInitial);
        s_blockDirections = true;
        s_blockFire = true;
    }

    // ====================================================================
    // benchBlocked — updateBench.cpp:639-650
    // ====================================================================
    // ALSO the ONLY place g_waitForPlayerToGoInTimer / g_cameraLeavingSubsTimer
    // tick down — the break-restart ladder (GameLoop.DispatchBreakCameraMode)
    // early-outs while either is non-zero, so without this decrement the
    // restart ceremony would deadlock after a substitution.
    private static bool BenchBlocked()
    {
        short waitTimer = Memory.ReadSignedWord(Memory.Addr.g_waitForPlayerToGoInTimer);
        if (waitTimer != 0)
        {
            Memory.WriteWord(Memory.Addr.g_waitForPlayerToGoInTimer, waitTimer - 1);
            return true;
        }

        short leavingTimer = Memory.ReadSignedWord(Memory.Addr.g_cameraLeavingSubsTimer);
        if (leavingTimer != 0)
        {
            Memory.WriteWord(Memory.Addr.g_cameraLeavingSubsTimer, leavingTimer - 1);
            return true;
        }

        return SubstituteInProgress()
            || Referee.RefereeActive()
            || Memory.ReadSignedWord(Memory.Addr.statsTimer) != 0;
    }

    // ====================================================================
    // benchUnavailable — updateBench.cpp:652-664
    // ====================================================================
    // The bench can only be summoned while the game is stopped: not during
    // open play (gameStatePl == kInProgress 100), not while a card is being
    // handed, not during penalties, not during the starting/half-time/game-
    // ended ceremonies (gameState 21..30, swos.h:582-591).
    private const int kGameStateKeeperHoldsTheBall = 3;
    private const int kGameStatePlInProgress       = 100;
    private const int kGameStateStartingGame       = 21;
    private const int kGameStateGameEnded          = 30;
    private static bool BenchUnavailable()
    {
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        short gameState   = Memory.ReadSignedWord(Memory.Addr.gameState);

        if (gameStatePl == kGameStatePlInProgress
            || Referee.CardHandingInProgress()
            || Memory.ReadSignedWord(Memory.Addr.playingPenalties) != 0
            || (gameState >= kGameStateStartingGame && gameState <= kGameStateGameEnded))
        {
            s_pl1TapState.Reset();
            s_pl2TapState.Reset();
            s_bench1Called = false;
            s_bench2Called = false;
            return true;
        }

        return false;
    }

    // updateBench.cpp:666-674 — getNonBenchControlsTeam. When no explicit
    // request is queued, alternate the polled team each tick.
    private static int GetNonBenchControlsTeam()
    {
        if (s_bench1Called)
            return TeamData.TopBase;
        if (s_bench2Called)
            return TeamData.BottomBase;
        return (++s_alternateTeamsTimer & 1) != 0 ? TeamData.TopBase : TeamData.BottomBase;
    }

    // ====================================================================
    // updateNonBenchControls — updateBench.cpp:676-701
    // ====================================================================
    // Rebuilds m_controls from the polled team's live control fields (which
    // InputControls.UpdateTeamControlsInternal fills from the Godot input
    // bridge, including secondaryFire ← kGameEventBench).
    private static bool UpdateNonBenchControls(int teamBase)
    {
        short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        if (playerNumber != 0)
        {
            short direction = Memory.ReadSignedWord(teamBase + TeamData.OffDirection);
            s_controls = (int)InputControls.DirectionToEvents(direction);
            if (Memory.ReadByte(teamBase + TeamData.OffFirePressed) != 0)
                s_controls |= (int)GCE.kGameEventKick;
            if (Memory.ReadByte(teamBase + TeamData.OffSecondaryFire) != 0)
                s_controls |= (int)GCE.kGameEventBench;
        }
        else
        {
            short playerCoachNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerCoachNumber);
            switch (playerCoachNumber)
            {
                case 1:
                    s_controls = (int)InputControls.GetPlayerEvents(InputControls.kPlayer1);
                    break;
                case 2:
                    s_controls = (int)InputControls.GetPlayerEvents(InputControls.kPlayer2);
                    break;
                default:
                    // updateBench.cpp:692-696 — assert(false) + fallthrough to
                    // case 0 → return false (pure-AI team never calls a bench).
                    return false;
            }
        }

        return true;
    }

    // ====================================================================
    // updateBenchControls — updateBench.cpp:703-714
    // ====================================================================
    private static void UpdateBenchControls()
    {
        int teamBase = s_teamsSwapped
            ? Memory.ReadSignedDword(s_teamBase + TeamData.OffOpponentsTeam)
            : s_teamBase;
        if (teamBase == 0) teamBase = s_teamBase;

        short playerNumber      = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        short playerCoachNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerCoachNumber);
        int player = (playerNumber == 1 || playerCoachNumber == 1)
            ? InputControls.kPlayer1
            : InputControls.kPlayer2;
        s_controls = (int)InputControls.GetPlayerEvents(player);
    }

    // updateBench.cpp:716-723 — bumpGoToBenchTimer.
    private static bool BumpGoToBenchTimer()
    {
        if (s_goToBenchTimer <= 0)
            return false;

        s_goToBenchTimer--;
        return true;
    }

    // ====================================================================
    // filterControls — updateBench.cpp:725-758
    // ====================================================================
    // Returns true if the controls were filtered (blocked). Keeps menu
    // navigation at a readable pace and blocks the fire that opened a menu
    // from immediately re-triggering inside it.
    private static bool FilterControls()
    {
        const int kMovementDelay = 4;

        int currentDirection = s_controls & (int)InputControls.kGameEventMovementMask;
        bool sameDirectionHeld = currentDirection != 0
            && s_blockDirections
            && s_lastDirection == currentDirection;

        if (sameDirectionHeld &&
            (GetBenchState() == kBenchStateInitial ||
             (s_lastDirection != (int)GCE.kGameEventUp && s_lastDirection != (int)GCE.kGameEventDown) ||
             ++s_movementDelayTimer != kMovementDelay))
            return true;

        s_blockDirections = false;

        s_movementDelayTimer = 0;
        s_lastDirection = currentDirection;

        bool firing = (s_controls & (int)GCE.kGameEventKick) != 0;

        if (firing)
            s_fireTimer++;
        else
            s_fireTimer = 0;

        if (s_blockFire && firing)
            return true;
        s_blockFire = false;

        return false;
    }

    // ====================================================================
    // benchInvoked — updateBench.cpp:760-794
    // ====================================================================
    // Game is stopped and bench call is possible; check if it's actually
    // invoked: secondary fire (kGameEventBench), or any one direction tapped
    // quickly enough times (tapCount reaches kNumTapsForBench — i.e. three
    // physical taps: the first arms previousDirection, the next two count).
    //
    // Verified 1:1 against the actual SWOS disassembly (2026-07-02):
    // swos.asm BenchCheckControls @@sub_controls_updated_branch_on_team
    // (swos.asm:92110-92200, subsPl{1,2}TapCount / subsPrevPl{1,2}Direction /
    // pl{1,2}BenchBlockWhileHoldingDirection / pl{1,2}BenchTimeoutCounter):
    //   - ALL 8 directions count (subsDirection is the full 0..7 word from
    //     team.direction), not just left/right;
    //   - a "tap" is a PRESS EDGE: blockWhileHoldingDirection latches on the
    //     first tick a direction is seen and only a fully-released poll
    //     (movement mask empty / subsDirection < 0) clears it — a HELD
    //     direction therefore counts as exactly ONE tap, never repeats;
    //   - `cmp subsPl1TapCount, 2 / jz @@enter_substitutes_menu` — three
    //     press edges from a cold chain ("tapped three times quickly");
    //     when a direction was already HELD as the game stopped, that hold
    //     contributes the arming edge, so the user-visible gesture is the
    //     classic "double-tap" after release;
    //   - 15-tick release timeout (pl1BenchTimeoutCounter == 15 → chain
    //     reset), counted only on polls with no direction pressed;
    //   - a DIFFERENT direction resets the chain without arming (arm happens
    //     on the next blocked-free poll).
    // Regression-guarded by `--bench-test` (Main.RunBenchTest tap probes):
    // held-200-ticks must NOT open; cold triple-tap opens on press 3;
    // hold+double-tap opens on tap 2; open-play taps/B never open (gate
    // resets first); CPU teams can never invoke.
    private static bool BenchInvoked(int teamBase)
    {
        if ((s_controls & (int)GCE.kGameEventBench) != 0)
            return true;

        bool topTeam = teamBase == TeamData.TopBase;
        ref TapCounterState state = ref s_pl1TapState;
        if (!topTeam) state = ref s_pl2TapState;

        if ((s_controls & (int)InputControls.kGameEventMovementMask) == 0)
        {
            state.BlockWhileHoldingDirection = false;
            if (++state.TapTimeoutCounter == kTapTimeoutTicks)
                state.Reset();
        }
        else if (!state.BlockWhileHoldingDirection)
        {
            if (state.GotLastTapDirection())
            {
                state.TapTimeoutCounter = 0;
                if (state.HoldingSameDirectionAsLastTap(s_controls))
                {
                    if (++state.TapCount >= kNumTapsForBench)
                        return true;
                    state.BlockWhileHoldingDirection = true;
                }
                else
                {
                    state.PreviousDirection = 0;
                    state.TapCount = 0;
                }
            }
            else
            {
                state.PreviousDirection = s_controls & (int)InputControls.kGameEventMovementMask;
                state.BlockWhileHoldingDirection = true;
            }
        }

        return false;
    }

    // ====================================================================
    // findInitialPlayerToBeSubstituted — updateBench.cpp:796-838
    // ====================================================================
    // Picks the default candidate to take off for the chosen substitute:
    // exact position match > same position group (defence / midfield) >
    // first player who isn't sent off.
    //
    // NOTE: the reference C++ has a translation bug — its `isPositionIn`
    // lambda returns the std::find ITERATOR which is truthy even for "not
    // found" (end() is a non-null pointer), so the group check always passes.
    // We implement the evident intent (found != end) that the position-group
    // tables exist for; the exactMatch tier dominates in practice either way.
    private static void FindInitialPlayerToBeSubstituted()
    {
        int enterInfoAddr = s_teamGameBase
            + s_playerToEnterGameIndex * TeamDataLoader.PlayerInfoSize;
        sbyte position = (sbyte)Memory.ReadByte(enterInfoAddr + TeamDataLoader.OffPosition);

        int exactMatch = -1;
        int approximateMatch = -1;
        int firstAvailablePlayer = -1;

        for (int i = 0; i < 11; i++)
        {
            int infoAddr = GetBenchPlayerInfoAddrByOrd(i);
            byte cards = Memory.ReadByte(infoAddr + TeamDataLoader.OffCards);
            if (cards >= 2)   // PlayerInfo.sentOff() — swos.h:205-207
                continue;

            if (firstAvailablePlayer < 0)
                firstAvailablePlayer = i;

            sbyte playerPosition = (sbyte)Memory.ReadByte(infoAddr + TeamDataLoader.OffPosition);

            if (approximateMatch < 0)
            {
                // kDefencePositions = { kDefender(3), kRightBack(1), kLeftBack(2) }
                // kMidfieldPositions = { kMidfielder(6), kRightWing(4), kLeftWing(5) }
                bool bothDefence  = IsDefencePosition(position)  && IsDefencePosition(playerPosition);
                bool bothMidfield = IsMidfieldPosition(position) && IsMidfieldPosition(playerPosition);
                if (bothDefence || bothMidfield)
                    approximateMatch = i;
            }

            if (exactMatch < 0 && position == playerPosition)
                exactMatch = i;
        }

        if (exactMatch >= 0)
            s_playerToBeSubstitutedOrd = exactMatch;
        else if (approximateMatch >= 0)
            s_playerToBeSubstitutedOrd = approximateMatch;
        else
            s_playerToBeSubstitutedOrd = firstAvailablePlayer >= 0 ? firstAvailablePlayer : 0;

        s_playerToBeSubstitutedPos = GetBenchPlayerPosition(s_playerToBeSubstitutedOrd);
    }

    private static bool IsDefencePosition(int p)  => p == 3 || p == 1 || p == 2;
    private static bool IsMidfieldPosition(int p) => p == 6 || p == 4 || p == 5;

    // Internal variant of GetBenchPlayerInfoAddr that doesn't re-run the
    // getBenchTeam() sync (matches the original's direct getBenchPlayer use).
    private static int GetBenchPlayerInfoAddrByOrd(int ord)
        => s_teamGameBase + GetBenchPlayerPosition(ord) * TeamDataLoader.PlayerInfoSize;

    // updateBench.cpp:840-845 — markPlayer (hold fire on an on-pitch player
    // in the marking menu toggles the team's markedPlayer).
    private static void MarkPlayer()
    {
        int markedAddr = s_teamGameBase - 22;
        short marked = Memory.ReadSignedWord(markedAddr);
        bool alreadyMarked = marked == s_playerToBeSubstitutedPos;
        Memory.WriteWord(markedAddr, alreadyMarked ? -1 : s_playerToBeSubstitutedPos);
        s_selectedMenuPlayerIndex = -1;
    }

    // updateBench.cpp:847-851 — swapPlayerShirtNumbers.
    private static void SwapPlayerShirtNumbers(int ord1, int ord2)
    {
        int row = s_teamGameBase == Memory.ReadSignedDword(Memory.Addr.topTeamInGame) ? 1 : 0;
        byte t = s_shirtNumberTable[row, ord1];
        s_shirtNumberTable[row, ord1] = s_shirtNumberTable[row, ord2];
        s_shirtNumberTable[row, ord2] = t;
    }

    // updateBench.cpp:853-860 — increasePlayerIndex.
    private static void IncreasePlayerIndex()
    {
        while (++s_arrowPlayerIndex <= kMaxSubstitutes && !IsPlayerOkToSelect())
        {
        }

        if (s_arrowPlayerIndex > kMaxSubstitutes)
            DecreasePlayerIndex(false);
    }

    // updateBench.cpp:862-870 — decreasePlayerIndex.
    private static void DecreasePlayerIndex(bool allowBenchSwitch)
    {
        if (s_arrowPlayerIndex != 0)
        {
            while (--s_arrowPlayerIndex > 0 && !IsPlayerOkToSelect())
            {
            }
        }
        else if (allowBenchSwitch)
        {
            TrainingSwapBenchTeams();
        }
    }

    // updateBench.cpp:872-888 — increase/decreasePlayerToSubstitute.
    // The original pair mutually recurses until an eligible player is found;
    // PORT-GUARD: a bounded loop (32 steps) replaces unbounded recursion so
    // pathological data (all 11 sent off) can't hang the tick. With sane data
    // the guard is never reached.
    private static void IncreasePlayerToSubstitute()
    {
        int guard = 32;
        int step = +1;
        do
        {
            s_playerToBeSubstitutedOrd += step;
            if (s_playerToBeSubstitutedOrd >= 11) { s_playerToBeSubstitutedOrd = 10; step = -1; }
            if (s_playerToBeSubstitutedOrd < 0)   { s_playerToBeSubstitutedOrd = 0;  step = +1; }
            UpdatePlayerToBeSubstitutedPosition();
        } while (!IsPlayerOkToSubstitute() && --guard > 0);
    }

    private static void DecreasePlayerToSubstitute()
    {
        int guard = 32;
        int step = -1;
        do
        {
            s_playerToBeSubstitutedOrd += step;
            if (s_playerToBeSubstitutedOrd < 0)   { s_playerToBeSubstitutedOrd = 0;  step = +1; }
            if (s_playerToBeSubstitutedOrd >= 11) { s_playerToBeSubstitutedOrd = 10; step = -1; }
            UpdatePlayerToBeSubstitutedPosition();
        } while (!IsPlayerOkToSubstitute() && --guard > 0);
    }

    // updateBench.cpp:890-905 — isPlayerOkToSelect. A bench row is selectable
    // if the team still has substitutions left (numSubs != gameMinSubstitutes)
    // and the substitute himself hasn't already played + been taken off.
    private static bool IsPlayerOkToSelect()
    {
        short numSubs = Memory.ReadSignedWord(
            s_teamNumber == 1 ? Memory.Addr.team1NumSubs : Memory.Addr.team2NumSubs);

        if (GameMinSubstitutes == numSubs)
            return false;

        int infoAddr = s_teamGameBase + (s_arrowPlayerIndex + 10) * TeamDataLoader.PlayerInfoSize;
        // PlayerInfo.canBeSubstituted() — release semantics = !wasSubstituted()
        // (swos.h:190-200; the SWOS_TEST variant preserves an original
        // uninitialised-register bug we don't reproduce).
        if (PlayerWasSubstituted(infoAddr))
            return false;

        return true;
    }

    // updateBench.cpp:907-913 — isPlayerOkToSubstitute.
    private static bool IsPlayerOkToSubstitute()
    {
        if (GetBenchState() == kBenchStateMarkingPlayers)
            return s_playerToBeSubstitutedOrd != 0;

        if (s_playerToBeSubstitutedPos < 0 || s_playerToBeSubstitutedPos > 10)
            return false;
        int infoAddr = s_teamGameBase
            + s_playerToBeSubstitutedPos * TeamDataLoader.PlayerInfoSize;
        return Memory.ReadByte(infoAddr + TeamDataLoader.OffCards) < 2;
    }

    // updateBench.cpp:915-922 — trainingSwapBenchTeams.
    private static void TrainingSwapBenchTeams()
    {
        s_teamsSwapped = !s_teamsSwapped;
        s_trainingTopTeam = !s_trainingTopTeam;
        SwapBenchWithOpponent();
        s_teamBase = Memory.ReadSignedDword(s_teamBase + TeamData.OffOpponentsTeam);
        short teamNumber = Memory.ReadSignedWord(s_teamBase + TeamData.OffTeamNumber);
        s_teamGameBase = Memory.ReadSignedDword(teamNumber == 2
            ? Memory.Addr.bottomTeamInGame
            : Memory.Addr.topTeamInGame);
    }

    // PlayerInfo.wasSubstituted() — swos.h:202-204:
    //   substituted || position == kSubstituted(-1) || cards >= 2.
    private static bool PlayerWasSubstituted(int infoAddr)
    {
        if (Memory.ReadByte(infoAddr + TeamDataLoader.OffSubstituted) != 0)
            return true;
        if ((sbyte)Memory.ReadByte(infoAddr + TeamDataLoader.OffPosition) == -1)
            return true;
        return Memory.ReadByte(infoAddr + TeamDataLoader.OffCards) >= 2;
    }

    // ====================================================================
    // UpdateSubstitutedPlayerWalk — updatePlayers.cpp:9051-9197
    // (l_check_for_substituted_player + l_new_player_about_to_go_in +
    //  l_set_substituted_player_destination + l_set_player_going_in_speed)
    // ====================================================================
    // Drives the outgoing player's walk to the bench, the swap trigger and
    // the incoming player's walk onto the pitch, keyed off the signed
    // g_substituteInProgress word:
    //    1 → walking to the touchline point (39, 449)
    //    2 → walking to the bench entry (plComingX/Y = 26, 449)
    //   -1 → data swapped; the SAME sprite (now the new player) teleported to
    //        (plSubstitutedX/Y) walks to its break/tactics position; when its
    //        deltas settle to zero the state returns to 0.
    //
    // PORT-DIVERGENCE (documented): in the original this runs inside
    // updatePlayers' per-player loop (before movePlayers) only for the sprite
    // equal to substitutedPlSprite and jumps straight to
    // l_update_player_speed_and_deltas, bypassing the break repositioning for
    // that sprite. UpdatePlayers.cs (owned by another session) marks the
    // label as TODO, so the FSM steps HERE at the updateBench slot of the
    // tick (gameLoop.cpp:315). Consequences, both self-correcting:
    //   - dest/delta writes land after this tick's movePlayers → applied from
    //     the next tick (1-tick latency, deterministic);
    //   - on the sprite's 1-in-11 round-robin turn UpdatePlayers'
    //     SetPlayerPositionsForGameBreak briefly writes a break dest; our
    //     later re-write wins for every following tick.
    // The asm's explicit `speed = kSubstitutedPlayerSpeed` writes are
    // reproduced even though updatePlayerSpeedAndFrameDelay immediately
    // overwrites speed for kNormal sprites (same dead-write as the original
    // executes at l_update_player_speed_and_deltas — player.cpp:17-32).
    private static void UpdateSubstitutedPlayerWalk()
    {
        short sip = Memory.ReadSignedWord(Memory.Addr.g_substituteInProgress);
        if (sip == 0)
            return;

        int spriteAddr = Memory.ReadSignedDword(Memory.Addr.substitutedPlSprite);
        if (spriteAddr == 0)
            return;

        int teamBase = Memory.ReadSignedDword(Memory.Addr.teamThatSubstitutes);
        if (teamBase == 0)
            teamBase = TeamData.TopBase;   // defensive; original would deref

        if (sip < 0)
        {
            // updatePlayers.cpp:9154-9197 — l_set_player_going_in_speed:
            // keep the entry speed pinned; when both deltas are zero the
            // walk-in is complete → g_substituteInProgress = 0.
            Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, kSubstitutedPlayerSpeed);
            int deltaX = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffDeltaX);
            int deltaY = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffDeltaY);
            if (deltaX == 0 && deltaY == 0)
                Memory.WriteWord(Memory.Addr.g_substituteInProgress, 0);
            return;
        }

        // updatePlayers.cpp:9063-9087 — only the substituted sprite reacts;
        // injuryLevel == -2 (stretchered off) skips straight to the swap.
        short injuryLevel = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffInjuryLevel);
        if (injuryLevel == -2)
        {
            NewPlayerAboutToGoInTransition(spriteAddr, teamBase);
            return;
        }

        short xWhole = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short yWhole = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);

        // updatePlayers.cpp:9090-9118 — still travelling → keep the dest fresh.
        if (xWhole != s_substitutedPlDestX || yWhole != s_substitutedPlDestY)
        {
            SetSubstitutedPlayerDestination(spriteAddr, teamBase);
            return;
        }

        // Arrived. updatePlayers.cpp:9120-9129 — phase 2 already? → swap.
        if (sip == 2)
        {
            NewPlayerAboutToGoInTransition(spriteAddr, teamBase);
            return;
        }

        // updatePlayers.cpp:9131-9138 — enter phase 2: walk from the touchline
        // to the bench entry point (plComingX/Y).
        Memory.WriteWord(Memory.Addr.g_substituteInProgress, 2);
        s_substitutedPlDestX = Memory.ReadSignedWord(Memory.Addr.plComingX);
        s_substitutedPlDestY = Memory.ReadSignedWord(Memory.Addr.plComingY);
        SetSubstitutedPlayerDestination(spriteAddr, teamBase);
    }

    // updatePlayers.cpp:9140-9152 — l_new_player_about_to_go_in.
    private static void NewPlayerAboutToGoInTransition(int spriteAddr, int teamBase)
    {
        Memory.WriteWord(Memory.Addr.g_substituteInProgress, unchecked((short)-1));
        Memory.WriteWord(spriteAddr + PlayerSprite.OffX + 2, s_plSubstitutedX);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffY + 2, s_plSubstitutedY);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, kSubstitutedPlayerSpeed);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSentAway, 0);
        // Tail recompute (l_update_player_speed_and_deltas) so the deltas
        // point at the still-current dest and the sip<0 settle check can't
        // fire on the very teleport tick.
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // updatePlayers.cpp:9154-9170 — l_set_substituted_player_destination +
    // the shared l_update_player_speed_and_deltas tail (10086-10106).
    private static void SetSubstitutedPlayerDestination(int spriteAddr, int teamBase)
    {
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, s_substitutedPlDestX);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, s_substitutedPlDestY);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, kSubstitutedPlayerSpeed);
        // l_update_player_speed_and_deltas — speed table + delta recompute
        // (RecomputeSpriteDeltas is folded into the C# helper).
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // ====================================================================
    // Data-swap helpers (substitutePlayer / selectOrSwapPlayers)
    // ====================================================================

    // std::swap of two 61-byte PlayerInfo records inside the bench team's
    // TeamGame.players[] (updateBench.cpp:525 / :580).
    private static void SwapPlayerInfoRecords(int idxA, int idxB)
    {
        int a = s_teamGameBase + idxA * TeamDataLoader.PlayerInfoSize;
        int b = s_teamGameBase + idxB * TeamDataLoader.PlayerInfoSize;
        for (int i = 0; i < TeamDataLoader.PlayerInfoSize; i++)
        {
            byte t = Memory.ReadByte(a + i);
            Memory.WriteByte(a + i, Memory.ReadByte(b + i));
            Memory.WriteByte(b + i, t);
        }
    }

    // updateBench.cpp:526-528 —
    //   std::swap(*m_team->players[posA], *m_team->players[posB]);
    //   std::swap(players[posA]->playerOrdinal, players[posB]->playerOrdinal);
    // i.e. swap the full 110-byte Sprite structs, then swap the ordinals BACK
    // so each slot keeps its own ordinal (net: everything but playerOrdinal).
    private static void SwapSpriteContentsKeepingOrdinals(int posA, int posB)
    {
        bool top = s_teamBase == TeamData.TopBase;
        int a = TeamData.GetTeamSpriteAddr(top, posA);
        int b = TeamData.GetTeamSpriteAddr(top, posB);
        if (a == 0 || b == 0) return;

        for (int i = 0; i < PlayerSprite.SpriteSize; i++)
        {
            byte t = Memory.ReadByte(a + i);
            Memory.WriteByte(a + i, Memory.ReadByte(b + i));
            Memory.WriteByte(b + i, t);
        }

        // Ordinals travelled with the structs — swap them back.
        short ordA = Memory.ReadSignedWord(a + PlayerSprite.OffPlayerOrdinal);
        short ordB = Memory.ReadSignedWord(b + PlayerSprite.OffPlayerOrdinal);
        Memory.WriteWord(a + PlayerSprite.OffPlayerOrdinal, ordB);
        Memory.WriteWord(b + PlayerSprite.OffPlayerOrdinal, ordA);
    }

    // ====================================================================
    // initializePlayerSpriteFrameIndices — gameSprites.cpp:113-134
    // ====================================================================
    // Re-derives each sprite's frameOffset from its (possibly just-swapped)
    // PlayerInfo.face: keeper gets the goalkeeper atlas row, outfielders the
    // face-tinted player row. Called after every PlayerInfo swap so the
    // renderer shows the right skin/kit frames for the new occupant.
    private static void InitializePlayerSpriteFrameIndices()
    {
        for (int t = 0; t < 2; t++)
        {
            bool top = t == 0;
            int teamGame = Memory.ReadSignedDword(top
                ? Memory.Addr.topTeamInGame
                : Memory.Addr.bottomTeamInGame);
            if (teamGame == 0)
                continue;

            // gameSprites.cpp:124 — keeper: getGoalkeeperSpriteOffset(top, players[0].face).
            int keeperAddr = TeamData.GetTeamSpriteAddr(top, 0);
            if (keeperAddr != 0)
            {
                byte keeperFace = Memory.ReadByte(teamGame + TeamDataLoader.OffFace);
                Memory.WriteWord(keeperAddr + PlayerSprite.OffFrameOffset,
                    GameSprites.GetGoalkeeperSpriteOffset(top, keeperFace));
            }

            // gameSprites.cpp:126-132 — outfielders 1..10.
            for (int i = 1; i < PlayerSprite.TeamSize; i++)
            {
                int spriteAddr = TeamData.GetTeamSpriteAddr(top, i);
                if (spriteAddr == 0) continue;
                byte face = Memory.ReadByte(teamGame + i * TeamDataLoader.PlayerInfoSize
                    + TeamDataLoader.OffFace);
                Memory.WriteWord(spriteAddr + PlayerSprite.OffFrameOffset,
                    GameSprites.GetPlayerSpriteOffsetFromFace(face));
            }
        }
    }

    // ====================================================================
    // checkForThrowInAndKeepersBall — bench.cpp:131-143 (static)
    // ====================================================================
    // If the player who just stopped to enter the bench was mid-throw-in,
    // make them drop the ball and revert to normal standing — otherwise the
    // throw-in animation freezes mid-pose against the bench overlay.
    //
    // PlayerState::kThrowIn = 5 (Sprite.h:22). PlayerState::kNormal = 0.
    private const int kPlayerStateNormal  = 0;
    private const int kPlayerStateThrowIn = 5;
    private static void CheckForThrowInAndKeepersBall()
    {
        // bench.cpp:133 — auto player = lastTeamPlayedBeforeBreak->controlledPlayer.
        int lastTeamBase = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        int playerAddr = 0;
        if (lastTeamBase != 0)
        {
            playerAddr = TeamData.ControlledPlayerFromBase(lastTeamBase);
        }

        // bench.cpp:134-140 — gate on `player && player->state == kThrowIn`.
        if (playerAddr != 0)
        {
            byte state = Memory.ReadByte(playerAddr + PlayerSprite.OffPlayerState);
            if (state == kPlayerStateThrowIn)
            {
                // bench.cpp:135 — swos.hideBall = 0.
                Memory.WriteWord(Memory.Addr.hideBall, 0);
                // bench.cpp:136 — player->state = kNormal.
                Memory.WriteByte(playerAddr + PlayerSprite.OffPlayerState, kPlayerStateNormal);
                // bench.cpp:137-139 — SetPlayerAnimationTable(playerNormalStandingAnimTable, player).
                PlayerActions.SetPlayerAnimationTable(playerAddr, Memory.Addr.playerNormalStandingAnimTable);
            }
        }

        // bench.cpp:142 — always run keeper-ball cleanup.
        CheckIfGoalkeeperClaimedTheBall();
    }

    // ====================================================================
    // checkIfGoalkeeperClaimedTheBall — game.cpp:1218-1235
    // ====================================================================
    // Helper invoked at bench-enter and bench-exit. Two branches:
    //   A. gameState == kKeeperHoldsTheBall (3): drive the full keeper-claim
    //      transition via the existing PlayerUpdate.GoalkeeperClaimedTheBall
    //      port. The keeper sprite is the goalie of lastTeamPlayedBeforeBreak.
    //   B. otherwise: stop play cleanly — breakCameraMode = -1, gameStatePl =
    //      kStopped, clear stoppage timers, stopAllPlayers(), zero camera
    //      velocity.
    //
    // Source: external/swos-port/src/game/game.cpp:1218-1235
    //         external/swos-port/src/game/team.cpp:26-44 (stopAllPlayers)
    //         external/swos-port/src/swos/swos.h:571,593 (GameState)
    private const int kGameStatePlStopped = 101;
    public static void CheckIfGoalkeeperClaimedTheBall()
    {
        short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gs == kGameStateKeeperHoldsTheBall)
        {
            // game.cpp:1220-1225 — auto team = swos.lastTeamPlayedBeforeBreak;
            //                      A1 = team->players[0]; goalkeeperClaimedTheBall();
            int teamBase = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
            if (teamBase == 0) return;  // defensive — would have null-derefed in C++

            // team->players[0] = goalie sprite address (per-team sprites table).
            int keeperAddr = TeamData.GetTeamSpriteAddr(
                top: teamBase == TeamData.TopBase,
                slotInTeam: 0);
            if (keeperAddr == 0) return;

            PlayerUpdate.GoalkeeperClaimedTheBall(keeperAddr, teamBase == TeamData.TopBase);
        }
        else
        {
            // game.cpp:1227-1233 — clean stoppage.
            Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
            Memory.WriteWord(Memory.Addr.gameStatePl, kGameStatePlStopped);
            Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
            Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
            StopAllPlayers();
            Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
            Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
        }
    }

    // ====================================================================
    // stopAllPlayers — team.cpp:26-44
    // ====================================================================
    // Wipe all per-team in-play flags + freeze every outfielder at their
    // current position (clear pending dest) for normal/non-sentaway players.
    // The original goalkeeperPlaying bug (top team's flag NOT cleared) is
    // preserved per swos-port comment — we mirror SWOS behavior exactly,
    // setting goalkeeperPlaying = 0 only for the bottom team. See
    // team.cpp:39-43.
    private static void StopAllPlayers()
    {
        for (int t = 0; t < 2; t++)
        {
            bool top = (t == 0);
            int teamBase = TeamData.Base(top);

            // team.cpp:46-55 — stopPlayers() — freeze each outfielder.
            for (int slot = 0; slot < PlayerSprite.TeamSize; slot++)
            {
                int spriteAddr = TeamData.GetTeamSpriteAddr(top, slot);
                if (spriteAddr == 0) continue;

                byte state = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerState);
                short sentAway = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSentAway);
                if (state == kPlayerStateNormal && sentAway == 0)
                {
                    // destX = x.whole(), destY = y.whole() — pin sprite where it stands.
                    short xWhole = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
                    short yWhole = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, xWhole);
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, yWhole);
                }
            }

            // team.cpp:30-37 — per-team flag wipes.
            Memory.WriteWord(teamBase + TeamData.OffBallInPlay, 0);
            Memory.WriteWord(teamBase + TeamData.OffBallOutOfPlay, 0);
            Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, 0);
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
            Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 0);
            Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);

            // team.cpp:39-43 — the original SWOS bug: top team's
            // goalkeeperPlaying is NEVER cleared. We preserve this fidelity
            // bug per the project's 1:1 charter (release builds skip the
            // SWOS_TEST guard, so only the bottom team is cleared).
            if (!top)
            {
                Memory.WriteWord(teamBase + TeamData.OffGoalkeeperPlaying, 0);
            }
        }
    }
}
