namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// AI helper functions ported mechanically from
// external/swos-port/src/game/updatePlayers/updatePlayers.cpp.
//
// These are the SMALL AI helpers (~700 LOC total). The HUGE function
// AI_SetControlsDirection (3333 LOC at line 15980) is owned by another agent.
//
// Function map (line numbers refer to updatePlayers.cpp):
//   AI_Kick                              ........ 19313  (~148 LOC)
//   AI_SetDirectionTowardOpponentsGoal  ........ 19461  (~116 LOC)
//   AI_DecideWhetherToTriggerFire       ........ 19577  (~268 LOC)
//   AI_ResumeGameDelay                   ........ 19845  (~ 25 LOC)
//   findClosestPlayerToBallFacing        ........ 19870  (~129 LOC)
//
// Hard rules: mechanical port, NO floats, cite source, preserve gotos / magic
// numbers / "useless" flag updates, stub callees not yet ported.
//
// asm register/memory convention used by swos-port translations:
//   A1 .. A6 = absolute addresses (sprites, team data)
//   D0 .. D7 = working registers (word/dword grain)
//   readMemory(addr, sz)  → Memory.Read{Byte,Word,Dword}
//   writeMemory(addr, sz) → Memory.Write{Byte,Word,Dword}
//
// "TeamGeneralInfo offsets" map to TeamData.Off{…}; "Sprite" offsets map to
// PlayerSprite.Off{…}. The asm literal 522792 (offset topTeamData) maps to
// TeamData.TopBase; 522940 (offset bottomTeamData) to TeamData.BottomBase.
public static class AiHelpers
{
    // ====================================================================
    // AI_Kick — updatePlayers.cpp:19313
    // ====================================================================
    //
    // In:
    //   A1 -> player controlling the ball   (a1PlayerAddr)
    //   A6 -> team general info             (a6TeamBase)
    //
    // Effect (when conditions match):
    //   Sets team.currentAllowedDirection = team.allowedDirections,
    //   team.firePressed = 1, team.fireThisFrame = 1, team.normalFire = 1.
    //
    // The two opening dead-var checks (g_memByte[449467] / [449493]) gate a
    // never-taken early-out branch — preserved verbatim for mechanical parity.
    //
    // From updatePlayers.cpp:19313.
    public static void AI_Kick(int a1PlayerAddr, int a6TeamBase)
    {
        // 19315-19321 — ax = deadVarAlways0; if zero goto cseg_85B62. Since
        // deadVarAlways0 is always 0, the conditional jump fires every time
        // and the next two checks are dead. We keep them faithfully.
        short ax = Memory.ReadSignedWord(Memory.Addr.deadVarAlways0);
        if (ax == 0)
            goto cseg_85B62;

        // 19323-19334 — cmp A6, dseg_1309C1; if equal return. Dead path.
        int eax = Memory.ReadSignedDword(Memory.Addr.dseg_1309C1);
        if (a6TeamBase == eax)
            return;

        // 19336-19350 — A0 = team.opponentsTeam; cmp A0, dseg_1309C1; if equal return.
        int a0 = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
        eax = Memory.ReadSignedDword(Memory.Addr.dseg_1309C1);
        if (a0 == eax)
            return;

    cseg_85B62:;
        // 19352-19371 — Distance / phase gate.
        // D0 = currentGameTick & 6 (computed but only flag-effects matter; the
        // value isn't used past the and). Preserve the "useless" flag write.
        ax = Memory.ReadSignedWord(Memory.Addr.currentGameTick);
        int d0 = ax & 6;
        // Side effect: discard d0 — only the and's flags are set on the asm
        // path. d0 is never read again. Keep the local for documentation.
        _ = d0;

        // cmp [A1+Sprite.ballDistance], 200 ; ja @@out  (ball too far away)
        int ballDistance = Memory.ReadSignedDword(a1PlayerAddr + PlayerSprite.OffBallDistance);
        if (ballDistance > 200)
            return;

        // 19373-19374 — D7 = [A6+TeamGeneralInfo.allowedDirections] (saved for later).
        short d7 = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAllowedDirections);

