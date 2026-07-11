namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Header / no-ball-destination helpers ported from
// external/swos-port/src/game/updatePlayers/updatePlayers.cpp.
//
// Four functions land here, all mechanically translated from the asm-style
// reference:
//
//   playerAttemptingJumpHeader     — updatePlayers.cpp:15253
//   setPlayerWithNoBallDestination — updatePlayers.cpp:15380
//   attemptStaticHeader            — updatePlayers.cpp:15803
//   setStaticHeaderDirection       — updatePlayers.cpp:15873
//
// The first two mutate one player sprite (A1). The third additionally reads
// per-team state (A6) and the ball-quadrant globals + player x/y carried in
// D6/D7 (passed in by the caller, see updatePlayers.cpp:9911-9918 for the
// goalkeeper branch that consumes them).
//
// All numeric constants are paraphrased from the asm — no copyright-bearing
// expressions are reused. See the per-line citations.
//
// **Status**: mechanical port. Animation-table assignment falls through to
// the existing stub helper (renderer reads playerState directly). Tactics
// data backing is zero-init for now (g_tacticsTable points at a 19-slot
// pool of empty TeamTactics structs); the lookup returns the top-left
// quadrant centroid (98, 149) until tactics.cpp lands a loader.
public static class PlayerHeader
{
    // Game-state constants used by setPlayerWithNoBallDestination
    // (updatePlayers.cpp:15403-15431). Verified against ball.cpp:1782-1808 +
    // ball.cpp:3704-3769 which assigns these literals into gameState.
    private const short ST_GOAL_OUT_LEFT    = 1;
    private const short ST_GOAL_OUT_RIGHT   = 2;
    private const short ST_KEEPER_HOLDS_BALL = 3;

    // PlayerState constants — verified at updatePlayers.cpp:15263 / 15863.
    private const byte PL_STATIC_HEADING = 8;
    private const byte PL_JUMP_HEADING   = 9;

    // Tactics struct field offsets (swos.h:415-421, TeamTactics is 370 bytes).
    //   +0   char name[9]
    //   +9   PlayerPositions positions[10]   ← Tactics.playerPos
    //   +359 byte unkTable[10]
    //   +369 byte ballOutOfPlayTactics       ← Tactics.ballOutOfPlayTactics
    private const int TacticsOffPlayerPos          = 9;
    private const int TacticsOffBallOutOfPlay      = 369;

    // Animation-table addresses now resolve through the ported
    // AnimationTablesData block (Memory.Addr.k*AnimTableAddr).
    // SetPlayerAnimationTable (swos.asm:104309) writes the address into
    // Sprite.animationTable so the renderer can read frame indices.
    private static int kJumpHeaderAttemptAnimTable   => OpenSwos.SwosVm.Memory.Addr.kJumpHeaderAttemptAnimTableAddr;
    private static int kStaticHeaderAttemptAnimTable => OpenSwos.SwosVm.Memory.Addr.kStaticHeaderAttemptAnimTableAddr;

    // updatePlayers.cpp:15486 / 15592 — compares A6 against the absolute
    // address of topTeamData. In our Memory remap, top team's TeamData is at
    // TeamData.TopBase; the test is "is this the top team?".
    private static bool IsTopTeam(int teamBase) => teamBase == TeamData.TopBase;

