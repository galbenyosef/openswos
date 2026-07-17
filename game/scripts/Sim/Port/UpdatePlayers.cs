namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Per-tick orchestration ported from external/swos-port/src/game/updatePlayers/updatePlayers.cpp.
// The function `updatePlayers(TeamGeneralInfo *team)` (line 47) is the practical
// main SWOS procedure — called once per team per tick, it runs:
//   1. Per-team timer decay (goalkeeperSavedCommentTimer, passKickTimer,
//      wonTheBallTimer, playerSwitchTimer).
//   2. Pre-loop ball logic (applyBallAfterTouch + ball-location flags
//      ballInUpperPenaltyArea / ballInLowerPenaltyArea / ballInGoalkeeperArea).
//   3. The 11-player loop iterating each sprite in the team and dispatching
//      to the appropriate per-state handler (TACKLED / ROLLING_INJURED /
//      GOALKEEPER / NORMAL).
//
// **Scope of this port (this commit):** entry function + main orchestration
// skeleton + per-player loop dispatch shell. The actual state handler bodies
// (TickHumanControlled, TickAiControlled, TickGoalkeeper, etc.) are STUBS
// to be ported by follow-up agents. Where we already have ported helpers
// (PlayerUpdate.TickGoalieCatchingBall etc.), the dispatch wires them in.
//
// **Source mapping:** line numbers refer to updatePlayers.cpp.
//   - updatePlayers entry              ........  47
//   - lastKeeperPlayed reset           ........  50- 88
//   - goalkeeperSavedCommentTimer dec  ........  90-126
//   - passKickTimer dec / clear        ........ 128-153
//   - wonTheBallTimer dec              ........ 155-172
//   - playerSwitchTimer dec            ........ 174-195
//   - applyBallAfterTouch + ball loc   ........ 197-336
//   - goalOut reset                    ........ 338-368
//   - updatePlayerIndex increment      ........ 370-393
//   - per-player loop                  ........ 395-...
//     - sprite ptr table dereference   ........ 401-410
//     - direction copy + isMoving      ........ 411-427
//     - state switch (TACKLED/ROLLING) ........ 429-455
//     - controlledPlayer / pass branch ........ 457-535
//     - update ball distance flags     ........ 537-597
//     - update ball height flags       ........ 599-679
//     - state dispatch switch          ........ 680-836
//     - not-controlled tail:
//       - round-robin updatePlayerIndex gate . 8676-8691
//       - destReachedState 1→2 promotion ..... 8806-8900
//       - sent-away gate ..................... 9199-9207
//       - break positions (formation walk) ... 9209-9934 (SetPlayerPositionsForGameBreak)
//       - no-ball dest + foul pushback ....... 9936-10077
//       - speed + delta recompute ............ 10086-10106
//   - loop epilogue                    ........ 10356-10456
//
// Hard rules: NO floats. Cite swos-port lines. Keep stubs marked.
public static class UpdatePlayers
{
    // --- Constants from swos-port (verified against swos.h / updatePlayers.cpp) ---

    // ST_GAME_IN_PROGRESS = 100 (swos.h GameState enum).
    private const short kStGameInProgress = 100;
    // ST_KEEPER_HOLDS_BALL = 3.
    private const short kStKeeperHoldsBall = 3;

    // Telemetry: how often the chase-ball fallback fires per team. Kept as
    // a cheap signal for whether the AI brain port is making progress —
    // when these go to zero we know we no longer need the heuristic.
    private static int s_fallbackChasesTop = 0;
    private static int s_fallbackChasesBot = 0;

    // Telemetry: how often the off-ball KICK fallback (lines ~785-833 below)
    // fires PlayerKickingBall outside the normal asm path. When AiBrain's
    // cseg_84A85/cseg_84F4B/l_activate_normal_fire branches write normalFire
    // correctly, this should trend toward zero.
    private static int s_kickFallbackTop = 0;
    private static int s_kickFallbackBot = 0;

    // Telemetry: how often the re-aim sub-clause inside the kick fallback
    // (goal-ward direction override at lines ~885-905) actually adjusts the
    // direction. Split per team to expose attacking-third asymmetry.
    private static int s_reaimAppliedTop = 0;
    private static int s_reaimAppliedBot = 0;
    // Diagnostic: kick fallback splits by Y zone for each team.
    // For top team (attacks Y=769): defensive third = [129, 343], midfield = (343, 555),
    //                                attacking third = [555, 681], penalty box = [682, 769].
    // For bot team (attacks Y=129): defensive third = [555, 769], midfield = (343, 555),
    //                                attacking third = [217, 343], penalty box = [129, 216].
    private static int s_kickFallbackTopDef = 0, s_kickFallbackTopMid = 0, s_kickFallbackTopAtt = 0, s_kickFallbackTopBox = 0;
    private static int s_kickFallbackBotDef = 0, s_kickFallbackBotMid = 0, s_kickFallbackBotAtt = 0, s_kickFallbackBotBox = 0;

    // Public read-only access for smoke-test reporting (Main.cs:RunSwosPortSmokeTest).
    public static int FallbackChasesTop => s_fallbackChasesTop;
    public static int FallbackChasesBot => s_fallbackChasesBot;
    public static int KickFallbackTop => s_kickFallbackTop;
    public static int KickFallbackBot => s_kickFallbackBot;
    public static int ReaimAppliedTop => s_reaimAppliedTop;
    public static int ReaimAppliedBot => s_reaimAppliedBot;
    public static int KickFallbackTopDef => s_kickFallbackTopDef;
    public static int KickFallbackTopMid => s_kickFallbackTopMid;
    public static int KickFallbackTopAtt => s_kickFallbackTopAtt;
    public static int KickFallbackTopBox => s_kickFallbackTopBox;
    public static int KickFallbackBotDef => s_kickFallbackBotDef;
    public static int KickFallbackBotMid => s_kickFallbackBotMid;
    public static int KickFallbackBotAtt => s_kickFallbackBotAtt;
    public static int KickFallbackBotBox => s_kickFallbackBotBox;

    // 2026-06-06 — debounce state for the controlledPlayer re-establishment
    // heuristic. The asm's `l_noone_near` swap (updatePlayers.cpp:18958-18966)
    // only fires when AiBrain decides the prior pointer is stale; it does NOT
    // re-elect every tick on a transient null. We approximate that by
    // requiring `controlledPlayer == 0` to STAY zero for at least
    // kReElectDebounceTicks consecutive ticks before we pick a new sprite.
    // Per team. Reset on any non-null read (the asm's "valid pointer" state).
    private const int kReElectDebounceTicks = 4;
    private static int s_zeroTicksTop = 0;
    private static int s_zeroTicksBot = 0;
    // 2026-06-06 — carrier-stall counters for the teammate-hysteresis safety
    // net at line ~1145. Bumped when the team's controlledPlayer is plVeryClose
    // to the ball but the kick hasn't fired; allows an off-ball teammate to
    // bypass the 64 sq-px takeover hysteresis when the carrier is stalled > 30
    // ticks (otherwise the game deadlocks with an AiBrain-stubbed carrier
    // glued to the ball forever).
    private static int s_carrierStallTicksTop = 0;
    private static int s_carrierStallTicksBot = 0;
    public static void ResetFallbackCounters()
    {
        s_fallbackChasesTop = 0;
        s_fallbackChasesBot = 0;
        s_kickFallbackTop = 0;
        s_kickFallbackBot = 0;
        s_reaimAppliedTop = 0;
        s_reaimAppliedBot = 0;
        s_kickFallbackTopDef = s_kickFallbackTopMid = s_kickFallbackTopAtt = s_kickFallbackTopBox = 0;
        s_kickFallbackBotDef = s_kickFallbackBotMid = s_kickFallbackBotAtt = s_kickFallbackBotBox = 0;
        s_zeroTicksTop = 0;
        s_zeroTicksBot = 0;
        s_carrierStallTicksTop = 0;
        s_carrierStallTicksBot = 0;
    }

    // updatePlayers.cpp:210-271 — pitch coordinate constants for ball-location
    // tests. Penalty area X span [193, 478]; upper penalty area Y <= 216;
    // lower penalty area Y >= 682; goalkeeper area X span [273, 398], Y
    // <= 158 (upper) or >= 740 (lower).
    private const short kPenaltyAreaXMin            = 193;
    private const short kPenaltyAreaXMax            = 478;
    private const short kPenaltyAreaYMin            = 129;
    private const short kPenaltyAreaYMax            = 769;
    private const short kUpperPenaltyAreaYBoundary  = 216;
    private const short kLowerPenaltyAreaYBoundary  = 682;
    private const short kGoalkeeperAreaXMin         = 273;
    private const short kGoalkeeperAreaXMax         = 398;
    private const short kGoalkeeperAreaUpperY       = 158;
    private const short kGoalkeeperAreaLowerY       = 740;

    // ===================================================================
    // updatePlayers — updatePlayers.cpp:47
    // ===================================================================
    //
    // In: teamIndex (0=top, 1=bottom). Caller is gameLoop / SwosVm tick.
    //
    // The function comment from swos-port reads:
    //   "This is practically main SWOS procedure. Everything happens here."
    //
    // We translate `A6 = team` into a `bool topTeam` flag indexing TeamData.
    // All `esi = A6; readMemory(esi + N)` become `TeamData.<accessor>(topTeam)`
    // or `Memory.ReadXxx(TeamData.Base(topTeam) + N)`.
    public static void Update(int teamIndex)
    {
        // Sanity. Caller should pass 0 or 1.
        if (teamIndex < 0 || teamIndex > 1) return;
        bool topTeam = teamIndex == 0;
        int teamBase = TeamData.Base(topTeam);


        // ===================================================================
        // BUG FIX (2026-06-02) — re-establish controlledPlayer if cleared.
        // ===================================================================
        // The off-ball AI-kick fallback (this file, ~line 1147) and various
        // SetPieces / SWOS-native callers clear team.controlledPlayer to 0
        // after a kick / break (asm: updatePlayers.cpp:5397). In retail SWOS
        // that 0 is short-lived — AiBrain's `l_noone_near` chain
        // (updatePlayers.cpp:18958-18966) swaps controlledPlayer with
        // passToPlayerPtr on the very next AI_SetControlsDirection call,
        // restoring a valid pointer before the dispatch loop reads it.
        //
        // Our AiBrain port leaves that swap stubbed. Without it, once
        // controlledPlayer is cleared:
        //   - DispatchByPlayerState's `if (spriteAddr == controlled)` test
        //     fails for every sprite (0 doesn't match any sprite addr).
        //   - TickHumanControlled is never called → PlayerControlled's
        //     cseg_80C0C `playerHasBall = 0` clear at line 4442 and the
        //     cseg_80EAA `playerHasBall = 1` set at line 4806 NEVER run.
        //   - The kickoff team's playerHasBall stays at 1 forever (set in
        //     Kickoff.PrepareForInitialKick), the opponent stays at 0.
        //   - Result: USER-VISIBLE BUG — one team appears to "always have
        //     the ball" per the SWOS state flags, even though no carrier
        //     actually controls it. AI behaviour decays into the chase
        //     fallback only (UpdatePlayers.cs:741-779).
        //
        // STAND-IN FIX: if team.controlledPlayer is 0 at the start of this
        // tick, re-establish it to the closest non-keeper sprite to the
        // ball. This is the same heuristic the asm's `l_noone_near` chain
        // uses (the swap with passToPlayerPtr happens precisely when the
        // current controlled player is too far from the ball / null).
        //
        // GATE (2026-06-06): only run for AI teams (playerNumber == 0). For a
        // human team, controlledPlayer is seeded to the CF at kick-off by
        // Kickoff.PrepareForInitialKick (Sim/Port/Kickoff.cs:184-187) and the
        // user expects to drive THAT sprite. If we re-route here, the user's
        // arrow input writes team.currentAllowedDirection but the dispatcher
        // hands it to whichever teammate happens to be closest to the ball —
        // input appears dead from the player's POV. Real SWOS uses the asm
        // swap (updatePlayers.cpp:18958-18966) which is part of the AI
        // brain, NOT something a human-controlled team executes.
        //
        // TODO: drop once AiBrain.SetControlsDirection ports
        // `l_pass_to_player_too_far_or_null` + `l_noone_near` (the swap at
        // updatePlayers.cpp:18958-18966).
        //
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:
        //   - 18958-18966 (l_noone_near: swap controlledPlayer ⟷ passToPlayerPtr)
        //   - 5397         (asm-faithful clear after kick)
        //
        // 2026-06-06 — POSSESSION-THRASH GATES (user-visible bug: ball jitters
        // back and forth on contact, camera shakes). The original stand-in
        // re-elected the closest sprite on EVERY tick controlledPlayer was 0.
        // Combined with the per-tick clear at kick-fallback (UpdatePlayers.cs:
        // 1373) and at PlayerControlled.cs:288, this means both AI teams could
        // independently re-elect their closest sprite each tick — when the
        // ball was between two opposing chasers, the elected sprite oscillated
        // and so did the AI re-aim, reversing ball velocity tick by tick.
        // Real SWOS NEVER does this: l_noone_near fires at most once per
        // AiBrain pass and only when the prior pointer is stale.
        //
        // Two gates restore SWOS-faithful behaviour:
        //   (a) DEBOUNCE — require controlledPlayer to STAY 0 for
        //       kReElectDebounceTicks consecutive ticks before we re-elect.
        //       Absorbs the per-tick `WriteDword(OffControlledPlayer, 0)` at
        //       UpdatePlayers.cs:1373 without leaving the team without a
        //       carrier for long.
        //   (b) BALL NOT CURRENTLY HELD — skip re-election entirely when
        //       EITHER team's `playerHasBall == 1`. In real SWOS only one
        //       team has the ball at a time (asm:18958-18966 swaps via
        //       passToPlayerPtr, never simultaneously); re-electing a second
        //       carrier here is the very flip that causes the thrash.
        //
        // GATE (2026-07-02): only run while gameStatePl == ST_GAME_IN_PROGRESS.
        // During a stoppage (post-goal walk-back, gameStatePl == gameState == 0)
        // the asm's l_noone_near swap never executes — AI_SetControlsDirection
        // is only reached through the in-play controlled branch. Re-electing a
        // carrier mid-walk would route that sprite through TickHumanControlled,
        // which bypasses the destReachedState promotion at updatePlayers.cpp:
        // 8885-8900 and stalls the breakCameraMode ladder waiting for arrival.
        {
            short playerNumberAtStart = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
            int ctrlAtStart = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
            short gameStatePlAtStart = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (playerNumberAtStart == 0 && ctrlAtStart == 0 && gameStatePlAtStart == kStGameInProgress)
            {
                // Debounce — bump zero-tick counter for this team.
                int zeroTicks;
                if (topTeam) { s_zeroTicksTop++; zeroTicks = s_zeroTicksTop; }
                else         { s_zeroTicksBot++; zeroTicks = s_zeroTicksBot; }

                // Ball-not-currently-held gate.
                short topHas = Memory.ReadSignedWord(TeamData.TopBase    + TeamData.OffPlayerHasBall);
                short botHas = Memory.ReadSignedWord(TeamData.BottomBase + TeamData.OffPlayerHasBall);
                bool ballHeldBySomeone = topHas != 0 || botHas != 0;

                if (zeroTicks >= kReElectDebounceTicks && !ballHeldBySomeone)
                {
                    int slotBase  = topTeam ? 0 : PlayerSprite.TeamSize;
                    short bxFix   = BallSprite.XPixels;
                    short byFix   = BallSprite.YPixels;
                    int bestSlot  = -1;
                    long bestDist = long.MaxValue;
                    // Skip slot 0/11 (goalkeeper, playerOrdinal==1).
                    for (int s = slotBase + 1; s < slotBase + 11; s++)
                    {
                        int sa = PlayerSprite.Base(s);
                        short sx = Memory.ReadSignedWord(sa + PlayerSprite.OffX + 2);
                        short sy = Memory.ReadSignedWord(sa + PlayerSprite.OffY + 2);
                        int dxF = sx - bxFix;
                        int dyF = sy - byFix;
                        long sq = (long)dxF * dxF + (long)dyF * dyF;
                        if (sq < bestDist) { bestDist = sq; bestSlot = s; }
                    }
                    if (bestSlot >= 0)
                    {
                        int newAddr = PlayerSprite.Base(bestSlot);
                        Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, newAddr);
                        // Restart debounce after a successful re-election.
                        if (topTeam) s_zeroTicksTop = 0;
                        else         s_zeroTicksBot = 0;
                    }
                }
            }
            else
            {
                // Non-null pointer (or human team) — reset zero-tick counter so
                // the debounce starts fresh on the next clear.
                if (topTeam) s_zeroTicksTop = 0;
                else         s_zeroTicksBot = 0;
            }
        }
        // ===================================================================

        // === DIAG (2026-06-06) — carrier-stall tracker (per-team-per-tick) ====
        // Bumped when the team's controlledPlayer is plVeryClose to the ball
        // but hasn't kicked (passKickTimer == 0). Drives the teammate-
        // hysteresis safety net in the off-ball kick fallback (line ~1145):
        // once the carrier has been stalled > 30 ticks, an off-ball teammate
        // can bypass the 64 sq-px hysteresis to break the deadlock. Without
        // this, a controlled-player glued to the ball with stubbed AiBrain
        // would never relinquish possession.
        //
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:
        //   5397 (asm clears controlledPlayer after carrier kick — non-stalled
        //         carriers reset the counter naturally via this path).
        {
            int curCarrier = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
            short pkt2 = Memory.ReadSignedWord(teamBase + TeamData.OffPassKickTimer);
            if (curCarrier == 0 || pkt2 != 0)
            {
                if (topTeam) s_carrierStallTicksTop = 0;
                else         s_carrierStallTicksBot = 0;
            }
            else
            {
                int carrierBallDistTick = Memory.ReadSignedDword(curCarrier + PlayerSprite.OffBallDistance);
                if (carrierBallDistTick <= 32)
                {
                    if (topTeam) s_carrierStallTicksTop++;
                    else         s_carrierStallTicksBot++;
                }
                else
                {
                    if (topTeam) s_carrierStallTicksTop = 0;
                    else         s_carrierStallTicksBot = 0;
                }
            }
        }
        // ===================================================================