        // 19375-19385 — esi = A6.opponentsTeam; ax = opp.playerHasBall; if 0 return.
        a0 = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
        short oppPlayerHasBall = Memory.ReadSignedWord(a0 + TeamData.OffPlayerHasBall);
        if (oppPlayerHasBall == 0)
            return;

        // 19387-19399 — A0 = opp.controlledPlayer; if 0 return.
        int oppControlledPlayer = Memory.ReadSignedDword(a0 + TeamData.OffControlledPlayer);
        if (oppControlledPlayer == 0)
            return;

        // 19401-19421 — D0 = opp.controlledPlayer.allowedDirections << 5;
        // D1 = A1.direction << 5; D0_byte -= D1_byte.
        //
        // NOTE: the asm field comment says "Sprite.direction" — esi was retargeted
        // to A1 = our controlling player at this point. We use PlayerSprite.OffDirection.
        short oppAllowedDirs = Memory.ReadSignedWord(oppControlledPlayer + TeamData.OffAllowedDirections);
        d0 = (oppAllowedDirs << 5) & 0xFFFF;
        short ourDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        int d1 = (ourDir << 5) & 0xFFFF;
        sbyte d0Lo = (sbyte)((d0 & 0xFF) - (d1 & 0xFF));    // sub byte ptr D0, al

        // 19422-19432 — cmp byte ptr D0, -32; jl @@kick.
        if (d0Lo < -32)
            goto l_kick;

        // 19434-19444 — cmp byte ptr D0, 32; jle @@out.
        if (d0Lo <= 32)
            return;

