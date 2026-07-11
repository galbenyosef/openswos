namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Match-start / kickoff setup. Mechanical 1:1 port of:
//
//   - game.cpp:1381-1385     — determineStartingTeamAndTeamPlayingUp()
//   - game.cpp:551-705       — initPlayersBeforeEnteringPitch()
//   - game.cpp:396-421       — initTeamsData() scalar reset block (called by
//                              startingMatch; per-team pointer reseat at
//                              game.cpp:422-660 is wired at boot by
//                              TeamDataLoader — see InitTeamsData() note)
//   - game.cpp:1450-1475     — startingMatch()
//   - gameLoop.cpp:2113-2157 — prepareForInitialKick()
//   - swos.asm:245853-245856 — kTeamsStartingCoordinates (22 entry-side pairs)
//
// (All paths relative to external/swos-port/src/game/ unless noted.)
//
// Faithful state flow (no teleport short-circuit any more):
//   startingMatch() parks the ball OFF-pitch at (1672, 449), drops all 22
//   sprites at the pitch-side entry coordinates (x ≈ 691..907, i.e. right of
//   the 672-px-wide pitch), and sets gameState = 21 (kStartingGame) with
//   stoppageEventTimer = 100. The GameLoop break ladder then counts that
//   timer down and eventually calls prepareForInitialKick(), which puts the
//   ball on the centre spot (336, 449) and flips gameState = 0
//   (kPlayersGoingToInitialPositions). Under gameState 21/0 the per-player
//   AI in updatePlayers WALKS everyone to their formation spots (reading
//   top/bottomStartingPositions — swos.asm:117098-117263); the referee
//   whistle + kickoff pass follow once everyone has arrived. Post-goal
//   restarts do NOT come through prepareForInitialKick at all — ball.cpp's
//   goal handler writes gameState = 0 and lastTeamPlayedBeforeBreak (the
//   conceding team) directly; prepareForInitialKick is only for match start,
//   half/extra-time start, and the 825-tick CPU-timeout safety net
//   (gameLoop.cpp:519-572).
public static class Kickoff
{
    // gameLoop.cpp:2115-2116 — centre-spot coordinates used for the ball at
    // every initial kick.
    //
    // NOTE on (336, 449) vs image centre (336, 424):
    // SWOS pitch image is 672×848 but the PLAYING FIELD sits asymmetrically
    // inside it. Goal lines are at y=129 (top) and y=769 (bottom) — confirmed
    // by swos.asm comments at lines 110201, 111250, 112561 ("top goal line")
    // and 111263, 117804 ("lower/bottom goal line"). Playing-field centre =
    // (129+769)/2 = 449. swos.asm:98046 explicitly comments y=449 as
    // "center line" and swos.asm:102766 as "half of pitch by y".
    public const int CenterX = 336;
    public const int CenterY = 449;

    // ---- kTeamsStartingCoordinates ---------------------------------------
    // Source: external/swos-port/swos/swos.asm:245853-245856 (referenced from
    // initPlayersBeforeEnteringPitch @ game.cpp:570, `mov A1, offset
    // kTeamsStartingCoordinates` = 524064).
    //
    // 22 {x, y} word pairs consumed SEQUENTIALLY: the first 11 pairs go to
    // the team playing up (teamPlayingUp), the next 11 to the other team.
    // initPlayersBeforeEnteringPitch turns each pair into an entry-side
    // standing spot: x = pairX + 591 + (rand() & 7), y = pairY + 449 — i.e.
    // just off the RIGHT edge of the 672-px-wide pitch, staggered in a
    // diagonal line. Players then walk on from there during gameState 21/0.
    private static readonly (short X, short Y)[] KTeamsStartingCoordinates = new (short, short)[]
    {
        // swos.asm:245853 — first team (the one playing up)
        (300,  69), (280,  46), (260,  34), (240,  24), (220,  16), (200,   9),
        (180,   3), (160,  -2),
        // swos.asm:245855-245856 — tail of first team + all of second team
        (140,  -8), (120,  -9), (100, -11),
        (300, -65), (280, -42), (260, -30), (240, -20), (220, -12), (200,  -5),
        (180,   1), (160,   6), (140,  12), (120,  13), (100,  15),
    };

