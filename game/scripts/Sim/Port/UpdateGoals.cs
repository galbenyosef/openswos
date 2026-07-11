namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Goal scoring logic ported from external/swos-port/src/game/updateGoals.cpp.
// Triggered when ball crosses goal line — bumps team's goal counter, updates
// scoreline digits + stats, and registers scorer with goal type (regular /
// penalty / own goal). Cap is 99 goals per team (kMaxGoals).
//
// Source: external/swos-port/src/game/updateGoals.cpp (59 lines C++).
//
// Field semantics:
//   - teamN(Total|Penalty)Goals: cumulative match goal count for team N.
//     totalGoals always increments; penaltyGoals only increments when
//     playingPenalties != 0.
//   - teamNGoalsDigit{1,2}: scoreline display digits ('xy' rendered separately).
//   - statsTeamNGoals: per-team stats counter (also bumped).
//   - goalScored / runSlower: per-frame event flags read by gameLoop.
//   - lastTeamScored / lastPlayerScored / currentScorer: stats overlay.
//   - goalTypeScored: integer mirror of GoalType enum (0=regular, 1=penalty,
//     2=own goal) — also written to swos.goalTypeScored.
//
// `RegisterScorer` (from result.cpp) is now wired through Result.RegisterScorer
// (mechanical port at result.cpp:158-210, called from GoalScored below). The
// display list it populates is consumed by the host renderer.
public static class UpdateGoals
{
    private const int kMaxGoals = 99;

    // GoalType enum mirror (result.h:3-7).
    public enum GoalType
    {
        kRegular = 0,
        kPenalty = 1,
        kOwnGoal = 2,
    }

    // updateGoals.cpp:3-33 — bumpTeamGoals.
    // Updates team total + penalty + scoreline digits + stats counter for the
    // scoring team. Returns false if the team has already hit the 99-goal cap
    // (caller skips RegisterScorer in that case).
    //
    // Original uses a `static word *goalVars[2][2]` table indexed by
    // (playingPenalties, isSecondTeam) to select between total/penalty counter.
    // We expand into explicit branches.
    public static bool BumpTeamGoals(int teamNum)
    {
        bool isSecondTeam = teamNum == 2;
        bool playingPenalties = Memory.ReadWord(Memory.Addr.playingPenalties) != 0;

        // updateGoals.cpp:14-15 — cap check uses totalGoals[isSecondTeam].
        int totalAddr = isSecondTeam ? Memory.Addr.team2TotalGoals : Memory.Addr.team1TotalGoals;
        if (Memory.ReadWord(totalAddr) == kMaxGoals)
            return false;

        // updateGoals.cpp:17 — ++*goalVars[playingPenalties][isSecondTeam].
        int bumpAddr;
        if (playingPenalties)
            bumpAddr = isSecondTeam ? Memory.Addr.team2PenaltyGoals : Memory.Addr.team1PenaltyGoals;
        else
            bumpAddr = isSecondTeam ? Memory.Addr.team2TotalGoals : Memory.Addr.team1TotalGoals;

        Memory.WriteWord(bumpAddr, Memory.ReadWord(bumpAddr) + 1);

        // updateGoals.cpp:19-22 — kDigitsAndStats[isSecondTeam] gives
        // {digit1, digit2, statsGoals} for the scoring team.
        int digit1Addr = isSecondTeam ? Memory.Addr.team2GoalsDigit1   : Memory.Addr.team1GoalsDigit1;
        int digit2Addr = isSecondTeam ? Memory.Addr.team2GoalsDigit2   : Memory.Addr.team1GoalsDigit2;
        int statsAddr  = isSecondTeam ? Memory.Addr.statsTeam2Goals    : Memory.Addr.statsTeam1Goals;

        // updateGoals.cpp:26-29 — ++digit2; if hits 10, carry to digit1 + clear.
        int d2 = Memory.ReadWord(digit2Addr) + 1;
        if (d2 == 10)
        {
            Memory.WriteWord(digit1Addr, Memory.ReadWord(digit1Addr) + 1);
            d2 = 0;
        }
        Memory.WriteWord(digit2Addr, d2);

        // updateGoals.cpp:30 — ++statsGoals.
        Memory.WriteWord(statsAddr, Memory.ReadWord(statsAddr) + 1);

        return true;
    }

