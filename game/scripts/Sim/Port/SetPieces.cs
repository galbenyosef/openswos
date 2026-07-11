namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Set-piece logic ported from external/swos-port/src/game/updatePlayers/updatePlayers.cpp
// and external/swos-port/src/game/ball/ball.cpp.
//
// **Set-piece game states** (swos.h GameState enum, also used by ball.cpp:3007+):
//   ST_PLAYERS_TO_INITIAL_POSITIONS = 0   — post-goal restart
//   ST_GOAL_OUT_LEFT                = 1   — goal kick at left goal
//   ST_GOAL_OUT_RIGHT               = 2   — goal kick at right goal
//   ST_KEEPER_HOLDS_BALL            = 3   — keeper holds ball
//   ST_CORNER_LEFT                  = 4   — corner kick on left goal
//   ST_CORNER_RIGHT                 = 5   — corner kick on right goal
//   ST_FREE_KICK_LEFT1..3           = 6..8 — direct free kicks left
//   ST_FREE_KICK_CENTER             = 9
//   ST_FREE_KICK_RIGHT1..3          = 10..12 — direct free kicks right
//   ST_FOUL                         = 13
//   ST_PENALTY                      = 14
//   ST_THROW_IN_FORWARD_RIGHT       = 15
//   ST_THROW_IN_CENTER_RIGHT        = 16
//   ST_THROW_IN_BACK_RIGHT          = 17
//   ST_THROW_IN_FORWARD_LEFT        = 18
//   ST_THROW_IN_CENTER_LEFT         = 19
//   ST_THROW_IN_BACK_LEFT           = 20
//   ST_GAME_IN_PROGRESS             = 100 — normal play
//   ST_STOPPED                      = 101
//
// **Hard rules** (per project AGENTS.md):
//   - Mechanical asm-to-C# port pattern (matches PlayerUpdate.cs / BallOutOfPlay.cs).
//   - Cite source by file:line in C# comments.
//   - No floats; SWOS uses Q16.16 fixed-point for sub-pixel state.
//   - Preserve gotos via labelled C# goto statements; preserve magic constants.
//   - Stub callees that are not ported yet (`SetPlayerAnimationTable`, `DoPass`,
//     `PlayerKickingBall`).
//
// **asm register/memory convention** (same as AiHelpers / PlayerActions):
//   A1 -> player sprite (the set-piece taker)         ↔ a1PlayerAddr
//   A2 -> ball sprite                                  ↔ a2BallAddr (= BallSprite.Base)
//   A5 -> controlled player sprite (penalty)           ↔ a5PlayerAddr
//   A6 -> team general info                            ↔ a6TeamBase
//   D0..D7 = working registers (word/dword grain)
public static class SetPieces
{
    // ---- GameState enum constants (mirror swos.h) ----------------------------
    private const short ST_PLAYERS_TO_INITIAL_POSITIONS = 0;
    private const short ST_GOAL_OUT_LEFT                = 1;
    private const short ST_GOAL_OUT_RIGHT               = 2;
    private const short ST_KEEPER_HOLDS_BALL            = 3;
    private const short ST_CORNER_LEFT                  = 4;
    private const short ST_CORNER_RIGHT                 = 5;
    private const short ST_FREE_KICK_LEFT1              = 6;
    private const short ST_FREE_KICK_RIGHT3             = 12;
    private const short ST_FOUL                         = 13;
    private const short ST_PENALTY                      = 14;
    private const short ST_THROW_IN_FORWARD_RIGHT       = 15;
    private const short ST_THROW_IN_CENTER_RIGHT        = 16;
    private const short ST_THROW_IN_BACK_RIGHT          = 17;
    private const short ST_THROW_IN_FORWARD_LEFT        = 18;
    private const short ST_THROW_IN_CENTER_LEFT         = 19;
    private const short ST_THROW_IN_BACK_LEFT           = 20;
    // swos.h GameState — ST_PENALTIES (31). Used by the penalty shootout FSM
    // (gameState=31 while a pen is being taken; transitions out of 31 to
    // ST_PLAYERS_TO_INITIAL_POSITIONS when the ball is in the goal — see
    // BallOutOfPlay.cs:259). The shootout timer at gameLoop.cpp:449-467 only
    // bumps `penaltiesTimer` while gameState != 31 — i.e. AFTER the kick has
    // resolved — and fires NextPenalty() once it reaches m_penaltiesInterval.
    private const short ST_PENALTIES                    = 31;
    private const short ST_GAME_IN_PROGRESS             = 100;
    private const short ST_STOPPED                      = 101;
    // swos.h GameState — `ST_WAITING_ON_PLAYER`. Used by gameLoop.cpp:519-572:
    // while gameStatePl == 102 the stoppage timer accumulates; once it crosses
    // m_initalKickInterval (825 ticks ≈ 11.8 s) the AI safety net auto-fires
    // Kickoff.PrepareForInitialKick. Human teams retain manual control until
    // they press fire (InputControls.SetJoystickState routes space to the kick).
    private const short ST_WAITING_ON_PLAYER            = 102;

    // PlayerState enum (sprites/Sprite.h:16-34).
    private const byte PL_NORMAL    = 0;
    private const byte PL_THROW_IN  = 5;

    // player.cpp:24 — `kPlayerSpeedsGameStopped[] = { 1136, 1152, 1168, 1184, 1200,
    // 1216, 1232, 1248 }`. Looked up by playerInfo.speed (0..7) during ANY
    // non-ST_GAME_IN_PROGRESS state by UpdatePlayerSpeedAndFrameDelay
    // (PlayerActions.cs:81-83). We seed the kicker's first-tick speed with
    // slot [1] (1152) so MoveSprite has something to integrate before
    // PlayerActions.UpdatePlayerSpeedAndFrameDelay overwrites with the real
    // playerInfo.speed value on the same tick. Source:
    // external/swos-port/src/game/player.cpp:24.
    private const short kPlayerSpeedsGameStopped1 = 1152;

    // 4-pixel "stand behind the ball" offset table indexed by SWOS direction
    // 0..7 (0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW). The kicker walks to
    // a point 4 px BEHIND the ball relative to the kick direction (camDir),
    // so when MoveSprite walks them in they stop adjacent to (not on top of)
    // the ball. This matches how the asm break-camera FSM leaves the taker
    // a few pixels off the kick spot (see updatePlayers.cpp:15322-15374
    // for the throw-in 3px-inward equivalent).
    private static readonly short[] kKickerWalkupOffsetX = { 0, -3, -4, -3,  0, +3, +4, +3 };
    private static readonly short[] kKickerWalkupOffsetY = { +4, +3,  0, -3, -4, -3,  0, +3 };

    // ===================================================================
    // setThrowInPlayerDestinationCoordinates — updatePlayers.cpp:15322-15374
    // ===================================================================
    //
    // in:
    //      A1 -> player sprite that's taking the throw-in
    //
    // Sets the coordinates of a player that takes the throw in. The player
    // sprite is placed at the same Y as the ball (i.e. on the touchline) and
    // pulled 3 px in from the line — positive X when ball is on the right
    // half (X >= 336), negative when on the left.
    //
    // Pitch X = 336 is the visual centre of the pitch (it's also a magic
    // constant used elsewhere — see player.cpp:3596 throw-in dest-delta pick).
    //
    // The original asm uses A0 = ballSprite, A1 = player. We translate A1 to
    // an explicit `throwerSpriteAddr` parameter.
    public static void SetThrowInPlayerDestinationCoordinates(int throwerSpriteAddr)
    {
        // updatePlayers.cpp:15324-15327 — A0 = ballSprite; D1 = ax = ball.x.whole.
        // swos-port reads ball.x via `word ptr [esi + (Sprite.x + 2)]` — the
        // high 16 bits of the Q16.16 ball X position, i.e. whole pixels.
        int A0 = BallSprite.Base;
        int esi = A0;
        ushort ax = (ushort)Memory.ReadSignedWord(esi + 32);   // Sprite.x + 2 → high word
        ushort D1 = ax;

        // updatePlayers.cpp:15328-15337 — cmp word ptr D1, 336 ; jb @@left_half
        // Branch on UNSIGNED less-than (carry flag set after sub).
        bool carry = D1 < 336;
        if (carry)
            goto l_left_half;                                  // jb short @@left_half

        // updatePlayers.cpp:15339-15348 — right half: add D1, 3 ; jmp @@set_player_x
        // The asm `add` sets D1 += 3. Wraps as ushort (asm-equivalent).
        D1 = (ushort)(D1 + 3);
        goto l_set_player_x;                                   // jmp short @@set_player_x

    l_left_half:
        // updatePlayers.cpp:15350-15359 — left half: sub D1, 3.
        D1 = (ushort)(D1 - 3);

    l_set_player_x:
        // updatePlayers.cpp:15361-15365 — write D1 → player.x.whole AND player.destX.
        // esi = A1 (player). +32 is `Sprite.x + 2` (high word of Q16.16),
        // +58 is Sprite.destX (whole-pixel word).
        ax = D1;
        esi = throwerSpriteAddr;
        Memory.WriteWord(esi + 32, ax);                        // mov word ptr [esi+(Sprite.x+2)], ax
        Memory.WriteWord(esi + 58, ax);                        // mov [esi+Sprite.destX], ax

        // updatePlayers.cpp:15366-15369 — copy ball.y.whole → player.y.whole.
        esi = A0;
        ax = (ushort)Memory.ReadSignedWord(esi + 36);          // ball.y.whole
        esi = throwerSpriteAddr;
        Memory.WriteWord(esi + 36, ax);                        // player.y.whole = ball.y.whole

        // updatePlayers.cpp:15370-15373 — same value → player.destY.
        esi = A0;
        ax = (ushort)Memory.ReadSignedWord(esi + 36);
        esi = throwerSpriteAddr;
        Memory.WriteWord(esi + 60, ax);                        // mov [esi+Sprite.destY], ax
    }