    // ---- SWOS on-pitch formation coordinate tables ------------------------
    // KEPT for the gameState=0 walk-to-positions AI (updatePlayers
    // @@top_break / @@bottom_break — swos.asm:117098-117263), which reads the
    // KICKING team's table for every player's destX/destY and applies the
    // per-team transform:
    //   - Top-team player (defends north):    destX = tabX + 86,  destY = tabY + 129
    //   - Bottom-team player (defends south): destX = 590 - tabX, destY = 769 - tabY
    // That AI is ported in UpdatePlayers.cs; the tables live here (exposed
    // `internal`) so both modules share one copy. Kickoff itself no longer
    // consumes them — the old teleport-to-formation shortcut is gone.
    //
    // Source: external/swos-port/swos/swos.asm:246063-246068
    //   bottomStartingPositions dw 250, 1, 100, 155, 200, 133, 300, 133, 400, 155,
    //                              60, 320, 190, 244, 310, 244, 355, 320,
    //                              220, 320, 260, 316
    //   topStartingPositions    dw 250, 11, 120, 133, 210, 133, 290, 133, 380, 133,
    //                              90, 244, 220, 200, 280, 200, 410, 244,
    //                              200, 277, 300, 277
    internal static readonly (short X, short Y)[] BottomStartingPositions = new (short, short)[]
    {
        (250,   1),   // 0: goalkeeper
        (100, 155),   // 1: right back
        (200, 133),   // 2: right central defender
        (300, 133),   // 3: left central defender
        (400, 155),   // 4: left back
        ( 60, 320),   // 5: right wing
        (190, 244),   // 6: right midfielder
        (310, 244),   // 7: left midfielder
        (355, 320),   // 8: left wing
        (220, 320),   // 9: right forward
        (260, 316),   // 10: left forward (centre)
    };

    internal static readonly (short X, short Y)[] TopStartingPositions = new (short, short)[]
    {
        (250,  11),   // 0: goalkeeper
        (120, 133),   // 1: right back
        (210, 133),   // 2: right central defender
        (290, 133),   // 3: left central defender
        (380, 133),   // 4: left back
        ( 90, 244),   // 5: right wing
        (220, 200),   // 6: right midfielder
        (280, 200),   // 7: left midfielder
        (410, 244),   // 8: left wing
        (200, 277),   // 9: right forward
        (300, 277),   // 10: left forward (centre)
    };

    // ====================================================================
    // determineStartingTeamAndTeamPlayingUp — game.cpp:1381-1385
    // ====================================================================
    // Rolls which team defends the top goal (teamPlayingUp) and which team
    // takes the first kickoff (teamStarting). Both are 1-based (1 = team 1,
    // 2 = team 2). Called once per match from initMatch (game.cpp:82).
    public static void DetermineStartingTeamAndTeamPlayingUp()
    {
        // game.cpp:1383 — swos.teamPlayingUp = (SWOS::rand() & 1) + 1;
        // teamPlayingUp @ g_memByte[523146].
        Memory.WriteWord(Memory.Addr.teamPlayingUp, (Rng.NextByte() & 1) + 1);

        // game.cpp:1384 — swos.teamStarting = (SWOS::rand() & 1) + 1;
        // teamStarting @ g_memByte[523144].
        Memory.WriteWord(Memory.Addr.teamStarting, (Rng.NextByte() & 1) + 1);
    }

