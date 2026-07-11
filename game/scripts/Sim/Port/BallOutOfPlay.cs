namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Mechanical port of `checkIfBallOutOfPlay` and helpers from
// external/swos-port/src/game/ball/ball.cpp:3007-4020.
//
// Called by updateBall after the per-tick physics step. Three outcomes:
//   1. Goal scored — bumps stats, set state to ST_PLAYERS_TO_INITIAL_POSITIONS,
//      writes camera/audio cue state, falls through to break_handled.
//   2. Ball out of play (corner / throw-in / goal-out / near miss) — set
//      appropriate gameState / foul coords / camera, falls through to
//      break_handled.
//   3. Ball still in play — no-op (state already pristine).
//
// At l_starting_the_game (the function tail), playRefereeWhistle is checked
// and the whistle sample is queued. The original early-exits to this tail
// when gameStatePl == ST_STOPPED (101) — i.e. on the FIRST tick after a
// previous stoppage, when we already set state to "stopped". This skips the
// ball-out-of-bounds detection but still keeps the whistle fresh.
//
// Pitch coordinate constants used here (whole pixels, top-left origin):
//   Upper goal mouth Y range:  Z <= 15
//   Upper goal X range:        302 <= X <= 366
//   Y < 449 = upper half  /  Y > 449 = lower half
//   Y >= 449 == lower goal branch
//   Lower goal-line Y > 769 = goal-out region
//   D2 < 129 = upper corner range (above upper goal line + bias)
//   D1 < 336 = left half / D1 >= 336 = right half
//
// License: PARAPHRASE algorithms; constants/coords copied verbatim with
// `// from ball.cpp:NNN` cites for traceability.
public static class BallOutOfPlay
{
    // Sprite addresses from swos.asm:
    //   goalie1Sprite  = 326524
    //   goalie2Sprite  = 327756
    //   topTeamData    = 522792
    //   bottomTeamData = 522940
    // In our port, those map to:
    //   PlayerSprite.Base(SlotGoalie1)
    //   PlayerSprite.Base(SlotGoalie2)
    //   TeamData.TopBase / TeamData.BottomBase

    // Game state constants (swos.h GameState enum).
    private const short ST_GAME_IN_PROGRESS = 100;
    private const short ST_STOPPED          = 101;
    private const short ST_GOAL_OUT_LEFT    = 1;
    private const short ST_GOAL_OUT_RIGHT   = 2;
    private const short ST_KEEPER_HOLDS_BALL = 3;  // unused here
    private const short ST_CORNER_LEFT      = 4;
    private const short ST_CORNER_RIGHT     = 5;
    private const short ST_PLAYERS_TO_INITIAL_POSITIONS = 0;
    private const short ST_THROW_IN_FORWARD_RIGHT = 15;
    private const short ST_THROW_IN_CENTER_RIGHT  = 16;
    private const short ST_THROW_IN_BACK_RIGHT    = 17;
    private const short ST_THROW_IN_FORWARD_LEFT  = 18;
    private const short ST_THROW_IN_CENTER_LEFT   = 19;
    private const short ST_THROW_IN_BACK_LEFT     = 20;

    // GoalType (result.h:3-7).
    private const short GT_REGULAR = 0;
    private const short GT_PENALTY = 1;
    private const short GT_OWN_GOAL = 2;