    // ===================================================================
    // DispatchByGameState — set-piece state machine entry point
    // ===================================================================
    //
    // Routes a per-tick set-piece spawn/update based on `gameState`. Called by
    // the gameLoop port when the match is in a set-piece state (not
    // ST_GAME_IN_PROGRESS / ST_STOPPED).
    //
    // The original handlers live inline in `updatePlayers` (updatePlayers.cpp:47)
    // as labelled blocks reached by the per-player state dispatch. We expose
    // them here as standalone helpers so caller can provide the
    // (a1PlayerAddr, a2BallAddr, a5PlayerAddr, a6TeamBase) context that the
    // original asm receives via register state.
    public static void DispatchByGameState(
        int a1PlayerAddr,
        int a2BallAddr,
        int a5PlayerAddr,
        int a6TeamBase)
    {
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);

        // updatePlayers.cpp:16329-16383 — throw-in state range [15..20].
        if (gameState >= ST_THROW_IN_FORWARD_RIGHT && gameState <= ST_THROW_IN_BACK_LEFT)
        {
            TickThrowIn(a1PlayerAddr, a2BallAddr, a6TeamBase);
            return;
        }

        // updatePlayers.cpp:16395 — ST_FOUL.
        if (gameState == ST_FOUL)
        {
            TickFreeKick(a6TeamBase);
            return;
        }

        // updatePlayers.cpp:16408-16422 — free kick range [6..12].
        if (gameState >= ST_FREE_KICK_LEFT1 && gameState <= ST_FREE_KICK_RIGHT3)
        {
            TickFreeKick(a6TeamBase);
            return;
        }

        // updatePlayers.cpp:16499 — ST_PENALTY (in-match) OR ST_PENALTIES (shootout).
        // Both states route to the same per-tick handler: the keeper-dive
        // direction picker at updatePlayers.cpp:16859-16914. The shootout
        // case ALSO walks the shooter to the penalty spot + parks in
        // ST_WAITING_ON_PLAYER (see TickPenalty body for the shootout-only
        // resolve path).
        if (gameState == ST_PENALTY || gameState == ST_PENALTIES)
        {
            TickPenalty(a5PlayerAddr, a6TeamBase);
            return;
        }

        // ball.cpp:3334+ / 3792+ — corner + goal-out spawn states.
        if (gameState == ST_CORNER_LEFT || gameState == ST_CORNER_RIGHT)
        {
            TickCorner(a6TeamBase);
            return;
        }

        if (gameState == ST_GOAL_OUT_LEFT || gameState == ST_GOAL_OUT_RIGHT)
        {
            TickGoalKick(a6TeamBase);
            return;
        }

