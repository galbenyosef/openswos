namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Result tracking + goal registration ported from external/swos-port/src/game/result.cpp.
//
// SCOPE: Owns the per-match SCORERS list (which player scored which goal at
// what minute), the result-display TIMER state machine, and the goal-event
// pipeline (registerScorer). DOES NOT own the draw side of the result screen —
// Godot's UI layer reads our state and draws independently.
//
// Result timer (result.cpp:44-47, 122-135):
//   - Sentinels 30 000 (kEndOfHalfResult), 31 000 (kGameBreakResult), 32 000
//     (kMaxResultTicks) signal "open this kind of result screen". The first
//     tick of UpdateResult catches the sentinel, reinitialises the timer to
//     the actual length (275/165/29 000), and sets m_showResult=true.
//   - Each subsequent tick decrements by lastFrameTicks. When it reaches 0,
//     hide; if game-state is at half time / full time, then enqueue stats.
//
// registerScorer (result.cpp:158-210):
//   - Selects the scorer's team's scorers array.
//   - Own-goal swaps the teams and adds 1 000 to the shirt number (so the
//     shirt# space distinguishes "their #9 scored against us" from "our #9").
//   - Finds an existing slot for this shirt or the first empty slot.
//   - Appends the goal (type + minute as 3 BCD digits) to the slot's goals[]
//     array, bumps player.goalsScored (regular only) or team.numOwnGoals (OG).
//   - Calls updateScorersText which re-renders the scorer-line strings — we
//     stub this because it's pure text rendering.
//
// We back the SCORERS storage in Memory.Addr.res_scorers* arrays declared
// inline here (we don't add 8×10 byte arrays to Memory.cs because they're
// nested 3-deep; ManagedScorers in this class is the source of truth).
public static class Result
{
    // result.cpp:10-11.
    public const int kMaxScorersForDisplay = 8;
    public const int kMaxGoalsPerScorer    = 10;

    // result.cpp:13-14.
    public const int kMaxScorerLineWidth = 120;
    public const int kScorerLineHeight   = 7;

    // result.cpp:16.
    public const int kCharactersPerLine = 61;

    // result.cpp:18-31 — UI layout constants (kept for parity; Godot draws).
    public const int kResultX = 160;
    public const int kDashX   = 157;
    public const int kDashY   = 185;

    public const int kLeftMargin  = 128;
    public const int kRightMargin = 192;

    public const int kBigLeftResultDigitX     = 143;
    public const int kBigResultDigitY         = 177;
    public const int kSmallLeftResultDigitX   = 150;
    public const int kSmallRightResultDigitX  = 163;
    public const int kBigRightResultSecondDigitX = 165;

    public const int kFirstLineBelowResultY = 199;
    public const int kTeamNameY             = 182;
    public const int kGridTopY              = kTeamNameY - 15;
    public const int kPeriodEndSpriteY      = kGridTopY - 10;

    public const int kResultBigDigitWidth   = 12;
    public const int kResultSmallDigitWidth = 6;
    public const int kResultDigit1Offset    = 4;

    // result.cpp:41-47 — timer sentinel + duration constants.
    public const int kResultAtHalfTimeLength = 275;
    public const int kResultAtGameBreakLength = 165;

    public const int kEndOfHalfResult  = 30000;
    public const int kGameBreakResult  = 31000;
    public const int kMaxResultTicks   = 32000;
    public const int kResultTickClamped = 29000;

    // GameState enum (swos.h:568-595) — values referenced here.
    public const short kGameStateResultOnHalftime  = 25;
    public const short kGameStateResultAfterTheGame = 26;

    // UpdateGoals.GoalType mirror.
    private const int kRegular = 0;
    private const int kPenalty = 1;
    private const int kOwnGoal = 2;

    // result.cpp:49-71 — GoalInfo (per-goal metadata in a scorer slot).
    public struct GoalInfo
    {
        public int Type;         // GoalType
        public byte TimeDigit1;  // hundreds
        public byte TimeDigit2;  // tens
        public byte TimeDigit3;  // ones

        public void Update(int goalType, int d1, int d2, int d3)
        {
            Type = goalType;
            TimeDigit1 = (byte)d1;
            TimeDigit2 = (byte)d2;
            TimeDigit3 = (byte)d3;
        }
    }

    // result.cpp:73-79 — ScorerInfo.
    public struct ScorerInfo
    {
        public int ShirtNum;        // 0 = empty slot
        public int NumGoals;
        public int NumLines;
        public GoalInfo[] Goals;    // length = kMaxGoalsPerScorer
    }

