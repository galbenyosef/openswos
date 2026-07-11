namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Match statistics collection ported from external/swos-port/src/game/stats.cpp.
//
// Stats counters live in two 14-byte TeamStatsData records (one per team,
// referenced by TeamData.teamStatsPtr → Memory.Addr.{top,bottom}TeamStatsData).
//
// Per-tick the updater bumps:
//   - ballPossession: every tick whichever team last touched the ball.
//   - goalAttempts / onTarget: when the ball moves into the keeper area, the
//     defender's team gets a goalAttempt (and onTarget if strikeDestX is
//     between kGoalLeft and kGoalRight).
//   - m_isGoalAttempt flag latches an in-flight attempt until the ball
//     reverses direction (deltaY) OR the attacking team regains possession.
//
// DRAW + UI side of stats (drawStats, drawDarkRectangles, drawStatsText) is
// pure render — Godot draws our own UI. We port the data path only.
public static class Stats
{
    // stats.cpp:8-10 — drawing constants (kept for symmetry; UI defers to Godot).
    public const int kLeftColumn   = 80;
    public const int kMiddleColumn = 160;
    public const int kRightColumn  = 240;

    // stats.cpp:12-14.
    public const int kTeamNamesY    = 43;
    public const int kGoalsY        = 61;
    public const int kGoalAttempsY  = 97;

    // stats.cpp:16.
    public const int kLineSpacing = 18;

    // pitchConstants.h:3-4, 6-10.
    public const int kPitchCenterY     = 449;
    public const int kGoalAttemptLeft  = 240;
    public const int kGoalAttemptRight = 431;
    public const int kGoalLeft         = 303;
    public const int kGoalRight        = 367;

    // swos.h:592 — kInProgress.
    private const short kGameStateInProgress = 100;

    // ---- TeamStatsData (swos.h:212-221) — 7 word fields ---------------------
    public const int OffBallPossession = 0;
    public const int OffCornersWon     = 2;
    public const int OffFoulsConceded  = 4;
    public const int OffBookings       = 6;
    public const int OffSendingsOff    = 8;
    public const int OffGoalAttempts   = 10;
    public const int OffOnTarget       = 12;

    // stats.cpp:29-33 — initStats.
    public static void InitStats()
    {
        Memory.WriteWord(Memory.Addr.st_isGoalAttempt, 0);
        Memory.WriteWord(Memory.Addr.st_showStats, 0);
    }

    // stats.cpp:35-44 — toggleStats.
    // Called when the user requests the stats overlay. If already showing the
    // user-requested overlay, hide it; else enqueue with a 1-tick timer + hide
    // result screen.
    public static void ToggleStats()
    {
        if (Memory.ReadSignedWord(Memory.Addr.st_showingUserRequestedStats) != 0)
        {
            HideStats();
        }
        else
        {
            Memory.WriteWord(Memory.Addr.st_showStats, 1);
            Memory.WriteWord(Memory.Addr.statsTimer, 1);
            // stats.cpp:43 — hideResult(). Result class is now ported; wire
            // through. Mechanical port from external/swos-port/src/game/result.cpp:139.
            Result.HideResult();
        }
    }

    // stats.cpp:46-51 — hideStats.
    public static void HideStats()
    {
        Memory.WriteWord(Memory.Addr.st_showStats, 0);
        Memory.WriteWord(Memory.Addr.st_showingUserRequestedStats, 0);
        Memory.WriteWord(Memory.Addr.statsTimer, 0);
    }

    // stats.cpp:53-56.
    public static bool StatsEnqueued()
        => Memory.ReadSignedWord(Memory.Addr.st_showStats) != 0;

    // stats.cpp:58-67 — showingUserRequestedStats.
    // Transitions m_showStats → m_showingUserRequestedStats on first call. Returns
    // whether the user-requested overlay is currently up.
    public static bool ShowingUserRequestedStats()
    {
        if (Memory.ReadSignedWord(Memory.Addr.st_showStats) != 0)
        {
            Memory.WriteWord(Memory.Addr.st_showStats, 0);
            Memory.WriteWord(Memory.Addr.st_showingUserRequestedStats, 1);
            Memory.WriteWord(Memory.Addr.statsTimer, 1);
        }

        return Memory.ReadSignedWord(Memory.Addr.st_showingUserRequestedStats) != 0;
    }