    l_kick:;
        // 19446-19452 — team.currentAllowedDirection = D7; team.firePressed = 1;
        // team.fireThisFrame = 1; team.normalFire = 1.
        Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d7);
        Memory.WriteByte(a6TeamBase + TeamData.OffFirePressed, 1);
        Memory.WriteByte(a6TeamBase + TeamData.OffFireThisFrame, 1);
        Memory.WriteByte(a6TeamBase + TeamData.OffNormalFire, 1);
    }


    // ====================================================================
    // AI_SetDirectionTowardOpponentsGoal — updatePlayers.cpp:19461
    // ====================================================================
    //
    // In:
    //   A6 -> team data (a6TeamBase)
    //
    // Set the direction toward the opposing goal and the ball. Limits for ball x
    // are less than 300, 300..371 and greater than 371 (those bound the goal
    // mouth approach lanes).
    //
    // Encoding of D1 (currentAllowedDirection):
    //   AI_attackHalf == 1 (attacking top):    bottom team & ball-x < 300 → 1
    //                                          bottom team & ball-x > 371 → 7
    //                                          bottom team & 300..371      → 0
    //   AI_attackHalf != 1 (attacking bottom): top team    & ball-x < 300 → 3
    //                                          top team    & ball-x > 371 → 5
    //                                          top team    & 300..371      → 4
    //
    // From updatePlayers.cpp:19461.
    public static void AI_SetDirectionTowardOpponentsGoal(int a6TeamBase)
    {
        // 19463-19468 — if AI_counter == 0 return.
        short aiCounter = Memory.ReadSignedWord(Memory.Addr.AI_counter);
        if (aiCounter == 0)
            return;

        // 19470-19471 — D0 = ballSprite.x+2 (whole-pixel ball X).
        short d0w = BallSprite.XPixels;

        // 19472-19482 — cmp AI_attackHalf, 1; jz l_attacking_top.
        short attackHalf = Memory.ReadSignedWord(Memory.Addr.AI_attackHalf);
        if (attackHalf == 1)
            goto l_attacking_top;

        // 19484-19493 — cmp A6, offset topTeamData; jnz @@out.
        // I.e. when attacking BOTTOM, only the TOP team's AI configures direction.
        if (a6TeamBase != TeamData.TopBase)
            return;

        // 19495-19505 — D1 = 3; cmp D0, 300; jb @@set_allowed_direction.
        int d1 = 3;
        if ((ushort)d0w < 300)
            goto l_set_allowed_direction;

        // 19507-19517 — D1 = 5; cmp D0, 371; ja @@set_allowed_direction.
        d1 = 5;
        if ((ushort)d0w > 371)
            goto l_set_allowed_direction;

        // 19519-19520 — D1 = 4; jmp @@set_allowed_direction.
        d1 = 4;
        goto l_set_allowed_direction;

    l_attacking_top:;
        // 19522-19532 — cmp A6, offset bottomTeamData; jnz @@out.
        // I.e. when attacking TOP, only the BOTTOM team's AI configures direction.
        if (a6TeamBase != TeamData.BottomBase)
            return;

        // 19534-19544 — D1 = 1; cmp D0, 300; jb @@set_allowed_direction.
        d1 = 1;
        if ((ushort)d0w < 300)
            goto l_set_allowed_direction;

        // 19546-19556 — D1 = 7; cmp D0, 371; ja @@set_allowed_direction.
        d1 = 7;
        if ((ushort)d0w > 371)
            goto l_set_allowed_direction;

        // 19558 — D1 = 0.
        d1 = 0;

    l_set_allowed_direction:;
        // 19560-19564 — team.currentAllowedDirection = D1.
        Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d1);
    }


    // ====================================================================
    // AI_DecideWhetherToTriggerFire — updatePlayers.cpp:19577
    // ====================================================================
    //
    // In:
    //   D7 - controlled player direction  (d7Direction, low word)
    //   A5 -> controlled player sprite    (a5PlayerAddr)
    //   A6 -> team general data           (a6TeamBase)
    // Out:
    //   returns true  → "firing" (D0 == 0 in asm, zero flag SET)
    //   returns false → "not gonna fire" (D0 == 1 in asm, zero flag CLEAR)
    //
    // Decides whether the AI should press fire this frame, given the controlled
    // player's facing direction, distance to the ball, and the ball's Z-height
    // band (we only want to volley balls within a specific height window).
    //
    // From updatePlayers.cpp:19577.
    public static bool AI_DecideWhetherToTriggerFire(int d7Direction, int a5PlayerAddr, int a6TeamBase)
    {
        // 19579-19590 — cmp [A5+Sprite.playerOrdinal], 1; jz @@no_fire.
        // Goalkeepers (ordinal 1) never AI-fire here.
        short playerOrdinal = Memory.ReadSignedWord(a5PlayerAddr + PlayerSprite.OffPlayerOrdinal);
        if (playerOrdinal == 1)
            goto l_no_fire;

        // 19592-19601 — cmp A6, offset topTeamData; jnz @@we_are_top.
        // The "jnz" here is an inverted naming bug in the original — when A6 IS
        // topTeamData we fall through (we are TOP, so opponent's goal is at the
        // BOTTOM); when A6 is NOT topTeamData we jump (we are BOTTOM, opponent
        // goal at TOP). We preserve the label name from swos-port verbatim.
        if (a6TeamBase != TeamData.TopBase)
            goto l_we_are_top;

        // ---- TOP-team branch (attacking south): valid facing dirs 3, 4, 5 ----
        // 19603-19612 — cmp D7, 3; jz @@facing_toward_opponents_goal.
        if (d7Direction == 3)
            goto l_facing_toward_opponents_goal;
        // 19614-19623 — cmp D7, 4; jz.
        if (d7Direction == 4)
            goto l_facing_toward_opponents_goal;
        // 19625-19634 — cmp D7, 5; jz.
        if (d7Direction == 5)
            goto l_facing_toward_opponents_goal;
        // 19636 — jmp @@no_fire.
        goto l_no_fire;

    l_we_are_top:;
        // ---- BOTTOM-team branch (attacking north): valid facing dirs 7, 0, 1 ----
        // 19638-19648 — cmp D7, 7; jz.
        if (d7Direction == 7)
            goto l_facing_toward_opponents_goal;
        // 19650-19659 — cmp D7, 0; jz.
        if (d7Direction == 0)
            goto l_facing_toward_opponents_goal;
        // 19661-19670 — cmp D7, 1; jnz @@no_fire.
        if (d7Direction != 1)
            goto l_no_fire;

    l_facing_toward_opponents_goal:;
        // 19673-19684 — cmp [A5+Sprite.ballDistance], 648; ja @@no_fire.
        int ballDistance = Memory.ReadSignedDword(a5PlayerAddr + PlayerSprite.OffBallDistance);
        if (ballDistance > 648)
            goto l_no_fire;

        // 19686-19691 — eax = ballSprite.deltaZ; or eax, eax; js @@ball_falling.
        // Negative deltaZ = falling.
        int deltaZ = BallSprite.DeltaZ;
        if (deltaZ < 0)
            goto l_ball_falling;

        // ---- Ball rising (deltaZ >= 0): accept Z window [8, 14] -----------
        // 19693-19703 — cmp ballSprite.z+2, 8; jb @@no_fire.
        short ballZ = BallSprite.ZPixels;
        if ((ushort)ballZ < 8)
            goto l_no_fire;

        // 19705-19715 — cmp ballSprite.z+2, 14; ja @@no_fire.
        if ((ushort)ballZ > 14)
            goto l_no_fire;

        goto l_trigger_joypad;

    l_ball_falling:;
        // ---- Ball falling (deltaZ < 0): accept Z window [12, 20] ----------
        // 19720-19730 — cmp ballSprite.z+2, 12; jb @@no_fire.
        ballZ = BallSprite.ZPixels;
        if ((ushort)ballZ < 12)
            goto l_no_fire;
        // 19732-19742 — cmp ballSprite.z+2, 20; ja @@no_fire.
        if ((ushort)ballZ > 20)
            goto l_no_fire;

    l_trigger_joypad:;
        // 19745-19746 — team.fireThisFrame = 1.
        Memory.WriteByte(a6TeamBase + TeamData.OffFireThisFrame, 1);

        // 19747-19766 — currentAllowedDirection candidate #1:
        //   D0 = A5.fullDirection
        //   byteLo D0 += 0x80   (rotate by 128 within byte)
        //   D0 &= 0xFF
        //   D0 >>= 5            (group of 32 per direction → 8 directions)
        //   team.currentAllowedDirection = D0
        short fullDir = Memory.ReadSignedWord(a5PlayerAddr + PlayerSprite.OffFullDirection);
        int d0 = fullDir & 0xFFFF;
        // add byte ptr D0, 128 — byte-wise add, only low 8 bits affected.
        int d0LoByte = (d0 & 0xFF) + 0x80;
        d0 = (d0 & ~0xFF) | (d0LoByte & 0xFF);
        d0 &= 0xFF;
        d0 >>= 5;
        Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0);

        // 19767-19789 — recompute with +16 added (still byte-wise) and compare
        // against D7. If they don't match → no_fire.
        fullDir = Memory.ReadSignedWord(a5PlayerAddr + PlayerSprite.OffFullDirection);
        int d0b = fullDir & 0xFFFF;
        int d0bLoByte = (d0b & 0xFF) + 0x80;
        d0b = (d0b & ~0xFF) | (d0bLoByte & 0xFF);
        // add word ptr D0, 16   (word-wide add, but at this point only low byte
        // is non-zero so this is effectively +16 to the byte that hasn't yet
        // been ANDed; the asm sequence is then `and ax, 0FFh; shr ax, 5`).
        d0b = (d0b + 16) & 0xFFFF;
        d0b &= 0xFF;
        d0b >>= 5;

        // 19790-19800 — cmp D0, D7; jnz @@no_fire.
        int d7Cmp = d7Direction & 0xFFFF;
        if (d0b != d7Cmp)
            goto l_no_fire;

        // 19802-19807 — team.currentAllowedDirection = D0;
        //               AI_counter = 15;
        //               AI_counterWriteOnly = D0;
        //               AI_attackHalf = 2.
        Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0b);
        Memory.WriteWord(Memory.Addr.AI_counter, 15);
        Memory.WriteWord(Memory.Addr.AI_counterWriteOnly, d0b);
        Memory.WriteWord(Memory.Addr.AI_attackHalf, 2);

        // 19808-19819 — cmp A6, offset topTeamData; jz @@out_fire; else AI_attackHalf = 1.
        if (a6TeamBase == TeamData.TopBase)
            goto l_out_fire;
        Memory.WriteWord(Memory.Addr.AI_attackHalf, 1);

    l_out_fire:;
        // 19822-19827 — D0 = 0; xor ax, ax; return (zero flag SET = firing).
        return true;

    l_no_fire:;
        // 19830-19834 — D0 = 1; ax = 1; return (zero flag CLEAR = not firing).
        return false;
    }


    // ====================================================================
    // AI_ResumeGameDelay — updatePlayers.cpp:19845
    // ====================================================================
    //
    // In:
    //   A6 -> team (general)              (a6TeamBase)
    // Out:
    //   A0 -> opponents team              (returned via `out` parameter)
    //   carry flag = (passKickTimer == 13) → returned as `bool`
    //
    // Only used by AI. Reads team.opponentsTeam and tests if the pass/kick timer
    // sits exactly at 13. The asm carry flag is set when the unsigned compare
    // `passKickTimer - 13` would underflow, i.e. passKickTimer < 13 → carry set.
    // (The function comment says "pass/kick time == 13" but the cmp sets carry
    // on `<`, not `==`. We preserve the original semantics: carry = (timer < 13).)
    //
    // From updatePlayers.cpp:19845.
    public static bool AI_ResumeGameDelay(int a6TeamBase, out int a0OpponentsTeam)
    {
        // 19847-19849 — A0 = team.opponentsTeam.
        a0OpponentsTeam = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);

        // 19850-19858 — cmp team.passKickTimer, 13. Set carry if < 13.
        // The asm carry computation is `static_cast<uint16_t>(timer) < static_cast<uint16_t>(13)`.
        short passKickTimer = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPassKickTimer);
        bool carry = (ushort)passKickTimer < (ushort)13;
        return carry;
    }


    // ====================================================================
    // findClosestPlayerToBallFacing — updatePlayers.cpp:19870
    // ====================================================================
    //
    // In:
    //   D0 - full direction player must be facing (+/- 16)  (d0FullDir)
    //   A6 -> team (general)                                (a6TeamBase)
    // Out:
    //   D2 - ball distance of closest match                (out d2BallDistance)
    //   A0 -> player sprite address (-1 if none)           (return value)
    //
    // Find player closest to ball facing specified direction. Search BOTH
    // teams' spritesTables (the asm walks D3 from 1 down to 0, switching team
    // each iteration via D3-dec; D4 tracks per-team slot index 10..0).
    //
    // Skip rules per candidate:
    //   - sprite == team.controlledPlayer  (don't include ourselves)
    //   - sprite.sentAway != 0
    //   - sprite.playerState != PL_NORMAL (0)
    //   - abs(sprite.fullDirection - D0) > 16 (byte-wise compare)
    //
    // Pick:
    //   - First candidate with smallest sprite.ballDistance.
    //
    // From updatePlayers.cpp:19870.
    public static int FindClosestPlayerToBallFacing(int d0FullDir, int a6TeamBase, out int d2BallDistance)
    {
        // 19872-19874 — initial A0 = -1; D2 = -1; D3 = 1 (outer team loop counter).
        int a0 = -1;
        int d2 = -1;
        int d3 = 1;

        // 19874-19880 — D4 = 10 (inner slot counter); A3 = topTeamData;
        // A2 = A3.spritesTable.
        int d4 = 10;
        int a3 = TeamData.TopBase;
        int a2 = Memory.ReadSignedDword(a3 + TeamData.OffPlayers);

    l_players_loop:;
        // 19882-19890 — eax = [A2]; A2 += 4; A1 = eax.
        int a1Sprite = Memory.ReadSignedDword(a2);
        a2 += 4;

        // 19891-19903 — cmp A1, [A6+TeamGeneralInfo.controlledPlayer]; jz @@next_player.
        int controlledPlayer = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);
        if (a1Sprite == controlledPlayer)
            goto l_next_player;

        // 19905-19912 — ax = [A1+Sprite.sentAway]; or ax, ax; jnz @@next_player.
        short sentAway = Memory.ReadSignedWord(a1Sprite + PlayerSprite.OffSentAway);
        if (sentAway != 0)
            goto l_next_player;

        // 19914-19925 — cmp [A1+Sprite.playerState], PL_NORMAL (0); jnz @@next_player.
        byte playerState = Memory.ReadByte(a1Sprite + PlayerSprite.OffPlayerState);
        if (playerState != 0)
            goto l_next_player;

        // 19927-19935 — D1 = A1.fullDirection; D1_byte -= D0_byte.
        short fullDir = Memory.ReadSignedWord(a1Sprite + PlayerSprite.OffFullDirection);
        int d1 = fullDir & 0xFFFF;
        sbyte d1Lo = (sbyte)((d1 & 0xFF) - (d0FullDir & 0xFF));

        // 19936-19946 — cmp byte ptr D1, -16; jl @@next_player.
        if (d1Lo < -16)
            goto l_next_player;

        // 19948-19958 — cmp byte ptr D1, 16; jg @@next_player.
        if (d1Lo > 16)
            goto l_next_player;

        // 19960-19973 — D1 = A1.ballDistance; cmp D1, D2; jnb @@next_player.
        // Unsigned compare: `jnb` (jump if not below) means we skip when
        // `D1 >= D2`. I.e. we only take this candidate when D1 < D2.
        // D2 is initialised to -1 (all-ones unsigned) so the first valid
        // candidate always wins; subsequent ones must beat the current best.
        int d1Dist = Memory.ReadSignedDword(a1Sprite + PlayerSprite.OffBallDistance);
        if ((uint)d1Dist >= (uint)d2)
            goto l_next_player;

        // 19975-19978 — D2 = D1; A0 = A1.
        d2 = d1Dist;
        a0 = a1Sprite;

    l_next_player:;
        // 19980-19986 — dec word ptr D4; jns @@players_loop. JNS = jump if
        // sign-NOT-set, i.e. continue while D4 >= 0. Loop covers D4 = 10 down
        // to D4 = 0 inclusive (11 iterations = 11 players per team).
        d4 = (short)(d4 - 1);
        if (d4 >= 0)
            goto l_players_loop;

        // 19988-19992 — D4 = 10; A3 = bottomTeamData; A2 = A3.spritesTable.
        d4 = 10;
        a3 = TeamData.BottomBase;
        a2 = Memory.ReadSignedDword(a3 + TeamData.OffPlayers);

        // 19993-19998 — dec word ptr D3; jns @@players_loop. D3 = 1 → 0 →
        // continue (bottom team); D3 = 0 → -1 → exit.
        d3 = (short)(d3 - 1);
        if (d3 >= 0)
            goto l_players_loop;

        // Fall-through return. The asm doesn't have an explicit RET in this
        // snippet but the function ends here (next function follows).
        d2BallDistance = d2;
        return a0;
    }
}