    // result.cpp:81-82 — m_team1Scorers / m_team2Scorers (8 slots each).
    private static readonly ScorerInfo[] m_team1Scorers = NewScorerArray();
    private static readonly ScorerInfo[] m_team2Scorers = NewScorerArray();

    private static ScorerInfo[] NewScorerArray()
    {
        var a = new ScorerInfo[kMaxScorersForDisplay];
        for (int i = 0; i < a.Length; i++) a[i].Goals = new GoalInfo[kMaxGoalsPerScorer];
        return a;
    }

    // result.cpp:85-86 — m_team1ScorerLines / m_team2ScorerLines. Pure text
    // — we keep them as managed string buffers and never render them through
    // Memory. (The original packs `kMaxScorersForDisplay * kCharactersPerLine`
    // bytes; we keep equivalent storage in C# arrays for parity.)
    private static readonly string[] m_team1ScorerLines = new string[kMaxScorersForDisplay];
    private static readonly string[] m_team2ScorerLines = new string[kMaxScorersForDisplay];

    // result.cpp:104-118 — resetResult.
    public static void ResetResult(string team1Name, string team2Name)
    {
        Memory.WriteWord(Memory.Addr.res_showResult, 0);

        for (int i = 0; i < kMaxScorersForDisplay; i++)
        {
            m_team1Scorers[i].ShirtNum = 0;
            m_team1Scorers[i].NumGoals = 0;
            m_team1Scorers[i].NumLines = 0;
            m_team2Scorers[i].ShirtNum = 0;
            m_team2Scorers[i].NumGoals = 0;
            m_team2Scorers[i].NumLines = 0;
            m_team1ScorerLines[i] = string.Empty;
            m_team2ScorerLines[i] = string.Empty;
        }

        // result.cpp:115-117 — team1NameLength comes from getStringPixelLength.
        // Stub: use character count as a proxy (renderer overrides anyway).
        Memory.WriteWord(Memory.Addr.res_team1NameLength,
            team1Name == null ? 0 : team1Name.Length);

        // result.cpp:115-116 — store name pointers. We can't dereference C#
        // strings as raw pointers; stash them in a side table indexed by the
        // res_team{1,2}Name address (treated as a handle).
        StashTeamNames(team1Name, team2Name);
    }

    // Internal side-table for team name strings (since Memory can't hold them).
    private static string m_team1NameCache = string.Empty;
    private static string m_team2NameCache = string.Empty;
    private static void StashTeamNames(string? t1, string? t2)
    {
        m_team1NameCache = t1 ?? string.Empty;
        m_team2NameCache = t2 ?? string.Empty;
    }
    public static string GetTeam1Name() => m_team1NameCache;
    public static string GetTeam2Name() => m_team2NameCache;

    // result.cpp:120-137 — updateResult.
    public static void UpdateResult()
    {
        int resultTimer = Memory.ReadSignedDword(Memory.Addr.resultTimer);

        if (resultTimer < 0)
        {
            HideResult();
            return;
        }

        if (resultTimer > 0)
        {
            // result.cpp:125-128 — sentinel-driven first-frame initialisation.
            if (resultTimer == kEndOfHalfResult ||
                resultTimer == kGameBreakResult ||
                resultTimer == kMaxResultTicks)
            {
                ResetResultTimer();
                Memory.WriteWord(Memory.Addr.res_showResult, 1);
                // After ResetResultTimer, refresh resultTimer.
                resultTimer = Memory.ReadSignedDword(Memory.Addr.resultTimer);
            }

            // result.cpp:130-135 — countdown.
            short lastFrameTicks = Memory.ReadSignedWord(Memory.Addr.lastFrameTicks);
            resultTimer -= lastFrameTicks;
            Memory.WriteDword(Memory.Addr.resultTimer, resultTimer);

            if (resultTimer <= 0)
            {
                // result.cpp:131-133 — at half time / full time, enqueue stats.
                short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
                if (gameState == kGameStateResultOnHalftime ||
                    gameState == kGameStateResultAfterTheGame)
                {
                    Memory.WriteWord(Memory.Addr.statsTimer, kMaxResultTicks);
                }
                HideResult();
            }
        }
    }

    // result.cpp:139-143 — hideResult.
    public static void HideResult()
    {
        Memory.WriteDword(Memory.Addr.resultTimer, 0);
        Memory.WriteWord(Memory.Addr.res_showResult, 0);
    }

    // result.cpp:145-156 — drawResult. Pure UI — Godot draws via the C# host.
    public static bool ShouldDrawResult()
        => Memory.ReadSignedWord(Memory.Addr.res_showResult) != 0;