    // from ball.cpp:3007-4020 — checkIfBallOutOfPlay.
    //
    // The original uses several local register-like state pieces:
    //   D1 / D2 / D3 = ball X / Y / Z whole pixels (with some adjustments)
    //   D4 (byte)    = playerTurnFlags packed value
    //   A6           = active TeamData pointer (top or bottom)
    //
    // We use plain int locals here. Behaviour is preserved including the
    // jumps via `goto`; C# allows them with explicit labels.
    public static void CheckIfBallOutOfPlay()
    {
        // from ball.cpp:3009-3010 — clear stateGoal + whistle flag.
        Memory.WriteWord(Memory.Addr.stateGoal, 0);
        Memory.WriteWord(Memory.Addr.playRefereeWhistle, 0);

        // from ball.cpp:3012-3022 — short-circuit on ST_STOPPED.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl == ST_STOPPED)
        {
            goto l_starting_the_game;
        }

        // from ball.cpp:3024 — speculatively queue whistle (cleared again on goal).
        Memory.WriteWord(Memory.Addr.playRefereeWhistle, 1);

        // from ball.cpp:3025-3032 — read ball x/y/z whole pixels.
        int D1 = BallSprite.XPixels;
        int D2 = BallSprite.YPixels;
        int D3 = BallSprite.ZPixels;

        // from ball.cpp:3033-3038 — sub D1, 1 (offset by 1 px for goal X check).
        D1 = (short)(D1 - 1);

        // A6 (active TeamData) variable scoped across the goto chain.
        int A6 = 0;
        int D4 = 0;
        int D3v = 0;

        // from ball.cpp:3040-3049 — cmp D3, 15 / jg @@not_in_goal_check_near_miss.
        if (D3 > 15) goto l_not_in_goal_check_near_miss;

        // from ball.cpp:3051-3061 — cmp D1, 302 / jl @@not_in_goal_check_near_miss.
        if (D1 < 302) goto l_not_in_goal_check_near_miss;

        // from ball.cpp:3063-3073 — cmp D1, 366 / jg @@not_in_goal_check_near_miss.
        if (D1 > 366) goto l_not_in_goal_check_near_miss;

        // from ball.cpp:3075-3085 — cmp D2, 449 / jg @@lower_goal.
        if (D2 > 449) goto l_lower_goal;

        // from ball.cpp:3087-3100 — upper goal: ball entered the top-pitch net.
        // If bottomTeamData.teamNumber == 1 → team1_scored.
        A6 = TeamData.BottomBase;
        if (Memory.ReadSignedWord(A6 + TeamData.OffTeamNumber) == 1)
            goto l_team1_scored;
        // fall through to team2_scored

    l_team2_scored:
        // from ball.cpp:3102-3152.
        HandleGoalScoredTeam(2, A6);
        goto l_goal_handled;

    l_lower_goal:
        // from ball.cpp:3154-3168 — lower goal: ball entered the bottom-pitch net.
        // If topTeamData.teamNumber == 2 → team2_scored.
        A6 = TeamData.TopBase;
        if (Memory.ReadSignedWord(A6 + TeamData.OffTeamNumber) == 2)
            goto l_team2_scored;
        // fall through to team1_scored

    l_team1_scored:
        // from ball.cpp:3170-3219.
        HandleGoalScoredTeam(1, A6);
        goto l_goal_handled;

    l_goal_handled:
        // from ball.cpp:3221-3239.
        // The HandleGoalScoredTeam helper has written teamNumThatScored,
        // teamScoredDataPtr (= scoring team) and teamScoredGamePtr.
        A6 = Memory.ReadSignedDword(Memory.Addr.teamScoredDataPtr);

        // from ball.cpp:3227-3228 — `mov eax, [esi] ; mov A6, eax`: A6 is
        // RELOADED from the scoring team's opponentsTeam pointer (offset 0) —
        // from here on A6 is the CONCEDING team. Critically, l_break_handled
        // below stores this A6 into lastTeamPlayedBeforeBreak, so the
        // conceding team takes the restart kickoff.
        A6 = Memory.ReadSignedDword(A6 + TeamData.OffOpponentsTeam);

        // from ball.cpp:3229-3247 — `cmp A6, offset topTeamData` picks the
        // goal-comment style + turn flags (D3 / D4).
        if (A6 == TeamData.TopBase)
        {
            // from ball.cpp:3245-3247 — cseg_7D4DD path.
            D3v = 4;
            D4 = 124;
        }
        else
        {
            // from ball.cpp:3241-3243.
            D3v = 0;
            D4 = 199;
        }

        // from ball.cpp:3249-3267 — goalTypeScored switch (own-goal vs regular).
        {
            int goalType = Memory.ReadSignedDword(Memory.Addr.goalTypeScored);
            if (goalType == GT_OWN_GOAL)
            {
                StubPlayOwnGoalComment();
            }
            else
            {
                StubPlayGoalComment();
            }
        }

        // from ball.cpp:3269-3287 — teamNumThatScored: 2 = guests (away),
        // else home sample.
        if (Memory.ReadSignedWord(Memory.Addr.teamNumThatScored) == 2)
            StubPlayAwayGoalSample();
        else
            StubPlayHomeGoalSample();

        // from ball.cpp:3289-3291 — set_goal_state.
        Memory.WriteWord(Memory.Addr.stateGoal, -1);
        Memory.WriteWord(Memory.Addr.playRefereeWhistle, 0);

        // from ball.cpp:3292-3304 — D1 = (rand() >> 1) + 100. Used as a base
        // goalCounter value, then possibly overridden by score-difference branch.
        int D1_goal = (SwosRand() >> 1) + 100;

        // from ball.cpp:3305-3311 — if playingPenalties != 0, skip to penalty_scored.
        bool playingPenalties = Memory.ReadWord(Memory.Addr.playingPenalties) != 0;
        if (!playingPenalties)
        {
            // from ball.cpp:3313-3325 — D0 = statsTeam1Goals - statsTeam2Goals.
            int statsT1 = Memory.ReadSignedWord(Memory.Addr.statsTeam1Goals);
            int statsT2 = Memory.ReadSignedWord(Memory.Addr.statsTeam2Goals);
            int D0 = (short)(statsT1 - statsT2);

            if (D0 == 0)
            {
                // from ball.cpp:3326-3327 / cseg_7D5F6 — D1 = 200.
                D1_goal = 200;
            }
            else
            {
                // from ball.cpp:3329-3341 — D0 = statsTeam1GoalsCopy2 - statsTeam2GoalsCopy2.
                int copy1 = Memory.ReadSignedWord(Memory.Addr.statsTeam1GoalsCopy2);
                int copy2 = Memory.ReadSignedWord(Memory.Addr.statsTeam2GoalsCopy2);
                D0 = (short)(copy1 - copy2);

                if (D0 == 0)
                {
                    // from ball.cpp:3343 / cseg_7D5E0 — D1 = 300.
                    D1_goal = 300;
                }
                else if (D0 == 1 || D0 == -1)
                {
                    // from ball.cpp:3354 / cseg_7D5A8.
                    // recompute D0 = statsTeam1 - statsTeam2 (no overflow flag tracking).
                    int statsDiff = (short)(statsT1 - statsT2);
                    if (statsDiff == 2 || statsDiff == -2)
                    {
                        // from ball.cpp:3389-3401 / cseg_7D5EB — D1 = 200.
                        D1_goal = 200;
                    }
                    else
                    {
                        // from ball.cpp:3403-3405 / cseg_7D5D5 — D1 = 100.
                        D1_goal = 100;
                    }
                }
                else
                {
                    // from ball.cpp:3403-3405 / cseg_7D5D5 — D1 = 100.
                    D1_goal = 100;
                }
            }

            // from ball.cpp:3418-3430 / cseg_7D5FF — D1 += rand().
            D1_goal = (short)(D1_goal + SwosRand());
        }

        // l_penalty_scored: (ball.cpp:3432-3440)
        Memory.WriteWord(Memory.Addr.goalCounter, (short)D1_goal);
        Memory.WriteWord(Memory.Addr.patternsGoalCounter, 1);

        // Set D1 / D2 to centre-spot coords (used by l_break_handled to write foulX/Y).
        // from ball.cpp:3436-3437.
        D1 = 336;
        D2 = 449;

        // from ball.cpp:3438-3439 — gameState = ST_PLAYERS_TO_INITIAL_POSITIONS, breakCameraMode = -1.
        Memory.WriteWord(Memory.Addr.gameState, ST_PLAYERS_TO_INITIAL_POSITIONS);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // D3v / D4 already set above by goal-handled block.
        goto l_break_handled;

    l_not_in_goal_check_near_miss:
        // from ball.cpp:3442-3520 — near-miss detection. If ball came close to
        // an upper-goal sweet-spot (and was kicked hard enough), play near-miss
        // commentary + miss sample.
        // Re-read ball XYZ; original recomputes here.
        {
            int nearX = BallSprite.XPixels;
            // int nearY = BallSprite.YPixels;  // read but only D2 from prologue is used downstream
            int nearZ = BallSprite.ZPixels + 2;  // from ball.cpp:3451-3456 — add 2

            // from ball.cpp:3458-3468 — cmp speed, 768 / jb @@check_for_corner_goal_out.
            if (BallSprite.Speed >= 768)
            {
                // from ball.cpp:3470-3504 — near-miss window:
                //   D1 in [290, 381]  AND  D3 <= 25 (after the +2 bump)
                if (nearX >= 290 && nearX <= 381 && nearZ <= 25)
                {
                    // from ball.cpp:3506-3519 — play near-miss commentary.
                    StubPlayNearMissComment();
                    StubPlayMissGoalSample();
                    // from ball.cpp:3520 — clear whistle (we won't whistle for a miss).
                    Memory.WriteWord(Memory.Addr.playRefereeWhistle, 0);
                }
            }
            // D2 used by check_for_corner_goal_out below; we keep the D2 from
            // the function-prologue read (which matches the asm — D2 was never
            // overwritten by the near-miss recompute since the asm uses fresh
            // reads but then immediately falls through to corner logic).
        }
        goto l_check_for_corner_goal_out;

    l_check_for_corner_goal_out:
        // from ball.cpp:3522-3525 — clearPenaltyFlag().
        ClearPenaltyFlag();
        A6 = TeamData.TopBase;  // mov A6, offset topTeamData

        // from ball.cpp:3526-3536 — cmp D2, 129 / jge @@not_upper_corner_goal_out.
        if (D2 >= 129) goto l_not_upper_corner_goal_out;

        {
            // from ball.cpp:3538-3549 — if topTeamData != lastTeamPlayed → goto upper_goal_out.
            int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
            if (A6 != lastTeamPlayed) goto l_upper_goal_out;
        }

        A6 = TeamData.BottomBase;

        // from ball.cpp:3552-3562 — cmp D1, 336 / jl @@left_upper_corner.
        if (D1 < 336) goto l_left_upper_corner;

        goto l_right_upper_corner;

    l_not_upper_corner_goal_out:
        A6 = TeamData.BottomBase;

        // from ball.cpp:3568-3578 — cmp D2, 769 / jle @@ball_in_pitch.
        if (D2 <= 769) goto l_ball_in_pitch;

        {
            // from ball.cpp:3580-3591 — if bottomTeamData != lastTeamPlayed → lower_goal_out.
            int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
            if (A6 != lastTeamPlayed) goto l_lower_goal_out;
        }

        A6 = TeamData.TopBase;

        // from ball.cpp:3593-3604 — cmp D1, 336 / jl @@lower_left_corner.
        if (D1 < 336) goto l_lower_left_corner;

        // from ball.cpp:3606-3616 — cmp forceLeftTeam, 1 / jz @@right_upper_corner.
        if (Memory.ReadSignedWord(Memory.Addr.forceLeftTeam) == 1)
            goto l_right_upper_corner;

        // from ball.cpp:3619-3625 — right-side bottom corner.
        D1 = 585; D2 = 764;
        D3v = 6; D4 = 193;
        Memory.WriteWord(Memory.Addr.gameState, ST_CORNER_LEFT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        goto l_its_a_corner;

    l_lower_left_corner:
        // from ball.cpp:3627-3639 — cmp forceLeftTeam, 1 / jz @@left_upper_corner.
        if (Memory.ReadSignedWord(Memory.Addr.forceLeftTeam) == 1)
            goto l_left_upper_corner;

        // from ball.cpp:3641-3647 — left-side bottom corner.
        D1 = 86; D2 = 764;
        D3v = 2; D4 = 7;
        Memory.WriteWord(Memory.Addr.gameState, ST_CORNER_RIGHT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        goto l_its_a_corner;

    l_right_upper_corner:
        // from ball.cpp:3649-3656 — right-side top corner.
        D1 = 585; D2 = 134;
        D3v = 6; D4 = 112;
        Memory.WriteWord(Memory.Addr.gameState, ST_CORNER_RIGHT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        goto l_its_a_corner;

    l_left_upper_corner:
        // from ball.cpp:3658-3664 — left-side top corner.
        D1 = 86; D2 = 134;
        D3v = 2; D4 = 28;
        Memory.WriteWord(Memory.Addr.gameState, ST_CORNER_LEFT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // fall through to l_its_a_corner

    l_its_a_corner:
        // from ball.cpp:3666-3683 — bump cornersWon stat + enqueue corner sample.
        {
            int teamStatsPtr = Memory.ReadSignedDword(A6 + TeamData.OffTeamStatsPtr);
            if (teamStatsPtr != 0)
            {
                // TeamStatisticsData.cornersWon @ offset +2 (word).
                short prev = Memory.ReadSignedWord(teamStatsPtr + 2);
                Memory.WriteWord(teamStatsPtr + 2, (short)(prev + 1));
            }
        }
        StubEnqueueCornerSample();
        goto l_break_handled;

    l_upper_goal_out:
        // from ball.cpp:3686-3697 — cmp D1, 336 / jl @@left_upper_goal_out.
        if (D1 < 336) goto l_left_upper_goal_out;

        // cseg_7D8C0: from ball.cpp:3699-3706.
        D1 = 396; D2 = 154;
        D3v = 4; D4 = 124;
        Memory.WriteWord(Memory.Addr.gameState, ST_GOAL_OUT_LEFT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        goto l_goal_out_tail;

    l_left_upper_goal_out:
        // from ball.cpp:3708-3715.
        D1 = 276; D2 = 154;
        D3v = 4; D4 = 124;
        Memory.WriteWord(Memory.Addr.gameState, ST_GOAL_OUT_RIGHT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        goto l_goal_out_tail;

    l_lower_goal_out:
        // from ball.cpp:3717-3728 — cmp D1, 336 / jl @@left_lower_goal_out.
        if (D1 < 336) goto l_left_lower_goal_out;

        // from ball.cpp:3730-3741 — cmp forceLeftTeam, 1 / jz cseg_7D8C0.
        if (Memory.ReadSignedWord(Memory.Addr.forceLeftTeam) == 1)
        {
            // Equivalent to cseg_7D8C0 path.
            D1 = 396; D2 = 154;
            D3v = 4; D4 = 124;
            Memory.WriteWord(Memory.Addr.gameState, ST_GOAL_OUT_LEFT);
            Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
            goto l_goal_out_tail;
        }

        // from ball.cpp:3743-3749.
        D1 = 396; D2 = 744;
        D3v = 0; D4 = 199;
        Memory.WriteWord(Memory.Addr.gameState, ST_GOAL_OUT_RIGHT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        goto l_goal_out_tail;

    l_left_lower_goal_out:
        // from ball.cpp:3751-3763 — cmp forceLeftTeam, 1 / jz @@left_upper_goal_out.
        if (Memory.ReadSignedWord(Memory.Addr.forceLeftTeam) == 1)
        {
            // Re-run the @@left_upper_goal_out branch.
            D1 = 276; D2 = 154;
            D3v = 4; D4 = 124;
            Memory.WriteWord(Memory.Addr.gameState, ST_GOAL_OUT_RIGHT);
            Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
            goto l_goal_out_tail;
        }

        // from ball.cpp:3765-3771.
        D1 = 276; D2 = 744;
        D3v = 0; D4 = 199;
        Memory.WriteWord(Memory.Addr.gameState, ST_GOAL_OUT_LEFT);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // fall through to l_goal_out_tail

    l_goal_out_tail:
        // from ball.cpp:cseg_7D9C3-3793 — shared tail of all goal-out branches.
        // from ball.cpp:3772-3789 — if playerNumber == 0, D4 &= 0xBB.
        // "Useless" flag updates after the AND are intentionally preserved.
        {
            short playerNumber = Memory.ReadSignedWord(A6 + TeamData.OffPlayerNumber);
            if (playerNumber == 0)
            {
                D4 = D4 & 0xBB;
            }
        }
        // from ball.cpp:3792 — goalOut = 1.
        Memory.WriteWord(Memory.Addr.goalOut, 1);
        goto l_break_handled;

    l_ball_in_pitch:
        // from ball.cpp:3795-3973 — throw-in path.
        // A6 = lastTeamPlayed → opponentsTeam.
        {
            int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
            A6 = lastTeamPlayed;
            if (A6 != 0)
                A6 = Memory.ReadSignedDword(A6 + TeamData.OffOpponentsTeam);
        }

        // from ball.cpp:3801-3811 — cmp D1, 336 / jl @@left_half_of_pitch.
        if (D1 < 336)
        {
            // l_left_half_of_pitch — from ball.cpp:3881-3921.
            D1 = 81;
            D3v = 2; D4 = 31;

            if (A6 != TeamData.TopBase)
            {
                // l_lh_right_team_throw_in — from ball.cpp:3923-3946.
                if (D2 < 342)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_BACK_RIGHT);
                }
                else if (D2 < 556)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_CENTER_RIGHT);
                }
                else
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_FORWARD_RIGHT);
                }
            }
            else
            {
                // Left half of pitch, A6 == topTeamData — from ball.cpp:3897-3921.
                if (D2 < 342)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_FORWARD_LEFT);
                }
                else if (D2 < 556)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_CENTER_LEFT);
                }
                else
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_BACK_LEFT);
                }
            }
        }
        else
        {
            // from ball.cpp:3813-3826 — right half of pitch.
            D1 = 590;
            D3v = 6; D4 = 241;

            if (A6 != TeamData.TopBase)
            {
                // l_rh_right_team_throw_in — from ball.cpp:3854-3879.
                if (D2 < 342)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_FORWARD_LEFT);
                }
                else if (D2 < 556)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_CENTER_LEFT);
                }
                else
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_BACK_LEFT);
                }
            }
            else
            {
                // Right half of pitch, A6 == topTeamData — from ball.cpp:3828-3852.
                if (D2 < 342)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_FORWARD_RIGHT);
                }
                else if (D2 < 556)
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_CENTER_RIGHT);
                }
                else
                {
                    Memory.WriteWord(Memory.Addr.gameState, ST_THROW_IN_BACK_RIGHT);
                }
            }
        }

        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        StubEnqueueThrowInSample();
        // fall through to l_break_handled

    l_break_handled:
        // from ball.cpp:3975-4008 — break_handled.
        // from ball.cpp:3976-3989 — `cmp forceLeftTeam, 1; jnz cseg_7DB29`:
        // the `A6 = offset topTeamData` assignment is SKIPPED unless
        // forceLeftTeam == 1 (training-mode pin). Normal play keeps the A6
        // chosen by the goal / corner / goal-out / throw-in branch above.
        // (Previous port had the polarity inverted, which pinned every
        // stoppage's lastTeamPlayedBeforeBreak to the top team.)
        if (Memory.ReadSignedWord(Memory.Addr.forceLeftTeam) == 1)
            A6 = TeamData.TopBase;

        // from ball.cpp:3992 — gameStatePl = 101 (ST_STOPPED).
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_STOPPED);

        // from ball.cpp:3993 — gameNotInProgressCounter write-only (preserve fidelity).
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);

        // from ball.cpp:3994-4001 — write foul coords + camera direction + turn flags.
        Memory.WriteWord(Memory.Addr.foulXCoordinate, (short)D1);
        Memory.WriteWord(Memory.Addr.foulYCoordinate, (short)D2);
        Memory.WriteWord(Memory.Addr.cameraDirection, (short)D3v);
        Memory.WriteByte(Memory.Addr.playerTurnFlags, (byte)D4);

        // from ball.cpp:4002-4003 — lastTeamPlayedBeforeBreak = A6.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, A6);

        // from ball.cpp:4004-4005 — stoppage timers reset.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);

        // from ball.cpp:4006 — stopAllPlayers().
        StopAllPlayers();

        // from ball.cpp:4007-4008 — camera velocities reset.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
        // fall through to l_starting_the_game

    l_starting_the_game:
        // from ball.cpp:4010-4019 — final whistle queue.
        short whistle = Memory.ReadSignedWord(Memory.Addr.playRefereeWhistle);
        if (whistle != 0)
        {
            StubPlayRefereeWhistleSample();
        }
    }

    // from ball.cpp:3102-3219 — collapsed goal-scoring path. teamNum = 1 or 2
    // (which team scored). teamDataBase is the TeamData * of the SCORING team
    // (i.e., the team that put the ball in the opposing net) — the asm uses A6
    // for this, mapped at the call sites in CheckIfBallOutOfPlay.
    private static void HandleGoalScoredTeam(int teamNum, int teamDataBase)
    {
        // from ball.cpp:3103 / 3171 — mov D0, teamNum (goal team for SWOS::GoalScored).
        int A6 = teamDataBase;

        // from ball.cpp:3104-3140 / 3172-3208 — determine scorer sprite (A0).
        // If lastKeeperPlayed != 0 AND lastPlayerPlayed pointed at a goalie, the
        // scorer is the keeper; otherwise it's lastPlayerPlayed.
        int lastPlayerPlayed = Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed);
        int lastKeeperPlayed = Memory.ReadSignedDword(Memory.Addr.lastKeeperPlayed);
        int scorerSprite = lastPlayerPlayed;

        if (lastKeeperPlayed != 0)
        {
            // Original compares against goalie1Sprite (326524) and goalie2Sprite (327756)
            // — in our port, those map to player slot 0 and slot 11 base addresses.
            int goalie1Base = PlayerSprite.Base(PlayerSprite.SlotGoalie1);
            int goalie2Base = PlayerSprite.Base(PlayerSprite.SlotGoalie2);
            if (scorerSprite == goalie1Base || scorerSprite == goalie2Base)
                scorerSprite = lastKeeperPlayed;
        }

        // from ball.cpp:3143-3146 / 3211-3214 — snapshot stats to *Copy2.
        short statsT1 = Memory.ReadSignedWord(Memory.Addr.statsTeam1Goals);
        Memory.WriteWord(Memory.Addr.statsTeam1GoalsCopy2, statsT1);
        short statsT2 = Memory.ReadSignedWord(Memory.Addr.statsTeam2Goals);
        Memory.WriteWord(Memory.Addr.statsTeam2GoalsCopy2, statsT2);

        // from ball.cpp:3148 / 3216 — SWOS::GoalScored(teamNum, scorerSprite).
        // UpdateGoals.GoalScored expects a slot index. The original takes a sprite
        // pointer. We bridge by computing the slot from scorerSprite base.
        int scorerSlot = SpriteBaseToSlot(scorerSprite);
        UpdateGoals.GoalScored(teamNum, scorerSlot);

        // from ball.cpp:3150 / 3218 — goalCameraMode = 1.
        Memory.WriteWord(Memory.Addr.goalCameraMode, 1);
        // from ball.cpp:3151 / 3219 — teamNumThatScored.
        Memory.WriteWord(Memory.Addr.teamNumThatScored, (short)teamNum);

        // from ball.cpp:3222-3226 — write teamScoredDataPtr + teamScoredGamePtr.
        Memory.WriteDword(Memory.Addr.teamScoredDataPtr, A6);
        int inGamePtr = Memory.ReadSignedDword(A6 + TeamData.OffInGameTeamPtr);
        Memory.WriteDword(Memory.Addr.teamScoredGamePtr, inGamePtr);
    }

    // Map a sprite base address back to a 0..21 slot index. Returns 0 if the
    // address doesn't fall inside the sprite pool. Mirrors what the asm does
    // implicitly via pointer-compare against goalie1Sprite / goalie2Sprite.
    private static int SpriteBaseToSlot(int spriteBase)
    {
        if (spriteBase == 0) return 0;
        int off = spriteBase - PlayerSprite.SpritePoolBase;
        if (off < 0) return 0;
        int slot = off / PlayerSprite.SlotStride;
        if (slot < 0 || slot >= PlayerSprite.TotalSlots) return 0;
        return slot;
    }

    // ---- Stubs ----------------------------------------------------------------
    // These call into not-yet-ported helpers in external/swos-port/src/.
    // Each TODO points at the source path so future ports can find them.

    // SWOS::rand — table-driven xor-stream PRNG. Returns a byte (0..255).
    // Wired through to the deterministic stream-1 RNG in SwosVm.Rng (matches
    // PlayerActions.SwosRand and the in-asm `Rand` callsites). Used here by
    // ball.cpp:3292 (D1_goal = (rand() >> 1) + 100) and ball.cpp:3418
    // (D1_goal += rand()) — both deterministic-lockstep paths.
    // Source: external/swos-port/src/util/random.cpp
    private static int SwosRand() => Rng.NextByte();

    // External: comments.cpp PlayGoalComment / PlayOwnGoalComment.
    // TODO from external/swos-port/src/audio/comments.cpp
    private static void StubPlayGoalComment()       { /* TODO */ }
    private static void StubPlayOwnGoalComment()    { /* TODO */ }

    // External: sfx.cpp PlayHomeGoalSample / PlayAwayGoalSample /
    // PlayRefereeWhistleSample / PlayMissGoalSample.
    // TODO from external/swos-port/src/audio/sfx.cpp
    private static void StubPlayHomeGoalSample()        { /* TODO */ }
    private static void StubPlayAwayGoalSample()        { /* TODO */ }
    private static void StubPlayRefereeWhistleSample()  { /* TODO */ }
    private static void StubPlayMissGoalSample()        { /* TODO */ }
    private static void StubPlayNearMissComment()       { /* TODO */ }

    // External: comments.cpp enqueueCornerSample / enqueueThrowInSample.
    // TODO from external/swos-port/src/audio/comments.cpp
    private static void StubEnqueueCornerSample()  { /* TODO */ }
    private static void StubEnqueueThrowInSample() { /* TODO */ }

    // Port of comments.cpp:240-243 — clearPenaltyFlag (audio side: m_performingPenalty).
    // We keep this minimal: write swos.penalty = 0. The audio-side
    // m_performingPenalty flag belongs to the not-yet-ported commentary module
    // and would no-op anyway. Used by ball.cpp:3524 after a penalty resolves.
    private static void ClearPenaltyFlag()
    {
        Memory.WriteWord(Memory.Addr.penalty, 0);
    }

    // Mechanical port of gameLoop.cpp StopAllPlayers + team.cpp:stopAllPlayers
    // (external/swos-port/src/game/team.cpp:26-44).
    //
    // For each team:
    //   1. stopPlayers(team) — for each of the 11 players: if state == kNormal
    //      and !sentAway, write destX = current X (whole pixel), destY = Y.
    //      Players already in dive/tackle/etc. keep their existing destination.
    //   2. Clear ball-in-play / ball-out-of-play flags.
    //   3. Reset controlledPlayer + passToPlayerPtr pointers.
    //   4. Clear passing state (passingBall, passingToPlayer, passingKickingPlayer).
    //   5. Reset playerSwitchTimer.
    //   6. Clear goalkeeperPlaying.
    private static void StopAllPlayers()
    {
        for (int t = 0; t < 2; t++)
        {
            bool top = (t == 0);
            int teamBase = top ? TeamData.TopBase : TeamData.BottomBase;

            // stopPlayers(team) — freeze normal-state, not-sent-away players at
            // their current X/Y (destX/destY are whole-pixel words).
            int firstSlot = top ? 0 : PlayerSprite.TeamSize;
            for (int slotOff = 0; slotOff < PlayerSprite.TeamSize; slotOff++)
            {
                int slot = firstSlot + slotOff;
                byte state = PlayerSprite.PlayerState(slot);
                short sentAway = Memory.ReadSignedWord(
                    PlayerSprite.Base(slot) + PlayerSprite.OffSentAway);
                if (state == 0 /* kNormal */ && sentAway == 0)
                {
                    PlayerSprite.SetDestX(slot, PlayerSprite.XPixels(slot));
                    PlayerSprite.SetDestY(slot, PlayerSprite.YPixels(slot));
                }
            }

            // ballInPlay / ballOutOfPlay flags.
            Memory.WriteWord(teamBase + TeamData.OffBallInPlay, 0);
            Memory.WriteWord(teamBase + TeamData.OffBallOutOfPlay, 0);

            // controlledPlayer.reset() / passToPlayerPtr.reset() — both null pointers.
            Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, 0);
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);

            // passingBall = 0, passingToPlayer = 0, playerSwitchTimer = 0,
            // passingKickingPlayer.reset().
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
            Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 0);
            Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);

            // goalkeeperPlaying = 0. (Original SWOS had a bug: it failed to
            // reset this for the top team. swos-port preserves that bug
            // outside #ifdef SWOS_TEST — we follow the production path and
            // clear both, matching the un-#ifdef'd C++ branch.)
            Memory.WriteWord(teamBase + TeamData.OffGoalkeeperPlaying, 0);
        }
    }
}