        // ST_KEEPER_HOLDS_BALL / ST_PLAYERS_TO_INITIAL_POSITIONS / others —
        // no per-tick set-piece action needed at this layer (keeper-holds is
        // handled by PlayerUpdate; restart is handled by BallOutOfPlay).
    }

    // ===================================================================
    // TickThrowIn — updatePlayers.cpp:5780-6373 (`l_player_taking_throw_in`)
    // ===================================================================
    //
    // Triggered per-player tick when sprite.playerState == PL_THROW_IN.
    //
    // In:
    //   A1 -> thrower sprite      (a1PlayerAddr)
    //   A2 -> ball sprite         (a2BallAddr = BallSprite.Base)
    //   A6 -> thrower's team data (a6TeamBase)
    //
    // Side effects: writes Sprite.direction / playerState / playerDownTimer
    //   for the thrower; advances disallowedTurnFlagsCounter,
    //   deadThrowInDirectionVar; on fire writes gameState ← ST_GAME_IN_PROGRESS,
    //   transitions ball + team flags for pass/kick release.
    //
    // The function falls off the end into `l_update_player_speed_and_deltas`
    // in the original. Our port returns at the equivalent point.
    public static void TickThrowIn(int a1PlayerAddr, int a2BallAddr, int a6TeamBase)
    {
        // Working register-equivalents.
        int A1 = a1PlayerAddr;
        int A2 = a2BallAddr;
        int A6 = a6TeamBase;
        int A0 = 0;
        short D0 = 0;
        int esi;
        ushort ax;
        byte al, cl;
        int eax;
        bool zeroF;     // local zero flag tracker (clears asm `flags.zero`)

        // updatePlayers.cpp:5781-5788 — esi = A6; ax = team.playerNumber;
        // if non-zero, skip AI direction selection.
        esi = A6;
        ax = (ushort)Memory.ReadSignedWord(esi + 4);            // TeamData.OffPlayerNumber
        zeroF = ax == 0;
        if (!zeroF)
            goto l_test_allowed_turn_flags;                     // jnz short

        // updatePlayers.cpp:5790-5796 — AI thrower → AI_SetControlsDirection.
        AI_SetControlsDirection(A6);                            // call AI_SetControlsDirection

    l_test_allowed_turn_flags:
        // updatePlayers.cpp:5798-5820 — test playerTurnFlags & (1 << dir).
        esi = A1;
        ax = (ushort)Memory.ReadSignedWord(esi + PlayerSprite.OffDirection);
        D0 = (short)ax;
        cl = (byte)D0;
        ax = 1;
        if ((cl & 0x1F) != 0)
            ax = (ushort)(ax << (cl & 0x1F));                   // shl ax, cl
        {
            byte turnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            byte testRes = (byte)(turnFlags & (byte)ax);
            zeroF = testRes == 0;
        }
        if (!zeroF)
            goto l_test_turn_flags_with_camera_direction;

        // updatePlayers.cpp:5822-5831 — sprite.direction = cameraDirection;
        // disallowedTurnFlagsCounter += 1.
        ax = (ushort)Memory.ReadSignedWord(Memory.Addr.cameraDirection);
        Memory.WriteWord(esi + PlayerSprite.OffDirection, ax);
        {
            short v = Memory.ReadSignedWord(Memory.Addr.disallowedTurnFlagsCounter);
            Memory.WriteWord(Memory.Addr.disallowedTurnFlagsCounter, (short)(v + 1));
        }

    l_test_turn_flags_with_camera_direction:
        // updatePlayers.cpp:5833-5854 — re-test sprite.direction against playerTurnFlags.
        esi = A1;
        ax = (ushort)Memory.ReadSignedWord(esi + PlayerSprite.OffDirection);
        D0 = (short)ax;
        cl = (byte)D0;
        ax = 1;
        if ((cl & 0x1F) != 0)
            ax = (ushort)(ax << (cl & 0x1F));
        {
            byte turnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            byte testRes = (byte)(turnFlags & (byte)ax);
            zeroF = testRes == 0;
        }
        if (!zeroF)
            goto l_check_if_throw_in_taker_substituted;

        // updatePlayers.cpp:5857 — D0 = 7; rotate searching for an allowed direction.
        D0 = 7;

    l_next_direction:
        cl = (byte)D0;
        ax = 1;
        if ((cl & 0x1F) != 0)
            ax = (ushort)(ax << (cl & 0x1F));
        {
            byte turnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            byte testRes = (byte)(turnFlags & (byte)ax);
            zeroF = testRes == 0;
        }
        if (!zeroF)
            goto l_found_direction;

        D0 = (short)(D0 - 1);                                   // dec word ptr D0
        if (D0 >= 0)
            goto l_next_direction;                              // jns short

        D0 = 0;                                                 // (D0 went negative — fallback to 0)

    l_found_direction:
        // updatePlayers.cpp:5890-5901 — cameraDirection = D0; sprite.direction = D0;
        // deadThrowInDirectionVar += 1.
        ax = (ushort)D0;
        Memory.WriteWord(Memory.Addr.cameraDirection, ax);
        esi = A1;
        Memory.WriteWord(esi + PlayerSprite.OffDirection, ax);
        {
            short v = Memory.ReadSignedWord(Memory.Addr.deadThrowInDirectionVar);
            Memory.WriteWord(Memory.Addr.deadThrowInDirectionVar, (short)(v + 1));
        }

    l_check_if_throw_in_taker_substituted:
        // updatePlayers.cpp:5904-5910 — g_substituteInProgress check.
        ax = (ushort)Memory.ReadSignedWord(Memory.Addr.g_substituteInProgress);
        if (ax == 0)
            goto l_check_throw_in_game_state;

        // 5912-5923 — cmp A1, substitutedPlSprite; if equal → abort.
        eax = Memory.ReadSignedDword(Memory.Addr.substitutedPlSprite);
        if (A1 == eax)
            goto l_abort_throw_in;

    l_check_throw_in_game_state:
        // 5925-5937 — bail if gameState < ST_THROW_IN_FORWARD_RIGHT (15).
        {
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            if ((ushort)gs < (ushort)15)
                goto l_abort_throw_in;
        }
        // 5939-5950 — bail if gameState > ST_THROW_IN_BACK_LEFT (20).
        {
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            if ((ushort)gs > (ushort)20)
                goto l_abort_throw_in;
        }

        // 5952-5959 — al = sprite.playerDownTimer; if zero → ready_for_throw_in.
        esi = A1;
        al = Memory.ReadByte(esi + PlayerSprite.OffPlayerDownTimer);
        if (al == 0)
            goto l_ready_for_throw_in;

        // 5961-5974 — playerDownTimer -= 1; if zero → throw_in_over.
        {
            byte src = Memory.ReadByte(esi + PlayerSprite.OffPlayerDownTimer);
            byte res = (byte)(src - 1);
            Memory.WriteByte(esi + PlayerSprite.OffPlayerDownTimer, res);
            if (res == 0)
                goto l_throw_in_over;
        }

        // 5976-5987 — cmp [esi+playerDownTimer], 18; if not 18 → update_player_speed_and_deltas (return).
        {
            byte src = Memory.ReadByte(esi + PlayerSprite.OffPlayerDownTimer);
            if (src != 18)
                return;                                         // jnz @@update_player_speed_and_deltas
        }

        // 5989-6002 — at countdown=18 frames, switch to aboutToThrowInAnimTable
        // (unless substitutes menu is open).
        ax = (ushort)Memory.ReadSignedWord(Memory.Addr.g_inSubstitutesMenu);
        if (ax == 0)
            goto l_throw_in_done_check_pass_or_kick;

        A0 = Memory.Addr.aboutToThrowInAnimTable;               // mov A0, offset aboutToThrowInAnimTable
        SetPlayerAnimationTable(A1, A0);
        esi = A1;
        Memory.WriteByte(esi + PlayerSprite.OffPlayerState, PL_THROW_IN);
        Memory.WriteByte(esi + PlayerSprite.OffPlayerDownTimer, 0);
        goto l_ready_for_throw_in;

    l_throw_in_done_check_pass_or_kick:
        // 6004-6019 — countdown reached 18: lift ball; dispatch pass vs kick.
        esi = A1;
        ax = (ushort)Memory.ReadSignedWord(esi + PlayerSprite.OffDirection);
        D0 = (short)ax;
        esi = A2;
        Memory.WriteWord(esi + 40, 12);                         // ball.z.whole = 12
        Memory.WriteWord(Memory.Addr.hideBall, 0);
        ax = (ushort)Memory.ReadSignedWord(Memory.Addr.throwInPassOrKick);
        if (ax != 0)
            goto l_do_throw_in_pass;

        goto l_do_throw_in_kick;

    l_ready_for_throw_in:
        // 6021-6029 — AI thrower stoppage-timer check (early bail).
        esi = A6;
        ax = (ushort)Memory.ReadSignedWord(esi + 4);            // TeamData.OffPlayerNumber
        if (ax != 0)
            goto l_throw_in_check_input_direction;

        // 6031-6044 — AI: stoppageTimerActive > 55 → hide result (force release).
        {
            short stoppage = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
            if ((ushort)stoppage > (ushort)55)
                goto l_throw_in_hide_result;
        }
        goto l_throw_in_check_fire;

    l_throw_in_check_input_direction:
        // 6046-6054 — currentAllowedDirection sign check; if non-negative,
        // jump to stats check.
        esi = A6;
        {
            short cad = Memory.ReadSignedWord(esi + TeamData.OffCurrentAllowedDirection);
            if (cad >= 0)                                       // jns short
                goto l_check_if_stats_showing;
        }

    l_throw_in_check_fire:
        // 6056-6080 — fire input check.
        esi = A6;
        al = Memory.ReadByte(esi + TeamData.OffFirePressed);
        if (al != 0)
            goto l_check_if_stats_showing;

        al = Memory.ReadByte(esi + TeamData.OffQuickFire);
        if (al != 0)
            goto l_check_if_stats_showing;

        al = Memory.ReadByte(esi + TeamData.OffNormalFire);
        if (al == 0)
            goto l_throw_in_check_direction;

    l_check_if_stats_showing:
        // 6082-6091 — if statsTimer != 0, mask out fire input.
        ax = (ushort)Memory.ReadSignedWord(Memory.Addr.statsTimer);
        if (ax == 0)
            goto l_throw_in_hide_result;

        Memory.WriteWord(Memory.Addr.fireBlocked, 1);

    l_throw_in_hide_result:
        // 6093-6103 — neg timeVar; neg resultTimer (toggles sign back).
        {
            short v = Memory.ReadSignedWord(Memory.Addr.timeVar);
            Memory.WriteWord(Memory.Addr.timeVar, (short)(-v));
        }
        {
            short v = Memory.ReadSignedWord(Memory.Addr.resultTimer);
            Memory.WriteWord(Memory.Addr.resultTimer, (short)(-v));
        }

    l_throw_in_check_direction:
        // 6105-6114 — D0 = team.currentAllowedDirection; if non-negative
        // (input from joystick), use it; otherwise fall through to use
        // sprite.direction.
        esi = A6;
        ax = (ushort)Memory.ReadSignedWord(esi + TeamData.OffCurrentAllowedDirection);
        D0 = (short)ax;
        if (D0 >= 0)
            goto l_throw_in_got_input_direction;                // jns short

    l_throw_in_use_sprite_direction:
        // 6116-6123 — fallback: team.currentAllowedDirection = sprite.direction.
        esi = A1;
        ax = (ushort)Memory.ReadSignedWord(esi + PlayerSprite.OffDirection);
        esi = A6;
        Memory.WriteWord(esi + TeamData.OffCurrentAllowedDirection, ax);
        ax = (ushort)Memory.ReadSignedWord(esi + TeamData.OffCurrentAllowedDirection);
        D0 = (short)ax;
        goto l_throw_in_check_quick_fire;

    l_throw_in_got_input_direction:
        // 6125-6144 — test (1 << D0) against playerTurnFlags; if disallowed,
        // fall back to sprite.direction.
        cl = (byte)D0;
        ax = 1;
        if ((cl & 0x1F) != 0)
            ax = (ushort)(ax << (cl & 0x1F));
        {
            byte turnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            byte testRes = (byte)(turnFlags & (byte)ax);
            zeroF = testRes == 0;
        }
        if (zeroF)
            goto l_throw_in_use_sprite_direction;

        // 6146-6158 — if D0 == sprite.direction, skip the rotate.
        esi = A1;
        ax = (ushort)Memory.ReadSignedWord(esi + PlayerSprite.OffDirection);
        if (D0 == (short)ax)
            goto l_throw_in_check_quick_fire;

        // 6160-6169 — sprite.direction = D0; reposition player; switch anim.
        ax = (ushort)D0;
        Memory.WriteWord(esi + PlayerSprite.OffDirection, ax);
        SetThrowInPlayerDestinationCoordinates(A1);
        A0 = Memory.Addr.aboutToThrowInAnimTable;
        SetPlayerAnimationTable(A1, A0);
        esi = A1;
        Memory.WriteByte(esi + PlayerSprite.OffPlayerState, PL_THROW_IN);
        Memory.WriteByte(esi + PlayerSprite.OffPlayerDownTimer, 0);

    l_throw_in_check_quick_fire:
        // 6171-6206 — quickFire branch: throwInPassOrKick = 1; switch anim
        // table; playerDownTimer = 20.
        esi = A6;
        al = Memory.ReadByte(esi + TeamData.OffQuickFire);
        if (al == 0)
            goto l_throw_in_check_normal_fire;

        cl = (byte)D0;
        ax = 1;
        if ((cl & 0x1F) != 0)
            ax = (ushort)(ax << (cl & 0x1F));
        {
            ushort turnFlagsW = (ushort)Memory.ReadSignedWord(Memory.Addr.playerTurnFlags);
            ushort testRes = (ushort)(turnFlagsW & ax);
            zeroF = testRes == 0;
        }
        if (zeroF)
            goto l_throw_in_check_normal_fire;

        Memory.WriteWord(Memory.Addr.throwInPassOrKick, 1);
        A0 = Memory.Addr.throwInPassAnimTable;
        SetPlayerAnimationTable(A1, A0);
        esi = A1;
        Memory.WriteByte(esi + PlayerSprite.OffPlayerDownTimer, 20);
        return;                                                 // jmp @@update_player_speed_and_deltas

    l_throw_in_check_normal_fire:
        // 6208-6243 — normalFire branch: throwInPassOrKick = 0;
        // playerDownTimer = 25.
        esi = A6;
        al = Memory.ReadByte(esi + TeamData.OffNormalFire);
        if (al == 0)
            return;                                             // jz @@update_player_speed_and_deltas

        cl = (byte)D0;
        ax = 1;
        if ((cl & 0x1F) != 0)
            ax = (ushort)(ax << (cl & 0x1F));
        {
            ushort turnFlagsW = (ushort)Memory.ReadSignedWord(Memory.Addr.playerTurnFlags);
            ushort testRes = (ushort)(turnFlagsW & ax);
            zeroF = testRes == 0;
        }
        if (zeroF)
            return;                                             // jz @@update_player_speed_and_deltas

        Memory.WriteWord(Memory.Addr.throwInPassOrKick, 0);
        A0 = Memory.Addr.throwInKickAnimTable;
        SetPlayerAnimationTable(A1, A0);
        esi = A1;
        Memory.WriteByte(esi + PlayerSprite.OffPlayerDownTimer, 25);
        return;                                                 // jmp @@update_player_speed_and_deltas

    l_do_throw_in_pass:
        // 6245-6304 — release ball as a pass; transition gameState back to play.
        eax = A6;
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, eax);
        eax = A1;
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, eax);
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);
        DoPass(A6, A1);                                         // call DoPass

        eax = A1;
        esi = A6;
        Memory.WriteDword(esi + TeamData.OffLastHeadingPlayer, eax);
        esi = A2;
        Memory.WriteDword(esi + 54, 1);                         // ball.deltaZ = 1 (raw dword write)
        {
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp == 100)
                goto l_throw_in_ball_passed;
        }
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);

    l_throw_in_ball_passed:
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_GAME_IN_PROGRESS);
        Memory.WriteWord(Memory.Addr.gameState, ST_GAME_IN_PROGRESS);
        esi = A6;
        Memory.WriteWord(esi + TeamData.OffBallInPlay, 1);
        Memory.WriteWord(esi + TeamData.OffBallOutOfPlay, 1);
        eax = Memory.ReadSignedDword(esi + TeamData.OffOpponentsTeam);
        A0 = eax;
        esi = A0;
        Memory.WriteWord(esi + TeamData.OffBallInPlay, 1);
        Memory.WriteWord(esi + TeamData.OffBallOutOfPlay, 1);
        Memory.WriteWord(esi + TeamData.OffSpinTimer, -1);
        esi = A6;
        Memory.WriteDword(esi + TeamData.OffControlledPlayer, 0);
        eax = A1;
        Memory.WriteDword(esi + TeamData.OffPassingKickingPlayer, eax);
        Memory.WriteWord(esi + TeamData.OffPassKickTimer, 25);
        Memory.WriteWord(esi + TeamData.OffBallCanBeControlled, 0);
        eax = Memory.ReadSignedDword(esi + TeamData.OffOpponentsTeam);
        // NB: original sets A0=opponent again but we already have A0 set above.
        esi = A0;
        Memory.WriteWord(esi + TeamData.OffPassingToPlayer, 0);
        Memory.WriteDword(esi + TeamData.OffPassingKickingPlayer, 0);
        return;                                                 // jmp @@update_player_speed_and_deltas

    l_do_throw_in_kick:
        // 6306-6363 — release ball as a kick; same state transitions as pass.
        eax = A6;
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, eax);
        eax = A1;
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, eax);
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);
        PlayerKickingBall(A6, A1);                              // call PlayerKickingBall

        eax = A1;
        esi = A6;
        Memory.WriteDword(esi + TeamData.OffLastHeadingPlayer, eax);
        {
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp == 100)
                goto l_throw_in_ball_kicked;
        }
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);

    l_throw_in_ball_kicked:
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_GAME_IN_PROGRESS);
        Memory.WriteWord(Memory.Addr.gameState, ST_GAME_IN_PROGRESS);
        esi = A6;
        Memory.WriteWord(esi + TeamData.OffBallInPlay, 1);
        Memory.WriteWord(esi + TeamData.OffBallOutOfPlay, 1);
        eax = Memory.ReadSignedDword(esi + TeamData.OffOpponentsTeam);
        A0 = eax;
        esi = A0;
        Memory.WriteWord(esi + TeamData.OffBallInPlay, 1);
        Memory.WriteWord(esi + TeamData.OffBallOutOfPlay, 1);
        Memory.WriteWord(esi + TeamData.OffSpinTimer, -1);
        esi = A6;
        Memory.WriteDword(esi + TeamData.OffControlledPlayer, 0);
        eax = A1;
        Memory.WriteDword(esi + TeamData.OffPassingKickingPlayer, eax);
        Memory.WriteWord(esi + TeamData.OffPassKickTimer, 25);
        Memory.WriteWord(esi + TeamData.OffBallCanBeControlled, 0);
        eax = Memory.ReadSignedDword(esi + TeamData.OffOpponentsTeam);
        esi = A0;
        Memory.WriteWord(esi + TeamData.OffPassingToPlayer, 0);
        Memory.WriteDword(esi + TeamData.OffPassingKickingPlayer, 0);
        return;                                                 // jmp @@update_player_speed_and_deltas

    l_abort_throw_in:
        // 6365-6366 — hideBall = 0; fall into throw_in_over.
        Memory.WriteWord(Memory.Addr.hideBall, 0);

    l_throw_in_over:
        // 6368-6373 — sprite.playerState = PL_NORMAL; switch to
        // playerNormalStandingAnimTable.
        esi = A1;
        Memory.WriteByte(esi + PlayerSprite.OffPlayerState, PL_NORMAL);
        A0 = Memory.Addr.playerNormalStandingAnimTable;
        SetPlayerAnimationTable(A1, A0);
        return;                                                 // jmp @@update_player_speed_and_deltas
    }

    // ===================================================================
    // TickSetPieces — per-tick corner / goal-kick auto-resolver
    // ===================================================================
    //
    // Called from GameLoop.CoreGameUpdate every tick, AFTER UpdatePostGoalRestart
    // and BEFORE the per-team updatePlayers pass. Inspects gameState; for the
    // four set-piece states this module owns (ST_CORNER_LEFT/RIGHT,
    // ST_GOAL_OUT_LEFT/RIGHT) it spawns the ball at the configured spot,
    // assigns a kicker (nearest outfielder for corner / goalkeeper for goal
    // kick), runs PlayerKickingBall, and resumes live play.
    //
    // **Why an auto-resolver and not a multi-tick state machine**: the full
    // SWOS break-camera FSM in gameLoop.cpp:1110-1846 (~870 LOC asm) walks
    // the camera to the foul spot, gates on `breakCameraMode` 0/1/2, then
    // calls `setBallPosition(foulXCoordinate, foulYCoordinate)` only once
    // the camera arrives (gameLoop.cpp:1336-1340). The same FSM is what
    // gates the throw-in / corner / goal-kick taker walking up to the ball.
    // Until that FSM is ported, we mirror the pragmatic shortcut already
    // used for `UpdateGoals.UpdatePostGoalRestart` (UpdateGoals.cs:202+):
    // place ball + kicker directly, kick, resume.
    //
    // From external/swos-port/src/game/ball/ball.cpp:3623+ (corner spawn
    // gameState set) and ball.cpp:3704+ (goal-out spawn). Per-tick handlers
    // would live at updatePlayers.cpp:16320-16344 + 9220-9430.
    public static void TickSetPieces()
    {
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameState == ST_CORNER_LEFT || gameState == ST_CORNER_RIGHT)
        {
            ResolveCornerKick(gameState);
            return;
        }
        if (gameState == ST_GOAL_OUT_LEFT || gameState == ST_GOAL_OUT_RIGHT)
        {
            ResolveGoalKick(gameState);
            return;
        }
        // Penalty shootout: one-shot snap of shooter + ball at (336, 187) and
        // park the FSM in ST_WAITING_ON_PLAYER. NextPenalty (GameTime.cs:1199)
        // already set gameState=31 + gameStatePl=101 + foulX/Y + chose the
        // shooter. ResolvePenaltyShootout only fires on the FIRST tick of
        // each pen (gates on gameStatePl == ST_STOPPED) and is idempotent
        // thereafter. See ResolvePenaltyShootout for the cite chain.
        if (gameState == ST_PENALTIES)
        {
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp == ST_STOPPED)
                ResolvePenaltyShootout();
            return;
        }
        // Throw-in auto-resolver. The asm spawn (gameState ← ST_THROW_IN_*)
        // lives in BallOutOfPlay.cs:462-552, mirroring ball.cpp:3795-3973.
        // The thrower setup (updatePlayers.cpp:8055-8067) only fires DURING
        // the active kick-out tick; once gameState transitions to a throw-in
        // it never re-runs. Same pragmatic shortcut as corner/goal-kick:
        // place ball at the touchline spot, snap thrower on top, fire.
        if (gameState >= ST_THROW_IN_FORWARD_RIGHT && gameState <= ST_THROW_IN_BACK_LEFT)
        {
            ResolveThrowIn(gameState);
            return;
        }
        // Foul / free-kick auto-resolver. ST_FOUL (13) and ST_FREE_KICK_LEFT1
        // .. ST_FREE_KICK_RIGHT3 (6..12). Spawn happens elsewhere when the
        // referee book/foul logic lands; for now any foul that gets us into
        // these states needs to release the ball back into play.
        if (gameState == ST_FOUL ||
            (gameState >= ST_FREE_KICK_LEFT1 && gameState <= ST_FREE_KICK_RIGHT3))
        {
            ResolveFreeKick(gameState);
        }
    }

    // ===================================================================
    // TickCorner — per-tick AI turn / fire update during ST_CORNER_*
    // ===================================================================
    //
    // The corner-spawn (ball placement at flag, gameState ← ST_CORNER_*) lives
    // in BallOutOfPlay.cs, which mirrors ball.cpp:3623+ / 3645+ / 3654+ / 3663+.
    // Once the state is set, this per-tick handler runs as part of the
    // updatePlayers AI rotation tail (updatePlayers.cpp:16329-16344 routes
    // corner gameStates to `l_update_turn_direction`).
    //
    // The actual ball destination is selected by `getBallDestCoordinatesTable`
    // (player.cpp:4125-4203 — `l_corner` / `l_upper_right_corner` /
    // `l_lower_corner` / `l_lower_right_corner` branches) which we leave to
    // PlayerActions when it's needed at kick time.
    //
    // From updatePlayers.cpp:16320-16344 (corner is one of the gameStates
    // routed to `l_update_turn_direction`).
    public static void TickCorner(int a6TeamBase)
    {
        // updatePlayers.cpp:16311-16318 — if resultTimer != 0 return.
        // (Result overlay is up — set-piece input is suppressed.)
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        _ = a6TeamBase;
        // Per-tick body now lives in ResolveCornerKick, which is driven by
        // GameLoop.CoreGameUpdate → TickSetPieces. The DispatchByGameState
        // entry-point remains for symmetry with throw-in / free-kick.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        ResolveCornerKick(gameState);
    }

    // ===================================================================
    // TickGoalKick — per-tick wait during ST_GOAL_OUT_LEFT / _RIGHT
    // ===================================================================
    //
    // Same shape as TickCorner. The actual ball placement at the 6-yard line
    // lives in BallOutOfPlay.cs (ball.cpp:3699-3793). Per-tick rotation toward
    // the AI-picked release direction is in AiBrain.SetControlsDirection's
    // tail.
    //
    // From updatePlayers.cpp:16320-16344 (goal-out gameStates fall into the
    // same `l_update_turn_direction` path as corner / throw-in).
    public static void TickGoalKick(int a6TeamBase)
    {
        // updatePlayers.cpp:16311-16318 — if resultTimer != 0 return.
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        _ = a6TeamBase;
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        ResolveGoalKick(gameState);
    }

    // ===================================================================
    // ResolveCornerKick — spawn ball at corner flag, pick kicker, fire
    // ===================================================================
    //
    // Implements the corner-kick body. Coordinates from
    // external/swos-port/src/game/ball/ball.cpp:3619-3664 (the four corner
    // flags). foulXCoordinate / foulYCoordinate already hold one of:
    //   (86, 134)   — upper-left   (ST_CORNER_LEFT, cameraDir=2)
    //   (585, 134)  — upper-right  (ST_CORNER_RIGHT, cameraDir=6)
    //   (86, 764)   — lower-left   (ST_CORNER_RIGHT, cameraDir=2)
    //   (585, 764)  — lower-right  (ST_CORNER_LEFT, cameraDir=6)
    //
    // Pick kicker = nearest outfielder of the team that gets the corner
    // (lastTeamPlayedBeforeBreak per ball.cpp:4002-4003), then call
    // PlayerKickingBall (PlayerActions.cs:464 — itself a real port of
    // player.cpp:750) which writes ball.destX/Y/speed/deltaZ and clears the
    // out-of-play flags via gameStatePl == ST_STOPPED check at player.cpp:806.
    //
    // From ball.cpp:3623-3683 (spawn) + player.cpp:750 (kick).
    private static void ResolveCornerKick(short gameState)
    {
        // resultTimer guard mirrors updatePlayers.cpp:16311 — if a result
        // overlay (goal celebration / halftime) is showing, set-piece input
        // is suppressed.
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        // ball.cpp:3995-3997 wrote foulX/Y from D1/D2 at break_handled.
        short ballX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short ballY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);

        // ball.cpp:4002-4003 wrote lastTeamPlayedBeforeBreak = A6 (= team
        // taking the corner; A6 was set to the conceding team's opponent
        // back at ball.cpp:3568+).
        int kickerTeamBase = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (kickerTeamBase == 0)
            kickerTeamBase = TeamData.TopBase;  // defensive fallback

        // Place ball at the corner flag. Mirrors setBallPosition
        // (ball.cpp:4029-4042): speed=0, z=0, destX/Y = position, deltaZ=0.
        // Q16.16 fixed-point so whole-pixel coords shift left 16.
        BallSprite.Speed   = 0;
        BallSprite.X       = ballX << 16;
        BallSprite.Y       = ballY << 16;
        BallSprite.Z       = 0;
        BallSprite.DeltaX  = 0;
        BallSprite.DeltaY  = 0;
        BallSprite.DeltaZ  = 0;
        BallSprite.DestX   = ballX;
        BallSprite.DestY   = ballY;

        // Pick kicker — nearest outfielder of the taking team. We scan the
        // team's 11 slots, skip the goalkeeper (ordinal 1) so a regular
        // outfielder takes the corner, and pick whichever sprite has the
        // smallest squared distance to the ball spot.
        int kickerSpriteAddr = FindNearestOutfielderToPoint(kickerTeamBase, ballX, ballY);
        if (kickerSpriteAddr == 0)
            return;  // shouldn't happen — defensive guard

        // WALK-UP: kicker starts wherever they were (don't write x/y), and we
        // set destX/destY = ball + small back-offset so the kicker walks to a
        // spot just behind the ball relative to the kick direction. Per-tick
        // PlayerActions.UpdatePlayerSpeedAndFrameDelay → RecomputeSpriteDeltas
        // computes deltaX/Y from (destX/Y, x/y, speed); SpriteUpdate.MoveSprite
        // integrates the deltas. Seed first-tick speed with kPlayerSpeedsGameStopped[1]
        // so MoveSprite has something to chew on before the per-tick path
        // overwrites it.
        // cameraDirection / playerTurnFlags were already written by
        // ball.cpp:3999-4001 from D3/D4.
        short cameraDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
        short cdIdx = (short)(cameraDir & 7);
        short destX = (short)(ballX + kKickerWalkupOffsetX[cdIdx]);
        short destY = (short)(ballY + kKickerWalkupOffsetY[cdIdx]);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDestX, destX);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDestY, destY);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDirection, cameraDir);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffFullDirection, (short)(cameraDir * 32));
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffSpeed, kPlayerSpeedsGameStopped1);

        // team.controlledPlDirection mirrors the kicker's direction — used by
        // PlayerKickingBall (player.cpp:758) when picking the kick-table entry.
        Memory.WriteWord(kickerTeamBase + TeamData.OffControlledPlDirection, cameraDir);
        // team.controlledPlayer = our kicker so PlayerKickingBall reads the
        // right ballDestDelta base via [A6+TeamGeneralInfo.controlledPlayer].
        Memory.WriteDword(kickerTeamBase + TeamData.OffControlledPlayer, kickerSpriteAddr);

        // Update "had ball" bookkeeping mirroring the throw-in tail
        // (updatePlayers.cpp:6248-6304): lastTeamPlayed = taker's team,
        // lastPlayerPlayed = the kicker sprite, playerHadBall = 1.
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, kickerTeamBase);
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, kickerSpriteAddr);
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);

        // CEREMONY DEFER: instead of firing PlayerKickingBall immediately
        // (which made set pieces instant — see swos.h ST_WAITING_ON_PLAYER /
        // gameLoop.cpp:519-572), park the FSM in gameStatePl = 102. While in
        // this state the GameLoop.UpdateGameTimersAndCameraBreakMode block at
        // GameLoop.cs:374-419 accumulates stoppageTimerActive every tick;
        // human players retain control (InputControls.SetJoystickState routes
        // joystick fire to PlayerActions.PlayerKickingBall via the regular
        // controlledPlayer path), and AI teams auto-fire via the safety net
        // once stoppageTimerActive >= m_initalKickInterval (825 ticks @ 70Hz
        // ≈ 11.8 s) → Kickoff.PrepareForInitialKick.
        //
        // passingKickingPlayer is the dword pointer SWOS uses to identify
        // "who is taking this set piece" (TeamData.OffPassingKickingPlayer @
        // offset 104; see swos.asm:112185 and InputControls.cs:570). Writing
        // it here mirrors updatePlayers.cpp:6311 / Kickoff.cs:194.
        // Cite: swos.h GameState enum (ST_WAITING_ON_PLAYER = 102).
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_WAITING_ON_PLAYER);
        Memory.WriteDword(kickerTeamBase + TeamData.OffPassingKickingPlayer, kickerSpriteAddr);
        // NOTE: PlayerKickingBall(kickerTeamBase, kickerSpriteAddr) intentionally
        // not called here — defer to human input or m_initalKickInterval timeout.

        _ = gameState;  // unused beyond the gate above
    }

    // ===================================================================
    // ResolveGoalKick — spawn ball at 6-yard line, keeper takes the kick
    // ===================================================================
    //
    // Implements the goal-kick body. Coordinates from
    // external/swos-port/src/game/ball/ball.cpp:3699-3771:
    //   (276, 154) — upper-left   (ST_GOAL_OUT_RIGHT, cameraDir=4)
    //   (396, 154) — upper-right  (ST_GOAL_OUT_LEFT,  cameraDir=4)
    //   (276, 744) — lower-left   (ST_GOAL_OUT_LEFT,  cameraDir=0)
    //   (396, 744) — lower-right  (ST_GOAL_OUT_RIGHT, cameraDir=0)
    //
    // Kicker = goalkeeper of the team awarded the goal kick (the team
    // defending the goal the ball went out next to). lastTeamPlayedBeforeBreak
    // holds the conceding team (= team that gets the goal kick).
    //
    // From ball.cpp:3699-3793 (spawn) + player.cpp:750 (kick).
    private static void ResolveGoalKick(short gameState)
    {
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        short ballX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short ballY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);

        int kickerTeamBase = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (kickerTeamBase == 0)
            kickerTeamBase = TeamData.TopBase;

        // Set ball at the 6-yard line. Mirrors setBallPosition.
        BallSprite.Speed   = 0;
        BallSprite.X       = ballX << 16;
        BallSprite.Y       = ballY << 16;
        BallSprite.Z       = 0;
        BallSprite.DeltaX  = 0;
        BallSprite.DeltaY  = 0;
        BallSprite.DeltaZ  = 0;
        BallSprite.DestX   = ballX;
        BallSprite.DestY   = ballY;

        // Goalkeeper takes the goal kick. The keeper slot is always the first
        // slot of the team (slot 0 for top, slot 11 for bottom).
        int kickerSpriteAddr = FindKeeperSprite(kickerTeamBase);
        if (kickerSpriteAddr == 0)
            return;

        // WALK-UP — same shape as ResolveCornerKick: set destX/destY = ball +
        // back-offset, don't write x/y, seed first-tick speed.
        short cameraDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
        short cdIdx = (short)(cameraDir & 7);
        short destX = (short)(ballX + kKickerWalkupOffsetX[cdIdx]);
        short destY = (short)(ballY + kKickerWalkupOffsetY[cdIdx]);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDestX, destX);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDestY, destY);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDirection, cameraDir);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffFullDirection, (short)(cameraDir * 32));
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffSpeed, kPlayerSpeedsGameStopped1);

        Memory.WriteWord(kickerTeamBase + TeamData.OffControlledPlDirection, cameraDir);
        Memory.WriteDword(kickerTeamBase + TeamData.OffControlledPlayer, kickerSpriteAddr);

        Memory.WriteDword(Memory.Addr.lastTeamPlayed, kickerTeamBase);
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, kickerSpriteAddr);
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);

        // CEREMONY DEFER — same shape as ResolveCornerKick. Park in
        // ST_WAITING_ON_PLAYER (swos.h GameState enum @ 102) and tag the
        // keeper as passingKickingPlayer so InputControls' fire routing
        // recognises them; the gameLoop.cpp:519-572 safety net auto-fires
        // PrepareForInitialKick after m_initalKickInterval = 825 ticks.
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_WAITING_ON_PLAYER);
        Memory.WriteDword(kickerTeamBase + TeamData.OffPassingKickingPlayer, kickerSpriteAddr);
        // NOTE: PlayerKickingBall(kickerTeamBase, kickerSpriteAddr) intentionally
        // not called here — defer to human input or m_initalKickInterval timeout.

        _ = gameState;
    }

    // ===================================================================
    // ResolveThrowIn — spawn ball at touchline spot, pick thrower, release
    // ===================================================================
    //
    // Implements the throw-in body. Coordinates from
    // external/swos-port/src/game/ball/ball.cpp:3795-3973 — the BallOutOfPlay
    // port (BallOutOfPlay.cs:462-552) already wrote foulX/Y to one of:
    //   (81,  Y)   — left touchline   (cameraDir=2, varied Y per state)
    //   (590, Y)   — right touchline  (cameraDir=6, varied Y per state)
    //
    // Thrower = nearest outfielder of the team awarded the throw-in (the
    // opponent of the team that last touched the ball — already resolved
    // via lastTeamPlayedBeforeBreak at ball.cpp:4002-4003). The asm walks
    // a thrower to the touchline via the break-camera FSM in
    // gameLoop.cpp:1110-1846 then transitions the chosen sprite to
    // PL_THROW_IN (updatePlayers.cpp:8055-8067). Until that FSM lands we
    // collapse: place ball + thrower, release ball into play via
    // PlayerActions.PlayerKickingBall with the throw-in direction lifted
    // from AI_throwInDirections[gameState-15] (asm L16608-16638).
    //
    // The release direction is the 0..7 SWOS direction encoded as one of
    // 6 bytes in the table (one per ST_THROW_IN_* state). The asm rotates
    // the byte by 4 (swap nibbles) for top team so the same table covers
    // both sides; we mirror that.
    //
    // From ball.cpp:3795-3973 (spawn) + updatePlayers.cpp:6306-6363
    // (throw-in kick tail) + updatePlayers.cpp:16608-16638 (direction pick).
    private static void ResolveThrowIn(short gameState)
    {
        // resultTimer guard mirrors updatePlayers.cpp:16311 — if a result
        // overlay is up, set-piece input is suppressed.
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        // BallOutOfPlay wrote foulX/Y from D1/D2 at break_handled
        // (ball.cpp:3995-3997).
        short ballX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short ballY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);

        // ball.cpp:4002-4003 wrote lastTeamPlayedBeforeBreak = A6 (= team
        // taking the throw-in; A6 was set to lastTeamPlayed.opponentsTeam
        // back at ball.cpp:3796-3799).
        int throwerTeamBase = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (throwerTeamBase == 0)
            throwerTeamBase = TeamData.TopBase;  // defensive fallback

        // Place ball at the touchline spot. Mirrors setBallPosition
        // (ball.cpp:4029-4042). Throw-ins release at Z=12 in the asm
        // (updatePlayers.cpp:6010 `ball.z.whole = 12`); we'll set it after
        // PlayerKickingBall since that writes Z=0.
        BallSprite.Speed   = 0;
        BallSprite.X       = ballX << 16;
        BallSprite.Y       = ballY << 16;
        BallSprite.Z       = 0;
        BallSprite.DeltaX  = 0;
        BallSprite.DeltaY  = 0;
        BallSprite.DeltaZ  = 0;
        BallSprite.DestX   = ballX;
        BallSprite.DestY   = ballY;

        // Pick thrower — nearest outfielder of the team taking the throw.
        int throwerSpriteAddr = FindNearestOutfielderToPoint(throwerTeamBase, ballX, ballY);
        if (throwerSpriteAddr == 0)
            return;

        // Direction. updatePlayers.cpp:16608-16638 reads
        // AI_throwInDirections[gameState-15] and (for the top team) rotates
        // the byte by 4. The table at swos.asm:246201 is:
        //   db 3, 6, 12, 129, 192, 96, 0, 0
        // Each byte is a bitmap of allowed directions for one of 6 throw-in
        // states. The asm picks a direction by testing (1 << currentDir)
        // against the bitmap. We take the first allowed direction in the
        // bitmap as our release direction — equivalent to picking direction
        // 0 (N), then 1 (NE), ... until we find an allowed bit.
        int tableIdx = gameState - ST_THROW_IN_FORWARD_RIGHT;
        if (tableIdx < 0 || tableIdx > 5) tableIdx = 0;
        byte dirBitmap = Memory.ReadByte(Memory.Addr.AI_throwInDirections + tableIdx);
        // Top team: ror byte by 4 (swap nibbles).
        if (throwerTeamBase == TeamData.TopBase)
            dirBitmap = (byte)(((dirBitmap >> 4) & 0x0F) | ((dirBitmap & 0x0F) << 4));
        short releaseDir = 0;
        for (int d = 0; d < 8; d++)
        {
            if ((dirBitmap & (1 << d)) != 0)
            {
                releaseDir = (short)d;
                break;
            }
        }

        // WALK-UP — set destX/destY = ball + 3-px touchline offset (per
        // SetThrowInPlayerDestinationCoordinates at SetPieces.cs:97-124: thrower
        // sits 3 px IN from touchline, sharing ball.y). The 3-px direction is
        // determined by which half the ball is on: ball.x < 336 = left half so
        // thrower stands left of ball (subtract 3); otherwise right of ball
        // (add 3). Don't write x/y; let MoveSprite walk the thrower in.
        short throwOffsetX = (ballX < 336) ? (short)-3 : (short)+3;
        short destX = (short)(ballX + throwOffsetX);
        short destY = ballY;
        Memory.WriteWord(throwerSpriteAddr + PlayerSprite.OffDestX, destX);
        Memory.WriteWord(throwerSpriteAddr + PlayerSprite.OffDestY, destY);
        Memory.WriteWord(throwerSpriteAddr + PlayerSprite.OffDirection, releaseDir);
        Memory.WriteWord(throwerSpriteAddr + PlayerSprite.OffFullDirection, (short)(releaseDir * 32));
        Memory.WriteWord(throwerSpriteAddr + PlayerSprite.OffSpeed, kPlayerSpeedsGameStopped1);

        // team.controlledPlDirection mirrors thrower's direction — used by
        // PlayerKickingBall (player.cpp:758) for the kick-table entry.
        Memory.WriteWord(throwerTeamBase + TeamData.OffControlledPlDirection, releaseDir);
        Memory.WriteDword(throwerTeamBase + TeamData.OffControlledPlayer, throwerSpriteAddr);

        // updatePlayers.cpp:6306-6311 — release-as-kick bookkeeping.
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, throwerTeamBase);
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, throwerSpriteAddr);
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);

        // CEREMONY DEFER — same shape as ResolveCornerKick. Park in
        // ST_WAITING_ON_PLAYER (swos.h GameState enum @ 102) so the thrower
        // ceremony plays out: human teams retain control until they press
        // fire; AI teams auto-release after m_initalKickInterval (825 ticks)
        // via gameLoop.cpp:519-572. Releasing the ball loft (Z=12, DeltaZ=1)
        // happens at the actual kick site, not here.
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_WAITING_ON_PLAYER);
        Memory.WriteDword(throwerTeamBase + TeamData.OffPassingKickingPlayer, throwerSpriteAddr);
        // NOTE: PlayerKickingBall(throwerTeamBase, throwerSpriteAddr) intentionally
        // not called here — defer to human input or m_initalKickInterval timeout.

        _ = gameState;
    }

    // ===================================================================
    // ResolveFreeKick — spawn ball at foul spot, pick taker, release
    // ===================================================================
    //
    // Implements the foul / direct-free-kick auto-resolver. Same shape as
    // ResolveCornerKick: nearest outfielder of the awarded team kicks the
    // ball from foulXCoordinate / foulYCoordinate. The awarded team is in
    // lastTeamPlayedBeforeBreak.
    //
    // The real asm path runs the break-camera FSM in gameLoop.cpp:1110-1846
    // to walk the taker to the spot. Until ported, we collapse.
    //
    // From updatePlayers.cpp:16385-16448 (per-tick handler) +
    // ball.cpp:setBallPosition + player.cpp:750 (kick).
    private static void ResolveFreeKick(short gameState)
    {
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        short ballX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short ballY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);

        int kickerTeamBase = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (kickerTeamBase == 0)
            kickerTeamBase = TeamData.TopBase;

        // Place ball at the foul spot.
        BallSprite.Speed   = 0;
        BallSprite.X       = ballX << 16;
        BallSprite.Y       = ballY << 16;
        BallSprite.Z       = 0;
        BallSprite.DeltaX  = 0;
        BallSprite.DeltaY  = 0;
        BallSprite.DeltaZ  = 0;
        BallSprite.DestX   = ballX;
        BallSprite.DestY   = ballY;

        int kickerSpriteAddr = FindNearestOutfielderToPoint(kickerTeamBase, ballX, ballY);
        if (kickerSpriteAddr == 0)
            return;

        // Direction = cameraDirection (asm uses currentAllowedDirection from
        // controlledPlDirection but those default to facing the opponent's
        // goal during a foul). cameraDirection was already set by
        // BallOutOfPlay.cs:570 from D3v.
        short cameraDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
        if (cameraDir < 0 || cameraDir > 7) cameraDir = 0;

        // WALK-UP — same shape as ResolveCornerKick: set destX/destY = foul
        // spot + back-offset, don't write x/y, seed first-tick speed.
        short cdIdx = (short)(cameraDir & 7);
        short destX = (short)(ballX + kKickerWalkupOffsetX[cdIdx]);
        short destY = (short)(ballY + kKickerWalkupOffsetY[cdIdx]);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDestX, destX);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDestY, destY);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffDirection, cameraDir);
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffFullDirection, (short)(cameraDir * 32));
        Memory.WriteWord(kickerSpriteAddr + PlayerSprite.OffSpeed, kPlayerSpeedsGameStopped1);

        Memory.WriteWord(kickerTeamBase + TeamData.OffControlledPlDirection, cameraDir);
        Memory.WriteDword(kickerTeamBase + TeamData.OffControlledPlayer, kickerSpriteAddr);

        Memory.WriteDword(Memory.Addr.lastTeamPlayed, kickerTeamBase);
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, kickerSpriteAddr);
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);

        // CEREMONY DEFER — same shape as ResolveCornerKick. Park in
        // ST_WAITING_ON_PLAYER (swos.h GameState enum @ 102) so the kicker
        // walks up to the foul spot before kicking. Human input drives the
        // kick via InputControls.SetJoystickState; AI safety net at
        // gameLoop.cpp:519-572 fires PrepareForInitialKick after 825 ticks.
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_WAITING_ON_PLAYER);
        Memory.WriteDword(kickerTeamBase + TeamData.OffPassingKickingPlayer, kickerSpriteAddr);
        // NOTE: PlayerKickingBall(kickerTeamBase, kickerSpriteAddr) intentionally
        // not called here — defer to human input or m_initalKickInterval timeout.

        _ = gameState;
    }

    // Find the nearest outfielder of `teamBase` to (px, py). Returns 0 if
    // none found. Skips goalkeeper (ordinal 1) — a regular outfielder takes
    // corners in SWOS. Distance metric is squared-pixel, identical to the
    // ballDistance writer in updateBall (ball.cpp ballDistance updater).
    private static int FindNearestOutfielderToPoint(int teamBase, int px, int py)
    {
        int tableAddr = Memory.ReadSignedDword(teamBase + TeamData.OffPlayers);
        if (tableAddr == 0)
            return 0;

        int bestSprite = 0;
        long bestDistSq = long.MaxValue;
        // 11 sprites per team (slot 0 = keeper, 1..10 = outfielders).
        for (int slotInTeam = 1; slotInTeam < PlayerSprite.TeamSize; slotInTeam++)
        {
            int spriteAddr = Memory.ReadSignedDword(tableAddr + slotInTeam * 4);
            if (spriteAddr == 0)
                continue;
            // Skip sent-off / non-normal sprites — same gate as
            // FindClosestPlayerToBallFacing (AiHelpers.cs:413).
            short sentAway = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSentAway);
            if (sentAway != 0)
                continue;

            short x = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
            short y = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
            long dx = x - px;
            long dy = y - py;
            long distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestSprite = spriteAddr;
            }
        }
        return bestSprite;
    }

    // Find the keeper sprite of `teamBase`. Slot 0 of each team table.
    private static int FindKeeperSprite(int teamBase)
    {
        int tableAddr = Memory.ReadSignedDword(teamBase + TeamData.OffPlayers);
        if (tableAddr == 0)
            return 0;
        return Memory.ReadSignedDword(tableAddr);  // slot 0 = keeper
    }

    // ===================================================================
    // TickFreeKick — updatePlayers.cpp:16385-16448 (`l_no_throw_in` →
    //                `l_foul_or_free_kick`)
    // ===================================================================
    //
    // Per-tick handler for ST_FOUL / ST_FREE_KICK_LEFT1..ST_FREE_KICK_RIGHT3.
    // Reads the joystick direction D5 (kept here as the team's
    // controlledPlDirection, which is already populated by the input
    // pipeline), then writes team.currentAllowedDirection = ((D5+16) & 0xFF) >> 5.
    //
    // The transform packs the 0..7 joystick direction into a 3-bit nibble
    // biased by 16 (= half a direction-slot, so the 8 input angles map onto
    // the 8 SWOS direction enum values with mid-quantisation).
    //
    // From updatePlayers.cpp:16385-16448.
    public static void TickFreeKick(int a6TeamBase)
    {
        // updatePlayers.cpp:16311-16318 — if resultTimer != 0 return.
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        // 16426 — ax = D5 (joystick direction). swos-port leaves D5 set from
        // the AI/input pipeline above. We read team.controlledPlDirection
        // which holds the same value in our port.
        int esi = a6TeamBase;
        ushort ax = (ushort)Memory.ReadSignedWord(esi + TeamData.OffControlledPlDirection);
        ushort D0 = ax;

        // 16428-16433 — D0 += 16.
        D0 = (ushort)(D0 + 16);

        // 16434-16437 — D0 &= 0xFF.
        D0 = (ushort)(D0 & 0xFF);

        // 16438-16444 — D0 >>= 5.
        D0 = (ushort)(D0 >> 5);

        // 16445-16447 — team.currentAllowedDirection = D0.
        ax = D0;
        Memory.WriteWord(esi + TeamData.OffCurrentAllowedDirection, ax);
    }

    // ===================================================================
    // TickPenalty — updatePlayers.cpp:16859-16914 (`l_doing_penalties`)
    // ===================================================================
    //
    // Per-tick handler for ST_PENALTY (in-match) and ST_PENALTIES (shootout).
    // The asm body at 16859-16914 picks a random AI dive direction from
    // playerTurnFlags; that's the keeper-side update for either mode. For
    // ST_PENALTIES we ADDITIONALLY snap the shooter to the penalty spot and
    // park gameStatePl in ST_WAITING_ON_PLAYER (matching the ceremony
    // pattern used by ResolveCornerKick / ResolveFreeKick) — handled by
    // ResolvePenaltyShootout below, which is one-shot per pen (only fires
    // while gameStatePl is NOT yet ST_WAITING_ON_PLAYER).
    //
    // In:
    //   A5 -> controlled player sprite (the taker / keeper)  ↔ a5PlayerAddr
    //   A6 -> team general info                              ↔ a6TeamBase
    //
    // From updatePlayers.cpp:16859-16914.
    public static void TickPenalty(int a5PlayerAddr, int a6TeamBase)
    {
        // updatePlayers.cpp:16311-16318 — if resultTimer != 0 return.
        short rt = Memory.ReadSignedWord(Memory.Addr.resultTimer);
        if (rt != 0)
            return;

        // Shootout-only: walk-to-spot + park. Mirrors the ceremony pattern
        // used by ResolveCornerKick (SetPieces.cs:801) — ball + shooter snap
        // to (336, 187), gameStatePl → ST_WAITING_ON_PLAYER (102). Only fires
        // while gameStatePl is the initial ST_STOPPED set by NextPenalty
        // (game.cpp:981); becomes a no-op once parked.
        //
        // game.cpp:979-981 — foul coords = (336, 187), gameStatePl = 101.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameState == ST_PENALTIES)
        {
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp == ST_STOPPED)
                ResolvePenaltyShootout();
        }

        int A5 = a5PlayerAddr;
        int A6 = a6TeamBase;
        int esi;
        ushort ax;
        byte cl;
        bool zeroF;

        // 16860-16865 — D0 = AI_rand & 7.
        ax = (ushort)Memory.ReadSignedWord(Memory.Addr.AI_rand);
        ushort D0 = (ushort)(ax & 7);

        // 16866-16882 — test (1 << D0) against playerTurnFlags.
        cl = (byte)D0;
        ax = 1;
        if ((cl & 0x1F) != 0)
            ax = (ushort)(ax << (cl & 0x1F));
        {
            ushort turnFlagsW = (ushort)Memory.ReadSignedWord(Memory.Addr.playerTurnFlags);
            ushort testRes = (ushort)(turnFlagsW & ax);
            zeroF = testRes == 0;
        }
        if (zeroF)
            goto l_penalty_random_direction_disallowed;

        // 16886-16889 — team.currentAllowedDirection = D0; return.
        ax = D0;
        esi = A6;
        Memory.WriteWord(esi + TeamData.OffCurrentAllowedDirection, ax);
        return;

    l_penalty_random_direction_disallowed:
        // 16891-16899 — if sprite.direction == 0 return.
        esi = A5;
        ax = (ushort)Memory.ReadSignedWord(esi + PlayerSprite.OffDirection);
        if (ax == 0)
            return;

        // 16901-16912 — if sprite.direction == 4 return.
        ax = (ushort)Memory.ReadSignedWord(esi + PlayerSprite.OffDirection);
        if (ax == 4)
            return;

        // 16914 — falls into `l_apply_after_touch` (after-touch is handled
        // by AiBrain in our port). Leave the placeholder branch for clarity.
        // (No further action here — the original asm jumps to l_apply_after_touch
        // which mutates D2/D7 inside AI_SetControlsDirection; ownership is
        // AiBrain in our port.)
    }

    // ===================================================================
    // ResolvePenaltyShootout — walk shooter to penalty spot + park
    // ===================================================================
    //
    // One-shot per pen. Reproduces the asm walk-to-spot ceremony that the
    // break-camera FSM (gameLoop.cpp:1110-1846) drives during the shootout.
    // SWOS uses the same Mode1 -> Mode2 -> setBallPosition path it uses for
    // every set piece (gameLoop.cpp:1323-1340 writes the ball at
    // foulXCoordinate / foulYCoordinate once the camera has reached the
    // foul spot). That FSM is not yet ported — we collapse, identical to
    // ResolveCornerKick / ResolveFreeKick / ResolveGoalKick:
    //
    //   1. Pull ball + shooter to (foulX, foulY) = (336, 187) — already
    //      written by NextPenalty (GameTime.cs:1304-1305 / game.cpp:979-980).
    //   2. Snap the shooter sprite (penaltyShooterSprite) on top of the ball,
    //      facing north (cameraDirection = 0 set by NextPenalty at
    //      game.cpp:977).
    //   3. Park gameStatePl in ST_WAITING_ON_PLAYER (102) so the kick is
    //      driven by either:
    //        - human input → InputControls.SetJoystickState routes fire to
    //          PlayerActions.PlayerKickingBall (the same path the in-match
    //          set-pieces use; InputControls.cs:729-740 already handles the
    //          ST_PENALTIES + penaltyShooterSprite case)
    //        - AI safety net → gameLoop.cpp:519-572 / Kickoff.PrepareForInitial-
    //          Kick after m_initalKickInterval (825 ticks ≈ 11.8 s) elapses.
    //
    // Source: external/swos-port/src/game/game.cpp:942-1078 (per-pen FSM
    // init; ball/shooter coords + gameStatePl=101 are set there but the
    // walk-to-spot itself runs through the break-camera FSM at
    // gameLoop.cpp:1310-1341 which we collapse).
    private static void ResolvePenaltyShootout()
    {
        // game.cpp:979-980 — foul coords already written by NextPenalty.
        short ballX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short ballY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);

        // game.cpp:1040 — penaltyShooterSprite already written by NextPenalty.
        int shooterSprite = Memory.ReadSignedDword(Memory.Addr.penaltyShooterSprite);
        if (shooterSprite == 0)
            return;

        // Place ball at the penalty spot. Mirrors setBallPosition
        // (ball.cpp:4029-4042): speed=0, z=0, destX/Y = position.
        // Q16.16 fixed-point so whole-pixel coords shift left 16.
        BallSprite.Speed   = 0;
        BallSprite.X       = ballX << 16;
        BallSprite.Y       = ballY << 16;
        BallSprite.Z       = 0;
        BallSprite.DeltaX  = 0;
        BallSprite.DeltaY  = 0;
        BallSprite.DeltaZ  = 0;
        BallSprite.DestX   = ballX;
        BallSprite.DestY   = ballY;

        // Snap shooter onto the ball spot. cameraDirection was already set by
        // NextPenalty (game.cpp:977 = 0 = facing N — same direction the
        // shooter faces the keeper's goal).
        short cameraDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
        if (cameraDir < 0 || cameraDir > 7) cameraDir = 0;
        Memory.WriteWord(shooterSprite + PlayerSprite.OffX + 2, ballX);
        Memory.WriteWord(shooterSprite + PlayerSprite.OffY + 2, ballY);
        Memory.WriteWord(shooterSprite + PlayerSprite.OffDestX, ballX);
        Memory.WriteWord(shooterSprite + PlayerSprite.OffDestY, ballY);
        Memory.WriteWord(shooterSprite + PlayerSprite.OffDirection, cameraDir);
        Memory.WriteWord(shooterSprite + PlayerSprite.OffFullDirection, (short)(cameraDir * 32));

        // Identify the shooter's team via TeamData.OffPlayers spritesTable
        // lookup — the shooter is in either topTeamData.spritesTable or
        // bottomTeamData.spritesTable. NextPenalty resolved A6 = bottomTeam
        // unconditionally (game.cpp:983), and the shooter was pulled from
        // bottomTeamData.spritesTable (game.cpp:1031-1040). So shooter.team
        // == bottomTeamData; mirror that here.
        int shooterTeamBase = TeamData.BottomBase;

        // Wire the shooter as the team's controlled player so PlayerKickingBall
        // (player.cpp:750) reads the right ballDestDelta base via
        // [A6+TeamGeneralInfo.controlledPlayer]. Same shape as ResolveCornerKick.
        Memory.WriteWord(shooterTeamBase + TeamData.OffControlledPlDirection, cameraDir);
        Memory.WriteDword(shooterTeamBase + TeamData.OffControlledPlayer, shooterSprite);

        // updatePlayers.cpp:6306-6311 (mirrored across all set-piece resolves)
        // — "who had the ball" bookkeeping.
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, shooterTeamBase);
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, shooterSprite);
        Memory.WriteWord(Memory.Addr.playerHadBall, 1);

        // CEREMONY DEFER — same shape as ResolveCornerKick / ResolveFreeKick.
        // Park in ST_WAITING_ON_PLAYER (102) so the kicker animation plays;
        // human teams kick via InputControls.SetJoystickState fire routing,
        // AI safety-net auto-fires at gameLoop.cpp:519-572 after
        // m_initalKickInterval. passingKickingPlayer identifies the kicker.
        Memory.WriteWord(Memory.Addr.gameStatePl, ST_WAITING_ON_PLAYER);
        Memory.WriteDword(shooterTeamBase + TeamData.OffPassingKickingPlayer, shooterSprite);
        // NOTE: PlayerKickingBall(shooterTeamBase, shooterSprite) intentionally
        // not called here — defer to human input or m_initalKickInterval timeout.
    }

    // ===================================================================
    // AdvancePenaltiesTimer — inter-pen pause + NextPenalty trigger
    // ===================================================================
    //
    // Mechanical port of gameLoop.cpp:430-470 (the penalty branch of
    // updateGameTimersAndCameraBreakMode). While the shootout is active AND
    // we are NOT in ST_PENALTIES (i.e. the kick just resolved and the ball
    // has been cleared away by BallOutOfPlay / a save), bump the inter-pen
    // pause timer. Once it reaches m_penaltiesInterval (default 110 ticks ≈
    // 1.57 s @ 70Hz), call NextPenalty to spawn the next kicker.
    //
    // Original gate logic (gameLoop.cpp:430-447):
    //   if (playingPenalties == 0)       skip (no shootout active)
    //   if (gameState == 31)              skip (current pen still in progress)
    //   penaltiesTimer += 1
    //   if (penaltiesTimer == m_penaltiesInterval) nextPenalty()
    //
    // The asm is the COMPLETE FSM body — there's no "shooter walks to spot"
    // phase here. The walk-to-spot for a NEW pen happens once NextPenalty()
    // re-sets gameState=31 and ResolvePenaltyShootout (above) one-shots the
    // ball + shooter snap on the next tick.
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:430-470.
    public static void AdvancePenaltiesTimer()
    {
        // gameLoop.cpp:430-435 — if playingPenalties == 0 skip.
        short pp = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        if (pp == 0)
            return;

        // gameLoop.cpp:437-447 — if gameState == ST_PENALTIES skip (kick
        // still in progress; the per-tick TickPenalty above handles it).
        short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gs == ST_PENALTIES)
            return;

        // gameLoop.cpp:449-456 — penaltiesTimer += 1.
        short timer = Memory.ReadSignedWord(Memory.Addr.penaltiesTimer);
        timer = (short)(timer + 1);
        Memory.WriteWord(Memory.Addr.penaltiesTimer, timer);

        // gameLoop.cpp:457-467 — cmp penaltiesTimer, m_penaltiesInterval.
        // The original uses `jnz` so only an EXACT match fires — defensive
        // when m_penaltiesInterval is reduced mid-shootout (we'd otherwise
        // miss the trigger entirely). Match the asm semantics.
        short interval = Memory.ReadSignedWord(Memory.Addr.m_penaltiesInterval);
        if (timer != interval)
            return;

        // gameLoop.cpp:469 — call NextPenalty (decision + per-pen init).
        // GameTime.NextPenalty also re-zeros penaltiesTimer via game.cpp:991
        // (= NextPenalty's `mov penaltiesTimer, 0`).
        GameTime.NextPenalty();
    }

    // ===================================================================
    // Stubbed callees (callable from the throw-in handler when ported).
    // These match the swos-port names so the future port reads cleanly.
    // ===================================================================

    // updatePlayers.cpp:5793 — AI_SetControlsDirection. Owned by AiBrain in
    // our port. Forward to AiBrain.SetControlsDirection so the throw-in
    // handler picks AI-driven release direction.
    private static void AI_SetControlsDirection(int a6TeamBase)
    {
        AiBrain.SetControlsDirection(a6TeamBase);
    }

    // updatePlayers.cpp:SetPlayerAnimationTable — caller passes A0 = anim
    // table pointer; resets timers and derives frameIndicesTable from the
    // table indexed by (team, ordinal, direction). Forwards to the real port
    // in PlayerActions (swos.asm:104309).
    internal static void SetPlayerAnimationTable(int spriteAddr, int animTablePtr)
    {
        PlayerActions.SetPlayerAnimationTable(spriteAddr, animTablePtr);
    }

    // updatePlayers.cpp:DoPass — passes ball with low trajectory.
    // Real port lives in PlayerActions.DoPass (player.cpp:2560 — picks closest
    // non-controlled teammate in current direction, writes team.passToPlayerPtr +
    // passingBall/passingToPlayer flags, then computes ball.destX/destY for the
    // pass trajectory). Forward to it so set-piece passes share the same
    // mechanics as in-play passes from PlayerControlled.RunControlledBranch.
    // Source: external/swos-port/src/game/player.cpp:2560.
    internal static void DoPass(int teamBase, int passerSpriteAddr)
    {
        PlayerActions.DoPass(teamBase, passerSpriteAddr);
    }

    // updatePlayers.cpp:PlayerKickingBall — kicks ball with full power.
    // Real port lives in PlayerActions.PlayerKickingBall (player.cpp:750).
    internal static void PlayerKickingBall(int teamBase, int kickerSpriteAddr)
    {
        PlayerActions.PlayerKickingBall(teamBase, kickerSpriteAddr);
    }
}