        // ----- updatePlayers.cpp:50-88 ----------------------------------
        // Snapshot prevLastPlayer / prevLastTeamPlayed (used by epilogue to
        // detect goalie change after the loop). Then clear lastKeeperPlayed
        // unless the previous-tick last player was a keeper.
        int lastPlayerPlayed   = Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed);
        int lastTeamPlayed     = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
        int lastKeeperPlayed   = Memory.ReadSignedDword(Memory.Addr.lastKeeperPlayed);

        Memory.WriteDword(Memory.Addr.prevLastPlayer, lastPlayerPlayed);
        Memory.WriteDword(Memory.Addr.prevLastTeamPlayed, lastTeamPlayed);

        if (lastKeeperPlayed != 0)
        {
            // updatePlayers.cpp:62-88 — only clear if neither goalie was the
            // last player. The swos-port asm compares against the literal
            // sprite addresses (goalie1Sprite=326524, goalie2Sprite=327756).
            // In our port both keepers are simply PlayerSprite.Base of slot
            // 0 or 11. Compare with current pool addresses.
            int goalie1Addr = PlayerSprite.Base(PlayerSprite.SlotGoalie1);
            int goalie2Addr = PlayerSprite.Base(PlayerSprite.SlotGoalie2);
            if (lastPlayerPlayed != goalie1Addr && lastPlayerPlayed != goalie2Addr)
            {
                Memory.WriteDword(Memory.Addr.lastKeeperPlayed, 0);
            }
        }

        // ----- updatePlayers.cpp:90-126 ---------------------------------
        // goalkeeperSavedCommentTimer: signed-counter that moves TOWARD zero.
        // Positive → decrement; negative → increment; zero → leave alone.
        short gksTimer = TeamData.GoalkeeperSavedCommentTimer(topTeam);
        if (gksTimer != 0)
        {
            if (gksTimer < 0) gksTimer++;       // l_decay_goalkeeper_saved_timer
            else              gksTimer--;
            TeamData.SetGoalkeeperSavedCommentTimer(topTeam, gksTimer);
        }

        // ----- updatePlayers.cpp:128-153 --------------------------------
        // passKickTimer: counts down to zero; on reaching zero, clear
        // passingKickingPlayer pointer.
        short passKickTimer = Memory.ReadSignedWord(teamBase + TeamData.OffPassKickTimer);
        if (passKickTimer != 0)
        {
            passKickTimer--;
            Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, passKickTimer);
            if (passKickTimer == 0)
            {
                // updatePlayers.cpp:153 — clear passingKickingPlayer.
                Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);
            }
        }

        // ----- updatePlayers.cpp:155-172 --------------------------------
        // wonTheBallTimer: simple decrement (no clear-side-effect).
        // swos-port reads [esi+138]. There is no TeamData accessor yet for
        // this field — read directly via raw offset.
        const int kOffWonTheBallTimer = 138;
        short wonBallTimer = Memory.ReadSignedWord(teamBase + kOffWonTheBallTimer);
        if (wonBallTimer != 0)
        {
            wonBallTimer--;
            Memory.WriteWord(teamBase + kOffWonTheBallTimer, wonBallTimer);
        }

        // ----- updatePlayers.cpp:174-195 --------------------------------
        // playerSwitchTimer: simple decrement.
        short pswTimer = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerSwitchTimer);
        if (pswTimer != 0)
        {
            pswTimer--;
            Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, pswTimer);
        }

        // === STALE-playerHasBall RECONCILIATION (2026-06-02) ===========
        // After PlayerKickingBall's `l_stop_player` exit (updatePlayers.cpp
        // :5397) the team's controlledPlayer is cleared to 0 but
        // playerHasBall is left at 1 (it was set earlier this tick by
        // cseg_80EAA at L4806). The asm RELIES on the next controlled
        // player's cseg_80C0C (L4442) to clear playerHasBall — but if no
        // teammate becomes the new carrier (e.g. a shot fired at our own
        // goal with all teammates upfield), the flag stays stuck at 1.
        // That stale flag tripping `cseg_7F7BC` (L2086) `if this.playerHasBall
        // != 0 → skip` blocks `CheckShotAtGoalAndKeeperSave` from ever
        // entering the save chain — the keeper just stands there as the
        // ball flies past. Diagnosed 2026-06-02 in 5000-tick smoke: top
        // team's outfielder kicks ball at own goal, top team's playerHasBall
        // remains 1 for 30+ ticks while the keeper's save-check bails out
        // every frame. Fix: defensively zero playerHasBall when
        // controlledPlayer is null — this mirrors what cseg_80C0C would do
        // the moment a new carrier emerged.
        // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:4442
        //     external/swos-port/src/game/updatePlayers/updatePlayers.cpp:5397
        {
            int ctrlReconcile = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
            if (ctrlReconcile == 0)
            {
                short stalePhb = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerHasBall);
                if (stalePhb != 0)
                {
                    Memory.WriteWord(teamBase + TeamData.OffPlayerHasBall, 0);
                }
            }
        }

        // ----- updatePlayers.cpp:197-198 --------------------------------
        // applyBallAfterTouch(). swos-port's call is parameterless — it reads
        // A6 internally (current team). Our BallUpdate variant takes a
        // topTeam bool, which we pass through.
        BallUpdate.ApplyBallAfterTouch(topTeam);

        // ----- Pre-loop ball-variable predictor wiring -----------------
        // updatePlayers.cpp:1193-1194 — calls updateBallVariables /
        // calculateBallNextGroundXYPositions once per per-player-iteration in
        // the asm path. We hoist the OnceperTick version to the pre-loop so
        // downstream chase / dive / catch branches have a fresh prediction
        // each tick. A1 is filled at iteration time inside the loop below.
        BallVariables.CalculateBallNextGroundXYPositions(BallSprite.Base);

        // ----- updatePlayers.cpp:199-336 --------------------------------
        // Read ball whole-pixel (x, y) and reset the three "ball location"
        // flag globals. Then test each polygon and set the matching flag.
        short ballX = BallSprite.XPixels;
        short ballY = BallSprite.YPixels;
        Memory.WriteWord(Memory.Addr.ballInUpperPenaltyArea, 0);
        Memory.WriteWord(Memory.Addr.ballInLowerPenaltyArea, 0);
        Memory.WriteWord(Memory.Addr.ballInGoalkeeperArea, 0);

        // updatePlayers.cpp:209-254 — penalty area test.
        // X in [193, 478] AND Y in [129, 769] AND (Y <= 216 ? upper : Y >= 682 ? lower : not_in).
        bool inPenaltyXRange =
            ballX >= kPenaltyAreaXMin && ballX <= kPenaltyAreaXMax;
        bool inPenaltyYRange =
            ballY >= kPenaltyAreaYMin && ballY <= kPenaltyAreaYMax;
        bool inPenaltyArea = false;
        if (inPenaltyXRange && inPenaltyYRange)
        {
            if (ballY <= kUpperPenaltyAreaYBoundary)
            {
                // updatePlayers.cpp:284 — ballInUpperPenaltyArea = 1.
                Memory.WriteWord(Memory.Addr.ballInUpperPenaltyArea, 1);
                inPenaltyArea = true;
            }
            else if (ballY >= kLowerPenaltyAreaYBoundary)
            {
                // updatePlayers.cpp:280 — ballInLowerPenaltyArea = 1.
                Memory.WriteWord(Memory.Addr.ballInLowerPenaltyArea, 1);
                inPenaltyArea = true;
            }
        }

        // updatePlayers.cpp:286-336 — goalkeeper area test (subset of penalty).
        // X in [273, 398] AND (Y <= 158 [upper] OR Y >= 740 [lower]).
        // The asm computes after the penalty-area test; we mirror that.
        if (inPenaltyArea)
        {
            bool inGkXRange =
                ballX >= kGoalkeeperAreaXMin && ballX <= kGoalkeeperAreaXMax;
            if (inGkXRange &&
                (ballY <= kGoalkeeperAreaUpperY || ballY >= kGoalkeeperAreaLowerY))
            {
                // updatePlayers.cpp:336 — ballInGoalkeeperArea = 1.
                Memory.WriteWord(Memory.Addr.ballInGoalkeeperArea, 1);
            }
        }

        // ----- updatePlayers.cpp:338-368 --------------------------------
        // Clear goalOut if game is in progress AND goalOut was set AND
        // ball is NOT in upper penalty area. Semantically: a goal-out (goal
        // kick) is "consumed" once the ball has cleared the upper penalty
        // area.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl == kStGameInProgress)
        {
            short goalOut = Memory.ReadSignedWord(Memory.Addr.goalOut);
            if (goalOut != 0)
            {
                uint upPa = Memory.ReadDword(Memory.Addr.ballInUpperPenaltyArea);
                if (upPa == 0)
                {
                    Memory.WriteWord(Memory.Addr.goalOut, 0);
                }
            }
        }

        // ----- updatePlayers.cpp:370-393 --------------------------------
        // updatePlayerIndex: cycles 0..10 across ticks. Used by AI / camera
        // logic to amortise per-player work that doesn't need to run every
        // sprite every tick.
        short upIdx = Memory.ReadSignedWord(teamBase + TeamData.OffUpdatePlayerIndex);
        upIdx++;
        if (upIdx == 11) upIdx = 0;
        Memory.WriteWord(teamBase + TeamData.OffUpdatePlayerIndex, upIdx);

        // ----- updatePlayers.cpp:395-... --------------------------------
        // Per-player loop. D4 counter starts at 10 (i.e., 11 iterations).
        // Each iteration: dereference team.players[i] to get sprite pointer,
        // copy direction → playerDirection, set isMoving from delta, branch
        // on playerState.
        //
        // We iterate slotInTeam 0..10 (0 = keeper, 1..10 = outfielders).
        for (int slotInTeam = 0; slotInTeam < 11; slotInTeam++)
        {
            int spriteAddr = TeamData.GetTeamSpriteAddr(topTeam, slotInTeam);
            if (spriteAddr == 0) continue;  // not yet wired

            // updatePlayers.cpp:411-414 — playerDirection = direction.
            short direction = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDirection);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffPlayerDirection, direction);

            // updatePlayers.cpp:415-427 — isMoving = (deltaX | deltaY) != 0.
            // Default isMoving=0; set to -1 (all bits) if either delta is non-zero.
            int dx = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffDeltaX);
            int dy = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffDeltaY);
            // The asm uses byte offset +94, writing -1 (0xFF). We mirror that.
            byte isMoving = (byte)((dx == 0 && dy == 0) ? 0x00 : 0xFF);
            Memory.WriteByte(spriteAddr + PlayerSprite.OffIsMoving, isMoving);

            // OpenSWOS energy drain (optional enhancement; original SWOS has no
            // in-match fatigue). Runs every per-team-tick for every player so
            // the energy bar reflects real movement; the speed penalty it feeds
            // is gated separately by PlayerEnergy.EffectEnabled.
            PlayerEnergy.DrainSlot(spriteAddr);

            // updatePlayers.cpp:429-455 — early-out branches for TACKLED + ROLLING_INJURED.
            byte state = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerState);
            if (state == (byte)PortPlayerState.kTackled)
            {
                TickTackledPlayer(spriteAddr, topTeam);
                continue;
            }
            if (state == (byte)PortPlayerState.kInjured)
            {
                TickInjuredRollingPlayer(spriteAddr, topTeam);
                continue;
            }

            // updatePlayers.cpp:457-535 + 537-679 are pre-dispatch:
            //   - controlledPlayer check + injury-skip → updatePlayerBallDistance
            //   - update ball distance flags (plVeryCloseToBall etc.)
            //   - update ball height flags (ballLessEqual4 etc.)
            // We collapse into a single stub call until ported.
            UpdatePlayerBallDistanceAndHeight(teamBase, spriteAddr);

            // updatePlayers.cpp:1193-1194 (non-controlled-player branch) —
            // populate per-iteration ball prediction (ballDefensive*, ballNotHigh*,
            // strikeDestX/Y/Z). The asm passes A1 (player) + A2 (ball) + A6 (team).
            // Wire it through BallVariables.UpdateBallVariables.
            BallVariables.UpdateBallVariables(spriteAddr, BallSprite.Base, teamBase);

            // updatePlayers.cpp:680-836 — state dispatch.
            DispatchByPlayerState(state, spriteAddr, slotInTeam, topTeam);

            // l_check_if_this_player_getting_booked (updatePlayers.cpp:8933-
            // 9049) used to be called HERE, post-dispatch, for every sprite.
            // That diverged from the asm ordering: the original branch lives
            // INSIDE the l_not_controlled_player stoppage tail, between the
            // destReachedState promotion (:8885-8900) and the substituted/
            // sent-away/position chain (:9051+), and BOTH of its exits jump
            // to l_update_player_speed_and_deltas — skipping the break-
            // position writers. Called from here instead, the free-kick
            // wall / foul-pushback dest writers (:9432-9667 / :9939-10077)
            // ran FIRST each round-robin tick and their dest fed the delta
            // recompute, so the booked player equilibrated short of the
            // (foulX+21, foulY) spot, refState pinned at kRefWaitingPlayer,
            // and the breakCameraMode==3 gate froze the match (task #170).
            // Moved to TickAiControlled's stoppage tail (asm position).
        }

        // ----- updatePlayers.cpp:10367-10456 ----------------------------
        // Loop epilogue: post-loop "did keeper just play?" tracking and the
        // nobodysBallTimer = 50 reset when the goalie changes since previous
        // tick. lastPlayerPlayed + prevLastPlayer compared against
        // goalie1Sprite / goalie2Sprite. Fully ported in
        // TickEpilogueKeeperTransition (defined below in this file).
        TickEpilogueKeeperTransition(topTeam);
    }

    // ====================================================================
    // CheckIfThisPlayerGettingBooked — updatePlayers.cpp:8933-9049
    // ====================================================================
    //
    // Per-player post-dispatch check that drives the booking-walk path. The
    // C++/asm body is:
    //   if (A1 == bookedPlayer) {
    //       D1 = foulXCoordinate + 21;
    //       D2 = foulYCoordinate;
    //       if (sprite.x.whole != D1 || sprite.y.whole != D2) {
    //           // l_player_not_by_foul_spot — walk toward foul spot.
    //           sprite.destX = D1;
    //           sprite.destY = D2;
    //       } else if (refState == kRefWaitingPlayer) {
    //           refState = kRefAboutToGiveCard;
    //           SetPlayerAnimationTable(plGetting{Yellow,Red,2ndYellow}Card);
    //           sprite.playerState = PL_BOOKED;
    //           sprite.playerDownTimer = 1;
    //       }
    //   }
    //
    // The asm has additional gates (controlledPlayer check, gameStatePl ==
    // ST_GAME_IN_PROGRESS check via the `l_this_is_substituted_player`
    // upstream branch, breakState filtering for corners / free kicks). For
    // the smoke-readiness wire we keep the core path: walk + state transition.
    // The card-receiving animation tables (plGettingYellowCardAnimTable etc.)
    // are not yet in Memory.cs — we reuse playerNormalStandingAnimTable as a
    // placeholder so the player stops moving while booked. Visible behavior
    // for the smoke test: the referee's FSM advances kRefIncoming →
    // kRefWaitingPlayer → kRefAboutToGiveCard → kRefBooking, the blink
    // sentinel fires PutRefereeToLeavingState, and (for red cards) the
    // booked player is sent off via SendPlayerAway.
    //
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:8933-9049.
    private const short kRefWaitingPlayer    = 2;
    private const short kRefAboutToGiveCard  = 3;
    private const byte  kPlayerStateBooked   = 12;
    // Returns TRUE when this sprite IS the booked player — the caller must
    // then jump straight to l_update_player_speed_and_deltas (asm:9040-9049
    // `jmp @@update_player_speed_and_deltas` on every exit of the branch),
    // skipping the substituted / sent-away / break-position chain.
    private static bool CheckIfThisPlayerGettingBooked(int spriteAddr, int teamBase)
    {
        int bookedPlayer = Memory.ReadSignedDword(Memory.Addr.bookedPlayer);
        if (bookedPlayer == 0 || bookedPlayer != spriteAddr) return false;

        // updatePlayers.cpp:8947-8956 — D1 = foulXCoordinate + 21; D2 = foulYCoordinate.
        short foulX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short foulY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);
        short destD1 = (short)(foulX + 21);
        short destD2 = foulY;

        // updatePlayers.cpp:8958-8982 — cmp word ptr D1, [esi+(Sprite.x+2)] /
        //                                cmp word ptr D2, [esi+(Sprite.y+2)].
        // If either differs → l_player_not_by_foul_spot (set dest + walk).
        short curX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short curY = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        if (curX != destD1 || curY != destD2)
        {
            // updatePlayers.cpp:9043-9049 — l_player_not_by_foul_spot.
            // Aim the sprite at the foul spot so the regular move path
            // (UpdatePlayerSpeedAndFrameDelay → RecomputeSpriteDeltas)
            // walks it there next tick.
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, destD1);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, destD2);
            return true;    // asm:9043-9049 → jmp @@update_player_speed_and_deltas
        }

        // updatePlayers.cpp:8984-8995 — cmp refState, REF_WAITING_PLAYER.
        // If the referee hasn't arrived yet (still kRefIncoming), bail out;
        // the player will keep standing on the foul spot until the ref
        // catches up.
        short refState = Memory.ReadSignedWord(Memory.Addr.refState);
        if (refState != kRefWaitingPlayer)
            return true;    // asm:8994-8995 → jnz @@update_player_speed_and_deltas

        // updatePlayers.cpp:8997 — mov refState, REF_ABOUT_TO_GIVE_CARD.
        // Next Referee.UpdateReferee call's UpdateRefereeState switch will
        // transition kRefAboutToGiveCard → kRefBooking (initialising the
        // card-specific animation table) on the very next tick.
        Memory.WriteWord(Memory.Addr.refState, kRefAboutToGiveCard);
        Referee.NotifyEnteredAboutToGiveCard();

        // updatePlayers.cpp:8998-9035 — per-card SetPlayerAnimationTable
        // dispatch on whichCard. The plGetting{Yellow,Red,2ndYellow}CardAnimTable
        // addresses aren't ported yet; use the standing-animation table as a
        // placeholder so the booked player at least stops cleanly (the card
        // visibility itself comes from the referee + booked-number sprites).
        PlayerActions.SetPlayerAnimationTable(spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);

        // updatePlayers.cpp:9037-9041 — playerState = PL_BOOKED, playerDownTimer = 1.
        // PL_BOOKED is the gate UpdateBookedPlayerNumberSprite checks at
        // referee.cpp:106 — without it, the player-number digit never paints
        // and the blink-table never advances.
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, kPlayerStateBooked);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, 1);
        return true;        // asm:9040 → jmp @@update_player_speed_and_deltas
    }

    // ===================================================================
    // Per-state stubs — TODO ports by follow-up agents.
    // ===================================================================

    // updatePlayers.cpp:~880-1300 — l_player_goalkeeper handler.
    // Triggered when playerOrdinal == 1 (keeper). Bookkeeps shot chance
    // table, decides whether to dive, drives goalkeeper movement.
    //
    // Mapping (the asm dispatches sub-state inside l_player_goalkeeper):
    //   - kGoalieCatchingBall (state=4) → PlayerUpdate.TickGoalieCatchingBall.
    //     from updatePlayers.cpp:3059 + 1021.
    //   - kGoalieClaimed (state=11) → PlayerUpdate.TickGoalieClaimed.
    //     from updatePlayers.cpp:3163.
    //   - kNormal: dive-decision via ShouldGoalkeeperDive → GoalkeeperJumping.
    //     from updatePlayers.cpp:10485 + 10829.
    //   - kGoalieDivingHigh/Low (states 6/7): timer-driven completion.
    //     from updatePlayers.cpp:3188 (l_goalie_diving).
    private static void TickGoalkeeper(int spriteAddr, bool topTeam, int slotInTeam)
    {
        int teamBase = TeamData.Base(topTeam);
        byte state = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerState);

        // ----- MAIN-DISPATCHER PARITY — updatePlayers.cpp:743-758 -----------
        // USER-VISIBLE BUG FIX (2026-07-02, "keeper runs to the pitch corner
        // when the ball goes out behind his goal"):
        // The original per-sprite dispatcher (l_check_player_state) routes
        // PL_GOALIE_DIVING_HIGH (6) / PL_GOALIE_DIVING_LOW (7) straight to
        // l_goalie_diving (updatePlayers.cpp:743-745 + 756-758) BEFORE the
        // `playerOrdinal == 1 → l_player_goalkeeper` check at :843-849. A
        // diving keeper therefore NEVER executes the l_player_goalkeeper head
        // below — in particular the unconditional speed refresh at :962-969
        // (speed = kGoalkeeperGameSpeed = 1024).
        //
        // Our port used to run the head for ALL keeper states and handled the
        // dive AFTER it: the per-tick 1024 speed rewrite cancelled the dive's
        // own speed decay (l_set_new_goalkeeper_speed, :3403-3418), so the
        // keeper glided at full walk speed toward GoalkeeperJumping's dive
        // destination (keeper.pos ± kDefaultDestinations[dir] ≈ ±1000 px,
        // :10967-11016) for the whole 75-tick playerDownTimer — ~150 px of
        // travel that read on-screen as "keeper sprints toward the corner
        // flag during the goal-out/corner ceremony, then walks back".
        // Routing the dive FIRST (as the original dispatcher does) both
        // skips the speed refresh and lets the faithful decay stick.
        if (state == (byte)PortPlayerState.kGoalieDivingHigh ||
            state == (byte)PortPlayerState.kGoalieDivingLow)
        {
            TickGoalieDiving(spriteAddr, teamBase, topTeam);
            return;
        }

        // ----- updatePlayers.cpp:917-941 — goalkeeperPlaying branch ---------
        // (l_player_goalkeeper head, after updatePlayerShotChanceTable.)
        // If the keeper currently "plays like an ordinary player" (he took a
        // back-pass with his feet), assert goaliePlayingOrOut=1 /
        // ballOutOfPlayOrKeeper=0 and — if he is NOT the controlled player —
        // kludge goalkeeperPlaying back to 0 (asm comment: "goalkeeper
        // playing, but not controlled player, kludge it").
        //
        // BUG FIX (2026-07-03, task #166 "keeper abandons a received back-pass"):
        // the original `cmp A1, [esi+controlledPlayer] / jz l_its_controlled_player`
        // at :928-939 jumps STRAIGHT to the controlled-player branch. It must
        // NOT fall through to l_update_ball_out_or_keepers (:943-946), whose
        // `goaliePlayingOrOut = 0` write would erase the flag InputControls.
        // UpdateControlledPlayer needs to keep the keeper switch-eligible
        // (swos.asm:100911-100917 skips ordinal-1 sprites whenever
        // goaliePlayingOrOut == 0). Our previous fall-through clobbered the
        // flag every keeper tick, control switched off the keeper to an
        // outfielder, the next keeper tick then took the `spriteAddr != gkCtrl`
        // arm and kludged goalkeeperPlaying back to 0 — keeper AI resumed and
        // walked him away from the ball he had just received.
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:917-941
        //         external/swos-port/swos/swos.asm:112833-112843.
        {
            short gkPlaying = Memory.ReadSignedWord(teamBase + TeamData.OffGoalkeeperPlaying);
            if (gkPlaying != 0)
            {
                // :926-927 — byte writes in the asm; the fields are word slots
                // zero-initialised by InitTeamsData, so a byte write leaves the
                // high byte 0 and the word reads used by InputControls see the
                // same non-zero/zero truth value as the original.
                Memory.WriteByte(teamBase + TeamData.OffGoaliePlayingOrOut, 1);
                Memory.WriteByte(teamBase + TeamData.OffBallOutOfPlayOrKeeper, 0);
                int gkCtrl = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
                if (spriteAddr == gkCtrl)
                {
                    // :938-939 — jz l_its_controlled_player. Guarded on kNormal
                    // for dispatcher parity: a state-4/11 keeper is routed by
                    // the original main dispatcher (updatePlayers.cpp:743-836)
                    // BEFORE the ordinal-1 check and never executes this block.
                    if (state == (byte)PortPlayerState.kNormal)
                    {
                        // updatePlayers.cpp:907 — updatePlayerShotChanceTable
                        // runs before this branch in the original head; keep it
                        // for the controlled-keeper route too.
                        int piBaseCtrl = OwnGoaliePlayersBase(topTeam);
                        if (piBaseCtrl != 0)
                            TeamPort.UpdatePlayerShotChanceTable(topTeam, piBaseCtrl);
                        TickHumanControlled(spriteAddr, topTeam);
                        return;
                    }
                }
                else
                {
                    // :941 — mov [esi+TeamGeneralInfo.goalkeeperPlaying], 0.
                    Memory.WriteWord(teamBase + TeamData.OffGoalkeeperPlaying, 0);
                }
            }
        }

        // ----- updatePlayers.cpp:943-969 — l_update_ball_out_or_keepers -----
        // BUG FIX (2026-07-02, stoppage fidelity): every keeper tick recomputes
        // the two election-eligibility flags:
        //   ballOutOfPlayOrKeeper = 0; goaliePlayingOrOut = 0;
        //   if (gameStatePl != ST_GAME_IN_PROGRESS) { both = -1; speed = 1024; }
        //   speed = kGoalkeeperGameSpeed (1024);
        // The stopped-state -1 writes were MISSING from our port. They are what
        // makes the KEEPER eligible in UpdateControlledPlayer (gate on
        // goaliePlayingOrOut, swos.asm:100911-100917) and in
        // UpdatePlayerBeingPassedTo (gate on ballOutOfPlayOrKeeper,
        // swos.asm:101275-101281) DURING A BREAK — i.e. they are the mechanism
        // by which the goalkeeper is elected as the GOAL-KICK taker (his break
        // spot at the 6-yard box corner is the closest sprite to the goal-kick
        // foul spot). Without them an outfielder was elected instead.
        // kGoalkeeperSpeedWhenGameStopped @ swos.asm:203909 = 1024;
        // kGoalkeeperGameSpeed @ swos.asm:203921 = 1024 (same value — the
        // second write is kept for asm parity).
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:943-969
        //         external/swos-port/swos/swos.asm:112845-112861.
        const short kGoalkeeperSpeedWhenGameStopped = 1024;
        const short kGoalkeeperGameSpeed = 1024;
        {
            Memory.WriteByte(teamBase + TeamData.OffBallOutOfPlayOrKeeper, 0);   // :945
            Memory.WriteByte(teamBase + TeamData.OffGoaliePlayingOrOut, 0);      // :946
            short gsPlFlags = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsPlFlags != kStGameInProgress)                                  // :947-958
            {
                Memory.WriteByte(teamBase + TeamData.OffGoaliePlayingOrOut, 0xFF);    // :960 (byte -1)
                Memory.WriteByte(teamBase + TeamData.OffBallOutOfPlayOrKeeper, 0xFF); // :961 (byte -1)
                Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, kGoalkeeperSpeedWhenGameStopped); // :962-964
            }
        }
        // updatePlayers.cpp:966-969 — l_update_goalkeeper_speed: unconditional
        // sprite.speed = kGoalkeeperGameSpeed. Without this our keeper sits at
        // speed=0 and never moves toward the destX/destY that
        // SetPlayerWithNoBallDestination wrote, so any shot sails past while
        // the keeper stands still.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, kGoalkeeperGameSpeed);

        // Keeper-holds-ball stoppage timer + auto-release.
        // Mechanical port of updatePlayers.cpp:16469-16776 + gameLoop.cpp:530-537.
        // While gameState == ST_KEEPER_HOLDS_BALL, the holding keeper's
        // stoppageTimerActive counts up; at 150 (~2.1 s @ 70 Hz) the keeper
        // auto-fires PlayerKickingBall to put the ball back in play, then
        // gameState transitions to ST_GAME_IN_PROGRESS. Without this, the
        // keeper holds the ball forever and the match stalls (smoke-test
        // symptom 2026-06-01).
        // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:16759
        //     external/swos-port/src/game/player.cpp:750
        if (PlayerUpdate.TickGoalkeeperHoldAutoRelease(spriteAddr, topTeam))
        {
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:1021 — kGoalieCatchingBall sub-state.
        if (state == (byte)PortPlayerState.kGoalieCatchingBall)
        {
            PlayerUpdate.TickGoalieCatchingBall(spriteAddr, topTeam);
            // updatePlayers.cpp post-catching → falls into update_player_speed.
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:3163 — kGoalieClaimed sub-state.
        if (state == (byte)PortPlayerState.kGoalieClaimed)
        {
            PlayerUpdate.TickGoalieClaimed(spriteAddr);
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // (Dive states 6/7 are routed to TickGoalieDiving at the top of this
        // method — main-dispatcher parity, updatePlayers.cpp:743-758.)

        // updatePlayers.cpp:1102-1141 — l_goalie_not_catching_the_ball routing.
        // A normal-state keeper is re-dispatched through the SAME three-way
        // router as an outfielder BEFORE any keeper-specific logic runs:
        //   cmp A1, [esi+controlledPlayer]  ; jz  l_its_controlled_player
        //   cmp A1, [esi+passToPlayerPtr]   ; jz  l_player_expecting_pass
        //   cmp gameStatePl, 100            ; jnz l_not_controlled_player
        // The third check is what walks the keeper back to his goal mouth
        // during restarts (gameState=0 post-goal / pre-kickoff): he runs the
        // generic not-controlled stoppage tail (SetPlayerPositionsForGameBreak
        // + destReachedState promotion) exactly like the other 20 sprites.
        // Without it the keeper's destReachedState never reaches kReached and
        // the breakCameraMode-3 arrival poll (gameLoop.cpp:1577-1596) waits
        // forever. The first check is equally load-bearing: after a claim
        // GoalkeeperClaimedTheBall sets controlledPlayer=keeper (player.cpp:2326)
        // and this route is how the human (or AI joystick) moves the keeper
        // and fires the release kick.
        {
            int gkControlled = TeamData.ControlledPlayer(topTeam);
            if (spriteAddr == gkControlled)
            {
                // updatePlayers.cpp:1114-1115 — jz @@its_controlled_player.
                TickHumanControlled(spriteAddr, topTeam);
                return;
            }
            int gkPassTo = Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr);
            if (spriteAddr == gkPassTo && gkPassTo != 0)
            {
                // updatePlayers.cpp:1126-1127 — jz @@player_expecting_pass.
                //
                // STOPPAGE ROUTE (2026-07-02): during a break the pass-expecting
                // branch must take the faithful cseg_82AAF..cseg_82EC2 path
                // (walk to the ball; promote to taker only at ballDistance==0;
                // hold position when the taker already owns the spot). The
                // PlayerControlled port of that slice commits l_pass_success
                // unconditionally — see TickPassExpectingStopped for the
                // faithful stoppage body. In-progress ticks keep the existing
                // PlayerControlled route.
                short gkGsPl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
                if (gkGsPl != kStGameInProgress)
                {
                    TickPassExpectingStopped(spriteAddr, topTeam);
                    return;
                }
                PlayerControlled.RunPassExpectingBranch(spriteAddr, topTeam);
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
                return;
            }
            short gsPlRoute = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsPlRoute != kStGameInProgress)
            {
                // updatePlayers.cpp:1139-1140 — jnz @@not_controlled_player.
                TickAiControlled(spriteAddr, topTeam, slotInTeam);
                return;
            }
        }

        // updatePlayers.cpp:910-960 — normal-state keeper: read shot chance
        // table, check if ball is in penalty area / on a trajectory to score.
        // If so, decide whether to dive. This is the "main" goalie tick.
        //
        // updatePlayers.cpp:907 — updatePlayerShotChanceTable(team, sprite).
        // Looks up the goalie's PlayerInfo by shirt number and points the
        // team's shotChanceTable at the appropriate row of kGoalieSkillTables.
        // Mechanical port: resolve PlayerInfo from sprite ordinal +
        // OwnPlayersBase, then dispatch via TeamPort.
        // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:907.
        {
            short ordinal = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
            int piBase = OwnGoaliePlayersBase(topTeam);
            if (ordinal >= 1 && ordinal <= 11 && piBase != 0)
            {
                int piAddr = piBase + (ordinal - 1) * 61;
                TeamPort.UpdatePlayerShotChanceTable(topTeam, piAddr);
            }
        }

        // updatePlayers.cpp:10485 — ShouldGoalkeeperDive returns true if the
        // keeper should attempt a save this frame.
        ushort upPa = Memory.ReadWord(Memory.Addr.ballInUpperPenaltyArea);
        ushort loPa = Memory.ReadWord(Memory.Addr.ballInLowerPenaltyArea);
        bool ballInOurArea = (topTeam && upPa != 0) || (!topTeam && loPa != 0);

        // ===================================================================
        // TASK #71 DEEP AUDIT (2026-07-03): mechanical port of the ORIGINAL
        // keeper in-area body — updatePlayers.cpp:1143-2940. The previous
        // paraphrase here (a) never repositioned the keeper toward the shot
        // (dest stayed at his own position, so sprite.deltaX stayed 0 and the
        // shouldGoalkeeperDive `framesNeeded(keeper.deltaX, …) == 0` gate at
        // :10692 vetoed every dive until the desperation window), (b) never
        // ran the cseg_7FCD0 CATCH chain (goalkeeperCaughtTheBall had NO
        // caller), and (c) invented a fallback dive with the keeper's current
        // facing as direction. Measured effect: 27/40 straight shots at a
        // skill-7 keeper scored. All three replaced with the original chain.
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp
        //         :1143-2940 (labels preserved below).
        // ===================================================================
        RunGoalkeeperInAreaChain(spriteAddr, teamBase, topTeam, ballInOurArea);
    }

    // ===================================================================
    // RunGoalkeeperInAreaChain — updatePlayers.cpp:1143-2940
    // (l_ball_in_penalty_area .. l_clamp_ball_y_inside_pitch, plus the
    //  l_check_pass_to_player / l_this_player_last_played tails)
    // ===================================================================
    //
    // Entered from the keeper tick with gameStatePl == ST_GAME_IN_PROGRESS
    // (the router above already diverted every stoppage tick). Ends at
    // l_update_player_speed_and_deltas in all paths.
    //
    // shotChanceTable row semantics (derived from consumers; row = 30 int16s
    // of kGoalieSkillTables[goalieSkill], team.cpp:5-13):
    //   [+6]  (elem 3)  — keeper run-to-shot speed (832 worst .. 1024 best)
    //                     consumers :1699/:2056/:2591.
    //   [+8]  (elem 4)  — keeper fall-back speed after a no-dive decision
    //                     (160 worst .. 256 best), consumer :2557.
    //   [+10..+40] (elems 5..20) — per-tick dive-delta selector, consumer
    //                     shouldGoalkeeperDive :10744 (0 best → slowest
    //                     kGoalkeeperDiveDeltas[0] → largest frames → dive
    //                     passes the `d3 >= d1` gate most often).
    //   [+48] (elem 24) — catching-state claim-vs-deflect gate, :3130.
    //   [+52]/[+54] (elems 26/27) — deflect strength windows, player.cpp:2431+.
    //   [+58] (elem 29) — reaction-frequency gate ((tick&0xF0)>>4 compared),
    //                     consumers :1217 and :2650 (12 best → reacts 12/16).
    //
    // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:1143.
    private static void RunGoalkeeperInAreaChain(int spriteAddr, int teamBase,
        bool topTeam, bool ballInOurArea)
    {
        int a2Ball = BallSprite.Base;
        int scTab = Memory.ReadSignedDword(teamBase + TeamData.OffShotChanceTable);
        // Local accessor for the shot-chance row; byteOff is the asm byte
        // offset ([esi+N]). Falls back to the outfielder table if the per-team
        // buffer was never seated (should not happen post-SeedTeamData).
        short Row(int byteOff) => scTab != 0
            ? Memory.ReadSignedWord(scTab + byteOff)
            : TeamPort.kPlayerShotChanceTable[byteOff >> 1];

        short d0w, d1w, d2w;

        // ----- updatePlayers.cpp:970-1008 — wrong-half guard -----------------
        // A keeper caught in the OPPOSITE half skips the whole in-area body:
        // keeper.y <= 449 (upper half): bottom-team keeper →
        // l_this_player_last_played (:983-993); keeper.y > 449 (ja,
        // l_goalkeeper_in_lower_half): top-team keeper → same (:997-1008).
        // (The original places this before the controlled/pass router; we run
        // it at the chain head so the stoppage routing above stays intact —
        // during breaks l_this_player_last_played routes identically.)
        {
            ushort gkYw0 = (ushort)Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
            if (gkYw0 <= 449)
            {
                if (!topTeam) goto l_this_player_last_played;
            }
            else
            {
                if (topTeam) goto l_this_player_last_played;
            }
        }

        // ----- updatePlayers.cpp:1143-1177 — ball in OUR penalty area? -------
        // top team ↔ ballInUpperPenaltyArea (:1160-1168); bottom team ↔
        // ballInLowerPenaltyArea (cseg_7F0DF, :1170-1177). Precomputed by the
        // caller from the same globals.
        if (!ballInOurArea) goto l_check_pass_to_player;

        // ----- l_ball_in_penalty_area (:1179-1191) ---------------------------
        // cmp A1, lastPlayerPlayed ; jz @@this_player_last_played.
        if (spriteAddr == Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed))
            goto l_this_player_last_played;

        // :1193-1194 — updateBallVariables + calculateBallNextGroundXYPositions.
        BallVariables.UpdateBallVariables(spriteAddr, a2Ball, teamBase);
        BallVariables.CalculateBallNextGroundXYPositions(a2Ball);

        // :1195-1201 — ballInGoalkeeperArea != 0 → @@ball_in_lower_goalkeeper_area.
        if (Memory.ReadWord(Memory.Addr.ballInGoalkeeperArea) != 0)
            goto l_ball_in_lower_goalkeeper_area;

        // :1203-1228 — D0 = (currentGameTick & 0xF0) >> 4; cmp D0, [scTab+58];
        // jnb @@ball_standing_in_goalkeeper_area (unsigned >=).
        {
            ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
            if ((ushort)((tick & 0xF0) >> 4) >= (ushort)Row(58))
                goto l_ball_standing_in_goalkeeper_area;
        }

    l_ball_in_lower_goalkeeper_area:;
        // :1231-1237 — ballNextGroundX < 0 (sentinel) → @@ball_standing….
        d1w = Memory.ReadSignedWord(Memory.Addr.ballNextGroundX);
        if (d1w < 0) goto l_ball_standing_in_goalkeeper_area;

        // :1239-1242 — D1 = ballNextGroundX; D2 = ballNextGroundY.
        d2w = Memory.ReadSignedWord(Memory.Addr.ballNextGroundY);

        // :1243-1352 — predicted landing point inside OUR penalty box?
        // top team (A6 != bottomTeamData): Y ∈ [137, 216]; bottom (cseg_7F1C7):
        // Y ∈ [682, 761]; both: X ∈ [193, 478]. Outside → @@ball_standing….
        if (topTeam)
        {
            if (d2w < 137) goto l_ball_standing_in_goalkeeper_area;   // :1255-1265 jl
            if (d2w > 216) goto l_ball_standing_in_goalkeeper_area;   // :1267-1277 jg
        }
        else
        {
            if (d2w < 682) goto l_ball_standing_in_goalkeeper_area;   // :1306-1316 jl
            if (d2w > 761) goto l_ball_standing_in_goalkeeper_area;   // :1318-1328 jg
        }
        if (d1w < 193) goto l_ball_standing_in_goalkeeper_area;       // :1279-1289 / :1330-1340
        if (d1w > 478) goto l_ball_standing_in_goalkeeper_area;       // :1291-1301 / :1342-1352

        // ----- l_in_penalty_area (:1354-1467) --------------------------------
        // D1acc = (nextX - keeper.x)² + (nextY - keeper.y)²  (imul 16→32);
        // D2acc = (nextX - ball.x)²   + (nextY - ball.y)²;
        // if ((D1acc << 2) > D2acc) → @@ball_standing… (ja, unsigned) — the
        // keeper can't beat the ball to the landing spot.
        {
            short gkXw = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
            short gkYw = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
            short bXw = BallSprite.XPixels;
            short bYw = BallSprite.YPixels;
            int dxK = (short)(d1w - gkXw); int dyK = (short)(d2w - gkYw);
            int d1Acc = dxK * dxK + dyK * dyK;                        // :1355-1398
            int dxB = (short)(d1w - bXw); int dyB = (short)(d2w - bYw);
            int d2Acc = dxB * dxB + dyB * dyB;                        // :1399-1442
            if ((uint)(d1Acc << 2) > (uint)d2Acc)                     // :1443-1458 ja
                goto l_ball_standing_in_goalkeeper_area;

            // :1460-1467 — dest = predicted landing spot; speed =
            // kGoalkeeperMoveToBallSpeed (1024, swos.asm:202379).
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, d1w);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, d2w);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed,
                Memory.ReadSignedWord(Memory.Addr.kGoalkeeperMoveToBallSpeed));
            goto cseg_7FCD0;
        }

    l_ball_standing_in_goalkeeper_area:;
        // :1469-1481 — gameStatePl != in-progress → cseg_7FCA1 →
        // l_this_player_last_played (:2596-2597).
        if (Memory.ReadSignedWord(Memory.Addr.gameStatePl) != kStGameInProgress)
            goto l_this_player_last_played;

        // :1483-1507 — strikeDestX < 295 (jb) → @@shot_on_goal_or_close;
        // strikeDestX <= 376 (jbe) → @@goal_attempt (shot heading INSIDE the
        // goal mouth). Both compares unsigned.
        {
            ushort strikeX = Memory.ReadWord(Memory.Addr.strikeDestX);
            if (strikeX < 295) goto l_shot_on_goal_or_close;
            if (strikeX <= 376) goto l_goal_attempt;
        }

    l_shot_on_goal_or_close:;
        // :1509-1541 — keeper.ballDistance < 512 (jnb → don't) AND !ballAbove17
        // → chase the ball directly (dest = ball.xy) → catch chain.
        if ((uint)Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance) >= 512u)
            goto l_goalkeeper_dont_throw;
        if (Memory.ReadByte(teamBase + TeamData.OffBallAbove17) != 0)
            goto l_goalkeeper_dont_throw;
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, BallSprite.XPixels);   // :1533-1536
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, BallSprite.YPixels);   // :1537-1540
        goto cseg_7FCD0;

    l_goalkeeper_dont_throw:;
        // :1543-1551 — ballAbove17 → cseg_7F511 (defensive-line intercept).
        if (Memory.ReadByte(teamBase + TeamData.OffBallAbove17) != 0)
            goto cseg_7F511;

        // :1553-1559 — ballInGoalkeeperArea → @@center_goalkeeper_on_ball_x.
        if (Memory.ReadWord(Memory.Addr.ballInGoalkeeperArea) != 0)
            goto l_center_goalkeeper_on_ball_x;

        // :1561-1583 — is the ball travelling TOWARD our end?
        //   ball.y < 449 (jb, unsigned) → cseg_7F3F8: deltaY >= 0 (jns) →
        //   cseg_7F511, else cseg_7F409;
        //   ball.y >= 449: deltaY <= 0 (jle) → cseg_7F511, else cseg_7F409.
        if ((ushort)BallSprite.YPixels < 449)
        {
            if (BallSprite.DeltaY >= 0) goto cseg_7F511;              // cseg_7F3F8 :1585-1593
        }
        else
        {
            if (BallSprite.DeltaY <= 0) goto cseg_7F511;              // :1575-1581
        }

        // cseg_7F409 (:1595-1622) — destX = ((ball.x - 336) >> 1 [sar]) + 336.
        {
            short dx0 = (short)(BallSprite.XPixels - 336);
            dx0 >>= 1;                                                // sar
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, (short)(dx0 + 336));
        }
        goto cseg_7F458;

    l_center_goalkeeper_on_ball_x:;
        // :1624-1628 — destX = ball.x.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, BallSprite.XPixels);

    cseg_7F458:;
        // :1630-1689 — destY: ball.y >= 449 → D0 = ((769 - ball.y) >> 1 [shr])
        // + ball.y; else (cseg_7F49A) D0 = ((ball.y - 129) >> 1 [shr]) + 129.
        {
            ushort bY = (ushort)BallSprite.YPixels;
            if (bY < 449)
            {
                d0w = (short)((ushort)((ushort)(bY - 129) >> 1) + 129);   // cseg_7F49A :1670-1689
            }
            else
            {
                d0w = (short)((ushort)((ushort)(769 - bY) >> 1) + bY);    // :1645-1668
            }
        }
        // cseg_7F4C3 (:1691-1713) — destY = D0; speed = [scTab+6]; D0 = speed>>1.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, d0w);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, Row(6));
        goto cseg_7FCD0;

    cseg_7F511:;
        // :1715-1736 — ball.z > 16 (ja) → cseg_7F626; ballDefensiveX < 0 (js)
        // → cseg_7F626.
        if ((ushort)BallSprite.ZPixels > 16) goto cseg_7F626;
        if (Memory.ReadSignedWord(Memory.Addr.ballDefensiveX) < 0) goto cseg_7F626;

        // :1738-1864 — the keeper only claims the intercept spot if NO closer
        // interested sprite exists: compare keeper.ballDistance (unsigned ja →
        // bail) against our controlledPlayer, our passToPlayerPtr, opponent's
        // controlledPlayer, opponent's passToPlayerPtr.
        {
            uint d0Dist = (uint)Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance);
            int opp = Memory.ReadSignedDword(teamBase + TeamData.OffOpponentsTeam);
            int cand;
            cand = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);      // :1741-1768
            if (cand != 0 && d0Dist > (uint)Memory.ReadSignedDword(cand + PlayerSprite.OffBallDistance))
                goto cseg_7F626;
            cand = Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr);       // cseg_7F56B :1770-1798
            if (cand != 0 && d0Dist > (uint)Memory.ReadSignedDword(cand + PlayerSprite.OffBallDistance))
                goto cseg_7F626;
            cand = Memory.ReadSignedDword(opp + TeamData.OffControlledPlayer);           // cseg_7F597 :1800-1831
            if (cand != 0 && d0Dist > (uint)Memory.ReadSignedDword(cand + PlayerSprite.OffBallDistance))
                goto cseg_7F626;
            cand = Memory.ReadSignedDword(opp + TeamData.OffPassToPlayerPtr);            // cseg_7F5CC :1833-1864
            if (cand != 0 && d0Dist > (uint)Memory.ReadSignedDword(cand + PlayerSprite.OffBallDistance))
                goto cseg_7F626;
        }
        // cseg_7F601 (:1866-1872) — dest = (ballDefensiveX, ballDefensiveY).
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX,
            Memory.ReadSignedWord(Memory.Addr.ballDefensiveX));
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY,
            Memory.ReadSignedWord(Memory.Addr.ballDefensiveY));
        goto cseg_7FCD0;

    cseg_7F626:;
        // :1874-1884 — both arms jump to @@this_player_last_played.
        goto l_this_player_last_played;

    l_goal_attempt:;
        // :1886-1906 — A6 == lastTeamPlayed && playerHadBall == 0 → cseg_7FBEF.
        if (teamBase == Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed) &&
            Memory.ReadSignedWord(Memory.Addr.playerHadBall) == 0)
            goto cseg_7FBEF;

        // cseg_7F65A (:1908-1940) — ballDistance < 512 (jnb → skip) AND
        // !ballAbove17 → dest = ball.xy → l_shot_at_goal.
        if ((uint)Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance) < 512u &&
            Memory.ReadByte(teamBase + TeamData.OffBallAbove17) == 0)
        {
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, BallSprite.XPixels);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, BallSprite.YPixels);
            goto l_shot_at_goal;
        }

        // cseg_7F6A3 (:1942-1964) — ballDistance > 2048 (ja) → cseg_7F7BC;
        // ballAbove17 → cseg_7F7BC.
        if ((uint)Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance) > 2048u)
            goto cseg_7F7BC;
        if (Memory.ReadByte(teamBase + TeamData.OffBallAbove17) != 0)
            goto cseg_7F7BC;

        // :1966-1987 — destX = ((ball.x - 336) >> 1 [sar]) + 336.
        {
            short dx0 = (short)(BallSprite.XPixels - 336);
            dx0 >>= 1;
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, (short)(dx0 + 336));
        }
        // :1988-2046 — destY projection (same halving as cseg_7F458).
        {
            ushort bY = (ushort)BallSprite.YPixels;
            if (bY < 449)
            {
                d0w = (short)((ushort)((ushort)(bY - 129) >> 1) + 129);   // cseg_7F745 :2027-2046
            }
            else
            {
                d0w = (short)((ushort)((ushort)(769 - bY) >> 1) + bY);    // :2002-2025
            }
        }
        // cseg_7F76E (:2048-2070) — destY = D0; speed = [scTab+6].
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, d0w);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, Row(6));
        goto l_shot_at_goal;

    cseg_7F7BC:;
        // :2072-2194 — the far-shot trip-wire (ported in PlayerUpdate).
        switch (PlayerUpdate.RunShotTripWire(spriteAddr, a2Ball, teamBase, topTeam))
        {
            case PlayerUpdate.ShotChainExit.ShotAtGoal: goto l_shot_at_goal;
            case PlayerUpdate.ShotChainExit.C7FBEF:     goto cseg_7FBEF;
            case PlayerUpdate.ShotChainExit.C7FC01:     goto cseg_7FC01;
            default:                                    goto cseg_7FC48;
        }

    l_shot_at_goal:;
        // :2196-2544 — shot-at-goal RNG + dive (ported in PlayerUpdate).
        switch (PlayerUpdate.RunShotAtGoal(spriteAddr, a2Ball, teamBase, topTeam))
        {
            case PlayerUpdate.ShotChainExit.C7FC01: goto cseg_7FC01;
            default:                                goto l_clamp_ball_y_inside_pitch;
        }

    cseg_7FBEF:;
        // :2546-2550 — speed = dseg_1105EF (512, swos.asm:202375) → cseg_7FC23.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed,
            Memory.ReadSignedWord(Memory.Addr.dseg_1105EF));
        goto cseg_7FC23;

    cseg_7FC01:;
        // :2552-2559 — speed = [scTab+8] (fall-back speed, elem 4).
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, Row(8));

    cseg_7FC23:;
        // :2561-2567 — dest = (ballNotHighX, ballNotHighY).
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX,
            Memory.ReadSignedWord(Memory.Addr.ballNotHighX));
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY,
            Memory.ReadSignedWord(Memory.Addr.ballNotHighY));
        goto cseg_7FCD0;

    cseg_7FC48:;
        // :2569-2594 — ballAbove17 → cseg_7FC01; else chase the ball directly:
        // dest = ball.xy, speed = [scTab+6].
        if (Memory.ReadByte(teamBase + TeamData.OffBallAbove17) != 0)
            goto cseg_7FC01;
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, BallSprite.XPixels);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, BallSprite.YPixels);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, Row(6));
        goto cseg_7FCD0;

    cseg_7FCD0:;
        // ----- :2605-2778 — the CATCH chain ---------------------------------
        // BUG FIX (task #71): previously unwired — GoalkeeperCaughtTheBall had
        // no caller, so the keeper could never enter kGoalieCatchingBall (4)
        // from open play and TickGoalieCatchingBall's claim/deflect gates were
        // dead code.
        // :2606-2625 — own team last played it deliberately → no hands.
        if (teamBase == Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed) &&
            Memory.ReadSignedWord(Memory.Addr.playerHadBall) == 0)
            goto l_goalie_cant_catch_ball;

        // l_opponent_last_played (:2627-2661) — outside the 6-yard box the
        // catch only arms on (tick&0xF0)>>4 < [scTab+58] ticks (reaction gate).
        if (Memory.ReadWord(Memory.Addr.ballInGoalkeeperArea) == 0)
        {
            ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
            if ((ushort)((tick & 0xF0) >> 4) >= (ushort)Row(58))       // jnb
                goto l_goalie_cant_catch_ball;
        }

        // cseg_7FD39 (:2663-2696) — ballNextGroundX >= 0; 12 < ballDefensiveZ
        // <= 27 (the "catchable at chest height" window).
        if (Memory.ReadSignedWord(Memory.Addr.ballNextGroundX) < 0)    // js
            goto l_goalie_cant_catch_ball;
        {
            short defZ = Memory.ReadSignedWord(Memory.Addr.ballDefensiveZ);
            if (defZ > 27) goto l_goalie_cant_catch_ball;              // jg
            if (defZ <= 12) goto l_goalie_cant_catch_ball;             // jle
        }

        // :2698-2710 — keeper.ballDistance <= 2116 (ja → bail).
        if ((uint)Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance) > 2116u)
            goto l_goalie_cant_catch_ball;

        // :2712-2776 — |keeper.x - ballDefensiveX| <= 12 AND
        // |keeper.y - ballDefensiveY| <= 12 (both signed window compares).
        {
            short dxw = (short)(Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2)
                                - Memory.ReadSignedWord(Memory.Addr.ballDefensiveX));
            if (dxw > 12) goto l_goalie_cant_catch_ball;               // jg
            if (dxw < -12) goto l_goalie_cant_catch_ball;              // jl
            short dyw = (short)(Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2)
                                - Memory.ReadSignedWord(Memory.Addr.ballDefensiveY));
            if (dyw > 12) goto l_goalie_cant_catch_ball;               // jg
            if (dyw < -12) goto l_goalie_cant_catch_ball;              // jl
        }

        // :2778 — goalkeeperCaughtTheBall() (player state → kGoalieCatchingBall,
        // dest = ballNextGround, speed = kGoalkeeperCatchSpeed).
        PlayerUpdate.GoalkeeperCaughtTheBall(spriteAddr, topTeam);

    l_goalie_cant_catch_ball:;
        // ----- updatePlayers.cpp:2780-2905 — l_goalie_cant_catch_ball body ----
        // (ported earlier as RunGoalieCantCatchBallPickup; true = the BACK-PASS
        // foot-takeover arm fired → :2893 jmp l_update_player_speed_and_deltas.)
        if (RunGoalieCantCatchBallPickup(spriteAddr, teamBase, topTeam))
        {
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }
        goto l_clamp_ball_y_inside_pitch;

    l_check_pass_to_player:;
        // :2971-2990 — ball NOT in our penalty area. ballOutOfPlayOrKeeper = 0
        // (word write, :2973); if WE are the pass target, clear the pass
        // bookkeeping (the router above normally intercepts that case — kept
        // for asm parity); → l_this_player_last_played.
        Memory.WriteWord(teamBase + TeamData.OffBallOutOfPlayOrKeeper, 0);
        if (spriteAddr == Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr))
        {
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);   // :2987
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);        // :2988
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);    // :2989
        }
        // fall through — :2990 jmp @@this_player_last_played

    l_this_player_last_played:;
        // :2942-2969 — in-progress keeper walks home only every 8th frame
        // (`frameCount & 0x0E`; frameCount is the gameLoop.cpp:269 counter we
        // mirror in Memory.Addr.frameCounter). The walk itself is
        // l_this_is_substituted_player → setPlayerWithNoBallDestination — see
        // PlayerHeader.SetPlayerWithNoBallDestination for the goal-mouth
        // projection (and the bug-2 note about out-of-band ball Y; the router
        // above guarantees gameStatePl == 100 here).
        {
            int frame = Memory.ReadSignedDword(Memory.Addr.frameCounter);
            if ((frame & 0x0E) != 0) goto l_clamp_ball_y_inside_pitch;   // :2956-2967
            try
            {
                PlayerHeader.SetPlayerWithNoBallDestination(spriteAddr, teamBase,
                    BallSprite.XPixels, BallSprite.YPixels);
            }
            catch (System.Exception)
            {
                // Tactics table not yet populated — fall back to a no-op.
            }
        }

    l_clamp_ball_y_inside_pitch:;
        // ----- updatePlayers.cpp:2907-2940 — l_clamp_ball_y_inside_pitch ----
        {
            short destYc = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestY);
            if (destYc < 129)                                                    // :2908-2922
                Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, 129);
            destYc = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestY);
            if (destYc > 769)                                                    // :2924-2939
                Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, 769);
        }
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }


    // ===================================================================
    // RunGoalieCantCatchBallPickup — updatePlayers.cpp:2780-2905
    // (l_goalie_cant_catch_ball .. l_opponent_player_touched_the_ball)
    // ===================================================================
    //
    // The keeper's "ball at my feet" pickup. Reached every non-dive keeper
    // tick; the gates only fire when the ball is literally at the keeper
    // (squared ballDistance <= 72, ball below 17 px). Two outcomes:
    //   - own team last played it deliberately (playerHadBall == 0, i.e. a
    //     BACK-PASS): keeper may NOT use his hands — he takes control with
    //     his FEET: becomes team.controlledPlayer, ball glued via
    //     UpdateBallWithControllingGoalkeeper, goalkeeperPlaying = 1
    //     (:2867-2893). Returns true (→ l_update_player_speed_and_deltas).
    //   - opponent played it last (or it was contested, playerHadBall != 0):
    //     l_opponent_player_touched_the_ball — keeper claims it with his
    //     hands (GoalkeeperClaimedTheBall, :2895-2905). Returns false
    //     (→ l_clamp_ball_y_inside_pitch).
    //
    // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:2780-2905
    //      external/swos-port/swos/swos.asm (same slice; labels preserved).
    private static bool RunGoalieCantCatchBallPickup(int spriteAddr, int teamBase, bool topTeam)
    {
        // :2781-2792 — cmp A1, lastPlayerPlayed; jz l_clamp_ball_y_inside_pitch.
        // The keeper himself played the ball last (e.g. he just kicked it
        // away) — do not re-take it.
        int lastPlayerPlayed = Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed);
        if (spriteAddr == lastPlayerPlayed)
            return false;

        // :2794-2806 — cmp [esi+Sprite.ballDistance], 72; ja l_clamp… (unsigned).
        int ballDist = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance);
        if ((uint)ballDist > 72)
            return false;

        // :2808-2815 — ball above 17 px → no pickup.
        if (Memory.ReadByte(teamBase + TeamData.OffBallAbove17) != 0)
            return false;

        // :2817-2829 — playerState == PL_GOALIE_CATCHING_BALL → cseg_7FE29
        // (skip the 12-17px band test); else :2831-2838 — ball12To17 != 0 → no pickup.
        byte plState = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerState);
        if (plState != (byte)PortPlayerState.kGoalieCatchingBall)
        {
            if (Memory.ReadByte(teamBase + TeamData.OffBall12To17) != 0)
                return false;
        }

        // cseg_7FE29 :2841-2845 — destX/destY = own whole-pixel position.
        short gkX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short gkY = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, gkX);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, gkY);

        // :2846-2864 — cmp A6, lastTeamPlayed / jnz l_opponent_player_touched…;
        // mov ax, playerHadBall / or ax, ax / jnz l_opponent_player_touched…
        int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
        short playerHadBall = Memory.ReadSignedWord(Memory.Addr.playerHadBall);
        if (teamBase == lastTeamPlayed && playerHadBall == 0)
        {
            // ----- :2867-2893 — BACK-PASS: keeper plays on with his feet ----
            PlayerUpdate.UpdateBallWithControllingGoalkeeper(spriteAddr);        // :2867
            Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, spriteAddr); // :2868-2870
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);       // :2871
            Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 25);     // :2872
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);            // :2873
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);        // :2874
            // :2875-2879 — destX/destY = own position (again, esi = A1).
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, gkX);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, gkY);
            Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);            // :2880-2881
            Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);        // :2882-2883
            Memory.WriteWord(Memory.Addr.penalty, 0);                           // :2884
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);                     // :2885
            Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 0);          // :2887
            Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);  // :2888 (+104)
            Memory.WriteWord(teamBase + 116, 0);                                // :2889 (word +116)
            Memory.WriteWord(teamBase + TeamData.OffGoalkeeperPlaying, 1);      // :2890 (word +140)
            Memory.WriteByte(teamBase + TeamData.OffGoaliePlayingOrOut, 1);     // :2891 (byte +86)
            Memory.WriteByte(teamBase + TeamData.OffBallOutOfPlayOrKeeper, 1);  // :2892 (byte +84)
            return true;                                                        // :2893 jmp l_update_player_speed_and_deltas
        }

        // ----- l_opponent_player_touched_the_ball :2895-2905 — hand claim ---
        Memory.WriteDword(Memory.Addr.lastKeeperPlayed, lastPlayerPlayed);      // :2896-2897
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);                // :2898-2899
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);            // :2900-2901
        Memory.WriteWord(Memory.Addr.penalty, 0);                               // :2902
        Memory.WriteWord(Memory.Addr.playerHadBall, 0);                         // :2903
        PlayerUpdate.UpdateBallWithControllingGoalkeeper(spriteAddr);           // :2904
        PlayerUpdate.GoalkeeperClaimedTheBall(spriteAddr, topTeam);             // :2905
        return false;                                                           // falls into l_clamp_ball_y_inside_pitch
    }

    // ===================================================================
    // TickGoalieDiving — l_goalie_diving, updatePlayers.cpp:3188-3435
    // (+ simplified collision stand-in for 3537-4090)
    // ===================================================================
    //
    // Entered from the main dispatcher for keeper states 6/7
    // (PL_GOALIE_DIVING_HIGH / PL_GOALIE_DIVING_LOW) — updatePlayers.cpp:
    // 743-758. Runs INSTEAD of l_player_goalkeeper, so none of that head's
    // flag/speed writes apply to a diving keeper.
    //
    // Structure (labels preserved as comments):
    //   3188-3202  sub playerDownTimer, 1; jnz @@goalkeeper_still_diving;
    //              == 0 → l_goalkeeper_rise.
    //   3213-3242  @@goalkeeper_still_diving: goalkeeperDivingRight != 0 AND
    //              timer <= 42 → complete the deferred right-dive claim
    //              (UpdateBallWithControllingGoalkeeper +
    //              GoalkeeperClaimedTheBall + ball.z = 5) → rise.
    //   3244-3272  @@goalie_diving_left: game IN PROGRESS and timer <= 60 →
    //              rise early (the in-play dive is only ~15 ticks; the full
    //              75 only plays out while the game is stopped).
    //   3273-3418  cseg_80282 → l_set_new_goalkeeper_speed: per-tick dive
    //              speed DECAY — this is what stops the keeper's slide:
    //                D0 = dseg_110611 (112)          — swos.asm:202406
    //                on-ground frame → dseg_110613 (192) — swos.asm:202407
    //                ball >5 px behind → dseg_110615 (128) — swos.asm:202409
    //                speed -= D0; floor 0.
    //   3420-3434  cseg_803A4: game stopped OR team.field_46 → skip the
    //              catch logic (straight to speed-and-deltas).
    //   3436-3464  cseg_803C4/cseg_803E8: only run the catch check when the
    //              ball is still travelling TOWARD this keeper's goal
    //              (top: ball.deltaY < 0; bottom: ball.deltaY > 0).
    //   3466-4090  cseg_80404 catch/claim chain — ported as the simplified
    //              collision check (see inline note).
    private static void TickGoalieDiving(int spriteAddr, int teamBase, bool topTeam)
    {
        // 3188-3202 — sub [esi+Sprite.playerDownTimer], 1 ; jnz still_diving.
        // Byte arithmetic: rise ONLY on an exact zero result (0-1 wraps to
        // 255 and keeps diving, like the asm).
        byte downTimer = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerDownTimer);
        downTimer = (byte)(downTimer - 1);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, downTimer);
        if (downTimer == 0)
        {
            GoalkeeperRise(spriteAddr, teamBase);
            return;
        }

        // l_goalkeeper_still_diving — 3213-3242. Deferred right-dive claim:
        // `cmp [esi+Sprite.playerDownTimer], 42 ; ja @@goalie_diving_left`
        // (unsigned) — completes once the timer has counted into [1..42].
        short divingRight = Memory.ReadSignedWord(teamBase + TeamData.OffGoalkeeperDivingRight);
        if (divingRight != 0 && downTimer <= 42)
        {
            Memory.WriteWord(teamBase + TeamData.OffGoalkeeperDivingRight, 0);   // :3237
            PlayerUpdate.UpdateBallWithControllingGoalkeeper(spriteAddr);        // :3238
            PlayerUpdate.GoalkeeperClaimedTheBall(spriteAddr, topTeam);          // :3239
            BallSprite.ZPixels = 5;                                              // :3240-3241
            GoalkeeperRise(spriteAddr, teamBase);                                // :3242 → rise
            return;
        }

        // l_goalie_diving_left — 3244-3272. In progress + timer <= 60 (jbe,
        // unsigned) → rise early.
        short gsPl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gsPl == kStGameInProgress && downTimer <= 60)
        {
            GoalkeeperRise(spriteAddr, teamBase);
            return;
        }

        // cseg_80282 — 3273-3402 — pick the dive speed-decay constant.
        // D0 = dseg_110611 (dw 112, swos.asm:202406).
        short d0Decay = 112;
        // 3276-3284 — al = [A6+TeamGeneralInfo.field_46] (byte at +70, the
        // same slot GoalkeeperJumping zeroes at :10902); non-zero → keep 112.
        byte field46 = Memory.ReadByte(teamBase + 70);
        if (field46 == 0)
        {
            // cseg_802A3 — 3286-3390 — keeper-on-ground frame test. Eight
            // imageIndex compares: SPR_GOALIE1_CAUGHT_BALL_RIGHT/LEFT
            // (976/978), SPR_GOALIE2_CAUGHT_BALL_RIGHT/LEFT (1106/1108) and
            // the on-ground dive frames 1092/1094/990/992.
            short img = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffImageIndex);
            if (img == 976 || img == 978 || img == 1106 || img == 1108 ||
                img == 1092 || img == 1094 || img == 990 || img == 992)
            {
                // l_goalkeeper_on_ground — 3444-3447 — D0 = dseg_110613
                // (dw 192, swos.asm:202407): extra friction on the ground.
                d0Decay = 192;
            }
            else
            {
                // 3395-3441 — D1 = ball.y.whole - keeper.y.whole; the ball
                // already >5 px BEHIND the keeper (top: D1 < -5 / bottom:
                // D1 > 5) → cseg_8037A: D0 = dseg_110615 (dw 128,
                // swos.asm:202409). Otherwise keep 112.
                short ballYd  = BallSprite.YPixels;
                short keepYd  = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
                short d1Diff  = (short)(ballYd - keepYd);
                if (teamBase == TeamData.TopBase)
                {
                    if (d1Diff < -5) d0Decay = 128;   // jl cseg_8037A
                }
                else
                {
                    if (d1Diff > 5) d0Decay = 128;    // jg cseg_8037A
                }
            }
        }

        // l_set_new_goalkeeper_speed — 3455-3470 — speed -= D0; floor at 0.
        short diveSpeed = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSpeed);
        diveSpeed = (short)(diveSpeed - d0Decay);
        if (diveSpeed < 0) diveSpeed = 0;               // js → mov speed, 0
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, diveSpeed);

        // cseg_803A4 — game stopped → l_update_player_speed_and_deltas (the
        // stoppage never runs the catch chain on a dead ball); team.field_46
        // non-zero → same skip.
        if (gsPl != kStGameInProgress || field46 != 0)
        {
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // cseg_803C4 / cseg_803E8 — the catch check only runs while the ball
        // is moving TOWARD this keeper's goal line:
        //   top team  (cseg_803C4): ball.deltaY >= 0 (jns) → skip;
        //   bottom    (cseg_803E8): ball.deltaY == 0 (jz) or < 0 (js) → skip.
        int ballDeltaY = BallSprite.DeltaY;
        if (teamBase == TeamData.TopBase)
        {
            if (ballDeltaY >= 0)
            {
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
                return;
            }
        }
        else
        {
            if (ballDeltaY <= 0)
            {
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
                return;
            }
        }

        // ===================================================================
        // cseg_80404 — updatePlayers.cpp:3537-4396 — diving-keeper ball
        // contact + outcome verdict.
        //
        // TASK #71 FIX (2026-07-03): the previous "simplified collision"
        // claimed UNCONDITIONALLY inside a generous box (|dx|<=10, |dy|<=7,
        // z<=20) — it ignored the skill-weighted three-way verdict at
        // :3934-4024 that reads shotChanceTable words +42/+44 (row elements
        // 21/22: e.g. 9/6 for a skill-7 keeper vs 3/5 for skill-0) and the
        // goalkeeperSavedCommentTimer sign (negative = the l_goal_scored RNG
        // already ruled the shot IN). Effect: every dive that touched the
        // ball became a clean claim regardless of keeper skill or the RNG's
        // goal verdict. Replaced with the mechanical chain: X span test from
        // the diving sprite width (:3537-3677), z <= 20 (:3679-3692), Y
        // window ±5 penalty / ±7 normal (:3694-3801), dive speed decay
        // (:3811-3856), verdict (:3857-4024) → clean claim (cseg_807CB),
        // parry (cseg_808BF) or weak deflect (cseg_80A1B).
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp
        //         :3537-4396 + swos.asm:203728 / 246164 data.
        // ===================================================================
        {
            short ballXw = BallSprite.XPixels;
            short ballYw = BallSprite.YPixels;
            short keeperXw = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
            short keeperYw = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
            ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
            int oppBase = Memory.ReadSignedDword(teamBase + TeamData.OffOpponentsTeam);

            // :3538-3546 — imageIndex < 0 → no contact test.
            short img = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffImageIndex);
            if (img < 0) goto l_dive_no_contact;

            // :3548-3560 — D0 = spriteGraphics[imageIndex].pixWidth.
            // STUB (cited): the SpriteGraphics tables aren't ported yet; the
            // keeper dive frames span ~24 px (vs 16 standing). TODO from
            // updatePlayers.cpp:3548 — read the real pixWidth once
            // g_spriteGraphicsPtr lands.
            short pixWidth = 24;

            // :3561-3677 — X overlap by dive side (keeper.deltaX sign):
            //   deltaX < 0 (:3572-3624): need keeper.x-5 < ball.x AND
            //     keeper.x-5+pixWidth+6 > ball.x (jnb / jbe bail).
            //   deltaX >= 0 (cseg_804C6 :3626-3677): D1 = keeper.x+5; need
            //     D1 >= ball.x (jb bail) AND D1-(pixWidth+6) <= ball.x (ja bail).
            {
                int keeperDeltaX = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffDeltaX);
                if (keeperDeltaX < 0)
                {
                    short d1 = (short)(keeperXw - 2 - 3);                     // :3572-3589
                    short d0 = (short)(pixWidth + 6);
                    if ((ushort)d1 >= (ushort)ballXw) goto l_dive_no_contact; // :3590-3602 jnb
                    d1 += d0;                                                 // :3604-3610
                    if ((ushort)d1 <= (ushort)ballXw) goto l_dive_no_contact; // :3611-3622 jbe
                }
                else
                {
                    short d1 = (short)(keeperXw + 2 + 3);                     // cseg_804C6 :3627-3644
                    short d0 = (short)(pixWidth + 6);
                    if ((ushort)d1 < (ushort)ballXw) goto l_dive_no_contact;  // :3645-3657 jb
                    d1 -= d0;                                                 // :3659-3665
                    if ((ushort)d1 > (ushort)ballXw) goto l_dive_no_contact;  // :3666-3677 ja
                }
            }

            // cseg_80519 (:3679-3692) — ball.z <= 20.
            if ((ushort)BallSprite.ZPixels > 20) goto l_dive_no_contact;

            // :3694-3708 — penalty? → cseg_80540 (±5 Y window + speed - speed/4)
            // else cseg_805A7 (±7 Y window + pass bookkeeping).
            bool isPenaltyDive =
                Memory.ReadSignedWord(Memory.Addr.playingPenalties) != 0 ||
                Memory.ReadSignedWord(Memory.Addr.penalty) != 0;
            if (isPenaltyDive)
            {
                // cseg_80540 (:3710-3765).
                short dy0 = (short)(keeperYw - ballYw);
                if (dy0 > 5) goto l_dive_no_contact;                          // jg
                if (dy0 < -5) goto l_dive_no_contact;                         // jl
                short spd = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSpeed);
                spd -= (short)((ushort)spd >> 2);                             // :3746-3764
                Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, spd);
            }
            else
            {
                // cseg_805A7 (:3767-3809).
                short dy0 = (short)(keeperYw - ballYw);
                if (dy0 > 7) goto l_dive_no_contact;                          // jg
                if (dy0 < -7) goto l_dive_no_contact;                         // jl
                Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 25);   // :3804
                Memory.WriteWord(oppBase + TeamData.OffPassingToPlayer, 0);   // :3808
                Memory.WriteDword(oppBase + TeamData.OffPassingKickingPlayer, 0); // :3809
            }

            // cseg_80616 (:3811-3856) — field_46 = 1; dive speed decay:
            // HIGH dive keeps 3/4 (sar 2), LOW dive keeps 1/2 (sar 1).
            Memory.WriteByte(teamBase + 70, 1);                               // :3813
            {
                short spd = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSpeed);
                byte st = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerState);
                short cut = st == (byte)PortPlayerState.kGoalieDivingHigh
                    ? (short)(spd >> 2)                                       // l_goalie_jumping_high :3840-3844
                    : (short)(spd >> 1);                                      // :3830-3837 sar 1
                Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, (short)(spd - cut)); // cseg_8064D
            }

            // :3857-3877 — A0 = team.shotChanceTable, or dseg_17EECC during
            // penalties (cseg_80681).
            int rowPtr = Memory.ReadSignedDword(teamBase + TeamData.OffShotChanceTable);
            if (isPenaltyDive) rowPtr = Memory.Addr.dseg_17EECC;

            // :3883-3890 — penalty = 0; playerHadBall = 0; passKickTimer = 25;
            // opp.passingToPlayer = 0.
            Memory.WriteWord(Memory.Addr.penalty, 0);
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 25);
            Memory.WriteWord(oppBase + TeamData.OffPassingToPlayer, 0);

            // :3891-3901 — commentTimer >= 0 → opp.passingKickingPlayer = 0.
            short commentTimer = TeamData.GoalkeeperSavedCommentTimer(topTeam);
            if (commentTimer >= 0)
                Memory.WriteDword(oppBase + TeamData.OffPassingKickingPlayer, 0);

            // :3904-3930 — !ballInGoalkeeperArea → opponent goalAttempts++.
            if (Memory.ReadWord(Memory.Addr.ballInGoalkeeperArea) == 0)
            {
                int statsPtr = Memory.ReadSignedDword(oppBase + TeamData.OffTeamStatsPtr);
                if (statsPtr != 0)
                {
                    short ga = Memory.ReadSignedWord(statsPtr + 10);          // TeamStatisticsData.goalAttempts
                    Memory.WriteWord(statsPtr + 10, (short)(ga + 1));
                }
            }

            // :3933 — resetBothTeamSpinTimers.
            BallUpdate.ResetBothTeamSpinTimers();

            // ----- the outcome verdict (:3934-4024) --------------------------
            // D1 = (tick & 0xF0) >> 4 minus row[+42] (elem 21) then row[+44]
            // (elem 22); the branch pair swaps on the commentTimer sign
            // (negative = l_goal_scored already ruled the shot IN).
            {
                short sample = (short)((tick & 0xF0) >> 4);
                short e21 = rowPtr != 0 ? Memory.ReadSignedWord(rowPtr + 42) : (short)1;
                short e22 = rowPtr != 0 ? Memory.ReadSignedWord(rowPtr + 44) : (short)6;
                sample -= e21;
                if (sample < 0) goto cseg_807CB;                              // js (:3965 / :4007)
                sample -= e22;
                if (commentTimer < 0)
                {
                    // :3943-3982 — scored-verdict arm: js → cseg_80A1B else cseg_808BF.
                    if (sample < 0) goto cseg_80A1B;
                    goto cseg_808BF;
                }
                // cseg_8077F (:3984-4024) — saved arm: js → cseg_808BF else cseg_80A1B.
                if (sample < 0) goto cseg_808BF;
                goto cseg_80A1B;
            }

        cseg_807CB:;
            // :4026-4090 — clean CLAIM, gated on |keeper.x - ball.x| <= 6.
            {
                short dx0 = (short)(keeperXw - ballXw);
                if (dx0 > 6) goto cseg_80A1B;                                 // jg
                if (dx0 < -6) goto cseg_80A1B;                                // jl
                Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);      // :4062-4063
                Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);  // :4064-4065
                BallSprite.XPixels = keeperXw;                                // :4066-4069
                BallSprite.YPixels = keeperYw;                                // :4070-4073
                BallSprite.DestX = keeperXw;                                  // :4074-4075
                BallSprite.DestY = keeperYw;                                  // :4076-4077
                BallSprite.Speed = 0;                                         // :4078
                Memory.WriteWord(teamBase + TeamData.OffGoalkeeperDivingRight, 1); // :4080
                PlayerUpdate.GoalkeeperClaimedTheBall(spriteAddr, topTeam);   // :4083
                // updatePlayers.cpp:4087-4088 — PlayGoalkeeperSavedComment / PlayMissGoalSample.
                OpenSwos.Audio.MatchAudio.KeeperSavedComment();
                OpenSwos.Audio.MatchAudio.PlayMissGoal();
                goto l_dive_done;                                             // :4090
            }

        cseg_808BF:;
            // :4092-4224 — PARRY: keeper gets a strong hand to it.
            {
                Memory.WriteDword(Memory.Addr.lastKeeperPlayed,
                    Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed));    // :4093-4094
                Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);      // :4095-4096
                Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);  // :4097-4098
                short bspd = BallSprite.Speed;
                bspd -= (short)((ushort)bspd >> 2);                           // :4099-4114
                BallSprite.Speed = bspd;
                if ((ushort)BallSprite.Speed > 1792)                          // :4115-4128
                    BallSprite.Speed = 1792;
                if (commentTimer < 0)
                {
                    // :4140-4169 — the RNG's goal verdict stands: bend the
                    // ball dest X by ((tick & 15) << 7) - 960 and let it fly.
                    short bend = (short)(((tick & 15) << 7) - 960);
                    BallSprite.DestX = (short)(BallSprite.DestX + bend);
                    goto cseg_80B1D;
                }
                // cseg_8095F (:4171-4224) — blocked: destY pulled back to
                // ball.y + ((destY - ball.y) >> dseg_110BDB[tick & 0x0E]);
                // destX = keeper dive destX.
                short d1y = BallSprite.YPixels;
                short d0y = (short)(BallSprite.DestY - d1y);
                short shift = Memory.ReadSignedWord(Memory.Addr.dseg_110BDB + (tick & 0x0E));
                d0y = (short)(d0y >> (shift & 0x1F));                         // sar
                BallSprite.DestY = (short)(d1y + d0y);                        // neg + sub = add
                BallSprite.DestX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestX); // :4218-4221
                goto cseg_80B1D;
            }

        cseg_80A1B:;
            // :4226-4317 — WEAK TOUCH: destY mirrored around ball.y plus a
            // tick-noise term; ball slowed by 1/4 (plus 1/8 on odd 0x10 tick).
            {
                Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);      // :4227-4228
                Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);  // :4229-4230
                short d1y = BallSprite.YPixels;
                short d0y = (short)(BallSprite.DestY - d1y);
                d1y = (short)(d1y - d0y);                                     // reflect
                short noise = (short)(((tick & 31) << 4) - 256);              // :4250-4265
                d1y += noise;
                BallSprite.DestY = d1y;                                       // :4273-4274
                BallSprite.DestX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestX); // :4275-4278
                short bspd = BallSprite.Speed;
                short quarter = (short)((ushort)bspd >> 2);                   // :4279-4293
                bspd -= quarter;
                BallSprite.Speed = bspd;
                if ((tick & 0x10) != 0)                                       // :4294-4317
                {
                    BallSprite.Speed = (short)(BallSprite.Speed - (short)((ushort)quarter >> 1));
                }
                // cseg_80B07 (:4319-4333) — cap at 1536.
                if ((ushort)BallSprite.Speed > 1536)
                    BallSprite.Speed = 1536;
                goto cseg_80B1D;
            }

        cseg_80B1D:;
            // :4337-4370 — common deflect tail: ball.y = keeper.y ± 1 (top
            // team +1, bottom -1).
            {
                short d0 = (short)(topTeam ? 1 : -1);                         // :4338-4351
                BallSprite.YPixels = (short)(keeperYw + d0);                  // :4353-4370
            }
            // cseg_80B5F (:4372-4389) — passKickTimer = 25; opp.passingToPlayer
            // = 0; commentTimer >= 0 → opp.passingKickingPlayer = 0.
            Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 25);
            Memory.WriteWord(oppBase + TeamData.OffPassingToPlayer, 0);
            if (TeamData.GoalkeeperSavedCommentTimer(topTeam) >= 0)
                Memory.WriteDword(oppBase + TeamData.OffPassingKickingPlayer, 0);
            // updatePlayers.cpp:4392-4394 — PlayGoalkeeperSavedComment / PlayMissGoalSample.
            OpenSwos.Audio.MatchAudio.KeeperSavedComment();
            OpenSwos.Audio.MatchAudio.PlayMissGoal();

        l_dive_done:;
        l_dive_no_contact:;
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
        }
    }

    // l_goalkeeper_rise — updatePlayers.cpp:3205-3210 → l_stop_player
    // (10079-10084) → l_update_player_speed_and_deltas.
    // State back to PL_NORMAL, standing animation, dest snapped to the
    // keeper's current position (the stop is what plants him where the dive
    // ended instead of letting him keep walking to the ±1000 dive dest).
    private static void GoalkeeperRise(int spriteAddr, int teamBase)
    {
        // :3206 — mov [esi+Sprite.playerState], PL_NORMAL.
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
        // :3207-3209 — SetPlayerAnimationTable(playerNormalStandingAnimTable).
        PlayerActions.SetPlayerAnimationTable(
            spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
        // l_stop_player — 10079-10084 — dest = current pos.
        short sx = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short sy = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, sx);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, sy);
        // l_update_player_speed_and_deltas — 10086.
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // updatePlayers.cpp:4398-5779 — l_its_controlled_player handler.
    // Drives the controlled (human-input) outfielder: applies the joystick
    // input, kicks, tackles, jump headers, etc. Delegates to the ported
    // PlayerControlled.RunControlledBranch which is the mechanical-port of
    // the giant inline branch.
    private static void TickHumanControlled(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);

        // from updatePlayers.cpp:4398 — call the ported branch.
        var exit = PlayerControlled.RunControlledBranch(spriteAddr, topTeam);

        // updatePlayers.cpp:10079 — l_stop_player: if the branch requested
        // a stop (e.g. just-kicked / passed), snap destX/Y to current pos.
        if (exit == PlayerControlled.Exit.StopPlayer)
        {
            short sx = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
            short sy = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, sx);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, sy);
        }

        // === PASS-RECEIPT TRIGGER (call-site preserved; guarded no-op for now) ===
        // updatePlayers.cpp:8180-8252 commits the pending pass when ball arrives
        // near the teammate. It lives in the `l_player_expecting_pass` branch
        // (updatePlayers.cpp:7604) which the per-sprite dispatcher at
        // updatePlayers.cpp:851-876 routes to when A1 == team.passToPlayerPtr —
        // a DIFFERENT branch from `l_its_controlled_player`. Wiring it from the
        // controlled-player path (here) caused a regression that zeroed both
        // teams' controlledPlayer pointers on tick 1 (passToPlayerPtr is null
        // for the controlled player, so the asm-style `controlledPlayer =
        // passToPlayerPtr` swap corrupted the team). The trigger now self-gates
        // on `spriteAddr == passToPlayerPtr` so this call stays as a marker for
        // future receiver-branch work but no-ops in the current dispatcher.
        // TODO from updatePlayers.cpp:851-876 — port the full
        // `l_player_expecting_pass` branch and invoke RunPassReceiptTrigger
        // from there with the receiver sprite.
        short passReceiptGsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (passReceiptGsp == kStGameInProgress)
        {
            PlayerControlled.RunPassReceiptTrigger(spriteAddr, topTeam);
        }

        // === BALL-CHASE OVERRIDE for the team's controlled player ===
        // In real SWOS the controlled-player branch eventually writes a chase
        // destination because AI_SetControlsDirection writes
        // team.currentAllowedDirection toward the ball and the asm path
        // at updatePlayers.cpp:5733-5778 (cseg_81793 / l_update_player_dest_x_y)
        // turns that into sprite.dest{X,Y} = sprite.{x,y} + kDefaultDestinations[dir].
        // Our AiBrain.SetControlsDirection is heavily stubbed — many branches
        // never write currentAllowedDirection, so PlayerControlled.cs:335-342
        // (updatePlayers.cpp:4730-4743) snaps dest = pos and the player stops
        // dead. Real SWOS effectively guarantees per-team movement toward the
        // ball via the controlledPlayer designation (see updatePlayers.cpp:18958-18966
        // where AI_SetControlsDirection swaps controlledPlayer <-> passToPlayerPtr
        // to put the closer-to-ball sprite in control).
        //
        // Minimal fix: override the controlled player's destX/destY with the
        // ball's pixel position (clamped to the playable pitch) whenever the
        // game is in progress and the player isn't standing on the ball.
        // Gives one chaser per team until the deeper AiBrain chase logic
        // (updatePlayers.cpp:17372-17555) is fully ported. See
        // OverrideDestToBallIfChaser for the gate semantics + why we don't
        // gate on team.playerHasBall yet.
        //
        // Cited source: updatePlayers.cpp:9234-9239 (l_set_player_positions_if_game_break
        // loads D6/D7 from BALL sprite when gameStatePl == ST_GAME_IN_PROGRESS)
        // and updatePlayers.cpp:18958-18966 (l_noone_near swaps controlledPlayer
        // with passToPlayerPtr to put the closer chaser in control).
        OverrideDestToBallIfChaser(spriteAddr, teamBase);

        // updatePlayers.cpp:10086 — l_update_player_speed_and_deltas:
        // both Exit.UpdateSpeed and Exit.StopPlayer paths converge here.
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // Helper for the ball-chase override (see TickHumanControlled comment).
    // Writes ball.x/y into sprite.destX/destY when this sprite is the team's
    // controlledPlayer and is not literally on top of the ball already.
    //
    // Gates:
    //   (c) gameStatePl == 100 — ST_GAME_IN_PROGRESS only. Set-pieces /
    //       kick-off / goal scored use foul or tactics coordinates and must
    //       not be overwritten.
    //   (a) sprite == team.controlledPlayer — only the designated chaser
    //       runs to the ball; other outfielders use SetPlayerWithNoBallDestination
    //       tactics positioning (set in TickAiControlled at L495).
    //   (d) abs(pos - ball) >= 2 px — when the controlled player is already
    //       on/touching the ball, leave dest alone so PlayerControlled.cs's
    //       in-possession routines (header / dribble / kick / pass) can pick
    //       a forward destination.
    //
    // Bounds: clamps to the playable pitch (the same [81..590, 129..769]
    // band used by setPlayerWithNoBallDestination at updatePlayers.cpp:15608-15666).
    // Without the clamp a ball that has rolled onto the goal-line area would
    // drag the chaser into the keeper-only zone.
    //
    // NOTE: We do NOT gate on team.playerHasBall here. The asm path that
    // sets playerHasBall (updatePlayers.cpp:4806, cseg_80EAA) relies on the
    // plVeryCloseToBall/plCloseToBall flags computed from Sprite.ballDistance.
    // ballDistance is now written per-sprite by InputControls.UpdateControlledPlayer
    // (swos.asm:100898) and by UpdatePlayerBallDistanceAndHeight (below at L1094),
    // but plVeryCloseToBall (teamBase+61) is a TEAM-LEVEL flag set by the LAST
    // sprite processed in the bucket loop, not per-sprite — so gating on it for
    // the controlledPlayer here would misread which sprite triggered it. Keep
    // the proximity check (gate d) as the right per-sprite test.
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:544-557.
    private static void OverrideDestToBallIfChaser(int spriteAddr, int teamBase)
    {
        // Gate (c): ST_GAME_IN_PROGRESS only.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl != 100) return;

        // Gate (b): only AI-controlled teams get the chase override. Human
        // teams (playerNumber != 0) own their controlled-player dest via the
        // joystick → cseg_81793 path in PlayerControlled.RunControlledBranch;
        // stomping it here would undo every user input.
        short pNum = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        if (pNum != 0) return;

        // Gate (a): this sprite is the team's controlledPlayer.
        int controlled = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
        if (controlled != spriteAddr) return;

        // Gate (d): not already on the ball.
        // Read player.x/y (whole-pixel half of Q16.16) and ball.x/y.
        short px = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short py = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        short bxPx = BallSprite.XPixels;
        short byPx = BallSprite.YPixels;
        int dpx = px - bxPx;
        int dpy = py - byPx;
        if ((dpx >= -2 && dpx <= 2) && (dpy >= -2 && dpy <= 2)) return;

        // Override: dest = ball.x/y, clamped to pitch.
        short destX = bxPx;
        short destY = byPx;
        if (destX < 81)  destX = 81;
        if (destX > 590) destX = 590;
        if (destY < 129) destY = 129;
        if (destY > 769) destY = 769;
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, destX);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, destY);
    }

    // ChaseDirFromDelta helper REMOVED 2026-06-01: replaced by canonical
    // SpriteUpdate.CalculateDeltaXAndY + ((angle+16)&0xFF)>>5 8-direction
    // quantisation (the same path the asm uses at cseg_84DE0,
    // updatePlayers.cpp:17985). Call sites now use the byte-precise SWOS
    // angle table directly.

    // updatePlayers.cpp:8654 (l_not_controlled_player) + 7604 (l_player_expecting_pass) —
    // off-ball outfielder handler. Drives AI runs, off-ball positioning,
    // pass-receiver destination. The asm runs AI_SetControlsDirection to
    // pick a movement direction + fire-trigger, then optionally calls
    // AI_Kick to commit the kick, then UpdatePlayerSpeedAndFrameDelay.
    //
    // Note: AI_SetControlsDirection takes the TEAM base (not the sprite) —
    // it's a per-team decision but indirectly references team.controlledPlayer
    // for the player's pose.
    private static void TickAiControlled(int spriteAddr, bool topTeam, int slotInTeam)
    {
        int teamBase = TeamData.Base(topTeam);

        // === CPU-TEAM GATE (2026-07-02, USER-VISIBLE BUG FIX) ==============
        // In the original, EVERY call site of AI_SetControlsDirection and
        // AI_Kick is gated on team.playerNumber == 0 (CPU team):
        //   - swos.asm:114526-114534 — @@check_for_cpu_team (controlled player;
        //     `or ax, ax / jnz cseg_80C0C` skips the call for a human team)
        //   - swos.asm:115325-115334 — @@player_taking_throw_in (same jnz skip)
        //   - swos.asm:116858-116869 — @@check_for_controlled_player loop tail
        //     (requires controlledPlayerSprite == 0 AND playerNumber == 0)
        //   - swos.asm:116393-116402 — @@cpu_passing_to → AI_Kick (same skip)
        // A human team's control fields (currentAllowedDirection, quickFire,
        // normalFire, firePressed, fireThisFrame) are written ONLY by the
        // joystick layer (UpdateAndApplyTeamControls, swos.asm:100568-100840).
        //
        // Our port used to run the block below — AiBrain.SetControlsDirection
        // (whose entry zeroes currentAllowedDirection and ALL fire flags,
        // updatePlayers.cpp:16043-16047 / AiBrain.cs:160-165), the port-only
        // chase fallback, AI_Kick, and the port-only off-ball kick fallback —
        // for every non-controlled kNormal sprite of BOTH teams. For a HUMAN
        // team that stomped the user's joystick direction up to 10x per tick
        // and let the AI fire a pass/kick on the human's behalf the moment the
        // team secured possession. Each phantom kick clears controlledPlayer
        // and sets passingKickingPlayer = carrier + passKickTimer = 25
        // (swos.asm:114990-114995 / PlayerControlled.cs:740-742), which makes
        // UpdateControlledPlayer skip the carrier (swos.asm:100919-100924) and
        // flap control between the carrier and a teammate, snapping dest=pos
        // on every flap (swos.asm:100986-100993) — the reported "control
        // toggles on/off, player jitters ±1 px, cannot be steered" bug.
        //
        // Mirror the asm gate: skip ALL AI control writes for a human team.
        // The tactical positioning tail at l_skip_ai_controls below
        // (l_set_player_positions_if_game_break, updatePlayers.cpp:9234-9239 +
        // the stoppage walk-back, updatePlayers.cpp:8676-10106) has no
        // playerNumber gate in the original and still runs for both teams.
        short pNumAiGate = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        if (pNumAiGate != 0)
            goto l_skip_ai_controls;    // jnz — swos.asm:114530 / 115330 / 116865 / 116398

        // updatePlayers.cpp:8902-8915 — l_check_for_controlled_player:
        //   cmp [esi+TeamGeneralInfo.controlledPlayer], 0
        //   jnz l_check_if_this_player_getting_booked
        // The off-ball loop may invoke AI_SetControlsDirection ONLY when the
        // team has no controlled player. When one exists, the single call at
        // l_check_for_cpu_team (updatePlayers.cpp:4433 →
        // PlayerControlled.RunControlledBranch) is the only one that tick.
        // Unconditional per-sprite calls wipe the fire flags the controlled
        // invocation committed (entry block updatePlayers.cpp:16043-16047)
        // and drain the global AI_resumePlayTimer 10×/tick, deterministically
        // starving the restart kick (the kicker never passes the
        // l_our_player_closest rpt==0 gate, updatePlayers.cpp:18116-18123) —
        // this was the 2700-5600-tick post-goal restart stall (2026-07-02).
        int ctrlAiGate = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
        if (ctrlAiGate == 0)
        {
            // from updatePlayers.cpp:8928 — call AI_SetControlsDirection.
            AiBrain.SetControlsDirection(teamBase);
        }

        // === FALLBACK heuristic (NOT a port) =============================
        // AiBrain.SetControlsDirection has many stubbed inner branches and
        // currently leaves the bottom team's currentAllowedDirection at -1
        // most ticks (diagnostic measured ~86% no-write on BOT team). Without
        // a written direction, the kick/pass pipeline never fires and the
        // ball stays glued at kick-off coordinates.
        //
        // Until the deep chase branches (cseg_84A85/cseg_84F4B/...) are
        // fully ported, pick a "chase the ball" direction whenever:
        //   1. AiBrain left dir == -1 for this team;
        //   2. ball is within ~250 px of THIS sprite (about 1/3 of the pitch);
        //   3. neither team currently controls the ball (playerHasBall == 0 on both).
        //
        // The 8-direction encoding matches SWOS: 0=N (-Y), 1=NE, 2=E (+X),
        // 3=SE, 4=S (+Y), 5=SW, 6=W (-X), 7=NW (clockwise from N).
        //
        // TODO: replace once AiBrain stubs at updatePlayers.cpp:17372-17555
        // (chase logic) and 18017-18105 (flip-direction) are wired up. Once
        // those branches write the direction we should never reach this
        // fallback under normal play.
        short postDir = Memory.ReadSignedWord(teamBase + TeamData.OffCurrentAllowedDirection);
        if (postDir == -1)
        {
            short t1Has = Memory.ReadSignedWord(TeamData.TopBase + TeamData.OffPlayerHasBall);
            short t2Has = Memory.ReadSignedWord(TeamData.BottomBase + TeamData.OffPlayerHasBall);
            if (t1Has == 0 && t2Has == 0)
            {
                short bx = BallSprite.XPixels;
                short by = BallSprite.YPixels;
                short px = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
                short py = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
                int dx = bx - px;
                int dy = by - py;
                // Cheap squared distance — pixels only, integer math.
                int distSq = dx * dx + dy * dy;
                if (distSq <= 250 * 250)
                {
                    // Use canonical SpriteUpdate.CalculateDeltaXAndY +
                    // ((angle+16)&0xFF)>>5 to get the 8-direction sector.
                    // Same quantisation the asm uses at cseg_84DE0
                    // (updatePlayers.cpp:17985). Replaces the previous
                    // ChaseDirFromDelta integer-wedge approximation with the
                    // byte-precise SWOS angle table from updateSprite.cpp.
                    var chaseResult = SpriteUpdate.CalculateDeltaXAndY(
                        256,    // speed (matches AiBrain.cs:245 convention)
                        px,     // current x
                        py,     // current y
                        bx,     // destX (ball)
                        by);    // destY (ball)
                    int chaseAngle = chaseResult.Direction;
                    if (chaseAngle >= 0)
                    {
                        int chaseDir = ((chaseAngle + 16) & 0xFF) >> 5;
                        Memory.WriteWord(teamBase + TeamData.OffCurrentAllowedDirection, chaseDir);
                        if (topTeam) s_fallbackChasesTop++;
                        else         s_fallbackChasesBot++;
                    }
                }
            }
        }
        // ================================================================

        // (2026-07-02 - REMOVED, confirmed by two independent audit probes:)
        // 1. The per-sprite AiHelpers.AI_Kick call that lived here had NO
        //    original counterpart. AI_Kick has exactly ONE call site in the
        //    original - updatePlayers.cpp:7905 inside l_cpu_passing_to (sole
        //    `call AI_Kick` at swos.asm:116402) - only the CPU team's
        //    pass-expecting sprite may commit an AI kick. Arming it from
        //    every off-ball CPU sprite every tick created a lockout-immune,
        //    cooldown-immune "poke the dribbler" kick whenever ANY defender
        //    crowded the carrier ("komputer zabiera mi wszystko" bug). The
        //    faithful call sites live in PlayerControlled.RunPassExpectingBranch
        //    (updatePlayers.cpp:7892-7908) and TickPassExpectingStopped.
        // 2. The "OFF-BALL AI-KICK FALLBACK" (2026-06-01) that lived here fired
        //    PlayerKickingBall directly from the off-ball iteration with NO
        //    possession gates (neither opp.playerHasBall nor own wonTheBallTimer)
        //    - a steal path with no original counterpart. With AiBrain's
        //    l_activate_normal_fire commit path now live it fired 0 times in
        //    8000-tick smokes - deleted outright.
        // ================================================================

    l_skip_ai_controls:
        // Human-team entry point — see the CPU-TEAM GATE at the top of this
        // method. From here down the original runs for BOTH teams (no
        // playerNumber gate on the positioning tail).

        // updatePlayers.cpp:9234-9239 — l_set_player_positions_if_game_break.
        // For not-controlled AI outfielders during ST_GAME_IN_PROGRESS, the
        // asm loads D6/D7 from ball.x/y and falls through to call
        // setPlayerWithNoBallDestination → writes destX/destY based on the
        // player's tactical position. Without this, AiBrain only sets the
        // team's currentAllowedDirection (which a non-stubbed apply path
        // would write into the controlled player's destX) and every other
        // outfielder keeps dest == pos forever, so they never move.
        //
        // AiBrain.SetControlsDirection is heavily stubbed and does NOT
        // write per-player destX/destY anywhere — this call is the missing
        // off-ball dest commit per outfielder.
        //
        // === BUG 2 FIX (2026-06-01) =====================================
        // Gate on `gameStatePl == 100` (ST_GAME_IN_PROGRESS). During PostGoal
        // celebration the ball sits at the goal mouth (y < 129 above top
        // goal line) and the goalkeeper branch of
        // SetPlayerWithNoBallDestination — line 15677-15794 in
        // updatePlayers.cpp — applies `(d7-129)*27/641` with an UNSIGNED
        // 16-bit divu, blowing up to destY ≈ 16481 when d7Ball < 129.
        // The asm explicitly gates this whole helper on game-in-progress
        // (updatePlayers.cpp:9234 `l_set_player_positions_if_game_break`);
        // we mirror that gate here. Outfielders also benefit — during
        // PostGoal nobody runs to formation-positions.
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:9234.
        //
        // === BUG FIX (2026-06-01): round-robin gate =====================
        // The asm `l_not_controlled_player` path (updatePlayers.cpp:8676-8691)
        // checks `if (team.updatePlayerIndex != current_player_loop_idx)`
        // and `goto l_next_player` if mismatched. Effect: the "off-ball
        // tactical re-position" runs for ONE outfielder per team per tick
        // (round-robin across all 11 slots). Without this gate we re-evaluate
        // tactical dest for all 10 off-ball outfielders every tick, so the
        // ±1px playerXQuadrantOffset drift (ball.cpp:1998) causes them to
        // visibly oscillate around their formation centroid.
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:8676-8691.
        short updatePlIdx = Memory.ReadSignedWord(teamBase + TeamData.OffUpdatePlayerIndex);
        bool myTurn = (updatePlIdx == slotInTeam);

        short gameStatePlAi = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePlAi == kStGameInProgress)
        {
            if (myTurn)
            {
                // l_this_is_substituted_player — updatePlayers.cpp:8806-8883.
                // In-progress gate before the destReachedState promotion:
                //   8820-8831 — if inGameCounter > 25 → promote (ja).
                //   8833-8844 — if breakState == ST_CORNER_LEFT (4) → next player.
                //   8846-8857 — if breakState == ST_CORNER_RIGHT (5) → next player.
                //   8859-8870 — if breakState < ST_FREE_KICK_LEFT1 (6) → promote (jb).
                //   8872-8883 — if breakState <= ST_FREE_KICK_RIGHT3 (12) → next player (jbe).
                //   else → promote.
                // i.e. within 25 ticks of resuming from a corner / free kick
                // this sprite's whole tail (promotion + reposition) is skipped.
                ushort inGameCounter = Memory.ReadWord(Memory.Addr.inGameCounter);
                if (inGameCounter <= 25)
                {
                    ushort breakStateGate = Memory.ReadWord(Memory.Addr.breakState);
                    if (breakStateGate == 4 || breakStateGate == 5 ||
                        (breakStateGate >= 6 && breakStateGate <= 12))
                    {
                        // asm: jz / jbe @@next_player. DIVERGENCE (documented):
                        // the asm's l_next_player also skips the per-tick speed
                        // refresh below; we keep our port's per-tick refresh for
                        // in-progress play because several stand-in AI branches
                        // (chase fallback etc.) were tuned around it.
                        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
                        return;
                    }
                }

                // l_update_destination_reached_state — updatePlayers.cpp:8885-8900.
                // `cmp [esi+Sprite.destReachedState], 1 / jnz skip / mov ..., 2`
                // kStarting (1) → kTraveling (2), per Sprite.h:36-42.
                short drsInPlay = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestReachedState);
                if (drsInPlay == 1)
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffDestReachedState, 2);

                // l_set_player_positions_if_game_break — updatePlayers.cpp:9234-9239.
                // Game in progress: D6/D7 = ball.x/y → setPlayerWithNoBallDestination.
                try
                {
                    short ballXPx = BallSprite.XPixels;
                    short ballYPx = BallSprite.YPixels;
                    PlayerHeader.SetPlayerWithNoBallDestination(spriteAddr, teamBase, ballXPx, ballYPx);
                }
                catch (System.Exception)
                {
                    // Tactics table not yet populated — fall back to a no-op.
                }
            }

            // from updatePlayers.cpp:10086 — l_update_player_speed_and_deltas.
            // DIVERGENCE (pre-existing, kept): the asm only reaches this exit
            // for the round-robin sprite; we refresh every sprite per tick.
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // ===================================================================
        // STOPPAGE PATH (gameStatePl != 100) — the post-goal / restart
        // walk-back to formation. Mechanical port of the l_not_controlled_player
        // tail, updatePlayers.cpp:8676-10106. This is what makes all 22
        // players WALK to their positions during a break instead of standing
        // (or chasing stale in-play destinations).
        // ===================================================================

        // l_test_update_player_index — updatePlayers.cpp:8676-8691.
        // `cmp D0(updatePlayerIndex), [esp](loop counter) / jnz @@next_player`.
        // Exactly ONE non-controlled sprite per team runs the tail per tick;
        // over 11 ticks every slot gets its destination + speed + deltas. The
        // stagger is authentic — players peel off toward formation one by one.
        if (!myTurn) return;

        // updatePlayers.cpp:8706-8804 — end-of-game winner/loser reaction
        // (gameState == ST_GAME_ENDED (30), winningTeamPtr, PL_HAPPY/PL_SAD).
        // TODO from updatePlayers.cpp:8706 — winningTeamPtr isn't modelled in
        // Memory.cs yet; Result.cs owns the full-time flow. Skipped.

        // l_update_destination_reached_state — updatePlayers.cpp:8885-8900.
        // gameStatePl != ST_GAME_IN_PROGRESS jumps straight here (8816-8818).
        // kStarting (1) → kTraveling (2); SpriteUpdate's
        // UpdateAnimationTableAndDestinationReached later promotes 2 → 3
        // (kReached) once the sprite is stationary (updateSprite.cpp:215-229).
        short drs = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestReachedState);
        if (drs == 1)
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestReachedState, 2);

        // l_check_for_controlled_player — updatePlayers.cpp:8902-8931.
        // asm: if team.controlledPlayer == 0 && team.playerNumber == 0 →
        // AI_SetControlsDirection. Our port already invokes
        // AiBrain.SetControlsDirection unconditionally at the top of this
        // method (pre-existing divergence) — do not double-call.

        // l_check_if_this_player_getting_booked — updatePlayers.cpp:8933-9049.
        // ASM-ORDER FIX (task #170): must run HERE, between the drs promotion
        // (:8885-8900) and the substituted/sent-away/position chain (:9051+).
        // Every exit of the branch jumps to l_update_player_speed_and_deltas,
        // skipping the break-position writers — otherwise the free-kick wall
        // (:9432-9667) / foul-pushback (:9939-10077) dest overwrote the
        // walk-to-referee dest on this sprite's round-robin turn and the
        // booked player never reached (foulX+21, foulY): refState pinned at
        // kRefWaitingPlayer, whichCard stayed set, and the breakCameraMode
        // ladder froze at Mode2/Mode3 (whichCard / refState gates at
        // gameLoop.cpp:1358-1363 / :1506-1511) — the "free kick never taken"
        // infinite wait.
        if (CheckIfThisPlayerGettingBooked(spriteAddr, teamBase))
        {
            // l_update_player_speed_and_deltas — recompute speed + deltas
            // from the freshly written walk-to-referee dest (asm falls into
            // :10086-10106 directly after the jmp).
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // l_check_for_substituted_player — updatePlayers.cpp:9051-9197.
        // TODO from updatePlayers.cpp:9051 — substitutions not ported yet.

        // l_check_if_sent_away — updatePlayers.cpp:9199-9207.
        // `mov ax, [esi+Sprite.sentAway] / or ax, ax / jnz
        // @@update_player_speed_and_deltas` — a sent-off player keeps his
        // leave-pitch destination; skip repositioning.
        short sentAway = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSentAway);
        if (sentAway == 0)
        {
            // l_set_player_positions_if_game_break chain —
            // updatePlayers.cpp:9209-9934 (+ foul pushback 9939-10077).
            SetPlayerPositionsForGameBreak(spriteAddr, teamBase, topTeam);
        }

        // l_update_player_speed_and_deltas — updatePlayers.cpp:10086-10106.
        // updatePlayerSpeedAndFrameDelay (player.cpp:17-77: game-stopped speed
        // table + runSlower 5/8 walk-back slowdown) then CalculateDeltaXAndY →
        // sprite.deltaX/deltaY (RecomputeSpriteDeltas is folded into the C#
        // helper).
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // ===================================================================
    // SetPlayerPositionsForGameBreak — updatePlayers.cpp:9209-9934 + 9939-10077
    // ===================================================================
    //
    // Per-player destination assignment during a break in play. Entered from
    // l_check_if_sent_away with A1 = sprite, A6 = team, and writes
    // sprite.destX/destY. The caller then runs
    // l_update_player_speed_and_deltas so the sprite walks there.
    //
    // Structure (labels preserved as comments):
    //   9209-9218  playingPenalties != 0 → D0 = ST_PENALTIES (31), foul coords.
    //   9220-9239  l_set_player_positions_if_game_break: game in progress →
    //              D6/D7 = ball.x/y → setPlayerWithNoBallDestination.
    //   9241-9313  l_game_interrupted_get_ball_x_y / l_get_foul_coordinates:
    //              D0 = gameState; D6/D7 = foulX/foulY; then:
    //                gameState 29/30 (half/game ended)   → bail (speed only)
    //                gameState 3/1/2 (keeper hold, gouts) → no-ball destination
    //   9315-9365  forceLeftTeam corner override → l_top_break when foulY < 449.
    //   9367-9409  table select: penalties → by team; else by
    //              lastTeamPlayedBeforeBreak (own break → bottom table).
    //   9411-9430  cseg_8364D: A5 = table[gameState]; null → no-ball destination.
    //   9432-9667  freeKickDestX wall computation (free kicks 7..11 only).
    //   9669-9934  cseg_8381B: decode (x, y) pair — 22222 sentinel, ±1000
    //              foul-relative markers, top/bottom mirroring — write dest.
    //   9936-10077 l_set_player_with_no_ball_destination + cseg_83A41 foul
    //              pushback (±70 px) for the non-restarting team.
    //
    // For the post-goal walk-back gameState == 0, so the table entry is
    // top/bottomStartingPositions (swos.asm:245963/245999 — index 0 of
    // top/bottomBallOutOfPlayPositions) — i.e. the FORMATION coordinates.
    private static void SetPlayerPositionsForGameBreak(int spriteAddr, int teamBase, bool topTeam)
    {
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        short gameState   = Memory.ReadSignedWord(Memory.Addr.gameState);

        short d0State;
        short d6;   // foul/ball X
        short d7;   // foul/ball Y

        // updatePlayers.cpp:9209-9218 — playingPenalties → D0 = ST_PENALTIES (31).
        short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        if (playingPenalties != 0)
        {
            d0State = 31;                       // mov word ptr D0, ST_PENALTIES
            // goto l_get_foul_coordinates
        }
        else
        {
            // l_set_player_positions_if_game_break — updatePlayers.cpp:9220-9239.
            if (gameStatePl == kStGameInProgress)
            {
                // 9234-9239 — D6/D7 = ball.x/y → l_set_player_with_no_ball_destination.
                d6 = BallSprite.XPixels;
                d7 = BallSprite.YPixels;
                SetPlayerWithNoBallDestinationForBreak(spriteAddr, teamBase, d6, d7, gameStatePl);
                return;
            }
            // l_game_interrupted_get_ball_x_y — updatePlayers.cpp:9241-9243.
            d0State = gameState;
        }

        // l_get_foul_coordinates — updatePlayers.cpp:9245-9249.
        d6 = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        d7 = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);

        // 9250-9274 — gameState == ST_FIRST_HALF_ENDED (29) or ST_GAME_ENDED (30)
        // → l_update_player_speed_and_deltas (players keep their leave-pitch dests).
        if (gameState == 29 || gameState == 30) return;

        // 9276-9313 — ST_KEEPER_HOLDS_BALL (3), ST_GOAL_OUT_LEFT (1),
        // ST_GOAL_OUT_RIGHT (2) → l_set_player_with_no_ball_destination.
        if (gameState == 3 || gameState == 1 || gameState == 2)
        {
            SetPlayerWithNoBallDestinationForBreak(spriteAddr, teamBase, d6, d7, gameStatePl);
            return;
        }

        // 9315-9365 — corner override: forceLeftTeam == 1 AND gameState is
        // ST_CORNER_LEFT (4) / ST_CORNER_RIGHT (5) AND foulY < 449 → l_top_break.
        bool useTopTable;
        short forceLeftTeam = Memory.ReadSignedWord(Memory.Addr.forceLeftTeam);
        int lastTeamBeforeBreak = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (forceLeftTeam == 1 && (gameState == 4 || gameState == 5) && d7 < 449)
        {
            // l_corner → jl @@top_break.
            useTopTable = true;
        }
        // l_check_for_penalty_shootout — updatePlayers.cpp:9367-9388.
        else if (playingPenalties != 0)
        {
            // `cmp A6, offset bottomTeamData / jnz @@top_break`.
            useTopTable = teamBase != TeamData.BottomBase;
        }
        else
        {
            // l_check_team_for_penalty_positions — updatePlayers.cpp:9390-9402.
            // `cmp A6, lastTeamPlayedBeforeBreak / jnz @@top_break` — the team
            // that last played the ball before the break uses the BOTTOM
            // table; the other team the TOP table.
            useTopTable = teamBase != lastTeamBeforeBreak;
        }

        // l_bottom_break / l_top_break / cseg_8364D — updatePlayers.cpp:9404-9430.
        // A5 = (top|bottom)BallOutOfPlayPositions[gameState]; null → no-ball dest.
        short[]?[] positionsList = useTopTable ? kTopBallOutOfPlayPositions
                                               : kBottomBallOutOfPlayPositions;
        // Defensive index clamp (asm indexes blindly; our tables cover 0..31 —
        // "indexed by gameState, states < 32" per swos.asm:245963 comment).
        short[]? positions = (d0State >= 0 && d0State < positionsList.Length)
            ? positionsList[d0State]
            : null;
        if (positions == null)
        {
            SetPlayerWithNoBallDestinationForBreak(spriteAddr, teamBase, d6, d7, gameStatePl);
            return;
        }

        // 9432 — mov freeKickDestX, 0.
        short freeKickDestX = 0;
        short myOrdinal = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
        short d0Slot;

        // 9433-9523 — free-kick wall gate: team must NOT be the restarting
        // team, gameState strictly inside (ST_FREE_KICK_LEFT1=6 ..
        // ST_FREE_KICK_RIGHT3=12), and ordinal in [2..5].
        bool inFreeKickWall =
            teamBase != lastTeamBeforeBreak &&
            gameState > 6 && gameState < 12 &&
            myOrdinal >= 2 && myOrdinal <= 5;
        if (inFreeKickWall)
        {
            // 9525-9588 — l_pl_loop_start: remap ordinal to wall slot,
            // skipping red-carded teammates (Sprite.cards sign bit set).
            //   D1 = 2; D0 = 2; A4 = spritesTable + 4 (sprite slot 1)
            //   while (D1 != myOrdinal) { if (sprites[A4++].cards >= 0) D0++; D1++; }
            short d0Count = 2;
            int walkSlot = 1;
            for (short d1Ord = 2; d1Ord != myOrdinal && walkSlot <= 10; d1Ord++)
            {
                int sa = TeamData.GetTeamSpriteAddr(topTeam, walkSlot);
                walkSlot++;
                if (sa == 0) continue;
                short cards = Memory.ReadSignedWord(sa + PlayerSprite.OffCards);
                if (cards >= 0) d0Count++;      // @@player_got_red_card_get_next skips this
            }

            // cseg_8375A — updatePlayers.cpp:9590-9617.
            //   D2 = freeKickFactorsX[gameState - 6]; freeKickDestX = D2 * 4;
            //   D2 back to the factor (shr 2).
            short factor = kFreeKickFactorsX[gameState - 6];
            freeKickDestX = (short)(factor << 2);

            // 9618-9665 — l_next_free_kick_taker: 4 iterations over sprite
            // slots 1..4; each teammate WITHOUT a red card subtracts the
            // factor back off freeKickDestX. (Net zero with a full wall;
            // shifts the wall when someone was sent off.)
            int takerSlot = 1;
            for (short d1Taker = 3; d1Taker >= 0; d1Taker--)
            {
                int sa = TeamData.GetTeamSpriteAddr(topTeam, takerSlot);
                takerSlot++;
                if (sa == 0) continue;
                short cards = Memory.ReadSignedWord(sa + PlayerSprite.OffCards);
                if (cards >= 0)                 // @@free_kick_taker_has_red_card skips
                    freeKickDestX -= factor;    // sub freeKickDestX, ax
            }

            d0Slot = d0Count;                   // → cseg_8381B with remapped slot
        }
        else
        {
            // l_not_in_free_kick_state — updatePlayers.cpp:9669-9672.
            d0Slot = myOrdinal;                 // mov ax, [esi+Sprite.playerOrdinal]
        }

        // cseg_8381B — updatePlayers.cpp:9674-9698.
        //   D0 = (D0 - 1) * 4  (byte offset; we index (x, y) word pairs)
        //   table[D0] == 22222 → l_set_player_with_no_ball_destination.
        int pairIdx = (d0Slot - 1) * 2;
        if (pairIdx < 0 || pairIdx + 1 >= positions.Length)
        {
            // Defensive: asm would read out of the 22-word table.
            SetPlayerWithNoBallDestinationForBreak(spriteAddr, teamBase, d6, d7, gameStatePl);
            return;
        }
        short tblX = positions[pairIdx];
        short tblY = positions[pairIdx + 1];
        if (tblX == 22222)
        {
            SetPlayerWithNoBallDestinationForBreak(spriteAddr, teamBase, d6, d7, gameStatePl);
            return;
        }

        short d1 = tblX;
        short d2 = tblY;
        bool foulRelative;

        // 9700-9750 — top team decode (`cmp A6, offset topTeamData / jnz cseg_838AA`).
        if (teamBase == TeamData.TopBase)
        {
            if (d1 <= -1000)
            {
                // cseg_83981 — updatePlayers.cpp:9831-9849.
                d1 = (short)(d1 + 1000);        // add word ptr D1, 1000
                d1 = (short)(d1 + freeKickDestX); // add word ptr D1, freeKickDestX
                foulRelative = true;            // → l_update_goalie_dest_x_y
            }
            else if (d1 >= 1000)
            {
                // cseg_83999 — updatePlayers.cpp:9851-9869.
                d1 = (short)(d1 - 1000);        // sub word ptr D1, 1000
                d1 = (short)(d1 + freeKickDestX);
                foulRelative = true;
            }
            else
            {
                // 9740-9750 — add word ptr D1, 5 → cseg_8394B.
                d1 = (short)(d1 + 5);
                foulRelative = false;
            }
        }
        else
        {
            // cseg_838AA — updatePlayers.cpp:9752-9805 — bottom team mirrors.
            if (d1 <= -1000)
            {
                // cseg_839B1 — updatePlayers.cpp:9871-9891.
                d1 = (short)(d1 + 1000);        // add word ptr D1, 1000
                d1 = (short)(-d1);              // neg word ptr D1
                d2 = (short)(-d2);              // neg word ptr D2
                d1 = (short)(d1 - freeKickDestX); // sub word ptr D1, freeKickDestX
                foulRelative = true;
            }
            else if (d1 >= 1000)
            {
                // cseg_839D7 — updatePlayers.cpp:9893-9908.
                d1 = (short)(d1 - 1000);        // sub word ptr D1, 1000
                d1 = (short)(-d1);              // neg
                d2 = (short)(-d2);              // neg
                d1 = (short)(d1 - freeKickDestX);
                foulRelative = true;
            }
            else
            {
                // 9784-9805 — D1 = 509 - x - 5; D2 = 640 - y → cseg_8394B.
                d1 = (short)(509 - tblX - 5);
                d2 = (short)(640 - tblY);
                foulRelative = false;
            }
        }

        short destX, destY;
        if (foulRelative)
        {
            // l_update_goalie_dest_x_y — updatePlayers.cpp:9910-9934.
            // dest = (D1 + foulX, D2 + foulY).
            destX = (short)(d1 + d6);
            destY = (short)(d2 + d7);
        }
        else
        {
            // cseg_8394B — updatePlayers.cpp:9807-9829.
            // dest = (D1 + 81, D2 + 129) — pitch-margin offsets.
            destX = (short)(d1 + 81);
            destY = (short)(d2 + 129);
        }
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, destX);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, destY);
        // → l_update_player_speed_and_deltas (caller).
    }

    // l_set_player_with_no_ball_destination + cseg_83A41 —
    // updatePlayers.cpp:9936-10077.
    //
    // Tactics-driven fallback destination (uses D6/D7 — foul coordinates when
    // the game is interrupted, ball coordinates when in progress) followed by
    // the "opponents stand back from the spot" pushback: when the game is
    // stopped, this sprite is not the keeper, and this team is NOT the
    // restarting team, any destination within 65 px (65² = 4225) of the foul
    // spot gets its X pushed 70 px away (toward the near touchline side of
    // pitch-centre X 336 + 81 = 417 screen / 336 pitch... asm compares the
    // RAW foulXCoordinate against 336).
    private static void SetPlayerWithNoBallDestinationForBreak(
        int spriteAddr, int teamBase, short d6, short d7, short gameStatePl)
    {
        // 9936-9937 — call SetPlayerWithNoBallDestination (D6/D7 in registers).
        try
        {
            PlayerHeader.SetPlayerWithNoBallDestination(spriteAddr, teamBase, d6, d7);
        }
        catch (System.Exception)
        {
            // Tactics table not yet populated — the tactics dest is skipped,
            // but DO NOT return: the cseg_83A41 pushback below must still run
            // on the sprite's existing dest. The original has no failure path
            // here; an early return silently disabled the "opponents stand
            // back from the keeper-hold / goal-out spot" guarantee (task #153
            // audit, 2026-07-03). Fall through instead.
        }

        // cseg_83A41 — updatePlayers.cpp:9939-9951: game in progress → done.
        if (gameStatePl == kStGameInProgress) return;

        // 9953-9965 — keeper (playerOrdinal == 1) → done.
        short ordinal = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
        if (ordinal == 1) return;

        // 9967-9978 — restarting team (A6 == lastTeamPlayedBeforeBreak) → done.
        int lastTeamBeforeBreak = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (teamBase == lastTeamBeforeBreak) return;

        // 9980-10033 — squared distance from dest to the foul spot;
        // `cmp D0, 4225 / ja @@update_player_speed_and_deltas` (65 px radius).
        short destX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestX);
        short destY = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestY);
        short foulX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short foulY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);
        int dx = (short)(destX - foulX);
        int dy = (short)(destY - foulY);
        int distSq = dx * dx + dy * dy;         // imul bx (16×16 → 32)
        if ((uint)distSq > 4225u) return;

        // 10035-10076 — push destX 70 px away from the spot:
        //   foulX >= 336 → destX = foulX - 70 (cseg_83B28 path)
        //   foulX <  336 → destX = foulX + 70 (cseg_83B20 path)
        short pushedX = foulX;
        if (pushedX >= 336) pushedX -= 70;      // sub word ptr D0, 70
        else                pushedX += 70;      // cseg_83B20: add word ptr D0, 70
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, pushedX);
        // → l_update_player_speed_and_deltas (caller).
    }

    // ===================================================================
    // TickPassExpectingStopped — l_player_expecting_pass, GAME-STOPPED slice.
    // updatePlayers.cpp:7604-8067 (entry → l_pass_success) +
    // cseg_82EC2 chase/hold (8312-8430) + cseg_8308D goal-out crowd-stop
    // (8563-8634) + l_stop_player (10079-10084).
    // ===================================================================
    //
    // This is the set-piece TAKER mechanism of the original:
    //   1. UpdatePlayerBeingPassedTo's stopped path (swos.asm:101204-101321)
    //      elects the closest eligible sprite of the restarting team as
    //      passToPlayerPtr (gated on team.ballInPlay, which the bcm7 ladder
    //      stage asserts each tick — gameLoop.cpp:1702-1703).
    //   2. THIS branch then runs for that sprite each tick:
    //      - not at the ball, nobody near it → passingBall=1 + CHASE
    //        (dest = team.ballX/ballY) — the taker WALKS up to the ball.
    //      - arrived EXACTLY (ballDistance == 0) → l_pass_success: promote to
    //        controlledPlayer, pin via UpdatePlayerWithBall, playerSwitchTimer
    //        = 25. This fires ONCE — afterwards the sprite is controlled and
    //        never re-enters this branch.
    //      - a teammate (the promoted taker) already owns the spot
    //        (controlled.ballDistance <= 3200 and closer) and THIS sprite is
    //        itself within 3200 sq-px → l_stop_player HOLD (dest = own pos):
    //        the second centre-forward simply STANDS next to the kicker.
    //   3. bcm7 sees controlledPlayer != 0 → standing anim + ballOutOfPlay=1 →
    //      bcm8 whistle → gameStatePl = 102 (kick on fire press).
    //
    // Our PlayerControlled.RunPassExpectingBranch port of the same slice
    // inverted the cseg_82AAF gates (committed l_pass_success for every
    // non-keeper-hold stoppage without the ballDistance == 0 check, and
    // replaced the hold branch with an unconditional chase) — root cause of the
    // kickoff / goal-kick position ping-pong. PlayerControlled.cs is owned by
    // another session, so the faithful stoppage body lives here and the
    // dispatchers route to it whenever gameStatePl != ST_GAME_IN_PROGRESS.
    //
    // Sources: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:
    //   7605-7662  pitch-bounds gate + pass-flag clear + l_stop_player
    //   7892-7908  l_cpu_passing_to → AI_Kick (CPU team only)
    //   7910-7935  cseg_82AAF: stopped + restarting-team gate
    //   7937-7972  keeper-hold keeper shortcut + cseg_82AE5 ballDistance==0
    //   7974-8067  cseg_82AF6 + l_pass_success + throw-in tail
    //   8312-8430  cseg_82EC2: hold-if-taker-near / l_its_a_pass / chase
    //   8563-8634  cseg_8308D: goal-out penalty-area crowd stop
    //   10079-10084 l_stop_player
    private static void TickPassExpectingStopped(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);

        // ----- updatePlayers.cpp:7605-7656 — pitch-bounds test --------------
        // x in [81, 590], y in [129, 769]; outside → clear pass flags + stop.
        short px = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short py = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        if (px < 81 || px > 590 || py < 129 || py > 769)
        {
            // :7658-7662 — passingToPlayer = 0; passingBall = 0; → l_stop_player.
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
            goto l_stop_player;
        }

        // ----- updatePlayers.cpp:7664-7891 — team.ballX/ballY pass lead -----
        // Human team only: samples the kBallFriction spin table to lead a
        // MOVING pass. During a stoppage the ball is stationary (speed 0,
        // deltas 0) so the lead degenerates to the ball position itself,
        // which UpdatePlayerBeingPassedTo already mirrored into team.ballX/
        // ballY this tick (swos.asm:101206-101213). DIVERGENCE (documented):
        // we skip re-sampling the table here — observable state is identical
        // for a dead ball.

        // ----- updatePlayers.cpp:7892-7908 — l_cpu_passing_to ---------------
        short pnPass = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        if (pnPass == 0)
        {
            // from updatePlayers.cpp:19313 — AI_Kick (CPU team only).
            AiHelpers.AI_Kick(spriteAddr, teamBase);
        }

        // ----- cseg_82AAF — updatePlayers.cpp:7910-7935 ---------------------
        // (gameStatePl != ST_GAME_IN_PROGRESS is guaranteed by the caller.)
        // Only the RESTARTING team's pass-target acts during a break.
        int lastBeforeBreak = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (lastBeforeBreak != teamBase)
            goto l_update_player_speed_and_deltas;

        // ----- updatePlayers.cpp:7937-7972 ----------------------------------
        //   gameState != ST_KEEPER_HOLDS_BALL → cseg_82AE5 (distance check)
        //   keeper-hold + playerOrdinal == 1  → cseg_82AF6 (direct success)
        //   cseg_82AE5: ballDistance != 0     → cseg_82EC2 (chase / hold)
        {
            short gsPass = Memory.ReadSignedWord(Memory.Addr.gameState);
            short ordPass = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
            if (gsPass == 3 && ordPass == 1)
                goto l_pass_success_entry;                      // :7950-7962 jz cseg_82AF6
            int bdPass = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance);
            if (bdPass != 0)
                goto l_chase_or_hold;                           // :7964-7972 jnz cseg_82EC2
        }

    l_pass_success_entry:
        // ----- cseg_82AF6 — updatePlayers.cpp:7974-8002 ---------------------
        // cameraDirection >= 8 → refresh it from the receiver's facing
        // (+ dead-stat counter dseg_132804).
        {
            short camDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
            if (camDir >= 8)
            {
                short sprDir = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDirection);
                Memory.WriteWord(Memory.Addr.cameraDirection, sprDir);
                short cnt = Memory.ReadSignedWord(Memory.Addr.dseg_132804);
                Memory.WriteWord(Memory.Addr.dseg_132804, (short)(cnt + 1));
            }
        }

        // ----- l_pass_success — updatePlayers.cpp:8004-8028 -----------------
        {
            short camDirNow = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDirection, camDirNow);   // :8005-8007
            int passToPtr = Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr);
            Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, passToPtr); // :8009-8010
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);          // :8011
            Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 25);        // :8012
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);               // :8013
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);           // :8014
            Memory.WriteWord(teamBase + TeamData.OffShooting, 0);                  // :8015
            // :8016-8017 — esi = A2 (ball sprite); the "currentAllowedDirection"
            // symbol is a mislabel — offset 44 on a Sprite is `speed`. Ball is
            // now owned by the receiver, momentum cancelled.
            BallSprite.Speed = 0;
            Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);               // :8018-8019
            Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);           // :8020-8021
            Memory.WriteWord(Memory.Addr.penalty, 0);                              // :8022
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);                        // :8023
            // :8024 — updatePlayerWithBall(A1): pins the taker at the ball spot
            // (± the per-direction dribble offset). This is the ONLY position
            // write of the promotion and fires exactly once — the sprite is
            // controlledPlayer from here on and never re-enters this branch.
            PlayerActions.UpdatePlayerWithBall(spriteAddr);
            Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 0);             // :8026
            Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);     // :8027 (offset 104)
            Memory.WriteWord(teamBase + 116, 0);                                   // :8028 — field_74
        }

        // ----- throw-in tail — updatePlayers.cpp:8029-8067 ------------------
        // gameState in [ST_THROW_IN_FORWARD_RIGHT(15) .. ST_THROW_IN_BACK_LEFT
        // (20)] → thrower setup: touchline stance coords, about-to-throw-in
        // animation, PL_THROW_IN state, hideBall.
        {
            short gsThrow = Memory.ReadSignedWord(Memory.Addr.gameState);
            if (gsThrow >= 15 && gsThrow <= 20)
            {
                // :8055-8059 — D0 = sprite.direction (consumed by the callee);
                // SetThrowInPlayerDestinationCoordinates reads it off the
                // sprite in our port.
                SetPieces.SetThrowInPlayerDestinationCoordinates(spriteAddr);
                // :8061-8062 — aboutToThrowInAnimTable.
                PlayerActions.SetPlayerAnimationTable(spriteAddr, Memory.Addr.aboutToThrowInAnimTable);
                // :8063-8065 — playerState = PL_THROW_IN (5); playerDownTimer = 0.
                Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 5);
                Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, 0);
                // :8066 — hideBall = 1 (thrower holds it above his head).
                Memory.WriteWord(Memory.Addr.hideBall, 1);
            }
        }
        goto l_update_player_speed_and_deltas;                                     // :8040/8053/8067

    l_chase_or_hold:
        // ----- cseg_82EC2 — updatePlayers.cpp:8312-8391 ---------------------
        // passingBall == 0 → check whether a teammate (the promoted taker)
        // already owns the ball spot; if so and THIS sprite is also within
        // 3200 sq-px (~56 px), HOLD in place. Otherwise latch passingBall = 1
        // (l_its_a_pass) and CHASE the ball.
        {
            short passingBall = Memory.ReadSignedWord(teamBase + TeamData.OffPassingBall);
            if (passingBall == 0)
            {
                int a5Ctrl = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
                bool itsAPass = false;
                if (a5Ctrl == 0)
                {
                    itsAPass = true;                                     // :8322-8334 jz l_its_a_pass
                }
                else
                {
                    int ctrlBd = Memory.ReadSignedDword(a5Ctrl + PlayerSprite.OffBallDistance);
                    int myBd = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance);
                    if ((uint)ctrlBd > 3200u)                            // :8336-8348 ja l_its_a_pass
                    {
                        itsAPass = true;
                    }
                    else if ((uint)ctrlBd > (uint)myBd)                  // :8350-8364 ja l_its_a_pass
                    {
                        itsAPass = true;
                    }
                    else if ((uint)myBd <= 3200u)                        // :8366-8377 ja → chase
                    {
                        // :8379-8387 — the taker is on the spot and this sprite
                        // is close by: HOLD — dest = own position, exit. This is
                        // what keeps the second centre-forward standing quietly
                        // next to the kickoff taker instead of oscillating.
                        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, px);
                        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, py);
                        goto l_update_player_speed_and_deltas;
                    }
                    // myBd > 3200 → fall through to chase without the
                    // passingBall latch (:8366-8377 ja l_player_chase_ball).
                }
                if (itsAPass)
                {
                    Memory.WriteWord(teamBase + TeamData.OffPassingBall, 1);   // :8389-8391
                }
            }
        }

        // ----- l_player_chase_ball — updatePlayers.cpp:8393-8430 ------------
        // dest = team.ballX/ballY (mirrored ball pos), then "already there?"
        {
            short tBallX = Memory.ReadSignedWord(teamBase + TeamData.OffBallX);
            short tBallY = Memory.ReadSignedWord(teamBase + TeamData.OffBallY);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, tBallX);          // :8394-8397
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, tBallY);          // :8398-8401
            if (px == tBallX && py == tBallY)                                      // :8402-8430
                goto l_update_player_speed_and_deltas;
        }

        // ----- l_player_still_moving → cseg_8308D — updatePlayers.cpp:8432-8444
        // + 8563-8634. In-progress branch (keeper pass-cancel) unreachable here
        // (gameStatePl != 100 → jnz cseg_8308D at :8443-8444).
        {
            short goalOutFlag = Memory.ReadSignedWord(Memory.Addr.goalOut);
            if (goalOutFlag == 0)
                goto l_update_player_speed_and_deltas;                             // :8564-8570
            short ordGoalOut = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
            if (ordGoalOut == 1)
                goto l_update_player_speed_and_deltas;                             // :8572-8584
            // D1/D2 = sprite whole-pixel pos (loaded at :8402-8405).
            if (px < 183)  goto l_update_player_speed_and_deltas;                  // :8586-8596 jl
            if (px > 488)  goto l_update_player_speed_and_deltas;                  // :8598-8608 jg
            if (py <= 226) goto l_stop_player;                                     // :8610-8620 jle
            if (py >= 672) goto l_stop_player;                                     // :8622-8632 jge
            goto l_update_player_speed_and_deltas;                                 // :8634
        }

    l_stop_player:
        // l_stop_player — updatePlayers.cpp:10079-10084: dest = own position.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, px);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, py);

    l_update_player_speed_and_deltas:
        // l_update_player_speed_and_deltas — updatePlayers.cpp:10086-10106.
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // ===================================================================
    // Break-positioning data — swos.asm:245961-246110.
    // ===================================================================
    //
    // freeKickFactorsX dw 0, 3, 4, 5, 4, 3, 0 — swos.asm:245961. Indexed by
    // gameState - 6 (free-kick states 6..12); scales the defensive-wall X
    // spread per free-kick position.
    private static readonly short[] kFreeKickFactorsX = { 0, 3, 4, 5, 4, 3, 0 };

    // Positions tables — 11 (x, y) pairs each, indexed by (ordinal - 1).
    // Values are raw table words from swos.asm (constants copied verbatim,
    // per the project licence rule). Sentinels:
    //   22222        → "no fixed spot": use setPlayerWithNoBallDestination.
    //   x <= -1000 / x >= 1000 → foul-spot-relative (offset ∓1000), mirrored
    //   and freeKickDestX-shifted per team (updatePlayers.cpp:9831-9908).
    //
    // swos.asm:246049 bottomStartingPositions — walk-back formation for the
    // restarting team (gameState 0). The decode maps the keeper pair (250, 1)
    // to (336, 130) for the top team and (335, 768) for the bottom team.
    private static readonly short[] kBottomStartingPositions = {
        250,1, 100,155, 200,133, 300,133, 400,155, 60,320,
        190,244, 310,244, 355,320, 220,320, 260,316
    };
    // swos.asm:246052 topStartingPositions — walk-back formation for the
    // non-restarting team (two forwards near the centre circle).
    private static readonly short[] kTopStartingPositions = {
        250,11, 120,133, 210,133, 290,133, 380,133, 90,244,
        220,200, 280,200, 410,244, 200,277, 300,277
    };
    // swos.asm:246019 dseg_17E831 — goal-out left, bottom list.
    private static readonly short[] kDseg17E831 = {
        290,10, 100,166, 200,133, 300,133, 400,166, 100,355,
        200,277, 300,277, 400,355, 200,444, 300,444
    };
    // swos.asm:246022 dseg_17E85D — goal-out left, top list.
    private static readonly short[] kDseg17E85D = {
        250,11, 100,144, 200,144, 300,144, 400,144, 100,244,
        200,277, 300,277, 400,244, 200,444, 300,444
    };
    // swos.asm:246025 dseg_17E889 — goal-out right, bottom list.
    private static readonly short[] kDseg17E889 = {
        210,10, 100,166, 200,133, 300,133, 400,166, 100,355,
        200,277, 300,277, 400,355, 200,444, 300,444
    };
    // swos.asm:246028 dseg_17E8B5 — goal-out right, top list.
    private static readonly short[] kDseg17E8B5 = {
        250,11, 100,144, 200,144, 300,144, 400,144, 100,244,
        200,277, 300,277, 400,244, 200,444, 300,444
    };
    // swos.asm:246031 dseg_17E8E1 — keeper-holds-ball, bottom list.
    private static readonly short[] kDseg17E8E1 = {
        1000,0, 100,166, 200,133, 300,133, 400,166, 100,355,
        200,277, 300,277, 400,355, 200,444, 300,444
    };
    // swos.asm:246034 dseg_17E90D — keeper-holds-ball, top list.
    private static readonly short[] kDseg17E90D = {
        250,11, 100,144, 200,144, 300,144, 400,144, 100,244,
        200,277, 300,277, 400,244, 200,444, 300,444
    };
    // swos.asm:246007 dseg_17E781 — corner left, bottom list.
    private static readonly short[] kDseg17E781 = {
        250,5, 100,600, 200,277, 300,277, 490,600, 150,500,
        250,600, 350,500, 450,555, 210,611, 290,611
    };
    // swos.asm:246010 dseg_17E7AD — corner left, top list.
    private static readonly short[] kDseg17E7AD = {
        250,2, 175,38, 225,22, 275,22, 325,38, 150,111,
        200,55, 300,83, 400,166, 200,300, 300,300
    };
    // swos.asm:246013 dseg_17E7D9 — corner right, bottom list.
    private static readonly short[] kDseg17E7D9 = {
        250,5, 10,600, 200,277, 300,277, 400,277, 50,555,
        150,500, 250,600, 350,500, 210,611, 290,611
    };
    // swos.asm:246016 dseg_17E805 — corner right, top list.
    private static readonly short[] kDseg17E805 = {
        250,2, 175,38, 225,22, 275,22, 325,38, 100,166,
        200,83, 300,55, 350,111, 200,300, 300,300
    };
    // swos.asm:246055 dseg_17E991 — free kick, top list (foul-relative walls).
    private static readonly short[] kDseg17E991 = {
        250,2, -1015,-54, -1005,-55, 1005,-55, 1015,-54, -1080,20,
        -1050,-55, 1060,-45, 1070,40, 22222,22222, 22222,22222
    };
    // swos.asm:246058 dseg_17E9BD — free kick, bottom list.
    private static readonly short[] kDseg17E9BD = {
        250,5, 22222,22222, 22222,22222, 22222,22222, 22222,22222, -1090,20,
        -1035,-35, 1020,-60, 1120,30, -1020,-10, 1040,-15
    };
    // swos.asm:246061 dseg_17E9E9 — free kick right 3, top list.
    private static readonly short[] kDseg17E9E9 = {
        250,2, 290,22, 220,27, -1048,-29, -1043,-37, 160,88,
        160,50, 250,27, 310,22, 22222,22222, 22222,22222
    };
    // swos.asm:246064 dseg_17EA15 — free kick right 3, bottom list.
    private static readonly short[] kDseg17EA15 = {
        250,5, 22222,22222, 22222,22222, 22222,22222, 22222,22222, -1000,-15,
        1030,-25, 200,500, 340,522, 220,605, 260,611
    };
    // swos.asm:246067 dseg_17EA41 — free kick left 1, top list.
    private static readonly short[] kDseg17EA41 = {
        250,2, 1043,-37, 1048,-29, 280,27, 210,22, 190,22,
        250,27, 340,50, 340,88, 22222,22222, 22222,22222
    };
    // swos.asm:246070 dseg_17EA6D — free kick left 1, bottom list.
    private static readonly short[] kDseg17EA6D = {
        250,5, 22222,22222, 22222,22222, 22222,22222, 22222,22222, 160,522,
        300,500, -1030,-25, 1000,-15, 240,611, 280,605
    };
    // swos.asm:246073 dseg_17EA99 — free kick right 2, top list.
    private static readonly short[] kDseg17EA99 = {
        250,2, -1048,-26, -1043,-31, -1037,-36, -1030,-41, 190,11,
        220,38, 250,33, 300,22, 22222,22222, 22222,22222
    };
    // swos.asm:246076 dseg_17EAC5 — free kick right 2, bottom list.
    private static readonly short[] kDseg17EAC5 = {
        250,5, 22222,22222, 22222,22222, 22222,22222, 22222,22222, -1000,-15,
        1030,-25, 250,411, 340,522, 220,611, 260,611
    };
    // swos.asm:246079 dseg_17EAF1 — free kick left 2, top list.
    private static readonly short[] kDseg17EAF1 = {
        250,2, 1030,-41, 1037,-36, 1043,-31, 1048,-26, 200,22,
        250,33, 280,38, 310,11, 22222,22222, 22222,22222
    };
    // swos.asm:246082 dseg_17EB1D — free kick left 2, bottom list.
    private static readonly short[] kDseg17EB1D = {
        250,5, 22222,22222, 22222,22222, 22222,22222, 22222,22222, 160,522,
        250,411, -1030,-25, 1000,-15, 240,611, 280,611
    };
    // swos.asm:246085 dseg_17EB49 — free kick centre, top list.
    private static readonly short[] kDseg17EB49 = {
        250,2, -1038,-44, -1031,-47, -1023,-50, -1014,-53, 190,11,
        220,38, 250,33, 300,22, 22222,22222, 22222,22222
    };
    // swos.asm:246088 dseg_17EB75 — free kick centre, bottom list.
    private static readonly short[] kDseg17EB75 = {
        250,5, 22222,22222, 22222,22222, 22222,22222, 22222,22222, -1000,-15,
        1030,-25, 250,411, 340,522, 220,611, 260,611
    };
    // swos.asm:246091 dseg_17EBA1 — free kick left 3, top list.
    private static readonly short[] kDseg17EBA1 = {
        250,2, 1014,-53, 1023,-50, 1031,-47, 1038,-44, 200,22,
        250,33, 280,38, 310,11, 22222,22222, 22222,22222
    };
    // swos.asm:246094 dseg_17EBCD — free kick left 3, bottom list.
    private static readonly short[] kDseg17EBCD = {
        250,5, 22222,22222, 22222,22222, 22222,22222, 22222,22222, 160,522,
        250,411, -1030,-25, 1000,-15, 240,611, 280,611
    };
    // swos.asm:246097 dseg_17EBF9 — throw-in walk-up, bottom list.
    private static readonly short[] kDseg17EBF9 = {
        250,5, 100,277, 200,277, 300,277, 400,277, 100,500,
        200,500, 300,500, 400,500, 220,555, 250,522
    };
    // swos.asm:246100 dseg_17EC25 — throw-in walk-up, top list.
    private static readonly short[] kDseg17EC25 = {
        250,2, 175,88, 225,122, 275,122, 325,88, 70,155,
        200,166, 300,166, 430,155, 150,300, 300,300
    };
    // swos.asm dseg_17EC51 — throw-in support runs (foul-relative).
    private static readonly short[] kDseg17EC51 = {
        22222,22222, 22222,22222, 22222,22222, -1075,0, -1025,0, 22222,22222,
        22222,22222, -1075,50, -1050,75, 22222,22222, 22222,22222
    };
    // swos.asm dseg_17EC7D.
    private static readonly short[] kDseg17EC7D = {
        22222,22222, 22222,22222, 22222,22222, 22222,22222, 22222,22222, 22222,22222,
        -1100,25, -1075,-20, -1025,-25, 22222,22222, -1075,75
    };
    // swos.asm dseg_17ECA9.
    private static readonly short[] kDseg17ECA9 = {
        22222,22222, 22222,22222, 22222,22222, 22222,22222, 22222,22222, 22222,22222,
        -1075,-50, -1075,0, -1025,0, 22222,22222, 22222,22222
    };
    // swos.asm dseg_17ECD5.
    private static readonly short[] kDseg17ECD5 = {
        22222,22222, 1025,0, 1075,0, 22222,22222, 22222,22222, 1075,50,
        1050,75, 22222,22222, 22222,22222, 22222,22222, 22222,22222
    };
    // swos.asm dseg_17ED01.
    private static readonly short[] kDseg17ED01 = {
        22222,22222, 22222,22222, 22222,22222, 22222,22222, 22222,22222, 1025,-25,
        1075,-20, 1100,25, 22222,22222, 22222,22222, 1075,75
    };
    // swos.asm dseg_17ED31.
    private static readonly short[] kDseg17ED31 = {
        22222,22222, 22222,22222, 22222,22222, 22222,22222, 22222,22222, 1025,0,
        1075,0, 1075,-50, 22222,22222, 22222,22222, 22222,22222
    };
    // swos.asm dseg_17ED5D — penalty-kick line-up, bottom list.
    private static readonly short[] kDseg17ED5D = {
        380,255, 365,261, 350,266, 335,272, 320,277, 305,283,
        290,288, 275,294, 260,300, 245,305, 230,311
    };
    // swos.asm dseg_17ED89 — penalty-kick line-up, top list.
    private static readonly short[] kDseg17ED89 = {
        120,255, 135,261, 150,266, 165,272, 180,277, 195,283,
        210,288, 225,294, 240,300, 255,305, 270,311
    };
    // swos.asm playersLeavingPitchBottom — everyone heads for the tunnel.
    private static readonly short[] kPlayersLeavingPitchBottom = {
        550,277, 550,277, 550,277, 550,277, 550,277, 550,277,
        550,277, 550,277, 550,277, 550,277, 550,277
    };
    // swos.asm playersLeavingPitchTop.
    private static readonly short[] kPlayersLeavingPitchTop = {
        -50,377, -50,377, -50,377, -50,377, -50,377, -50,377,
        -50,377, -50,377, -50,377, -50,377, -50,377
    };
    // swos.asm dseg_17EE0D — identical values to bottomStartingPositions
    // (second-half restart entries 27/28).
    private static readonly short[] kDseg17EE0D = {
        250,1, 100,155, 200,133, 300,133, 400,155, 60,320,
        190,244, 310,244, 355,320, 220,320, 260,316
    };
    // swos.asm dseg_17EE39 — identical values to topStartingPositions.
    private static readonly short[] kDseg17EE39 = {
        250,11, 120,133, 210,133, 290,133, 380,133, 90,244,
        220,200, 280,200, 410,244, 200,277, 300,277
    };
    // swos.asm bottomPenaltyPositions / topPenaltyPositions — shootout ring.
    private static readonly short[] kBottomPenaltyPositions = {
        200,522, 260,344, 280,333, 300,333, 320,344, 260,366,
        280,355, 300,355, 320,366, 280,377, 300,377
    };
    private static readonly short[] kTopPenaltyPositions = {
        250,2, 260,277, 280,266, 300,266, 320,277, 260,300,
        280,288, 300,288, 320,300, 280,311, 300,311
    };

    // bottomBallOutOfPlayPositions — swos.asm:245963-245998. "indexed by
    // gameState, states < 32; each pointer points to table of 22 elements,
    // x & y position, player ordinal * 2 is index". Selected when this team
    // IS lastTeamPlayedBeforeBreak (updatePlayers.cpp:9390-9406).
    private static readonly short[]?[] kBottomBallOutOfPlayPositions = {
        kBottomStartingPositions,   //  0 — post-goal / kick-off walk-back
        kDseg17E831,                //  1 — ST_GOAL_OUT_LEFT (bypassed at 9289-9300)
        kDseg17E889,                //  2 — ST_GOAL_OUT_RIGHT (bypassed)
        kDseg17E8E1,                //  3 — ST_KEEPER_HOLDS_BALL (bypassed)
        kDseg17E781,                //  4 — ST_CORNER_LEFT
        kDseg17E7D9,                //  5 — ST_CORNER_RIGHT
        kDseg17EA6D,                //  6 — ST_FREE_KICK_LEFT1
        kDseg17EB1D,                //  7
        kDseg17EBCD,                //  8
        kDseg17E9BD,                //  9
        kDseg17EB75,                // 10
        kDseg17EAC5,                // 11
        kDseg17EA15,                // 12 — ST_FREE_KICK_RIGHT3
        null,                       // 13
        kDseg17EBF9,                // 14 — throw-in
        kDseg17EC51,                // 15
        kDseg17EC7D,                // 16
        kDseg17ECA9,                // 17
        kDseg17ECD5,                // 18
        kDseg17ED01,                // 19
        kDseg17ED31,                // 20
        kDseg17ED5D,                // 21 — penalty kick
        kDseg17ED5D,                // 22
        kPlayersLeavingPitchBottom, // 23 — going to halftime
        kPlayersLeavingPitchBottom, // 24 — going to shower
        kPlayersLeavingPitchBottom, // 25
        kPlayersLeavingPitchBottom, // 26
        kDseg17EE0D,                // 27 — second-half restart
        kDseg17EE0D,                // 28
        null,                       // 29 — ST_FIRST_HALF_ENDED (bailed earlier)
        null,                       // 30 — ST_GAME_ENDED (bailed earlier)
        kBottomPenaltyPositions,    // 31 — ST_PENALTIES
    };

    // topBallOutOfPlayPositions — swos.asm:245999-246006 ("indexed by game
    // state"). Selected when this team is NOT lastTeamPlayedBeforeBreak.
    private static readonly short[]?[] kTopBallOutOfPlayPositions = {
        kTopStartingPositions,      //  0 — post-goal / kick-off walk-back
        kDseg17E85D,                //  1
        kDseg17E8B5,                //  2
        kDseg17E90D,                //  3
        kDseg17E7AD,                //  4 — ST_CORNER_LEFT
        kDseg17E805,                //  5 — ST_CORNER_RIGHT
        kDseg17EA41,                //  6 — ST_FREE_KICK_LEFT1
        kDseg17EAF1,                //  7
        kDseg17EBA1,                //  8
        kDseg17E991,                //  9
        kDseg17EB49,                // 10
        kDseg17EA99,                // 11
        kDseg17E9E9,                // 12 — ST_FREE_KICK_RIGHT3
        null,                       // 13
        kDseg17EC25,                // 14 — throw-in
        null,                       // 15
        null,                       // 16
        null,                       // 17
        null,                       // 18
        null,                       // 19
        null,                       // 20
        kDseg17ED89,                // 21 — penalty kick
        kDseg17ED89,                // 22
        kPlayersLeavingPitchTop,    // 23
        kPlayersLeavingPitchTop,    // 24
        kPlayersLeavingPitchTop,    // 25
        kPlayersLeavingPitchTop,    // 26
        kDseg17EE39,                // 27
        kDseg17EE39,                // 28
        null,                       // 29
        null,                       // 30
        kTopPenaltyPositions,       // 31 — ST_PENALTIES
    };

    // updatePlayers.cpp:7462 — l_player_tackled.
    // Counts down playerDownTimer (sprite+13). On reaching zero, transitions
    // to ROLLING_INJURED (if injuryLevel > 0) or back to NORMAL.
    //
    // While ground-bound, applies friction (kPlayerGroundConstant) to speed.
    private static void TickTackledPlayer(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);

        // updatePlayers.cpp:7463-7475 — sub [esi+Sprite.playerDownTimer], 1
        sbyte downTimer = (sbyte)Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerDownTimer);
        downTimer = (sbyte)(downTimer - 1);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, downTimer);

        if (downTimer != 0)
        {
            // updatePlayers.cpp:7477 — l_player_on_the_ground.
            // Friction-decay speed; if reached 0 → stop. Mechanical port of
            // updatePlayers.cpp:7502-7602 including goalkeeper-area extra friction.
            //
            // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:7502-7602.
            short speed = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSpeed);
            if (speed > 0)
            {
                // updatePlayers.cpp:7511-7536 — goalkeeper-area Y gate.
                //   cmp player.y, 159 / jl in_area_by_y
                //   cmp player.y, 739 / jg in_area_by_y  (jle player_not_in_area)
                short py = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
                bool inAreaByY = (py < 159) || (py > 739);
                if (inAreaByY)
                {
                    // updatePlayers.cpp:7538-7564 — X gate must overlap goal area.
                    //   cmp player.x, 265 / jl player_not_in_area
                    //   cmp player.x, 406 / jg player_not_in_area
                    short px = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
                    if (px >= 265 && px <= 406)
                    {
                        // updatePlayers.cpp:7566-7580 — extra friction inside the
                        // keeper area: `speed -= speed >> 2` (subtract 25%).
                        int sub = (ushort)speed >> 2;
                        speed = (short)(speed - sub);
                    }
                }

                // updatePlayers.cpp:7583 — sub speed, kPlayerGroundConstant.
                // Mechanical port of swos.asm:203803 `kPlayerGroundConstant dw 96`.
                // Constant — same value PC & Amiga (no amigaMode toggle). Used by
                // l_player_on_the_ground (tackled friction) and tackle ground-bound
                // path.
                // Source: external/swos-port/swos/swos.asm:203803
                const int kPlayerGroundConstant = 96;
                speed = (short)(speed - kPlayerGroundConstant);
                // updatePlayers.cpp:7598-7601 — `jns @@update_player_speed`; else
                // clamp to zero. Mirrors `if (speed went negative) speed = 0`.
                if (speed < 0) speed = 0;
                Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, speed);
            }
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:7479-7493 — timer zero: check injury level.
        // Sprite.injuryLevel word at offset +104 (PlayerSprite.OffInjuryLevel).
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:7479.
        short injuryLevel = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffInjuryLevel);
        if (injuryLevel != 0)
        {
            // Transition to ROLLING_INJURED (state=13).
            Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, 0);
            Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 13);
            Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, (byte)injuryLevel);
            // updatePlayers.cpp:7491 — SetPlayerAnimationTable(plInjuredAnimTable).
            PlayerActions.SetPlayerAnimationTable(spriteAddr, Memory.Addr.kPlInjuredAnimTableAddr);
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:7495-7500 — l_get_up_normally: back to PL_NORMAL.
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
        // updatePlayers.cpp:7499 — SetPlayerAnimationTable(playerNormalStandingAnimTable).
        PlayerActions.SetPlayerAnimationTable(spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);

        // updatePlayers.cpp:10079 — l_stop_player: snap destX/Y to pos.
        short sx = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short sy = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, sx);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, sy);
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // updatePlayers.cpp:6375 — l_player_tackling (PL_TACKLING, state=1) per-tick body.
    //
    // Without this body PL_TACKLING players never exit the state. They drift
    // toward their tackle destination, get clamped at the pitch cushion (e.g.
    // Y=129 at the top edge by PlayerBeginTackling's defensive clamp), and
    // sit there indefinitely — periodically punching the ball into the goal
    // via PlayersTackledTheBallStrong on the strike-range check above.
    //
    // The asm has TWO countdowns:
    //   - tacklingTimer (word at +106) — increments per tick from 0 while >= 0,
    //     or stays at -1 once flipped to the "computer-tackling downtime" sentinel.
    //   - playerDownTimer (byte at +13) — decrements per tick; reaching 0 ends
    //     the tackle and snaps the player back to PL_NORMAL + standing anim.
    //
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:6375-6661.
    private static void TickTacklingPlayer(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);

        // updatePlayers.cpp:6824 — playerTacklingTestFoul (called from the
        // l_tackling_empty_space branch, but we keep it at the top of the tick
        // for now since the foul check is independent of the tackling FSM and
        // matches what the previous shim did).
        PlayerTackle.PlayerTacklingTestFoul(spriteAddr, teamBase);

        // updatePlayers.cpp:6377-6392 — if tacklingTimer >= 0 then ++tacklingTimer.
        // (Negative is the "computer-tackling" sentinel which stays put.)
        short tackTimer = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffTacklingTimer);
        if (tackTimer >= 0)
        {
            Memory.WriteWord(spriteAddr + PlayerSprite.OffTacklingTimer, (short)(tackTimer + 1));
        }

        // updatePlayers.cpp:6394-6414 — sub [esi+Sprite.playerDownTimer], 1.
        // If timer reaches 0 → transition to PL_NORMAL + standing anim +
        // l_stop_player (snap destX/Y to current position so the player stops).
        sbyte downTimer = (sbyte)Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerDownTimer);
        downTimer = (sbyte)(downTimer - 1);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, downTimer);

        if (downTimer == 0)
        {
            // updatePlayers.cpp:6411-6414 — playerState = PL_NORMAL,
            // anim = playerNormalStandingAnimTable, jmp l_stop_player.
            Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
            PlayerActions.SetPlayerAnimationTable(spriteAddr, Memory.Addr.playerNormalStandingAnimTable);

            // updatePlayers.cpp:10079-10085 — l_stop_player: destX = x, destY = y.
            short sx = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
            short sy = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, sx);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, sy);
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:6416-6462 — l_player_down_tackling: handle the
        // "human releases fire while tackling" early-cancel via the tacklingTimer
        // negation trick. If tacklingTimer < 0 already → straight to l_computer_tackling.
        tackTimer = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffTacklingTimer);
        if (tackTimer >= 0)
        {
            // updatePlayers.cpp:6426-6441 — if (team.playerNumber != 0 &&
            //                                  team.firePressed == 0) then flip the timer.
            // playerNumber == 0 → CPU team → skip the human cancel path entirely.
            // firePressed != 0 → human still holding fire → keep tackling normally.
            short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
            byte firePressed = Memory.ReadByte(teamBase + TeamData.OffFirePressed);
            if (playerNumber != 0 && firePressed == 0)
            {
                // updatePlayers.cpp:6443-6462 — neg tacklingTimer; cmp -2; jl
                // l_computer_tackling; else write -1.
                short neg = (short)(-tackTimer);
                Memory.WriteWord(spriteAddr + PlayerSprite.OffTacklingTimer, neg);
                if (neg >= -2)
                {
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffTacklingTimer, -1);
                }
            }
        }

        // updatePlayers.cpp:6464-6493 — l_computer_tackling: speed -= kPlayerGroundConstant.
        // If speed went to 0 or below → clamp to 0 + setPlayerDowntimeAfterTackle +
        // jump straight to update_player_speed_and_deltas. Else fall into the
        // in-pitch ball-strike check.
        //
        // kPlayerGroundConstant: swos.asm:203803 `dw 96` — shared with TickTackledPlayer.
        const int kPlayerGroundConstant = 96;
        short speed = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSpeed);
        if (speed != 0)
        {
            speed = (short)(speed - kPlayerGroundConstant);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, speed);
            if (speed <= 0)
            {
                // updatePlayers.cpp:6491-6493 — speed=0; setPlayerDowntimeAfterTackle;
                //   jmp l_update_player_speed_and_deltas.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, 0);
                PlayerActions.SetPlayerDowntimeAfterTackle(teamBase, spriteAddr);
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
                return;
            }
        }
        else
        {
            // updatePlayers.cpp:6471-6472 — `jz @@update_player_speed_and_deltas`.
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:6495-6661 — l_player_still_tackling_and_moving:
        // bounds check + in-pitch / out-of-pitch / out-of-goalie-area branches.
        // The strong-tackle ball-strike check (l_pl_tackling_in_pitch →
        // l_player_tackling_the_ball → l_strong_tackle) is the same conservative
        // gate the previous shim implemented; keep it for now so we don't
        // regress ball-strike behaviour.
        //
        // updatePlayers.cpp:6663-6800 — l_pl_tackling_in_pitch + l_tackling_near_the_ball
        // + l_strong_tackle. Same gate as before.
        short tackTimer2 = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffTacklingTimer);
        short tackState  = Memory.ReadSignedWord(spriteAddr + 96);
        int ballDist    = Memory.ReadSignedDword(spriteAddr + 74);
        byte ballLe4    = Memory.ReadByte(teamBase + 64);
        byte ball4To8   = Memory.ReadByte(teamBase + 65);
        short gsPl      = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gsPl == kStGameInProgress
            && tackState == 0
            && (uint)ballDist <= 64u
            && tackTimer2 != -1
            && (ballLe4 != 0 || ball4To8 != 0))
        {
            // l_player_tackling_the_ball — updatePlayers.cpp:6730-6752.
            // Credit the tackler + record whether the opponent held the ball.
            Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);
            Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);
            Memory.WriteWord(Memory.Addr.penalty, 0);
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);
            int tackleOppBase = Memory.ReadSignedDword(teamBase + TeamData.OffOpponentsTeam);
            short tackleOppHasBall = Memory.ReadSignedWord(tackleOppBase + TeamData.OffPlayerHasBall);
            if (tackleOppHasBall != 0)
            {
                Memory.WriteWord(Memory.Addr.playerHadBall, 1);
            }

            PlayerTackle.PlayersTackledTheBallStrong(spriteAddr, teamBase);

            // cseg_821F5 — updatePlayers.cpp:6791-6803 / swos.asm:115876-115894.
            // Tackler cooldown + tackled team locked out of ball control for
            // 12 ticks (opponent.wonTheBallTimer at offset +138) — the
            // original's "a dispossession sticks" rule. NOTE: the original
            // applies this epilogue to BOTH weak and strong tackle branches;
            // the weak variant (tacklingTimer == -1, updatePlayers.cpp:
            // 6756-6779) is still un-ported and must share it when it lands.
            Memory.WriteDword(teamBase + TeamData.OffLastHeadingPlayer, spriteAddr);
            Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 25);
            Memory.WriteWord(tackleOppBase + TeamData.OffPassingToPlayer, 0);
            Memory.WriteDword(tackleOppBase + TeamData.OffPassingKickingPlayer, 0);
            Memory.WriteWord(tackleOppBase + TeamData.OffSpinTimer, -1);
            Memory.WriteWord(tackleOppBase + 138, 12);   // wonTheBallTimer (+138)
        }
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // updatePlayers.cpp:7432 — l_player_injured (PL_ROLLING_INJURED, state=13).
    // Manages the rolling-injured animation timer; resets to Normal when
    // the player has finished the get-up sequence. If injuriesForever flag
    // is set the player stays down indefinitely.
    private static void TickInjuredRollingPlayer(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);

        // updatePlayers.cpp:7433-7439 — if injuriesForever != 0 → skip timer
        // (jump to l_update_player_speed_and_deltas without decrementing the
        // downTimer or transitioning out of PL_ROLLING_INJURED). This is a
        // tester/debug switch (swos.asm:245614 `injuriesForever dw 0`),
        // defaults to 0 in normal play, but honouring it preserves test parity.
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:7433.
        short injuriesForever = Memory.ReadSignedWord(Memory.Addr.injuriesForever);
        if (injuriesForever != 0)
        {
            // Skip timer; refresh speed and exit (mirrors the jnz target).
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:7441-7455 — sub [esi+Sprite.playerDownTimer], 1.
        sbyte downTimer = (sbyte)Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerDownTimer);
        downTimer = (sbyte)(downTimer - 1);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, downTimer);

        if (downTimer != 0)
        {
            // Still rolling — just refresh speed.
            PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
            return;
        }

        // updatePlayers.cpp:7457-7460 — timer 0: state = PL_NORMAL,
        // anim table = playerNormalStandingAnimTable, then l_stop_player.
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
        // updatePlayers.cpp:7459 — SetPlayerAnimationTable(playerNormalStandingAnimTable).
        PlayerActions.SetPlayerAnimationTable(spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);

        // l_stop_player: snap destX/Y to current pos.
        short sx = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short sy = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, sx);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, sy);
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
    }

    // updatePlayers.cpp:537-679 — update ball-distance and ball-height flags
    // on the team struct. Used by AI / referee / kick decision code to
    // bucket "how close is the ball?" and "how high is the ball?".
    //
    // Distance buckets (sprite+74 = ballDistance, squared px):
    //   <= 32   → plVeryCloseToBall = 1  (very close)
    //   <= 72   → plCloseToBall     = 1  (close)
    //   <= 2450 → plNotFarFromBall  = 1  (in range)
    // Height buckets (ball.z.whole, sprite+38+2 on ball):
    //   <= 4    → ballLessEqual4  = 1
    //   5..8    → ball4To8        = 1
    //   9..12   → ball8To12       = 1
    //   13..17  → ball12To17      = 1
    //   > 17    → ballAbove17     = 1
    private static void UpdatePlayerBallDistanceAndHeight(int teamBase, int spriteAddr)
    {
        // === Sprite.ballDistance compute (was missing) =====================
        // Source: external/swos-port/swos/swos.asm:100884-100898 in
        // UpdateControlledPlayer. The asm computes
        //   D1 = (player.x.whole - ball.x.whole)
        //   D2 = (player.y.whole - ball.y.whole)
        //   eax = D1*D1 + D2*D2          ; 32-bit signed dword
        //   [esi+Sprite.ballDistance] = eax
        // and runs it for every sprite in the team's spritesTable. In the
        // swos-port C++ this happens inside UpdateControlledPlayer (called
        // BEFORE updatePlayers from UpdateAndApplyTeamControls @ swos.asm:
        // 100708 / 100828). That port has landed in
        // InputControls.UpdateControlledPlayer, so this per-sprite compute
        // is now an idempotent duplicate — kept here because the
        // teamSwitchCounter dispatch in SelectTeamForUpdate only refreshes
        // ONE team per tick, while the bucket flags below need fresh
        // ballDistance for the team currently being processed.
        short pxSp = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short pySp = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        short bxSp = BallSprite.XPixels;
        short bySp = BallSprite.YPixels;
        int dxSp = (int)(short)(pxSp - bxSp);
        int dySp = (int)(short)(pySp - bySp);
        int sqrDist = dxSp * dxSp + dySp * dySp;
        Memory.WriteDword(spriteAddr + PlayerSprite.OffBallDistance, sqrDist);

        // updatePlayers.cpp:539-541 — snapshot prevPlVeryCloseToBall, clear flags.
        byte plVeryClose = Memory.ReadByte(teamBase + 61);
        Memory.WriteByte(teamBase + 69, plVeryClose);   // prevPlVeryCloseToBall
        Memory.WriteByte(teamBase + 61, 0);             // plVeryCloseToBall
        Memory.WriteByte(teamBase + 62, 0);             // plCloseToBall
        Memory.WriteByte(teamBase + 63, 0);             // plNotFarFromBall

        // updatePlayers.cpp:544-557 — distance bucket dispatch.
        //
        // Thresholds restored to the original asm values 32 / 72 / 2450 on
        // 2026-06-01 after PlayerControlled.RunControlledBranch was wired to
        // call PlayerActions.UpdateControllingPlayer at cseg_80E8F (port of
        // external/swos-port/src/game/player.cpp:135-191). With the ball now
        // glued ±1 px to the controlling outfielder each tick they own it
        // (via kBallPlOffsets at swos.asm:245818), the carrier's sq-distance
        // sits at 1-2 sq-px and clears the original 32-pvc gate without any
        // gate widening. Source:
        //   external/swos-port/src/game/updatePlayers/updatePlayers.cpp:544-557
        //   external/swos-port/src/game/player.cpp:135-191 (UpdateControllingPlayer)
        //   external/swos-port/swos/swos.asm:245818 (kBallPlOffsets ±1 px)
        int ballDistance = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffBallDistance);
        if ((uint)ballDistance <= 32u)
        {
            Memory.WriteByte(teamBase + 61, 1);   // plVeryCloseToBall
        }
        else if ((uint)ballDistance <= 72u)
        {
            Memory.WriteByte(teamBase + 62, 1);   // plCloseToBall
        }
        else if ((uint)ballDistance <= 2450u)
        {
            Memory.WriteByte(teamBase + 63, 1);   // plNotFarFromBall
        }
        // else: all three remain 0 (ball is far away).

        // updatePlayers.cpp:599-679 — height bucket dispatch.
        Memory.WriteByte(teamBase + 64, 0);   // ballLessEqual4
        Memory.WriteByte(teamBase + 65, 0);   // ball4To8
        Memory.WriteByte(teamBase + 66, 0);   // ball8To12
        Memory.WriteByte(teamBase + 67, 0);   // ball12To17
        Memory.WriteByte(teamBase + 68, 0);   // ballAbove17

        // updatePlayers.cpp:606-657 — read ball.z.whole at A2 + 40.
        short ballZ = BallSprite.ZPixels;
        if (ballZ > 17)
        {
            Memory.WriteByte(teamBase + 68, 1);   // ballAbove17
        }
        else if (ballZ > 12)
        {
            Memory.WriteByte(teamBase + 67, 1);   // ball12To17
        }
        else if (ballZ > 8)
        {
            Memory.WriteByte(teamBase + 66, 1);   // ball8To12
        }
        else if (ballZ > 4)
        {
            Memory.WriteByte(teamBase + 65, 1);   // ball4To8
        }
        else
        {
            Memory.WriteByte(teamBase + 64, 1);   // ballLessEqual4
        }
    }

    // updatePlayers.cpp:10367-10456 — loop epilogue.
    //
    // Trigger condition: gameStatePl == ST_GAME_IN_PROGRESS (100) OR
    //                    gameState == ST_KEEPER_HOLDS_BALL (3).
    //
    // If lastPlayerPlayed is a keeper AND prevLastPlayer was NOT the same
    // keeper, this is a keeper transition. Record it:
    //   - lastPlayerBeforeGoalkeeper = prevLastPlayer
    //   - lastTeamScored             = prevLastTeamPlayed (used for OG attribution)
    //   - nobodysBallTimer           = 50
    private static void TickEpilogueKeeperTransition(bool topTeam)
    {
        // updatePlayers.cpp:10368-10391 — gameStatePl gate.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameStatePl != kStGameInProgress && gameState != kStKeeperHoldsBall)
        {
            return;
        }

        int lastPlayer = Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed);
        int prevPlayer = Memory.ReadSignedDword(Memory.Addr.prevLastPlayer);
        int goalie1Addr = PlayerSprite.Base(PlayerSprite.SlotGoalie1);
        int goalie2Addr = PlayerSprite.Base(PlayerSprite.SlotGoalie2);

        // updatePlayers.cpp:10394-10448 — is lastPlayer a goalie?
        bool lastIsGoalie1 = (lastPlayer == goalie1Addr);
        bool lastIsGoalie2 = (lastPlayer == goalie2Addr);
        if (!lastIsGoalie1 && !lastIsGoalie2) return;

        // If the previous-tick last player was the SAME goalie, no transition.
        if (lastIsGoalie1 && prevPlayer == goalie1Addr) return;
        if (lastIsGoalie2 && prevPlayer == goalie2Addr) return;

        // updatePlayers.cpp:10450-10455 — record transition.
        Memory.WriteDword(Memory.Addr.lastPlayerBeforeGoalkeeper, prevPlayer);
        int prevTeam = Memory.ReadSignedDword(Memory.Addr.prevLastTeamPlayed);
        Memory.WriteDword(Memory.Addr.lastTeamScored, prevTeam);
        Memory.WriteWord(Memory.Addr.nobodysBallTimer, 50);
    }

    // Resolve the PlayerInfo base for THIS team — mirrors PlayerUpdate's
    // OwnPlayersBase helper but local to UpdatePlayers so the goalie's
    // updatePlayerShotChanceTable wire can resolve PlayerInfo without
    // crossing the file boundary. Returns 0 if the in-game team pointer
    // hasn't been wired yet.
    // Source: same lookup pattern as PlayerUpdate.OwnPlayersBase.
    private static int OwnGoaliePlayersBase(bool isTopTeam)
    {
        int teamBase = TeamData.Base(isTopTeam);
        return Memory.ReadSignedDword(teamBase + TeamData.OffInGameTeamPtr);
    }

    // Shared "header finished → back to PL_NORMAL" tail — the asm's
    // l_set_player_to_normal + l_stop_player pair (updatePlayers.cpp:6989-6994
    // / 6848-6851): playerState = PL_NORMAL, animation = standing table, and
    // destX/destY snapped to the sprite's current whole-pixel position so it
    // stops moving. Used by both the jump- and static-header exits.
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:6989-6994.
    private static void HeaderExitToNormal(int spriteAddr)
    {
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);   // PL_NORMAL
        PlayerActions.SetPlayerAnimationTable(
            spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
        short sx = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short sy = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, sx);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, sy);
    }

    // updatePlayers.cpp:6972-7430 — l_player_jump_heading, ported in full.
    // Per-tick flight of a jumping header: countdown → speed decay → the
    // pitch-bounds ladder (main band / goal-mouth posts / in-goal) → the
    // winded-up CONTACT test → strike commit that fires PlayerHittingJumpHeader.
    // Registers in the asm: A1 = header sprite, A2 = ball, A6 = header's team.
    // EVERY path in the original ends at l_update_player_speed_and_deltas, so
    // this method funnels to a single `Done:` tail that calls
    // UpdatePlayerSpeedAndFrameDelay — mirroring the asm's `goto` control flow.
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:6972-7430.
    private static void TickJumpHeader(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);
        int a1 = spriteAddr;

        // :6973-6987 — sub playerDownTimer,1; jz → set player to normal.
        sbyte down = (sbyte)Memory.ReadByte(a1 + PlayerSprite.OffPlayerDownTimer);
        down = (sbyte)(down - 1);
        Memory.WriteByte(a1 + PlayerSprite.OffPlayerDownTimer, (byte)down);
        if (down == 0)
        {
            // :6989-6994 — l_set_player_to_normal + l_stop_player.
            HeaderExitToNormal(a1);
            goto Done;
        }

        // :6996-7071 — l_still_jump_heading: per-tick SPEED DECAY. Runs only
        // when speed != 0; may `shr speed,1` and leave (skip contact this
        // tick) or drop through the air-constant path into the pitch ladder.
        short sp = Memory.ReadSignedWord(a1 + PlayerSprite.OffSpeed);
        if (sp != 0)
        {
            // :7006 — SetJumpHeaderHitAnimTable(A1). May flip the anim table.
            PlayerActions.SetJumpHeaderHitAnimTable(a1);
            // :7008-7057 — halve-threshold picked by CURRENT anim table:
            //   attempt table → 37, else 17.
            int animTable = Memory.ReadSignedDword(a1 + PlayerSprite.OffAnimTablePtr);
            sbyte halveThreshold =
                (animTable == Memory.Addr.kJumpHeaderAttemptAnimTableAddr) ? (sbyte)37 : (sbyte)17;
            if (down > halveThreshold)
            {
                // :7073-7093 — l_update_heading_speed: speed -= kPlayerAirConstant
                // (72, swos.asm:203807); if it goes negative clamp to 0 and
                // leave, else continue into the pitch-bounds ladder.
                sp -= 72;
                Memory.WriteWord(a1 + PlayerSprite.OffSpeed, sp);
                if (sp < 0)
                {
                    Memory.WriteWord(a1 + PlayerSprite.OffSpeed, 0);
                    goto Done;
                }
                // fall through to the pitch-bounds ladder
            }
            else
            {
                // :7034-7044 / :7061-7071 — shr speed,1 then leave.
                sp >>= 1;
                Memory.WriteWord(a1 + PlayerSprite.OffSpeed, sp);
                goto Done;
            }
        }
        // sp == 0: :7003 jumps straight to the pitch-bounds ladder.

        // :7095-7327 — l_check_if_jump_header_inside_pitch ladder. All reads
        // are the sprite's whole-pixel x (OffX+2) / y (OffY+2).
        {
            short x = Memory.ReadSignedWord(a1 + PlayerSprite.OffX + 2);
            if (x < 73) goto SlowDownAndLeave;      // :7097-7108 jl
            if (x > 598) goto SlowDownAndLeave;     // :7110-7121 jg
            short y = Memory.ReadSignedWord(a1 + PlayerSprite.OffY + 2);
            if (y < 132) goto HeadingCloseToGoalLines;  // :7123-7134 jl
            if (y <= 766) goto WindedUp;                // :7136-7147 jle
            // fall to HeadingCloseToGoalLines (y > 766)
        }

    HeadingCloseToGoalLines:   // :7149 — goal-mouth post band
        {
            short x = Memory.ReadSignedWord(a1 + PlayerSprite.OffX + 2);
            if (x < 296) goto Cseg8253D;            // :7151-7162 jl
            if (x > 375) goto Cseg8253D;            // :7164-7175 jg
            if (x < 305) goto HeadingFinished;      // :7177-7188 jl
            if (x <= 366) goto Cseg8253D;           // :7190-7201 jbe
            // fall to HeadingFinished (x in 367..375)
        }

    HeadingFinished:   // :7203 — l_heading_finished
        Memory.WriteWord(a1 + PlayerSprite.OffSpeed, 0);
        Memory.WriteByte(a1 + PlayerSprite.OffPlayerDownTimer, 0);
        HeaderExitToNormal(a1);   // → l_set_player_to_normal + l_stop_player
        goto Done;

    Cseg8253D:   // :7209
        {
            short y = Memory.ReadSignedWord(a1 + PlayerSprite.OffY + 2);
            if (y < 129) goto HeadingIntoTheGoal;   // :7211-7222 jl
            if (y <= 769) goto WindedUp;            // :7224-7235 jle
            // fall to HeadingIntoTheGoal (y > 769)
        }

    HeadingIntoTheGoal:   // :7237
        {
            short x = Memory.ReadSignedWord(a1 + PlayerSprite.OffX + 2);
            if (x < 290) goto CheckInsideGoal;      // :7239-7250 jl
            if (x <= 381) goto HeadingFinished;     // :7252-7263 jle
            // fall to CheckInsideGoal (x > 381)
        }

    CheckInsideGoal:   // :7265 — l_check_if_heading_inside_a_goal
        {
            short y = Memory.ReadSignedWord(a1 + PlayerSprite.OffY + 2);
            if (y < 121) goto SlowDownAndLeave;     // :7267-7278 jl
            if (y <= 777) goto WindedUp;            // :7280-7291 jle
            // fall to SlowDownAndLeave (y > 777)
        }

    SlowDownAndLeave:   // :7293 — l_slow_down_heading_player_and_leave
        {
            // D0 = speed; speed -= D0>>2; D0 >>= 1; speed -= D0  (== speed>>3).
            short s = Memory.ReadSignedWord(a1 + PlayerSprite.OffSpeed);
            short d0 = (short)(s >> 2);
            s = (short)(s - d0);
            d0 = (short)(d0 >> 1);
            s = (short)(s - d0);
            Memory.WriteWord(a1 + PlayerSprite.OffSpeed, s);
        }
        goto Done;

    WindedUp:   // :7329 — l_check_if_header_winded_up (all gates must hold)
        if (Memory.ReadByte(a1 + PlayerSprite.OffPlayerDownTimer) < 42)  // :7331-7342 jb
            goto Done;
        if (Memory.ReadSignedWord(Memory.Addr.gameStatePl) != kStGameInProgress)  // :7344-7355
            goto Done;
        if (Memory.ReadSignedWord(a1 + 98 /*heading*/) != 0)             // :7357-7363
            goto Done;
        if ((uint)Memory.ReadSignedDword(a1 + PlayerSprite.OffBallDistance) > 64u)  // :7365-7376 ja
            goto Done;
        {
            short bz = BallSprite.ZPixels;                                // :7378-7403 z ∈ [8,15]
            if (bz < 8) goto Done;
            if (bz > 15) goto Done;
        }
        // :7405-7430 — strike commit.
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);         // :7408-7409
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, a1);             // :7410-7411
        Memory.WriteWord(Memory.Addr.penalty, 0);                        // :7412
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);                  // :7413
        PlayerActions.PlayerHittingJumpHeader(teamBase, a1);             // :7414-7422
        Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 25);      // :7423-7424
        {
            int opp = Memory.ReadSignedDword(teamBase + TeamData.OffOpponentsTeam);  // :7425-7427
            Memory.WriteWord(opp + TeamData.OffPassingToPlayer, 0);      // :7428
            Memory.WriteDword(opp + TeamData.OffPassingKickingPlayer, 0);// :7429
        }
        goto Done;

    Done:   // l_update_player_speed_and_deltas
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, a1);
    }

    // updatePlayers.cpp:6831-6970 — l_player_doing_static_header, ported in
    // full. Countdown → speed decay → SetStaticHeaderDirection → the winded-up
    // CONTACT gates → strike commit that fires PlayerHittingStaticHeader.
    // Registers: A1 = header sprite, A2 = ball, A6 = header's team. Like the
    // jump variant, every path lands on l_update_player_speed_and_deltas.
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:6831-6970.
    private static void TickStaticHeader(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);
        int a1 = spriteAddr;

        // :6833-6846 — sub playerDownTimer,1; jz → set player to normal.
        sbyte down = (sbyte)Memory.ReadByte(a1 + PlayerSprite.OffPlayerDownTimer);
        down = (sbyte)(down - 1);
        Memory.WriteByte(a1 + PlayerSprite.OffPlayerDownTimer, (byte)down);
        if (down == 0)
        {
            // :6848-6851 — l_set_player_to_normal + l_stop_player.
            HeaderExitToNormal(a1);
            goto Done;
        }

        // :6853-6870 — l_player_down_with_static_header: sub speed,16; jns
        // skip; else clamp to 0.
        short sp = Memory.ReadSignedWord(a1 + PlayerSprite.OffSpeed);
        sp -= 16;
        Memory.WriteWord(a1 + PlayerSprite.OffSpeed, sp);
        if (sp < 0) Memory.WriteWord(a1 + PlayerSprite.OffSpeed, 0);

        // :6873 — SetStaticHeaderDirection runs UNCONDITIONALLY each wind-up
        // tick (fine-rotates toward team.currentAllowedDirection).
        PlayerHeader.SetStaticHeaderDirection(a1, teamBase);

        // :6874-6943 — winded-up CONTACT gates. ALL must hold to strike.
        if (Memory.ReadSignedWord(Memory.Addr.gameStatePl) != kStGameInProgress)  // :6874-6885
            goto Done;
        if (Memory.ReadSignedWord(a1 + 98 /*heading*/) != 0)             // :6887-6894
            goto Done;
        if ((uint)Memory.ReadSignedDword(a1 + PlayerSprite.OffBallDistance) > 64u)  // :6896-6907 ja
            goto Done;
        {
            short bz = BallSprite.ZPixels;                                // :6909-6934 z ∈ [8,15]
            if (bz < 8) goto Done;
            if (bz > 15) goto Done;
        }
        // :6936-6943 — team.currentAllowedDirection (A6+44) must be >= 0.
        if (Memory.ReadSignedWord(teamBase + TeamData.OffCurrentAllowedDirection) < 0)
            goto Done;

        // :6945-6969 — strike commit.
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);         // :6948-6949
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, a1);             // :6950-6951
        Memory.WriteWord(Memory.Addr.penalty, 0);                        // :6952
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);                  // :6953
        PlayerActions.PlayerHittingStaticHeader(teamBase, a1);           // :6954-6962
        Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 25);      // :6963-6964
        {
            int opp = Memory.ReadSignedDword(teamBase + TeamData.OffOpponentsTeam);  // :6965-6967
            Memory.WriteWord(opp + TeamData.OffPassingToPlayer, 0);      // :6968
            Memory.WriteDword(opp + TeamData.OffPassingKickingPlayer, 0);// :6969
        }

    Done:   // l_update_player_speed_and_deltas
        PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, a1);
    }

    // Dispatch table-equivalent for the per-state switch in
    // updatePlayers.cpp:680-836. We've already handled TACKLED + ROLLING
    // before getting here, so they don't appear in this switch.
    private static void DispatchByPlayerState(byte state, int spriteAddr, int slotInTeam, bool topTeam)
    {
        // updatePlayers.cpp:847-849 — for the GOALKEEPER (playerOrdinal == 1),
        // the dispatch goes straight to l_player_goalkeeper REGARDLESS of
        // playerState. Detect goalie by ordinal and route accordingly.
        short ordinal = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
        if (ordinal == 1)
        {
            TickGoalkeeper(spriteAddr, topTeam, slotInTeam);
            return;
        }

        // For outfielders, route by state. The Sprite.h enum gives:
        //   kNormal=0, kTackling=1, kTackled=3, kGoalieCatchingBall=4,
        //   kThrowIn=5, kGoalieDivingHigh=6, kGoalieDivingLow=7,
        //   kStaticHeader=8, kJumpHeader=9, kDown=10, kGoalieClaimed=11,
        //   kBooked=12, kInjured=13, kSad=14, kHappy=15
        //
        // Outfielders never enter goalie-only states (4/6/7/11), so we
        // route those defensively to the stub anyway.
        //
        // updatePlayers.cpp:851-878 — the default branch (no state match)
        // hits debugBreak() / endless_loop. We log instead of crashing.
        switch ((PortPlayerState)state)
        {
            case PortPlayerState.kNormal:
                // Outfielder NORMAL: dispatch matches updatePlayers.cpp:851-876:
                //   if A1 == team.controlledPlayer → l_its_controlled_player
                //   if A1 == team.passToPlayerPtr  → l_player_expecting_pass
                //   else                            → l_not_controlled_player
                int controlled = TeamData.ControlledPlayer(topTeam);
                int passTo     = Memory.ReadSignedDword(TeamData.Base(topTeam) + TeamData.OffPassToPlayerPtr);

                if (spriteAddr == controlled)
                {
                    TickHumanControlled(spriteAddr, topTeam);
                }
                else if (spriteAddr == passTo && passTo != 0)
                {
                    // updatePlayers.cpp:7604 (l_player_expecting_pass) — ported
                    // in PlayerControlled.RunPassExpectingBranch. Falls through
                    // to update-player-speed-and-deltas like the other dispatches.
                    //
                    // STOPPAGE ROUTE (2026-07-02, USER-VISIBLE BUG FIX): during
                    // a break (gameStatePl != 100) route to the faithful
                    // cseg_82AAF..cseg_82EC2 body in TickPassExpectingStopped.
                    // PlayerControlled's stopped slice commits l_pass_success
                    // (controlledPlayer = sprite + UpdatePlayerWithBall
                    // TELEPORT onto the ball) WITHOUT the asm's
                    // `ballDistance == 0` gate (updatePlayers.cpp:7964-7972)
                    // and without the "taker already at the spot → hold this
                    // sprite in place" branch (updatePlayers.cpp:8322-8387).
                    // That inversion produced the reported set-piece chaos:
                    // every 25 ticks a fresh sprite was elected passToPlayerPtr,
                    // instantly teleported onto the ball and promoted, while
                    // the demoted taker walked back to his break-table spot —
                    // the "second centre-forward runs into the kicker and back"
                    // (kickoff) and "taker teleports on/off the ball" (goal
                    // kick) oscillations.
                    short gsPlPass = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
                    if (gsPlPass != kStGameInProgress)
                    {
                        TickPassExpectingStopped(spriteAddr, topTeam);
                    }
                    else
                    {
                        PlayerControlled.RunPassExpectingBranch(spriteAddr, topTeam);
                        PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                    }
                }
                else
                {
                    TickAiControlled(spriteAddr, topTeam, slotInTeam);
                }
                break;

            case PortPlayerState.kTackling:
                // updatePlayers.cpp:6375 — l_player_tackling.
                // Full per-tick body: foul test, tacklingTimer + playerDownTimer
                // countdowns, ground-friction speed decay, ball-strike check, and
                // transition back to PL_NORMAL when playerDownTimer hits 0.
                // from updatePlayers.cpp:6375.
                TickTacklingPlayer(spriteAddr, topTeam);
                break;

            case PortPlayerState.kJumpHeader:
                // updatePlayers.cpp:6972 — l_player_jump_heading.
                // FULL per-tick body now ported (2026-07-11): timer decay +
                // speed decay + pitch-bounds ladder + winded-up CONTACT test +
                // strike commit. Previously only the timer/speed-decay prefix
                // ran and the contact dispatch (:7095-7430) was deferred, so a
                // jump header could NEVER connect ("header whiffs even when
                // perfectly positioned"). See TickJumpHeader below.
                // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:6972-7430.
                TickJumpHeader(spriteAddr, topTeam);
                break;

            case PortPlayerState.kStaticHeader:
                // updatePlayers.cpp:6831 — l_player_doing_static_header.
                // FULL per-tick body now ported (2026-07-11): timer decay +
                // speed decay + SetStaticHeaderDirection + the winded-up
                // proximity / height / heading / allowed-direction CONTACT
                // gates + strike commit. Previously PlayerHittingStaticHeader
                // fired UNCONDITIONALLY every wind-up tick (on a false "the
                // function gates internally" assumption), which remote-kicked
                // the ball from anywhere, every tick. See TickStaticHeader.
                // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:6831-6970.
                TickStaticHeader(spriteAddr, topTeam);
                break;

            case PortPlayerState.kThrowIn:
                // updatePlayers.cpp:5780 — l_player_taking_throw_in.
                // Throw-in animation + release handler. Mirrors the asm
                // register convention: A1 = throwerSprite, A2 = ballSprite,
                // A6 = thrower's team. Ported to SetPieces.TickThrowIn.
                // The handler returns to "l_update_player_speed_and_deltas"
                // tail in the original — we mirror that fall-through here.
                // from updatePlayers.cpp:5780.
                SetPieces.TickThrowIn(spriteAddr, BallSprite.Base, TeamData.Base(topTeam));
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                break;

            case PortPlayerState.kGoalieDivingHigh:
            case PortPlayerState.kGoalieDivingLow:
                // updatePlayers.cpp:3188 — l_goalie_diving. Dive completion
                // handled inside TickGoalkeeper (above) since the asm jumps
                // to l_player_goalkeeper for ordinal==1. Outfielders cannot
                // enter these states; safe no-op.
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                break;

            case PortPlayerState.kGoalieCatchingBall:
                // updatePlayers.cpp:3059 — l_goalie_catching_the_ball.
                PlayerUpdate.TickGoalieCatchingBall(spriteAddr, topTeam);
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                break;

            case PortPlayerState.kGoalieClaimed:
                // updatePlayers.cpp:3163 — l_goalie_claimed.
                PlayerUpdate.TickGoalieClaimed(spriteAddr);
                PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                break;

            case PortPlayerState.kDown:
                // updatePlayers.cpp:3037-3057 — l_player_down_st_10.
                // Decrement playerDownTimer (byte). If it hits 0, transition
                // back to PL_NORMAL and reset the animation table to the
                // standing/normal table; otherwise fall through to speed update.
                // Mechanical port of the asm at L3037; see also the equivalent
                // pattern in PlayerUpdate.TickGoalieClaimed for the keeper side.
                // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:3037.
                {
                    sbyte downTimer = (sbyte)Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerDownTimer);
                    downTimer = (sbyte)(downTimer - 1);
                    Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, (byte)downTimer);
                    if (downTimer == 0)
                    {
                        // 3054 — Sprite.playerState = PL_NORMAL.
                        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
                        // 3055-3056 — SetPlayerAnimationTable(playerNormalStandingAnimTable).
                        PlayerActions.SetPlayerAnimationTable(
                            spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
                    }
                    PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                }
                break;

            case PortPlayerState.kBooked:
                // updatePlayers.cpp:2992-3006 — l_player_booked.
                // `if (whichCard != 0) goto update_player_speed_and_deltas;`
                // — i.e. while a card animation is still active, the booked
                // player just stands. Once whichCard returns to 0 the player
                // transitions back to PL_NORMAL via l_go_back_to_normal_state.
                // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:2992.
                {
                    short whichCard = Memory.ReadSignedWord(Memory.Addr.whichCard);
                    if (whichCard == 0)
                    {
                        // 3001-3005 — l_go_back_to_normal_state.
                        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
                        PlayerActions.SetPlayerAnimationTable(
                            spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
                    }
                    PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                }
                break;

            case PortPlayerState.kSad:
            case PortPlayerState.kHappy:
                // updatePlayers.cpp:3008-3035 — l_player_sad_or_happy.
                // Mirrors the asm: if gameState in {ST_GOING_TO_HALFTIME (23),
                // ST_PLAYERS_GOING_TO_SHOWER (24)} → l_go_back_to_normal_state
                // (clear state, swap to standing animation). Else fall through
                // to speed update with no state change. The handler is reached
                // when the player has finished celebrating / sulking after a
                // goal and needs to walk off to the bench.
                // from external/swos-port/src/game/updatePlayers/updatePlayers.cpp:3008.
                //
                // OpenSWOS extension: UpdateGoals.GoalScored now also writes
                // PL_HAPPY / PL_SAD during the post-goal hold (gameState=0).
                // The original SWOS reserves these states for end-of-match
                // only, but our port repurposes them so the 11-vs-11 reaction
                // is visible during the celebration dwell. We therefore add
                // ST_GAME_IN_PROGRESS (100) as a third clear-trigger — when
                // UpdateGoals.UpdatePostGoalRestart bumps gameState back to
                // 100, the next DispatchByPlayerState pass clears the state
                // and restores the standing animation. See UpdateGoals.cs:97
                // (GoalScored celebration block) and UpdateGoals.cs:238
                // (restart sets gameState back to 100).
                {
                    // 2026-07-02 (OpenSWOS extension, matches the ladder): also
                    // clear when the breakCameraMode ladder has armed the
                    // walk-back by stamping destReachedState = 1 (kStarting,
                    // Sprite.h:39). A sprite left in PL_HAPPY/PL_SAD would
                    // otherwise never re-enter kNormal, never walk, and never
                    // report kReached — stalling the post-goal FSM. Real SWOS
                    // never has celebrating sprites during the walk (PL_HAPPY/
                    // PL_SAD are end-of-match only); this trigger just unwinds
                    // our port's post-goal celebration repurpose in time.
                    short sadDrs = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestReachedState);
                    short gsSad = Memory.ReadSignedWord(Memory.Addr.gameState);
                    if (gsSad == 23 /* ST_GOING_TO_HALFTIME */ ||
                        gsSad == 24 /* ST_PLAYERS_GOING_TO_SHOWER */ ||
                        gsSad == kStGameInProgress /* OpenSWOS: post-goal exit */ ||
                        sadDrs == 1 /* OpenSWOS: bcm ladder armed the walk-back */)
                    {
                        // 3001-3005 — l_go_back_to_normal_state.
                        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
                        PlayerActions.SetPlayerAnimationTable(
                            spriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
                    }
                    PlayerActions.UpdatePlayerSpeedAndFrameDelay(TeamData.Base(topTeam), spriteAddr);
                }
                break;

            default:
                // updatePlayers.cpp:878 has `debugBreak()` here. In our port
                // we silently no-op so unported state values don't crash.
                break;
        }
    }
}

// ===================================================================
// PortPlayerState enum — mirrors swos-port src/sprites/Sprite.h:16-34.
// ===================================================================
//
// Named PortPlayerState (not PlayerState) to avoid clashing with the
// existing OpenSwos.Sim.PlayerState struct that owns the gameplay-side
// state machine. This enum is purely for byte-level comparisons against
// the SwosVM Memory backing (matches the C++ PlayerState enum values).
//
// Source of truth: external/swos-port/src/sprites/Sprite.h:16-34.
public enum PortPlayerState : byte
{
    kNormal             = 0,
    kTackling           = 1,
    // 2 is unused in the enum
    kTackled            = 3,
    kGoalieCatchingBall = 4,
    kThrowIn            = 5,
    kGoalieDivingHigh   = 6,
    kGoalieDivingLow    = 7,
    kStaticHeader       = 8,
    kJumpHeader         = 9,
    kDown               = 10,
    kGoalieClaimed      = 11,
    kBooked             = 12,
    kInjured            = 13,  // PL_ROLLING_INJURED in updatePlayers.cpp:447 asm comment
    kSad                = 14,
    kHappy              = 15,
    kUnknown            = 255,
}