    // ====================================================================
    // playerAttemptingJumpHeader — updatePlayers.cpp:15253
    // ====================================================================
    //
    // In:
    //   A1 -> player sprite (passed as `spriteAddr`)
    //   D0  = direction (passed as `direction`)
    //
    // Effect: configures the sprite to launch a jump-header attempt.
    //   - heading = 0
    //   - direction = D0
    //   - playerState = PL_JUMP_HEADING (9)
    //   - playerDownTimer = m_playerDownHeadingInterval (55 by default)
    //   - destX/destY = sprite.x.whole + kDefaultDestinations[direction*4]
    //                   sprite.y.whole + kDefaultDestinations[direction*4 + 2]
    //   - speed = kJumpHeaderSpeed (2048)
    //   - animation table = jumpHeaderAttemptAnimTable
    public static void PlayerAttemptingJumpHeader(int spriteAddr, int direction)
    {
        // 15255-15256 — writeMemory(esi+98, 2, 0). "heading = 0".
        Memory.WriteWord(spriteAddr + 98, 0);
        // 15257-15258 — Sprite.direction = direction.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDirection, direction);

        // 15259-15260 — SetPlayerAnimationTable(jumpHeaderAttemptAnimTable).
        SetPlayerAnimationTable(spriteAddr, kJumpHeaderAttemptAnimTable);

        // 15261-15263 — playerDownTimer = m_playerDownHeadingInterval;
        //               playerState     = PL_JUMP_HEADING.
        int downInterval = Memory.ReadSignedWord(Memory.Addr.m_playerDownHeadingInterval);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, downInterval);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, PL_JUMP_HEADING);

        // 15264-15277 — A5 = kDefaultDestinations + (direction << 2).
        int dstEntry = Memory.Addr.kDefaultDestinations + (direction << 2);

        // 15278-15296 — destX = (short)(*(word*)A5 + sprite.x.whole).
        short dx = Memory.ReadSignedWord(dstEntry);
        short playerXPx = PlayerSprite.XPixels(SlotFromAddr(spriteAddr));
        // 15297-15312 — destY = (short)(*(word*)(A5+2) + sprite.y.whole).
        short dy = Memory.ReadSignedWord(dstEntry + 2);
        short playerYPx = PlayerSprite.YPixels(SlotFromAddr(spriteAddr));
        // DEFENSIVE CLAMP — not in original asm (updatePlayers.cpp:15278-15312
        // writes dest = pos + kDefaultDestinations[dir] unclamped). The asm
        // relies on cseg_81793 being called every following tick to gate
        // motion at the pitch cushion. With our half-ported dispatcher we
        // cannot guarantee that re-entry for every sprite, so apply the
        // same [81..590, 129..769] band used by setPlayerWithNoBallDestination
        // (updatePlayers.cpp:15608-15666) as a fallback.
        short jhDestX = (short)(dx + playerXPx);
        short jhDestY = (short)(dy + playerYPx);
        if (jhDestX <  81) jhDestX =  81;
        if (jhDestX > 590) jhDestX = 590;
        if (jhDestY < 129) jhDestY = 129;
        if (jhDestY > 769) jhDestY = 769;
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, jhDestX);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, jhDestY);

        // 15313-15314 — sprite.speed = kJumpHeaderSpeed (2048).
        short speed = Memory.ReadSignedWord(Memory.Addr.kJumpHeaderSpeed);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, speed);
    }

    // ====================================================================
    // attemptStaticHeader — updatePlayers.cpp:15803
    // ====================================================================
    //
    // In:
    //   A1 -> player sprite (passed as `spriteAddr`)
    //   D0  = direction (passed as `direction`)
    //
    // Effect: configures the sprite to attempt a standing (in-place) header.
    //   - heading = 0
    //   - direction = D0
    //   - destX/destY = sprite.x.whole + kDefaultDestinations[direction*4]
    //                   sprite.y.whole + kDefaultDestinations[direction*4 + 2]
    //   - speed = kStaticHeaderPlayerSpeed (256)
    //   - animation table = staticHeaderAttemptAnimTable
    //   - playerState = PL_STATIC_HEADING (8)
    //   - playerDownTimer = 20
    public static void AttemptStaticHeader(int spriteAddr, int direction)
    {
        // 15805-15806 — heading = 0.
        Memory.WriteWord(spriteAddr + 98, 0);
        // 15807-15808 — Sprite.direction = direction.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDirection, direction);

        // 15809-15822 — A5 = kDefaultDestinations + (direction << 2).
        int dstEntry = Memory.Addr.kDefaultDestinations + (direction << 2);

        // 15823-15841 — destX = (short)(*(word*)A5 + sprite.x.whole).
        short dx = Memory.ReadSignedWord(dstEntry);
        short playerXPx = PlayerSprite.XPixels(SlotFromAddr(spriteAddr));
        // 15842-15857 — destY = (short)(*(word*)(A5+2) + sprite.y.whole).
        short dy = Memory.ReadSignedWord(dstEntry + 2);
        short playerYPx = PlayerSprite.YPixels(SlotFromAddr(spriteAddr));
        // DEFENSIVE CLAMP — see PlayerAttemptingJumpHeader for the same rationale
        // (updatePlayers.cpp:15823-15857 writes unclamped dest = pos +
        // kDefaultDestinations[dir]). Same band as setPlayerWithNoBallDestination
        // (updatePlayers.cpp:15608-15666).
        short shDestX = (short)(dx + playerXPx);
        short shDestY = (short)(dy + playerYPx);
        if (shDestX <  81) shDestX =  81;
        if (shDestX > 590) shDestX = 590;
        if (shDestY < 129) shDestY = 129;
        if (shDestY > 769) shDestY = 769;
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, shDestX);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, shDestY);

        // 15858-15859 — sprite.speed = kStaticHeaderPlayerSpeed (256).
        short speed = Memory.ReadSignedWord(Memory.Addr.kStaticHeaderPlayerSpeed);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffSpeed, speed);

        // 15860-15861 — SetPlayerAnimationTable(staticHeaderAttemptAnimTable).
        SetPlayerAnimationTable(spriteAddr, kStaticHeaderAttemptAnimTable);

        // 15862-15864 — playerState = PL_STATIC_HEADING (8); playerDownTimer = 20.
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, PL_STATIC_HEADING);
        Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerDownTimer, 20);
    }

    // ====================================================================
    // setStaticHeaderDirection — updatePlayers.cpp:15873
    // ====================================================================
    //
    // In:
    //   A1 -> player sprite (passed as `spriteAddr`)
    //   A6 -> team data (passed as `teamBase`)
    //
    // Effect: per-tick fine-rotation of a player currently completing a
    // static-header animation. While `playerDownTimer <= 18` (late phase of
    // the animation, since attemptStaticHeader seeds the timer at 20 and it
    // ticks down), the function compares `team.currentAllowedDirection`
    // against `sprite.direction` and nudges the player ±1 quantised
    // direction step on the short side, then renormalises with `& 7`.
    //
    // Guards (any one returns early — no-op):
    //   - team.currentAllowedDirection < 0      (no spin direction set)
    //   - sprite.playerDownTimer > 18            (still in early/wind-up phase;
    //                                             rotation only fires once the
    //                                             timer has counted down past
    //                                             18 — see L15893 `cmp 18 / ja`)
    //   - sprite.animationTable != staticHeaderAttemptAnimTable
    //                                            (only rotate while in the
    //                                             attempt animation, not after
    //                                             switching to hit/exit anim)
    //   - allowedDir == sprite.direction         (already aligned)
    //   - (allowedDir - sprite.direction) & 7 == 4 (180° — can't pick a side)
    //
    // Turn direction is "shortest way":
    //   diff & 7 in {1, 2, 3} → direction += 1  (CW, l_turn_right)
    //   diff & 7 in {5, 6, 7} → direction -= 1  (CCW, fall-through)
    //
    // Note on the L15881 `js return` branch: the cmp uses `or ax, ax` then
    // `js` so negative `currentAllowedDirection` (sentinel = -1, set when
    // no spin is allowed) bails. We mirror this with `< 0`.
    public static void SetStaticHeaderDirection(int spriteAddr, int teamBase)
    {
        // 15876-15882 — D0 = team.currentAllowedDirection; if signed-negative return.
        short curAllowedDir = Memory.ReadSignedWord(teamBase + TeamData.OffCurrentAllowedDirection);
        if (curAllowedDir < 0) return;

        // 15884-15895 — if sprite.playerDownTimer (byte) > 18, return.
        // sbyte cast to match `cmp [esi+13], 18` + `ja` (unsigned above) of
        // a byte — but playerDownTimer holds [0..55] so unsigned/signed agree
        // in this range.
        int downTimer = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerDownTimer) & 0xFF;
        if (downTimer > 18) return;

        // 15897-15907 — if sprite.animationTable != staticHeaderAttemptAnimTable, return.
        int animTable = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffAnimTablePtr);
        if (animTable != kStaticHeaderAttemptAnimTable) return;

        // 15909-15920 — D0 -= sprite.direction; if zero return (already aligned).
        short pDir = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDirection);
        int diff = (short)(curAllowedDir - pDir);
        if (diff == 0) return;

        // 15922-15935 — D0 &= 7; if D0 == 4 return (can't pick side for 180°).
        diff &= 7;
        if (diff == 4) return;

        // 15937-15962 — jb l_turn_right (carry = D0 < 4, unsigned).
        //   D0 ∈ {1, 2, 3}  → direction += 1
        //   D0 ∈ {5, 6, 7}  → direction -= 1
        if (diff < 4)
        {
            // l_turn_right (15953-15962) — `add [esi+Sprite.direction], 1`.
            pDir = (short)(pDir + 1);
        }
        else
        {
            // 15940-15950 — `sub [esi+Sprite.direction], 1`.
            pDir = (short)(pDir - 1);
        }

        // 15964-15974 — direction &= 7 (renormalise to 0..7).
        pDir = (short)(pDir & 7);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDirection, pDir);
    }

    // ====================================================================
    // setPlayerWithNoBallDestination — updatePlayers.cpp:15380
    // ====================================================================
    //
    // In:
    //   A1 -> player sprite (passed as `spriteAddr`)
    //   A6 -> team data (passed as `teamBase`)
    //   D6  = BALL.x.whole (or foulXCoordinate during set-pieces)
    //   D7  = BALL.y.whole (or foulYCoordinate during set-pieces)
    //
    // The caller computes D6/D7 — see updatePlayers.cpp:9234-9238 for the
    // ST_GAME_IN_PROGRESS path (reads BALL sprite x/y from A2) and 9247-9249
    // for the set-piece path (foulXY). The goalkeeper branch needs these as
    // the tracked point projected onto the goal mouth — passing the keeper's
    // own position causes Y to overflow when the keeper sits outside the
    // playable area [129..769] (e.g. starting at Y=80 above the goal line).
    //
    // Effect: sets the sprite's destX/destY based on tactical positioning.
    //   Outfielders consult Tactics.playerPos[playerOrdinal-2][ballQuadrantIndex]
    //   to find the desired pitch quadrant nibble (top 4 bits = X quadrant
    //   index, low 4 bits = Y quadrant index), then maps each nibble through
    //   playerX/YQuadrantsCoordinates to get a centroid in pitch pixels. For
    //   the BOTTOM team the index and nibble byte are mirrored
    //   (D1 = 34 - ballQuadrantIndex; byte = 0xEF - playerPos[...]) to keep
    //   tactics symmetric (updatePlayers.cpp:15484-15538). The destination is
    //   then clamped to the playable pitch area [81..590, 129..769].
    //
    //   Goalkeepers (playerOrdinal == 1, i.e. (ordinal - 2) < 0 → js branch)
    //   take the @@goalkeeper path: keeper's destination tracks the ball x/y
    //   relative to the goal mouth using a fixed-point ratio across the
    //   pitch width / depth.
    public static void SetPlayerWithNoBallDestination(int spriteAddr, int teamBase,
                                                      short ballXPx, short ballYPx)
    {
        bool topTeam = IsTopTeam(teamBase);

        // 15382-15393 — A0 = g_tacticsTable + (team.tactics << 2);
        //               A0 = *(dword*)A0;  (deref to TeamTactics ptr)
        int tacticsIdx = Memory.ReadSignedWord(teamBase + TeamData.OffTactics);
        int tableEntry = Memory.Addr.g_tacticsTable + (tacticsIdx << 2);
        int a0_tacticsPtr = Memory.ReadSignedDword(tableEntry);

        // 15394-15431 — if gameState in {ST_KEEPER_HOLDS_BALL, ST_GOAL_OUT_LEFT,
        //               ST_GOAL_OUT_RIGHT} → override tactics with
        //               ballOutOfPlayTactics indirection.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        bool ballOutOfPlay = (gameState == ST_KEEPER_HOLDS_BALL)
                          || (gameState == ST_GOAL_OUT_LEFT)
                          || (gameState == ST_GOAL_OUT_RIGHT);

        if (ballOutOfPlay)
        {
            // 15433-15446 — read signed byte ballOutOfPlayTactics, sign-extend
            // (cbw → ah = (al<0) ? -1 : 0), then index g_tacticsTable << 2.
            sbyte oop = (sbyte)Memory.ReadByte(a0_tacticsPtr + TacticsOffBallOutOfPlay);
            int oopIdx = oop;  // sign-extends through int
            int oopEntry = Memory.Addr.g_tacticsTable + (oopIdx << 2);
            a0_tacticsPtr = Memory.ReadSignedDword(oopEntry);
        }

        // 15449-15456 — A0 = tacticsPtr + 9  (now points at Tactics.playerPos).
        int playerPosBase = a0_tacticsPtr + TacticsOffPlayerPos;

        // 15457-15471 — D0 = playerOrdinal - 2.  If negative → goalkeeper branch.
        int playerOrdinal = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
        int d0_outfieldIdx = playerOrdinal - 2;

        if (d0_outfieldIdx < 0)
        {
            // Goalkeeper branch — line 15676+.
            HandleGoalkeeperBranch(spriteAddr, topTeam, ballXPx, ballYPx);
            return;
        }

        // 15473-15479 — D0 = (playerOrdinal - 2) * 35   (size of PlayerPositions).
        int posRowOff = d0_outfieldIdx * 35;

        // 15480-15483 — D3 = playerXQuadrantOffset; D4 = playerYQuadrantOffset.
        short d3 = Memory.ReadSignedWord(Memory.Addr.playerXQuadrantOffset);
        short d4 = Memory.ReadSignedWord(Memory.Addr.playerYQuadrantOffset);

        // 15484-15511 — branch on "is this the top team?"
        // Asm: `cmp A6, offset topTeamData / jnz @@right_team_invert` — the
        // fall-through (A6 == topTeamData, i.e. TOP team) is the STRAIGHT read:
        //   D0 += ballQuadrantIndex; D1 = playerPos[D0].
        // @@right_team_invert (BOTTOM team) mirrors:
        //   D1 = 34 - ballQuadrantIndex; D0 += D1; D1 = 0EFh - playerPos[D0].
        // (2026-07-03 task #161 fix: these two branches were previously
        // swapped, which put each team on the OTHER side's tactics quadrant —
        // the "teams stand in each other's break positions" symptom.)
        short ballQuad = Memory.ReadSignedWord(Memory.Addr.ballQuadrantIndex);

        int posIndex;
        int d1Byte;
        if (topTeam)
        {
            // 15496-15511 — top team straight read.
            posIndex = posRowOff + ballQuad;
            d1Byte = Memory.ReadByte(playerPosBase + posIndex);
        }
        else
        {
            // 15513-15538 — @@right_team_invert (bottom team) mirror:
            //   D1 = 34 - ballQuadrantIndex, posIndex = D0 + D1,
            //   D1 = 0xEF - playerPos[posIndex].
            int mirror = 34 - ballQuad;
            posIndex = posRowOff + mirror;
            int posByte = Memory.ReadByte(playerPosBase + posIndex);
            d1Byte = (byte)(239 - posByte);  // 0EFh = 239
        }

        // 15540-15554 — d2 = d1 & 0x0F   (Y quadrant nibble)
        //                d1 = (d1 >> 4) & 0x0F  (X quadrant nibble)
        int d2 = d1Byte & 0x0F;
        int d1 = (d1Byte >> 4) & 0x0F;

        // 15555-15568 — d3 += playerXQuadrantsCoordinates[d1 * 2].
        short xCoord = Memory.ReadSignedWord(Memory.Addr.playerXQuadrantsCoordinates + (d1 << 1));
        d3 = (short)(d3 + xCoord);

        // 15569-15588 — d4 += playerYQuadrantCoordinates[d2 * 2];
        //               d3 -= 4.
        short yCoord = Memory.ReadSignedWord(Memory.Addr.playerYQuadrantCoordinates + (d2 << 1));
        d4 = (short)(d4 + yCoord);
        d3 = (short)(d3 - 4);

        // 15589-15606 — if NOT topTeam → d3 += 8.
        if (!topTeam) d3 = (short)(d3 + 8);

        // 15608-15621 — clip d3 lower bound to 81.
        if (d3 < 81) d3 = 81;

        // 15623-15636 — clip d3 upper bound to 590.
        if (d3 > 590) d3 = 590;

        // 15638-15651 — clip d4 lower bound to 129.
        if (d4 < 129) d4 = 129;

        // 15653-15666 — clip d4 upper bound to 769.
        if (d4 > 769) d4 = 769;

        // 15668-15673 — sprite.destX = d3; sprite.destY = d4.
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, d3);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, d4);
        // 15674 — retn.
    }

    // --------------------------------------------------------------------
    // Goalkeeper branch of setPlayerWithNoBallDestination — line 15676.
    //
    // Inputs:
    //   spriteAddr  — A1 keeper sprite
    //   topTeam     — A6 vs topTeamData test
    //   d6Ball      — D6, ball.x.whole at call site (updatePlayers.cpp:9235)
    //   d7Ball      — D7, ball.y.whole at call site (updatePlayers.cpp:9237)
    //
    // Output: writes sprite.destX / destY.
    //
    // The keeper's destination is the ball's position PROJECTED onto the
    // goal-mouth band. The X projection maps ball.x in [81..591] to keeper X
    // in [285..387]. The Y projection maps ball.y in [129..769] to keeper Y
    // in [135..161] (top team) or [737..763] (bottom team).
    //
    // X dest formula:
    //   D2 = 285; D3 = 387                     ; goal-mouth lower / upper X
    //   D0 = d6Ball - 81                       ; offset into pitch width
    //   D1 = (D3 - D2) + 1 = 103
    //   D0 = (D0 * D1) / 510                   ; ratio of pitch width
    //   destX = D0 + D2
    //
    // Y dest formula:
    //   D2/D3 = top-team:    135 / 161         ; upper goal mouth Y
    //   D2/D3 = bottom-team: 737 / 763         ; lower goal mouth Y
    //   D0 = d7Ball - 129                      ; offset into pitch height
    //   D1 = (D3 - D2) + 1 = 27
    //   D0 = (D0 * D1) / 641                   ; ratio of pitch height
    //   destY = D0 + D2
    // --------------------------------------------------------------------
    private static void HandleGoalkeeperBranch(int spriteAddr, bool topTeam,
                                               short d6Ball, short d7Ball)
    {
        // --- X axis (lines 15677-15725) ---
        short d2 = 285;
        short d3 = 387;
        // d0 = d6 - 81
        int d0 = d6Ball - 81;
        // d1 = (d3 - d2) + 1
        int d1 = (d3 - d2) + 1;
        // d0 = (d0 * d1) / 510   (unsigned div per asm `div bx` after `mul`)
        // d0 in [-something..+something]; mul/div in asm is signed-into-word.
        // We mirror the 16-bit mul + 16-bit div behaviour.
        ushort axMul = unchecked((ushort)((short)d0 * (short)d1));
        ushort dxMul = unchecked((ushort)(((short)d0 * (short)d1) >> 16));
        uint dividend = ((uint)dxMul << 16) | axMul;
        ushort quot = (ushort)(dividend / 510u);
        short d0Result = (short)quot;
        // d0 += d2
        short destX = (short)(d0Result + d2);
        // destX → sprite.destX
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, destX);

        // --- Y axis (lines 15726-15794) ---
        // Default = top-team values (135, 161); bottom-team override = (737, 763).
        // Asm: `if (A6 == topTeamData) goto l_calc_y_goalie_dest;`
        // i.e. when topTeam==true → keep (135, 161). When bottom team → use
        // (737, 763). NOTE: the file COMMENT at line 15795 places the goalmouth
        // at the OPPOSITE end vs. the team; this is intentional — the keeper
        // defends his own goal which is at the far end relative to the team's
        // possession orientation. Asm preserves the literal values; we do too.
        d2 = 135;
        d3 = 161;
        if (!topTeam)
        {
            d2 = 737;
            d3 = 763;
        }

        // d0 = d7 - 129
        d0 = d7Ball - 129;
        // d1 = (d3 - d2) + 1
        d1 = (d3 - d2) + 1;
        // d0 = (d0 * d1) / 641
        axMul = unchecked((ushort)((short)d0 * (short)d1));
        dxMul = unchecked((ushort)(((short)d0 * (short)d1) >> 16));
        dividend = ((uint)dxMul << 16) | axMul;
        quot = (ushort)(dividend / 641u);
        d0Result = (short)quot;
        // d0 += d2
        short destY = (short)(d0Result + d2);
        // destY → sprite.destY
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, destY);
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    // Convert an absolute sprite address back into a slot index. PlayerSprite
    // accessors operate on slot indices; this round-trip is needed because
    // the asm-style API hands us A1 (an absolute byte address).
    private static int SlotFromAddr(int spriteAddr)
    {
        return (spriteAddr - PlayerSprite.SpritePoolBase) / PlayerSprite.SlotStride;
    }

    // updatePlayers.cpp:15260 / 15861 — SetPlayerAnimationTable hook.
    // Forwards to the real port in PlayerActions (swos.asm:104309).
    private static void SetPlayerAnimationTable(int spriteAddr, int animTablePtr)
    {
        PlayerActions.SetPlayerAnimationTable(spriteAddr, animTablePtr);
    }
}