    // stats.cpp:69-72 — showingPostGameStats.
    public static bool ShowingPostGameStats()
        => Memory.ReadSignedWord(Memory.Addr.statsTimer) > 0;

    // stats.cpp:74-112 — updateStatistics.
    // Main per-tick entry. Bumps possession + tracks goal attempts. Always
    // tail-calls CheckStatsTimer to auto-hide when timer goes negative.
    public static void UpdateStatistics()
    {
        bool showingUserStats = Memory.ReadSignedWord(Memory.Addr.st_showingUserRequestedStats) != 0;
        bool playingPenalties = Memory.ReadWord(Memory.Addr.playingPenalties) != 0;

        if (!showingUserStats && !playingPenalties)
        {
            short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gameStatePl == kGameStateInProgress)
            {
                // stats.cpp:78-80 — bump possession for lastTeamPlayed.
                int lastTeam = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
                if (lastTeam == TeamData.TopBase || lastTeam == TeamData.BottomBase)
                {
                    int statsPtr = Memory.ReadSignedDword(lastTeam + TeamData.OffTeamStatsPtr);
                    if (statsPtr != 0)
                    {
                        int possession = Memory.ReadWord(statsPtr + OffBallPossession);
                        Memory.WriteWord(statsPtr + OffBallPossession, possession + 1);
                    }
                }

                // stats.cpp:81-83 — start with top team, adjust per ball Y.
                int teamData = TeamData.TopBase;
                int statsAddr = Memory.ReadSignedDword(teamData + TeamData.OffTeamStatsPtr);
                int ballDelta = Memory.ReadSignedDword(BallSprite.Base + BallSprite.OffDeltaY);

                // stats.cpp:86, 93 — compares ball y (FixedPoint Q16.16) to int
                // kPitchCenterY. FixedPoint::operator>=(int) is whole() >= value
                // (so `m_value >= (value << 16)` equivalently), but operator<=(int)
                // is `!(whole > value || (whole == value && fraction))` — i.e.
                // strict "y is at-or-above center, fraction zero". We mirror that
                // by comparing the FULL Q16.16 dword to (kPitchCenterY << 16);
                // reading just the high word would round y=449.001 down to 449
                // and flip the side. Source: external/swos-port/src/util/FixedPoint.h:73-90.
                int ballYRaw = Memory.ReadSignedDword(BallSprite.Base + BallSprite.OffY);
                const int kPitchCenterYFp = kPitchCenterY << 16;

                bool isGoalAttempt = Memory.ReadSignedWord(Memory.Addr.st_isGoalAttempt) != 0;
                if (isGoalAttempt)
                {
                    // stats.cpp:85-91 — end-of-attempt check.
                    if (ballYRaw >= kPitchCenterYFp)
                    {
                        teamData = TeamData.BottomBase;
                        ballDelta = -ballDelta;
                    }
                    if (teamData == lastTeam || ballDelta >= 0)
                        Memory.WriteWord(Memory.Addr.st_isGoalAttempt, 0);
                }
                else if (Memory.ReadSignedWord(Memory.Addr.ballInGoalkeeperArea) != 0)
                {
                    // stats.cpp:92-104 — ball entered keeper area; maybe register attempt.
                    if (ballYRaw <= kPitchCenterYFp)
                    {
                        teamData = TeamData.BottomBase;
                        ballDelta = -ballDelta;
                        statsAddr = Memory.ReadSignedDword(teamData + TeamData.OffTeamStatsPtr);
                    }

                    bool playerHasBall = Memory.ReadSignedWord(teamData + TeamData.OffPlayerHasBall) != 0;
                    short strikeDestX = Memory.ReadSignedWord(Memory.Addr.strikeDestX);

                    if (ballDelta > 0 && teamData == lastTeam && !playerHasBall &&
                        strikeDestX >= kGoalAttemptLeft && strikeDestX <= kGoalAttemptRight)
                    {
                        if (statsAddr != 0)
                        {
                            if (strikeDestX >= kGoalLeft && strikeDestX <= kGoalRight)
                            {
                                int onTarget = Memory.ReadWord(statsAddr + OffOnTarget);
                                Memory.WriteWord(statsAddr + OffOnTarget, onTarget + 1);
                            }
                            int attempts = Memory.ReadWord(statsAddr + OffGoalAttempts);
                            Memory.WriteWord(statsAddr + OffGoalAttempts, attempts + 1);
                        }
                        Memory.WriteWord(Memory.Addr.st_isGoalAttempt, 1);
                    }
                }
            }
            else
            {
                // stats.cpp:107 — game not in progress → drop attempt latch.
                Memory.WriteWord(Memory.Addr.st_isGoalAttempt, 0);
            }
        }