    // result.cpp:158-210 — registerScorer.
    // Selects the scoring team (swapped for own goal), bumps that team's goal
    // counters, and writes the scorer + minute into the scorers list. Called
    // from UpdateGoals.GoalScored (which currently stubs this — wire when the
    // host migrates to the ported path).
    public static void RegisterScorer(int scorerSpriteAddr, int teamNum, int goalType)
    {
        // PORT-DIVERGENCE: the original reads topTeamPtr/bottomTeamPtr (the
        // TeamGame struct pointers). Our port never populates those (they stay
        // 0), and the rest of the port already resolves a scoring team's roster
        // through the topTeamInGame/bottomTeamInGame globals (players[0] base) —
        // see UpdateGoals.GoalScored:113-115 and the result-panel renderer in
        // Main.cs (team1 → topTeamInGame). Use the same globals here so the
        // shirt lookup below (base + (ordinal-1)*61) lands on the real
        // PlayerInfo record and the scorer list actually populates.
        int topTeamPtr    = Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
        int bottomTeamPtr = Memory.ReadSignedDword(Memory.Addr.bottomTeamInGame);
        if (topTeamPtr == 0 || bottomTeamPtr == 0) return;

        ScorerInfo[] scorers;
        int scoringTeamPtr, concedingTeamPtr;

        if (teamNum == 1)
        {
            scorers          = m_team1Scorers;
            scoringTeamPtr   = topTeamPtr;
            concedingTeamPtr = bottomTeamPtr;
        }
        else
        {
            scorers          = m_team2Scorers;
            scoringTeamPtr   = bottomTeamPtr;
            concedingTeamPtr = topTeamPtr;
        }

        // result.cpp:175-176 — player = scoringTeam->players[scorer.playerOrdinal-1].
        short playerOrdinal = scorerSpriteAddr == 0
            ? (short)1
            : Memory.ReadSignedWord(scorerSpriteAddr + PlayerSprite.OffPlayerOrdinal);
        int playerInfoAddr = scoringTeamPtr + (playerOrdinal - 1) * 61;
        int shirtNum = ReadPlayerShirtNumber(playerInfoAddr);

        if (goalType == kOwnGoal)
        {
            // result.cpp:178-182 — swap teams + bump conceding-team's numOwnGoals.
            (scoringTeamPtr, concedingTeamPtr) = (concedingTeamPtr, scoringTeamPtr);
            BumpNumOwnGoals(concedingTeamPtr);
            shirtNum += 1000;
        }
        else
        {
            // result.cpp:184 — player.goalsScored++.
            BumpGoalsScored(playerInfoAddr);
        }

        // result.cpp:187-209 — find slot + append goal.
        int currentSlot = 0;
        int currentLine = 0;
        while (currentSlot < kMaxScorersForDisplay && currentLine < kMaxScorersForDisplay)
        {
            ref ScorerInfo info = ref scorers[currentSlot];
            if (info.ShirtNum == 0 || info.ShirtNum == shirtNum)
            {
                if (info.ShirtNum == 0)
                {
                    info.NumGoals = 0;
                    info.NumLines = 1;
                }
                info.ShirtNum = shirtNum;
                if (info.NumGoals != kMaxGoalsPerScorer)
                {
                    // result.cpp:200-202.
                    var (d1, d2, d3) = GameTime.GameTimeAsBcd();
                    info.Goals[info.NumGoals].Update(goalType, d1, d2, d3);
                    info.NumGoals++;
                    MarkScorersTextDirty(scorerSpriteAddr, scoringTeamPtr, teamNum, currentSlot, currentLine);
                }
                break;
            }
            else
            {
                currentSlot++;
                currentLine += info.NumLines;
            }
        }
    }

    // result.cpp:212-227 — resetResultTimer.
    private static void ResetResultTimer()
    {
        int t = Memory.ReadSignedDword(Memory.Addr.resultTimer);
        int newT = t;
        switch (t)
        {
            case kEndOfHalfResult:  newT = kResultAtHalfTimeLength;  break;
            case kGameBreakResult:  newT = kResultAtGameBreakLength; break;
            case kMaxResultTicks:   newT = kResultTickClamped;       break;
        }
        Memory.WriteDword(Memory.Addr.resultTimer, newT);
    }

    // ---- Accessors for the host renderer (Godot UI side) -------------------
    public static ScorerInfo[] GetTeam1Scorers() => m_team1Scorers;
    public static ScorerInfo[] GetTeam2Scorers() => m_team2Scorers;
    public static string[] GetTeam1ScorerLines() => m_team1ScorerLines;
    public static string[] GetTeam2ScorerLines() => m_team2ScorerLines;