    // ====================================================================
    // initPlayersBeforeEnteringPitch — game.cpp:551-705
    // ====================================================================
    // Stands all 22 sprites at the pitch-side entry line (right of the
    // playing field), dest == position (players spawn already standing at
    // their entry spot; the WALK to formation happens later, under
    // gameState=0, driven by updatePlayers). The team playing up consumes
    // the first 11 kTeamsStartingCoordinates pairs, the other team the
    // remaining 11.
    public static void InitPlayersBeforeEnteringPitch()
    {
        // game.cpp:553 — call RemoveReferee.
        Referee.RemoveReferee();

        // game.cpp:554-567 — A0 = team1SpritesTable (330096); if
        // teamPlayingUp != 1 → A0 = team2SpritesTable (330140). The team
        // playing up is processed FIRST.
        short teamPlayingUp = Memory.ReadSignedWord(Memory.Addr.teamPlayingUp);
        int tableBase = teamPlayingUp == 1
            ? PlayerSprite.Team1TableBase       // team1SpritesTable @ 330096
            : PlayerSprite.Team2TableBase;      // team2SpritesTable @ 330140

        // game.cpp:570 — A1 = kTeamsStartingCoordinates (524064). Cursor
        // advances across BOTH team passes without resetting.
        int coordIndex = 0;

        // game.cpp:571 + 700-704 — mov word ptr D7, 1 / dec / jns → 2 passes.
        for (int teamPass = 0; teamPass < 2; teamPass++)
        {
            // game.cpp:574 + 678-682 — mov word ptr D1, 10 / dec / jns → 11 players.
            for (int i = 0; i < PlayerSprite.TeamSize; i++)
            {
                // game.cpp:577-585 — sprite = *(dword*)A0; A0 += 4.
                int spriteAddr = Memory.ReadSignedDword(tableBase + i * 4);

                // game.cpp:586-593 — ax = *(word*)A1; A1 += 2 (x), then again (y).
                (short coordX, short coordY) = KTeamsStartingCoordinates[coordIndex++];

                // Defensive only (NOT in the original — the asm never has a
                // null entry): skip unwired table slots instead of writing
                // into low Memory. The coord cursor above still advanced,
                // matching the asm's unconditional A1 walk.
                if (spriteAddr == 0)
                    continue;

                // game.cpp:595-603 — Sprite.x+2 (pixel word) = coordX; += 591.
                short xPix = (short)(coordX + 591);
                Memory.WriteWord(spriteAddr + PlayerSprite.OffX + 2, xPix);

                // game.cpp:613-624 — Sprite.y+2 (pixel word) = coordY; += 449.
                short yPix = (short)(coordY + 449);
                Memory.WriteWord(spriteAddr + PlayerSprite.OffY + 2, yPix);

                // game.cpp:625-639 — call Rand; and word ptr D0, 7;
                // Sprite.x+2 += D0. (One rand consumed per player, AFTER the
                // y write — keep this order for RNG-stream parity.)
                xPix = (short)(xPix + (Rng.NextByte() & 7));
                Memory.WriteWord(spriteAddr + PlayerSprite.OffX + 2, xPix);

                // game.cpp:640-643 — destX = x pixels, destY = y pixels.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, xPix);
                Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, yPix);

                // game.cpp:644 — Sprite.z+2 (pixel word) = 0.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffZ + 2, 0);
                // game.cpp:645 — Sprite.speed = 0.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, 0);
                // game.cpp:646 — Sprite.playerState = PL_NORMAL (0).
                Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, 0);
                // game.cpp:647 — Sprite.playerDownTimer = 0.
                Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, 0);
                // game.cpp:648 — Sprite.frameIndex = -1.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffFrameIndex, -1);
                // game.cpp:649 — Sprite.cycleFramesTimer = 1.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffCycleFramesTimer, 1);
                // game.cpp:650 — Sprite.imageIndex = -1.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffImageIndex, -1);
                // game.cpp:651 — Sprite.direction = 0.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffDirection, 0);
                // game.cpp:652 — Sprite.onScreen = 1.
                Memory.WriteWord(spriteAddr + PlayerSprite.OffOnScreen, 1);