        CheckStatsTimer();
    }

    // stats.cpp:252-256 — checkStatsTimer.
    private static void CheckStatsTimer()
    {
        if (Memory.ReadSignedWord(Memory.Addr.statsTimer) < 0)
            HideStats();
    }

    // stats.cpp:114-125 — getStats.
    // Returns a copy of both teams' TeamStatsData (resolving which side maps to
    // which team via the topTeamInGame check). We expose as a struct copy.
    public struct TeamStatsCopy
    {
        public ushort BallPossession;
        public ushort CornersWon;
        public ushort FoulsConceded;
        public ushort Bookings;
        public ushort SendingsOff;
        public ushort GoalAttempts;
        public ushort OnTarget;
    }

    public struct GameStats
    {
        public TeamStatsCopy Team1;
        public TeamStatsCopy Team2;
    }

    public static GameStats GetStats()
    {
        (int leftPtr, int rightPtr) = GetTeamStatsPointers();
        return new GameStats
        {
            Team1 = ReadTeamStats(leftPtr),
            Team2 = ReadTeamStats(rightPtr),
        };
    }

    private static TeamStatsCopy ReadTeamStats(int statsAddr)
    {
        if (statsAddr == 0) return default;
        return new TeamStatsCopy
        {
            BallPossession = Memory.ReadWord(statsAddr + OffBallPossession),
            CornersWon     = Memory.ReadWord(statsAddr + OffCornersWon),
            FoulsConceded  = Memory.ReadWord(statsAddr + OffFoulsConceded),
            Bookings       = Memory.ReadWord(statsAddr + OffBookings),
            SendingsOff    = Memory.ReadWord(statsAddr + OffSendingsOff),
            GoalAttempts   = Memory.ReadWord(statsAddr + OffGoalAttempts),
            OnTarget       = Memory.ReadWord(statsAddr + OffOnTarget),
        };
    }

    // stats.cpp:166-175 — getTeamStatsPointers.
    // Resolves which TeamStatsData is "left" (team 1) vs "right" (team 2)
    // based on whether topTeamData.inGameTeamPtr matches swos.topTeamInGame.
    private static (int, int) GetTeamStatsPointers()
    {
        int leftTeam  = TeamData.TopBase;
        int rightTeam = TeamData.BottomBase;

        int topInGameTeam = Memory.ReadSignedDword(TeamData.TopBase + TeamData.OffInGameTeamPtr);
        int swosTopInGame = Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
        if (topInGameTeam != swosTopInGame)
        {
            // Swap.
            int tmp = leftTeam; leftTeam = rightTeam; rightTeam = tmp;
        }

        int leftStats  = Memory.ReadSignedDword(leftTeam  + TeamData.OffTeamStatsPtr);
        int rightStats = Memory.ReadSignedDword(rightTeam + TeamData.OffTeamStatsPtr);
        return (leftStats, rightStats);
    }

    // stats.cpp:134-138 — drawStatsIfNeeded. Pure UI — Godot owns the canvas.
    // Stubbed: caller can use ShowingPostGameStats / ShowingUserRequestedStats
    // to know whether to draw.
    public static void DrawStatsIfNeeded()
    {
        // TODO from external/swos-port/src/game/stats.cpp:drawStatsIfNeeded
        // (skip — Godot draws this via the C# host layer).
    }
}
