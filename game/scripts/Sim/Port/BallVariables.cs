namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Per-tick ball world-state utilities, ported from
// external/swos-port/src/game/updatePlayers/updatePlayers.cpp.
//
// Both functions are called by `updatePlayers` ONCE per tick (per outfielder
// "this player last played" miss branch — see updatePlayers.cpp:1193-1194).
// They populate global g_memByte slots that downstream AI / chase / shot
// decisions read.
//
// Mechanical ports — preserve goto, magic numbers, control flow exactly.
// The original is asm-translated via swos-port's `D1..D7`, `A1/A2/A6`, and
// `flags.{overflow,carry,sign,zero}` shims. We collapse the flag math down to
// idiomatic signed-int compares (semantics match for int32_t arithmetic).
//
// **Inputs** (passed explicitly — original used machine registers):
//   - aPlayerSprite (A1): absolute Memory address of the player sprite.
//   - aBallSprite   (A2): absolute Memory address of the ball sprite
//                          (== BallSprite.Base in our setup).
//   - aTeamData     (A6): absolute Memory address of the player's TeamData
//                          (TeamData.TopBase or TeamData.BottomBase).
//
// **Outputs**: writes to Memory.Addr.ballDefensive{X,Y,Z}, ballNotHigh{X,Y,Z},
// strikeDestX, ballStrikeY, ballStrikeZ, ballNextGroundX, ballNextGroundY,
// ballNextGroundZDead.
public static class BallVariables
{
    // ====================================================================
    // updateBallVariables — updatePlayers.cpp:11198-12486
    // ====================================================================
    //
    // Predicts ball position when it intersects player Y (for top team) or
    // when it crosses 135-pixel / 129-pixel Y bands. Simulates motion in
    // Q16.16 with gravity loop until the predicted Y reaches the target,
    // then writes ballDefensive{X,Y,Z} (whole-pixel snapshot at intersection)
    // and ballNotHigh{X,Y,Z} (snapshot when Z would drop further still).
    //
    // The original is huge because every branch (going-up / going-down /
    // going-left / going-right / not-moving) does the same skeleton (gravity
    // loop + write defensive + write notHigh) over different axes / clamps.
    // We preserve the skeleton 1:1 — fidelity over compactness.
    public static void UpdateBallVariables(int aPlayerSprite, int aBallSprite, int aTeamData)
    {
        int A1 = aPlayerSprite;
        int A2 = aBallSprite;
        int A6 = aTeamData;
        int esi;

        // updatePlayers.cpp:11205-11219 — load ball pos/delta + gravity.
        esi = A2;
        int D1 = Memory.ReadSignedDword(esi + 30);   // ball.x  Q16.16
        int D2 = Memory.ReadSignedDword(esi + 34);   // ball.y  Q16.16
        int D3 = Memory.ReadSignedDword(esi + 38);   // ball.z  Q16.16
        int D4 = Memory.ReadSignedDword(esi + 46);   // deltaX  Q16.16
        int D5 = Memory.ReadSignedDword(esi + 50);   // deltaY  Q16.16
        int D6 = Memory.ReadSignedDword(esi + 54);   // deltaZ  Q16.16
        int D7 = Memory.ReadSignedDword(Memory.Addr.kGravityConstant);  // gravity (dword)

        // updatePlayers.cpp:11220-11268 — branch dispatch on delta magnitude.
        // cmp D5,-10000h / jl ball_going_up
        if (D5 < -65536) goto l_ball_going_up;
        // cmp D5,10000h / jg ball_going_down
        if (D5 > 65536) goto l_ball_going_down;
        // cmp D4,-10000h / jl ball_going_left
        if (D4 < -65536) goto l_ball_going_left;
        // cmp D4,10000h / jg ball_going_right
        if (D4 > 65536) goto l_ball_going_right;

        goto l_ball_not_moving;

    l_ball_going_up:
        // updatePlayers.cpp:11270-11317 — only continue if team is topTeamData.
        // cmp A6, offset topTeamData / jnz @@ball_not_moving
        if (A6 != TeamData.TopBase) goto l_ball_not_moving;

        // sub D2, [player.y]
        esi = A1;
        D2 = D2 - Memory.ReadSignedDword(esi + 34);
        // jge set_ball_y_to_0
        if (D2 >= 0) goto l_set_ball_y_to_0;

        // Snapshot ball whole-pixel x/y/z into ballDefensive*.
        esi = A2;
        Memory.WriteWord(Memory.Addr.ballDefensiveX, Memory.ReadWord(esi + 32));
        Memory.WriteWord(Memory.Addr.ballDefensiveY, Memory.ReadWord(esi + 36));
        Memory.WriteWord(Memory.Addr.ballDefensiveZ, Memory.ReadWord(esi + 40));

        // D2 += [player.y]  (restore D2 to be the upper-target Y in fixed-point).
        esi = A1;
        D2 = D2 + Memory.ReadSignedDword(esi + 34);
        goto l_ball_above_player_check;

    l_set_ball_y_to_0:
        // updatePlayers.cpp:11319-11353 — gravity loop body (decrement, integrate,
        // re-test against player Y).
        D6 = D6 - D7;
        D3 = D3 + D6;
        D1 = D1 + D4;
        D2 = D2 + D5;
        if (D2 >= 0) goto l_set_ball_y_to_0;

        // updatePlayers.cpp:11355-11442 — undo last step, swap halves, write defensives.
        D1 = D1 - D4;
        D2 = D2 - D5;
        D3 = D3 - D6;
        esi = A1;
        D2 = D2 + Memory.ReadSignedDword(esi + 34);

        // xchg high/low words of D1/D2/D3 (move high-word integer part to low word).
        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.ballDefensiveX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballDefensiveY, (short)D2);
        // Clamp Z low-word to 0 if negative (or ax,ax / jns / mov 0).
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;   // word ptr D3 = 0
        }
        Memory.WriteWord(Memory.Addr.ballDefensiveZ, (short)D3);

        // Swap halves back.
        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

    l_ball_above_player_check:
        // updatePlayers.cpp:11444-11474 — sub D2, 870000h ; jge @@ball_above_135
        D2 = D2 - 8847360;
        if (D2 >= 0) goto l_ball_above_135;

        // Below 135-band: copy defensive snapshot into notHigh.
        Memory.WriteWord(Memory.Addr.ballNotHighX, Memory.ReadWord(Memory.Addr.ballDefensiveX));
        Memory.WriteWord(Memory.Addr.ballNotHighY, Memory.ReadWord(Memory.Addr.ballDefensiveY));
        Memory.WriteWord(Memory.Addr.ballNotHighZ, Memory.ReadWord(Memory.Addr.ballDefensiveZ));
        D2 = D2 + 8847360;
        goto cseg_779F5;

    l_ball_above_135:
        // updatePlayers.cpp:11476-11586 — gravity loop, target Y == 135 (Q16.16).
        D6 = D6 - D7;
        D3 = D3 + D6;
        D1 = D1 + D4;
        D2 = D2 + D5;
        if (D2 >= 0) goto l_ball_above_135;

        D1 = D1 - D4;
        D3 = D3 - D6;
        D2 = 8847360;

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.ballNotHighX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballNotHighY, (short)D2);
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;
        }
        Memory.WriteWord(Memory.Addr.ballNotHighZ, (short)D3);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

    cseg_779F5:
        // updatePlayers.cpp:11587-11614 — sub D2, 810000h ; jge ball_between_129_135
        D2 = D2 - 8454144;
        if (D2 >= 0) goto l_ball_between_129_135;

        Memory.WriteWord(Memory.Addr.strikeDestX, 0);
        Memory.WriteWord(Memory.Addr.ballStrikeY, 0);
        Memory.WriteWord(Memory.Addr.ballStrikeZ, 0);
        D2 = D2 + 8454144;
        goto l_out;

    l_ball_between_129_135:
        // updatePlayers.cpp:11616-11726 — gravity loop, target Y == 129 (Q16.16).
        D6 = D6 - D7;
        D3 = D3 + D6;
        D1 = D1 + D4;
        D2 = D2 + D5;
        if (D2 >= 0) goto l_ball_between_129_135;

        D1 = D1 - D4;
        D3 = D3 - D6;
        D2 = 8454144;

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.strikeDestX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballStrikeY, (short)D2);
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;
        }
        Memory.WriteWord(Memory.Addr.ballStrikeZ, (short)D3);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);
        goto l_out;

    l_ball_going_down:
        // updatePlayers.cpp:11728-12184 — symmetric to ball_going_up but target is
        // bottomTeamData and target Y bands are at 50003968 / 50397184 (Q16.16).
        if (A6 != TeamData.BottomBase) goto l_ball_not_moving;

        esi = A1;
        D2 = D2 - Memory.ReadSignedDword(esi + 34);
        if (D2 < 0) goto l_ball_above_player;

        esi = A2;
        Memory.WriteWord(Memory.Addr.ballDefensiveX, Memory.ReadWord(esi + 32));
        Memory.WriteWord(Memory.Addr.ballDefensiveY, Memory.ReadWord(esi + 36));
        Memory.WriteWord(Memory.Addr.ballDefensiveZ, Memory.ReadWord(esi + 40));

        esi = A1;
        D2 = D2 + Memory.ReadSignedDword(esi + 34);
        goto cseg_77C95;

    l_ball_above_player:
        // updatePlayers.cpp:11777-11900 — gravity loop, target = player.y.
        D6 = D6 - D7;
        D3 = D3 + D6;
        D1 = D1 + D4;
        D2 = D2 + D5;
        if (D2 < 0) goto l_ball_above_player;

        D1 = D1 - D4;
        D2 = D2 - D5;
        D3 = D3 - D6;
        esi = A1;
        D2 = D2 + Memory.ReadSignedDword(esi + 34);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.ballDefensiveX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballDefensiveY, (short)D2);
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;
        }
        Memory.WriteWord(Memory.Addr.ballDefensiveZ, (short)D3);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

    cseg_77C95:
        // updatePlayers.cpp:11902-11932 — sub D2, 2FB0000h ; jl cseg_77CDD
        D2 = D2 - 50003968;
        if (D2 < 0) goto cseg_77CDD;

        Memory.WriteWord(Memory.Addr.ballNotHighX, Memory.ReadWord(Memory.Addr.ballDefensiveX));
        Memory.WriteWord(Memory.Addr.ballNotHighY, Memory.ReadWord(Memory.Addr.ballDefensiveY));
        Memory.WriteWord(Memory.Addr.ballNotHighZ, Memory.ReadWord(Memory.Addr.ballDefensiveZ));
        D2 = D2 + 50003968;
        goto cseg_77DD5;

    cseg_77CDD:
        // updatePlayers.cpp:11934-12043 — gravity loop, target Y == 50003968 (Q16.16).
        D6 = D6 - D7;
        D3 = D3 + D6;
        D1 = D1 + D4;
        D2 = D2 + D5;
        if (D2 < 0) goto cseg_77CDD;

        D1 = D1 - D4;
        D3 = D3 - D6;
        D2 = 50003968;

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.ballNotHighX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballNotHighY, (short)D2);
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;
        }
        Memory.WriteWord(Memory.Addr.ballNotHighZ, (short)D3);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

    cseg_77DD5:
        // updatePlayers.cpp:12045-12072 — sub D2, 3010000h ; jl cseg_77E0B
        D2 = D2 - 50397184;
        if (D2 < 0) goto cseg_77E0B;

        Memory.WriteWord(Memory.Addr.strikeDestX, 0);
        Memory.WriteWord(Memory.Addr.ballStrikeY, 0);
        Memory.WriteWord(Memory.Addr.ballStrikeZ, 0);
        D2 = D2 + 50397184;
        goto l_out;

    cseg_77E0B:
        // updatePlayers.cpp:12074-12184 — gravity loop, target Y == 50397184 (Q16.16).
        D6 = D6 - D7;
        D3 = D3 + D6;
        D1 = D1 + D4;
        D2 = D2 + D5;
        if (D2 < 0) goto cseg_77E0B;

        D1 = D1 - D4;
        D3 = D3 - D6;
        D2 = 50397184;

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.strikeDestX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballStrikeY, (short)D2);
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;
        }
        Memory.WriteWord(Memory.Addr.ballStrikeZ, (short)D3);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);
        goto l_out;

    l_ball_going_left:
        // updatePlayers.cpp:12186-12317 — sub D1, [player.x] ; jge ball_right_of_player
        esi = A1;
        D1 = D1 - Memory.ReadSignedDword(esi + 30);
        if (D1 >= 0) goto l_ball_right_of_player;

        // jmp ball_not_moving (after restoring D1).
        D1 = D1 + Memory.ReadSignedDword(esi + 30);
        goto l_ball_not_moving;

    l_ball_right_of_player:
        // updatePlayers.cpp:12215-12317 — gravity-loop equivalent, but stepping X
        // toward player.x. NOTE: the original adds eax (D4) to D1 (since
        // ball going left, eventually crosses player x).
        D6 = D6 - D7;
        D3 = D3 + D6;
        D2 = D2 + D5;
        D1 = D1 + D4;
        if (D1 >= 0) goto l_ball_right_of_player;

        // updatePlayers.cpp:12251-12317 — undo last step. NOTE: the original's
        // sub uses the stale `eax` value (D4 from the prior load — see :12253
        // `srcSigned = eax`). Mechanically preserve: subtract D4.
        D1 = D1 - D4;
        D2 = D2 - D5;
        D3 = D3 - D6;
        esi = A1;
        D1 = D1 + Memory.ReadSignedDword(esi + 30);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.ballDefensiveX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballDefensiveY, (short)D2);
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;
        }
        Memory.WriteWord(Memory.Addr.ballDefensiveZ, (short)D3);
        goto cseg_78136;

    l_ball_going_right:
        // updatePlayers.cpp:12319-12454 — mirror of ball_going_left.
        esi = A1;
        D1 = D1 - Memory.ReadSignedDword(esi + 30);
        if (D1 < 0) goto l_ball_left_of_player;

        D1 = D1 + Memory.ReadSignedDword(esi + 30);
        goto l_ball_not_moving;

    l_ball_left_of_player:
        D6 = D6 - D7;
        D3 = D3 + D6;
        D2 = D2 + D5;
        D1 = D1 + D4;
        if (D1 < 0) goto l_ball_left_of_player;

        D1 = D1 - D4;
        D2 = D2 - D5;
        D3 = D3 - D6;
        esi = A1;
        D1 = D1 + Memory.ReadSignedDword(esi + 30);

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.ballDefensiveX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballDefensiveY, (short)D2);
        if (((short)D3) < 0)
        {
            D3 = (D3 & ~0xFFFF) | 0;
        }
        Memory.WriteWord(Memory.Addr.ballDefensiveZ, (short)D3);
        goto cseg_78136;

    l_ball_not_moving:
        // updatePlayers.cpp:12456-12464 — ball static: just snapshot whole-pixel
        // x/y/z into ballDefensive*.
        esi = A2;
        Memory.WriteWord(Memory.Addr.ballDefensiveX, Memory.ReadWord(esi + 32));
        Memory.WriteWord(Memory.Addr.ballDefensiveY, Memory.ReadWord(esi + 36));
        Memory.WriteWord(Memory.Addr.ballDefensiveZ, Memory.ReadWord(esi + 40));

    cseg_78136:
        // updatePlayers.cpp:12465-12478 — copy defensive → notHigh, clear strike vars.
        Memory.WriteWord(Memory.Addr.ballNotHighX, Memory.ReadWord(Memory.Addr.ballDefensiveX));
        Memory.WriteWord(Memory.Addr.ballNotHighY, Memory.ReadWord(Memory.Addr.ballDefensiveY));
        Memory.WriteWord(Memory.Addr.ballNotHighZ, Memory.ReadWord(Memory.Addr.ballDefensiveZ));
        Memory.WriteWord(Memory.Addr.strikeDestX, 0);
        Memory.WriteWord(Memory.Addr.ballStrikeY, 0);
        Memory.WriteWord(Memory.Addr.ballStrikeZ, 0);

    l_out:
        return;
    }

    // ====================================================================
    // calculateBallNextGroundXYPositions — updatePlayers.cpp:12491-12697
    // ====================================================================
    //
    // Predicts where the ball will be (whole-pixel X/Y) when it next lands
    // on the ground (z reaches 0). Writes ballNextGroundX/Y. If the ball is
    // effectively static (|deltaX| <= 1 and |deltaY| <= 1 in whole pixels),
    // writes ballNextGroundX = -1 as the "ball standing" sentinel.
    //
    // Algorithm: starting at the ball's current Q16.16 position, repeatedly
    // subtract D0 (initialised to 0x140000) from z until z goes negative or
    // D0 falls to 0x0D0000 (then bail out as standing). When z hits the
    // window, run the gravity loop forward (integrate x/y, decay deltaZ via
    // gravity) until z drops below zero.
    public static void CalculateBallNextGroundXYPositions(int aBallSprite)
    {
        int A2 = aBallSprite;
        int esi = A2;

        // updatePlayers.cpp:12498-12508 — load ball pos + delta XY.
        int D1 = Memory.ReadSignedDword(esi + 30);   // ball.x  Q16.16
        int D2 = Memory.ReadSignedDword(esi + 34);   // ball.y  Q16.16
        int D3 = Memory.ReadSignedDword(esi + 38);   // ball.z  Q16.16
        int D4 = Memory.ReadSignedDword(esi + 46);   // deltaX  Q16.16
        int D5 = Memory.ReadSignedDword(esi + 50);   // deltaY  Q16.16

        // updatePlayers.cpp:12509-12555 — branch on |deltaX|/|deltaY|.
        // cmp D5,-10000h / jl ball_moving
        if (D5 < -65536) goto l_ball_moving;
        // cmp D5,10000h / jg ball_moving
        if (D5 > 65536) goto l_ball_moving;
        // cmp D4,-10000h / jl ball_moving
        if (D4 < -65536) goto l_ball_moving;
        // cmp D4,10000h / jg ball_moving
        if (D4 > 65536) goto l_ball_moving;

        goto l_ball_standing;

    l_ball_moving:
        // updatePlayers.cpp:12559-12565 — load deltaZ + gravity, init D0.
        esi = A2;
        int D6 = Memory.ReadSignedDword(esi + 54);   // deltaZ
        int D7 = Memory.ReadSignedDword(Memory.Addr.kGravityConstant);
        int D0 = 1310720;                            // 0x140000

    l_subtract_z:
        // updatePlayers.cpp:12567-12592 — descend D0 in 0x10000 steps until z goes
        // negative or D0 == 0x0D0000 (then ball is effectively standing).
        D3 = D3 - D0;
        if (D3 >= 0) goto l_z_in_range;

        if (D0 == 851968) goto l_ball_standing;     // 0x0D0000

        D3 = D3 + D0;
        D0 = D0 - 65536;                             // sub 0x10000
        goto l_subtract_z;

    l_z_in_range:
        // updatePlayers.cpp:12612-12646 — integrate forward at full delta until
        // z falls below 0.
        D1 = D1 + D4;
        D2 = D2 + D5;
        D6 = D6 - D7;
        D3 = D3 + D6;
        if (D3 >= 0) goto l_z_in_range;

        // updatePlayers.cpp:12648-12686 — add D0 back (unwind the last subtraction
        // window), swap halves, write whole-pixel results.
        D3 = D3 + D0;

        D1 = SwapWords(D1);
        D2 = SwapWords(D2);
        D3 = SwapWords(D3);

        Memory.WriteWord(Memory.Addr.ballNextGroundX, (short)D1);
        Memory.WriteWord(Memory.Addr.ballNextGroundY, (short)D2);
        // updatePlayers.cpp:12685 — `ax = D3` then `*(word *)&g_memByte[524756] = 0`.
        // i.e. ballNextGroundZDead is hardcoded to 0 regardless of D3.
        Memory.WriteWord(Memory.Addr.ballNextGroundZDead, 0);
        goto l_out;

    l_ball_standing:
        // updatePlayers.cpp:12688-12689 — sentinel: ballNextGroundX = -1.
        Memory.WriteWord(Memory.Addr.ballNextGroundX, -1);

    l_out:
        return;
    }

    // ----- helpers ------------------------------------------------------------

    // Emulates `xchg ax, word ptr Dn+2` followed by `mov word ptr Dn, ax`.
    // Net effect: swap the high and low 16-bit halves of a 32-bit register —
    // SWOS uses this to extract whole-pixel parts of Q16.16 values into the low
    // word for `mov word ptr [mem], Dn` writes.
    private static int SwapWords(int v)
    {
        uint u = (uint)v;
        return (int)((u >> 16) | (u << 16));
    }
}