                // game.cpp:653-667 — only when gameState == ST_STARTING_GAME
                // (21, i.e. true match start, not a halftime re-entry):
                // clear sentAway / cards / injuryLevel.
                if (Memory.ReadSignedWord(Memory.Addr.gameState) == 21)
                {
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffSentAway, 0);     // :665
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffCards, 0);        // :666
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffInjuryLevel, 0);  // :667
                }

                // game.cpp:670-677 — SetPlayerAnimationTable(sprite,
                // playerNormalStandingAnimTable @ 453234).
                PlayerActions.SetPlayerAnimationTable(
                    spriteAddr, Memory.Addr.playerNormalStandingAnimTable);
            }

            // game.cpp:684-697 — second pass takes the OTHER team's table:
            // A0 = team1SpritesTable; if teamPlayingUp != 2 → team2SpritesTable.
            tableBase = teamPlayingUp == 2
                ? PlayerSprite.Team1TableBase
                : PlayerSprite.Team2TableBase;
        }
    }

    // ====================================================================
    // startingMatch — game.cpp:1450-1475
    // ====================================================================
    // Match-start bookend: half 1, ball parked OFF pitch at (1672, 449),
    // teams data re-init, then gameState = 21 (kStartingGame) with a
    // 100-tick delay before the GameLoop ladder runs prepareForInitialKick.
    public static void StartingMatch()
    {
        // game.cpp:1452-1453 — constants.
        const int kStartingBallX = 1672;
        const int kStartingBallY = 449;
        const int kInitialDelayBeforeKickOff = 100;

        // game.cpp:1455 — swos.halfNumber = 1 (@ g_memByte[523142]).
        Memory.WriteWord(Memory.Addr.halfNumber, 1);
        // game.cpp:1456 — swos.hideBall = 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);
        // game.cpp:1457 — setBallPosition(1672, 449) — off-pitch right.
        BallUpdate.SetBallPosition(kStartingBallX, kStartingBallY);

        // game.cpp:1459 — initTeamsData(). Original comment: "careful, this
        // function resets some stuff, so keep it up here for now" — it slams
        // gameState/gameStatePl to 100 and zeroes the stoppage vars, which
        // the writes below then override. Order preserved.
        InitTeamsData();

        // game.cpp:1461 — swos.stoppageEventTimer = 100 (@ g_memByte[523122]).
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, kInitialDelayBeforeKickOff);
        // game.cpp:1462 — swos.gameState = GameState::kStartingGame (21).
        Memory.WriteWord(Memory.Addr.gameState, 21);
        // game.cpp:1463 — swos.gameStatePl = GameState::kStopped (101).
        Memory.WriteWord(Memory.Addr.gameStatePl, 101);
        // game.cpp:1464 — swos.lastTeamPlayedBeforeBreak = &swos.topTeamData
        // (522792 in the original address space).
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // game.cpp:1465-1466 — stoppage timers = 0.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);

        // game.cpp:1468 — swos.breakCameraMode = -1 (@ g_memByte[523120]).
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // game.cpp:1469 — swos.cameraDirection = -1 (@ g_memByte[523128]).
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // game.cpp:1470-1471 — camera velocities = 0.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);

        // game.cpp:1473 — stopAllPlayers().
        TeamPort.StopAllPlayers();
        // game.cpp:1474 — initPlayersBeforeEnteringPitch().
        InitPlayersBeforeEnteringPitch();

        // initMatch — game.cpp:95-96: `if (!swos.g_trainingGame)
        // swos.showFansCounter = 100;` (showFansCounter @ g_memByte[521018]).
        // Training games aren't modelled in OpenSwos, so the guard collapses
        // to an unconditional write; hoisted here (its caller-side position
        // in initMatch is immediately after startingMatch()) so every
        // match-start path gets the fans ramp.
        Memory.WriteWord(Memory.Addr.showFansCounter, 100);
    }

    // ====================================================================
    // initTeamsData scalar block — game.cpp:396-421
    // ====================================================================
    // Called from startingMatch (game.cpp:1459). Ported here as a private
    // helper because GameTime.InitTeamsDataForExtraTime — the other port of
    // the same source block — is private to GameTime.cs (same citations;
    // consolidate when either file next changes hands).
    //
    // The remainder of initTeamsData (game.cpp:422-660) reseats each
    // TeamGeneralInfo's pointers (opponentsTeam / inGameTeamPtr /
    // spritesTable / tactics / per-team scalar fields, with the top/bottom
    // xchg when teamPlayingUp != 1). In OpenSwos those pointers are wired
    // once at match boot by TeamDataLoader.SeedTeamData; TeamPort.
    // StopAllPlayers + the writes below cover the per-restart scalar subset.
    // The teamPlayingUp-dependent SWAP of which TeamGeneralInfo maps to
    // which end is NOT yet modelled — see "suspicious" note in the port
    // report.
    private static void InitTeamsData()
    {
        Memory.WriteDword(Memory.Addr.currentScorer, 0);                    // game.cpp:398
        Memory.WriteDword(Memory.Addr.lastPlayerBeforeGoalkeeper, 0);       // game.cpp:399
        Memory.WriteWord(Memory.Addr.goalScored, 0);                        // game.cpp:400
        Memory.WriteWord(Memory.Addr.runSlower, 0);                         // game.cpp:401
        Memory.WriteWord(Memory.Addr.whichCard, 0);                         // game.cpp:402
        Memory.WriteDword(Memory.Addr.bookedPlayer, 0);                     // game.cpp:403
        Memory.WriteWord(Memory.Addr.playerHadBall, 0);                     // game.cpp:404
        Memory.WriteDword(Memory.Addr.lastKeeperPlayed, 0);                 // game.cpp:405
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, 0);                   // game.cpp:406
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, 0);                 // game.cpp:407
        Memory.WriteWord(Memory.Addr.penalty, 0);                           // game.cpp:408
        Memory.WriteWord(Memory.Addr.goalCameraMode, 0);                    // game.cpp:409
        Memory.WriteWord(Memory.Addr.goalOut, 0);                           // game.cpp:410
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0); // game.cpp:411
        Memory.WriteWord(Memory.Addr.fireBlocked, 0);                       // game.cpp:412
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, 0);        // game.cpp:413
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);                // game.cpp:414
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);               // game.cpp:415
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 0);                // game.cpp:416
        Memory.WriteWord(Memory.Addr.inGameCounter, 0);                     // game.cpp:417
        Memory.WriteWord(Memory.Addr.gameStatePl, 100);                     // game.cpp:418
        Memory.WriteWord(Memory.Addr.gameState, 100);                       // game.cpp:419 ST_GAME_IN_PROGRESS
        Memory.WriteWord(Memory.Addr.breakState, 0);                        // game.cpp:420
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);                  // game.cpp:421
    }

    // ====================================================================
    // initTeamsData per-team reseat / end-swap — game.cpp:422-548
    // ====================================================================
    // task #173 BUG2 / task #148 — "teams don't switch ends at half-time".
    //
    // At half-time the original flips teamPlayingUp/teamStarting
    // (gameLoop.cpp:2015-2043) and re-runs initTeamsData(). The second half of
    // initTeamsData (game.cpp:422-548) walks BOTH TeamGeneralInfo structs and,
    // when teamPlayingUp != 1, does the top/bottom `xchg` (game.cpp:449-455 /
    // 535-541) that reassigns which struct owns which team's sprites, tactics,
    // inGameTeamPtr, teamStatsPtr, teamNumber and player/coach numbers. Every
    // downstream system keys off those TeamGeneralInfo fields:
    //   - the per-team update / break-position walk iterates team.spritesTable
    //     (TeamData.PlayersTable -> OffPlayers) and mirrors by IsTopTeam
    //     (PlayerHeader.SetPlayerWithNoBallDestination),
    //   - goal crediting reads top/bottomTeamData.teamNumber
    //     (ball.cpp:3087-3168 / BallOutOfPlay.cs:117-133),
    //   - human control reads team.playerNumber (gameControls.cpp:74),
    // so reassigning them swaps ends, flips the goal each team attacks/defends,
    // and moves the human's controller to the team's new end — all at once.
    //
    // Our port's match-start seed always wires top=team1 / bottom=team2
    // (TeamDataLoader.WireTeamFields), so the NET EFFECT of re-running
    // initTeamsData with the flipped teamPlayingUp for the H1->H2 transition is
    // a straight SWAP of the team-identity fields between TopBase and
    // BottomBase. We implement it as that swap (robust regardless of the H1
    // assignment) plus the per-team reset-to-constant block (game.cpp:479-508)
    // applied to both structs.
    //
    // Source: external/swos-port/src/game/game.cpp:422-548.
    public static void ReseatTeamsForNewHalf()
    {
        // game.cpp:449-455 / 462-478 / 535-541 — the fields the top/bottom xchg
        // reassigns. opponentsTeam (game.cpp:460) stays cross-linked (top's
        // opponent is always bottom) so it is NOT swapped.
        SwapDword(TeamData.OffInGameTeamPtr);      // game.cpp:462
        SwapDword(TeamData.OffTeamStatsPtr);       // game.cpp:464
        SwapWord (TeamData.OffPlayerNumber);       // game.cpp:466
        SwapWord (TeamData.OffPlayerCoachNumber);  // game.cpp:468
        SwapWord (TeamData.OffIsPlCoach);          // game.cpp:470
        SwapDword(TeamData.OffPlayers);            // game.cpp:472 (spritesTable)
        SwapWord (TeamData.OffTeamNumber);         // game.cpp:474
        SwapWord (TeamData.OffTactics);            // game.cpp:478
        // Not in initTeamsData, but our per-team shotChanceTable pointer holds
        // the owning team's per-player dive-chance data, so it must follow the
        // players it is keyed to (UpdatePlayerShotChanceTable rewrites it each
        // frame for whichever sprites the struct now owns).
        SwapDword(TeamData.OffShotChanceTable);

        // game.cpp:479-508 — per-team reset-to-constant fields, applied to both
        // structs (the asm writes these for each team in the loop body).
        ResetPerTeamFieldsForNewHalf(TeamData.TopBase);
        ResetPerTeamFieldsForNewHalf(TeamData.BottomBase);
    }

    // game.cpp:479-508 — the writeMemory(esi + N, ...) reset block run for each
    // team inside initTeamsData's loop body.
    private static void ResetPerTeamFieldsForNewHalf(int teamBase)
    {
        Memory.WriteWord (teamBase + TeamData.OffGoalkeeperPlaying, 0);            // game.cpp:479
        Memory.WriteWord (teamBase + TeamData.OffResetControls, 0);               // game.cpp:480
        Memory.WriteWord (teamBase + TeamData.OffUpdatePlayerIndex, 10);          // game.cpp:481
        Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, 0);            // game.cpp:482
        Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);             // game.cpp:483
        Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);        // game.cpp:484
        Memory.WriteWord (teamBase + TeamData.OffPlayerHasBall, 0);               // game.cpp:485
        Memory.WriteWord (teamBase + TeamData.OffGoalkeeperDivingRight, 0);       // game.cpp:486
        Memory.WriteWord (teamBase + TeamData.OffGoalkeeperDivingLeft, 0);        // game.cpp:487
        Memory.WriteWord (teamBase + TeamData.OffBallOutOfPlayOrKeeper, 0);       // game.cpp:488
        Memory.WriteWord (teamBase + TeamData.OffGoaliePlayingOrOut, 0);          // game.cpp:489
        Memory.WriteWord (teamBase + TeamData.OffPassingBall, 0);                 // game.cpp:490
        Memory.WriteWord (teamBase + TeamData.OffPassingToPlayer, 0);             // game.cpp:491
        Memory.WriteWord (teamBase + TeamData.OffPlayerSwitchTimer, 0);           // game.cpp:492
        Memory.WriteWord (teamBase + TeamData.OffBallInPlay, 0);                  // game.cpp:493
        Memory.WriteWord (teamBase + TeamData.OffBallOutOfPlay, 0);               // game.cpp:494
        Memory.WriteWord (teamBase + TeamData.OffPassKickTimer, 0);               // game.cpp:495
        Memory.WriteWord (teamBase + TeamData.OffBallCanBeControlled, 0);         // game.cpp:496
        Memory.WriteWord (teamBase + TeamData.OffBallControllingPlayerDirection, -1); // game.cpp:498
        Memory.WriteWord (teamBase + 138, 0);                                    // game.cpp:500 wonTheBallTimer (+138)
        Memory.WriteWord (teamBase + TeamData.OffSpinTimer, -1);                  // game.cpp:501
        Memory.WriteWord (teamBase + TeamData.OffGoalkeeperSavedCommentTimer, 0); // game.cpp:503
        Memory.WriteDword(teamBase + TeamData.OffLastHeadingPlayer, 0);           // game.cpp:504
        Memory.WriteWord (teamBase + TeamData.OffOfs78, 0);                       // game.cpp:505
        Memory.WriteWord (teamBase + TeamData.OffHeaderOrTackle, 0);              // game.cpp:506
        Memory.WriteWord (teamBase + TeamData.OffShooting, 0);                    // game.cpp:507
        Memory.WriteWord (teamBase + TeamData.OffPassInProgress, 0);              // game.cpp:508
    }

    private static void SwapWord(int fieldOffset)
    {
        short a = Memory.ReadSignedWord(TeamData.TopBase + fieldOffset);
        short b = Memory.ReadSignedWord(TeamData.BottomBase + fieldOffset);
        Memory.WriteWord(TeamData.TopBase + fieldOffset, b);
        Memory.WriteWord(TeamData.BottomBase + fieldOffset, a);
    }

    private static void SwapDword(int fieldOffset)
    {
        int a = Memory.ReadSignedDword(TeamData.TopBase + fieldOffset);
        int b = Memory.ReadSignedDword(TeamData.BottomBase + fieldOffset);
        Memory.WriteDword(TeamData.TopBase + fieldOffset, b);
        Memory.WriteDword(TeamData.BottomBase + fieldOffset, a);
    }

    // ====================================================================
    // prepareForInitialKick — gameLoop.cpp:2113-2157
    // ====================================================================
    // Ball to the centre spot, gameState = 0 (kPlayersGoingToInitialPositions)
    // so the updatePlayers walk-to-formation AI takes over, camera/turn flags
    // pointed at the kicking team. Called at match start (via the GameLoop
    // ladder out of kStartingGame), at half/extra-time starts, and by the
    // 825-tick CPU-timeout safety net (gameLoop.cpp:519-572). Re-reads
    // teamStarting/teamPlayingUp on every call — both flip between halves,
    // so this must NOT be treated as match-start-only.
    public static void PrepareForInitialKick()
    {
        // gameLoop.cpp:2115-2117 — setBallPosition(336, 449).
        BallUpdate.SetBallPosition(CenterX, CenterY);

        // gameLoop.cpp:2118-2120 — defaults (upper team starting):
        //   A0 = topTeamData (522792), D1 = 4 (camera dir), D2 = 0x7C (124).
        int lastTeamPtr = TeamData.TopBase;     // topTeamData
        short cameraDir = 4;
        byte turnFlags = 0x7C;                  // 124

        // gameLoop.cpp:2121-2137 — if teamStarting != teamPlayingUp the
        // kicking team is the one defending the BOTTOM goal:
        //   A0 = bottomTeamData (522940), D1 = 0, D2 = 0xC7 (199).
        // teamStarting @ 523144, teamPlayingUp @ 523146.
        short teamStarting = Memory.ReadSignedWord(Memory.Addr.teamStarting);
        short teamPlayingUp = Memory.ReadSignedWord(Memory.Addr.teamPlayingUp);
        if (teamStarting != teamPlayingUp)
        {
            lastTeamPtr = TeamData.BottomBase;  // bottomTeamData
            cameraDir = 0;
            turnFlags = 0xC7;                   // 199
        }

        // gameLoop.cpp:2140 — gameState = ST_PLAYERS_TO_INITIAL_POSITIONS (0).
        Memory.WriteWord(Memory.Addr.gameState, 0);
        // gameLoop.cpp:2141 — breakCameraMode = -1 (@ 523120).
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // gameLoop.cpp:2142 — gameStatePl = ST_STOPPED (101).
        Memory.WriteWord(Memory.Addr.gameStatePl, 101);
        // gameLoop.cpp:2143 — gameNotInProgressCounterWriteOnly = 0 (@ 523104).
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // gameLoop.cpp:2144-2145 — foul coordinates = centre spot
        // (foulXCoordinate @ 523124, foulYCoordinate @ 523126).
        Memory.WriteWord(Memory.Addr.foulXCoordinate, 336);
        Memory.WriteWord(Memory.Addr.foulYCoordinate, 449);
        // gameLoop.cpp:2146-2147 — cameraDirection = D1 (@ 523128).
        Memory.WriteWord(Memory.Addr.cameraDirection, cameraDir);
        // gameLoop.cpp:2148-2149 — playerTurnFlags = D2 (byte @ 523130).
        Memory.WriteByte(Memory.Addr.playerTurnFlags, turnFlags);
        // gameLoop.cpp:2150-2151 — lastTeamPlayedBeforeBreak = A0 (@ 523108).
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, lastTeamPtr);
        // gameLoop.cpp:2152-2153 — stoppage timers = 0.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);

        // gameLoop.cpp:2154 — call StopAllPlayers.
        TeamPort.StopAllPlayers();

        // gameLoop.cpp:2155-2156 — camera velocities = 0 (@ 449796 / 449798).
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
    }

    // PORT-COMPAT shim — remove after Main.cs rewire. The original
    // prepareForInitialKick takes no argument (it re-reads teamStarting /
    // teamPlayingUp from globals — gameLoop.cpp:2121-2123); the parameter of
    // the old heuristic API is ignored.
    public static void PrepareForInitialKick(int ignoredLegacyKickingTeamIndex)
    {
        PrepareForInitialKick();
    }
}