    // updateGoals.cpp:35-64 — goalScored.
    // teamNum = 1 or 2 (which team's net was hit). scorerSlot is the player
    // sprite slot whose foot/head touched the ball last (may be different team
    // for an own goal).
    //
    // Sets per-frame goal event flags, picks last-scoring team based on the
    // scorer's team number, calls BumpTeamGoals, then determines goal type:
    //   - Regular by default
    //   - OwnGoal if scorer.teamNumber != teamNum (i.e. scored against own net)
    //   - Penalty if swos.penalty flag is set and not an own goal
    // Calls Result.RegisterScorer when not a penalty-shootout goal.
    public static void GoalScored(int teamNum, int scorerSlot)
    {
        // updateGoals.cpp:39-43 — per-frame flag block.
        Memory.WriteWord(Memory.Addr.goalScored, 1);
        Memory.WriteWord(Memory.Addr.runSlower, 1);
        Memory.WriteWord(Memory.Addr.lastTeamScoredNumber, teamNum);
        Memory.WriteDword(Memory.Addr.lastPlayerScored, PlayerSprite.Base(scorerSlot));
        Memory.WriteDword(Memory.Addr.currentScorer, PlayerSprite.Base(scorerSlot));

        // updateGoals.cpp:45-46 — match scorer's team sprite to top/bottom
        // teamData. Original code:
        //     auto teamGame = scorer->teamNumber == 1 ? &topTeamInGame : &bottomTeamInGame;
        //     auto team = topTeamData.inGameTeamPtr == teamGame ? &topTeamData : &bottomTeamData;
        // We collapse the same logic: if scorer.teamNumber == 1, look at top
        // teamData's inGameTeamPtr; pick whichever side matches.
        short scorerTeamNum = PlayerSprite.TeamNumber(scorerSlot);
        int scorerTeamGame = scorerTeamNum == 1
            ? Memory.ReadSignedDword(Memory.Addr.topTeamInGame)
            : Memory.ReadSignedDword(Memory.Addr.bottomTeamInGame);

        int topInGamePtr = Memory.ReadSignedDword(TeamData.TopBase + TeamData.OffInGameTeamPtr);
        int teamPtr = topInGamePtr == scorerTeamGame ? TeamData.TopBase : TeamData.BottomBase;

        // updateGoals.cpp:48.
        Memory.WriteDword(Memory.Addr.lastTeamScored, teamPtr);

        // updateGoals.cpp:50 — early-out on goal cap OR penalty-shootout goal.
        bool playingPenalties = Memory.ReadWord(Memory.Addr.playingPenalties) != 0;
        if (!BumpTeamGoals(teamNum) || playingPenalties)
            return;

        // updateGoals.cpp:53-61 — pick goal type (default regular; own goal if
        // scorer's team != scoring team; penalty if swos.penalty set).
        GoalType goalType = GoalType.kRegular;
        Memory.WriteDword(Memory.Addr.goalTypeScored, (int)GoalType.kRegular);

        if (scorerTeamNum != teamNum)
        {
            goalType = GoalType.kOwnGoal;
            Memory.WriteDword(Memory.Addr.goalTypeScored, (int)GoalType.kOwnGoal);
        }
        else if (Memory.ReadWord(Memory.Addr.penalty) != 0)
        {
            goalType = GoalType.kPenalty;
        }

        // updateGoals.cpp:63 — registerScorer appends scorer name + minute to
        // the result sprite list, bumps the scoring player's goalsScored count,
        // and increments numOwnGoals on the conceding team for own goals.
        // Driven by Result.RegisterScorer (already ported, result.cpp:158-210).
        // Mechanical wire-through:
        //   - PlayerSprite.Base(scorerSlot) gives the sprite address used by
        //     updateGoals.cpp:39 (lastPlayerScored).
        //   - teamNum is 1/2 same as ours.
        // Source: external/swos-port/src/game/result.cpp:158.
        Result.RegisterScorer(PlayerSprite.Base(scorerSlot), teamNum, (int)goalType);

        // NOTE (2026-07-02): the previous port-only PL_HAPPY / PL_SAD sweep
        // that ran here after every goal was DELETED. The original writes
        // PL_HAPPY (15) / PL_SAD (14) ONLY at match end — updatePlayers.cpp:
        // 8706-8804, inside the `gameState == ST_GAME_ENDED (30)` arm gated
        // on `winningTeamPtr` — players walk back to the kickoff in PL_NORMAL
        // after a goal. On-pitch goal celebration is the frame-overlay trick
        // in SpriteUpdate (offset +24 cheer frames driven by `goalScored`),
        // not a PlayerState change.
    }

    // (UpdatePostGoalRestart — the port-only goal → kickoff collapse — was
    //  DELETED 2026-07-02. The faithful sequence is now:
    //    1. ball.cpp:3432-3440 (BallOutOfPlay goal tail): gameState=0,
    //       breakCameraMode=-1, goalCounter armed, l_break_handled sets
    //       gameStatePl=101.
    //    2. comments.cpp:206-207 (GameLoop.PlayEnqueuedSamples): goalCounter--.
    //    3. gameLoop.cpp:845-1108 dispatcher copies gameState → gameStatePl
    //       and arms the breakCameraMode 0→8 ladder (GameLoop.cs), which walks
    //       the players back and ends at gameStatePl=102.
    //    4. gameLoop.cpp:519-575: AI/human kick (updatePlayers) or the
    //       825-tick prepareForInitialKick fallback resumes play.)
}