    // ---- Stubs -------------------------------------------------------------

    // PlayerInfo struct field offsets — mechanical port from
    // external/swos-port/src/swos/swos.h:162-208. PlayerInfo is 61 bytes.
    //   +0  substituted (byte)
    //   +1  index (byte)
    //   +2  goalsScored (byte)
    //   +3  shirtNumber (byte)
    private const int kPlayerInfoOffGoalsScored = 2;
    private const int kPlayerInfoOffShirtNumber = 3;

    // PlayerInfo.shirtNumber accessor (swos.h:167).
    private static int ReadPlayerShirtNumber(int playerInfoAddr)
        => playerInfoAddr == 0 ? 1 : Memory.ReadByte(playerInfoAddr + kPlayerInfoOffShirtNumber);

    // TeamGame.numOwnGoals — used to credit conceding team on own goal.
    // Struct layout: external/swos-port/src/swos/swos.h:296-315 (TeamGame). The
    // numOwnGoals byte sits at offset +30 (10×word prShirt* + word markedPlayer
    // (10) + char teamName[17] (10..26) + unk_1 (27) + numOwnGoals (28) — wait,
    // recount: 5 prShirt words (10) + 5 secShirt words (10) → +20; markedPlayer
    // word → +22; teamName[17] → +39; unk_1 byte → +40; numOwnGoals → +41. The
    // canonical answer from swos.h:296-311:
    //   prShirtType.prShirtCol.prStripesCol.prShortsCol.prSocksCol (5×word=10)
    //   secShirtType.secShirtCol.secStripesCol.secShortsCol.secSocksCol (5×word=10)
    //   markedPlayer (word, +20)
    //   teamName[17] (+22..+38)
    //   unk_1 (byte, +39)
    //   numOwnGoals (byte, +40)
    // Source: external/swos-port/src/game/result.cpp:180 (concedingTeam->numOwnGoals++).
    private const int kTeamGameOffNumOwnGoals = 40;
    private static void BumpNumOwnGoals(int teamPtr)
    {
        // PORT-DIVERGENCE: `teamPtr` here is a topTeamInGame/bottomTeamInGame
        // base, which in our port points at players[0] (a PlayerInfo array),
        // NOT the full TeamGame struct the original's numOwnGoals field lives
        // in. Writing +40 would clobber player 0 (the keeper)'s fullName. The
        // numOwnGoals tally has NO reader anywhere in the ported scope (the
        // scorer display resolves own goals via the shirt+1000 tag, not this
        // counter), so skip the write rather than corrupt the roster. Restore a
        // real write only once the TeamGame header is modelled.
        _ = teamPtr;
        _ = kTeamGameOffNumOwnGoals;
    }

    // PlayerInfo.goalsScored bump (swos.h:166).
    // Per-player goals scored counter. Used by stats display (no other readers).
    private static void BumpGoalsScored(int playerInfoAddr)
    {
        if (playerInfoAddr == 0) return;
        byte g = Memory.ReadByte(playerInfoAddr + kPlayerInfoOffGoalsScored);
        Memory.WriteByte(playerInfoAddr + kPlayerInfoOffGoalsScored, (byte)(g + 1));
    }

    // Partial mechanical port of result.cpp:363-429 — updateScorersText. The
    // bulk of the original (string composition of "Smith 14, 67, 89") is
    // pure text rendering — Godot iterates m_team{1,2}Scorers at draw time
    // to build the same strings. We DO need to signal the renderer that the
    // scorer set just changed; the C++ does this implicitly by directly
    // re-rendering the line. Our cross-layer equivalent is a dirty flag.
    //
    // Sets Memory.Addr.res_scorersDirtyFlag = 1 to tell the host renderer
    // "scorer list changed this tick, re-build the displayed lines". The
    // renderer clears it after consumption — that's the contract we need
    // for the Godot host to detect "a new goal was registered" without
    // polling the full scorer arrays.
    //
    // Source: external/swos-port/src/game/result.cpp:363-429 (full text
    // composition); we port only the side-effect-on-state-change signal.
    private static void MarkScorersTextDirty(int scorerSpriteAddr, int teamPtr,
                                              int teamNum, int slot, int line)
    {
        // result.cpp:202 — call site is the tail of registerScorer after the
        // goal is committed to the scorer's slot. Latch the dirty flag so the
        // host renderer can pick up the change on its next paint cycle.
        Memory.WriteWord(Memory.Addr.res_scorersDirtyFlag, 1);
    }
}
