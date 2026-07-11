namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Ports from external/swos-port/src/sprites/updateSprite.cpp — shared sprite
// helpers used by ball, player, keeper, and referee update paths. Each method
// cites its source file:line.
//
// Phase B4: CalculateDeltaXAndY is the foundation. Every SWOS sprite that
// "moves toward a destination" (ball, keeper, referee, AI-controlled outfielder)
// calls this each tick to recompute deltaX/deltaY from (x, y, destX, destY,
// speed). Without it, applyBallAfterTouch's destX/destY shifts have no visible
// effect.
public static class SpriteUpdate
{
    // Result struct mirrors updateSprite.cpp:91-95.
    public struct DeltasAndAngle
    {
        public int DeltaX;       // FixedPoint Q16.16
        public int DeltaY;       // FixedPoint Q16.16
        public int Direction;    // -1 = no movement, else 0..255
    }

    // updateSprite.cpp:231-336.
    // Computes motion vector toward destination:
    //   speed:   how fast the sprite is moving (Q8.8 fixed point)
    //   x, y:    current position (whole pixels)
    //   destX,Y: target position (whole pixels)
    //
    // Returns:
    //   direction = -1 if speed == 0 OR (deltaX==deltaY==0)
    //   direction = 0..255 representing angle around circle (0 = up, increases CW)
    //   deltaX, deltaY = Q16.16 fixed-point per-tick motion (sign-aware)
    //
    // **41/64 PC sprite damping** (updateSprite.cpp:323-328) lives in here —
    // when running PC mode, both sin and cos are multiplied by 0.640625
    // (= 1 - 1/4 - 1/16 - 1/32 - 1/64). This is the "1.5× too fast" fix from
    // earlier in the project, now in its proper home.
    public static DeltasAndAngle CalculateDeltaXAndY(int speed, int x, int y, int destX, int destY)
    {
        DeltasAndAngle result = new DeltasAndAngle { DeltaX = 0, DeltaY = 0, Direction = -1 };

        // updateSprite.cpp:233-238 — split deltaX into sign + magnitude.
        bool xNegative = false;
        int deltaX = destX - x;
        if (deltaX < 0) { xNegative = true; deltaX = -deltaX; }

        // updateSprite.cpp:240-245 — split deltaY into sign + magnitude.
        bool yNegative = false;
        int deltaY = destY - y;
        if (deltaY < 0) { yNegative = true; deltaY = -deltaY; }

        // updateSprite.cpp:247-250 — halve repeatedly until both fit in [0, 32),
        // preserving ratio so kAngleTangent table (32×32) can be indexed.
        while (deltaX >= 32 || deltaY >= 32)
        {
            deltaX /= 2;
            deltaY /= 2;
        }

        // updateSprite.cpp:252 — table lookup. kAngleTangent[deltaY][deltaX]
        // returns angle 0..64 (1st quadrant). -1 if both deltas are 0.
        int angle = Tables.kAngleTangent[deltaY, deltaX];

        if (angle < 0) return result;  // no movement (deltaY==0 && deltaX==0)

        // updateSprite.cpp:267-291 — transform 1st-quadrant angle to full 0..255
        // circle based on sign of (deltaX, deltaY).
        if (xNegative)
        {
            if (yNegative)
                angle = 192 - angle;
            else
                angle += 192;
        }
        else
        {
            if (yNegative)
                angle += 64;
            else
                angle = 64 - angle;
        }
        angle &= 0xff;

        // updateSprite.cpp:304 — convert to SWOS direction convention (0 = top, CW).
        result.Direction = (256 - angle + 128) & 0xff;

        // updateSprite.cpp:307-308 — fetch sin/cos. cos = sine[angle],
        // sin = sine[(angle + 64) & 0xFF]. Both 16-bit Q15-ish (peak 32767).
        int cos = Tables.kSineCosineTable[angle];
        int sin = Tables.kSineCosineTable[(angle + 64) & 0xff];

        // updateSprite.cpp:319-320 — scale by speed, shift down to Q16.16.
        // SWOS_TEST path (line 316-317) shifts first for stability — we use
        // production path. NOTE: `sin * speed` can overflow int32 when both
        // are large — but speed maxes at ~2688 (kHighKickBallSpeed) and
        // sin maxes at 32767, so 32767 * 2688 = 88 million, safe in int32.
        sin = (sin * speed) >> 8;
        cos = (cos * speed) >> 8;

        // updateSprite.cpp:323-329 — PC mode damping (× 41/64 ≈ 0.640625).
        // We're hard-locked to PC mode in OpenSWOS (CLAUDE.md, PlayerSim.cs).
        // Translates the 41/64 multiplier into shifts: result = x - x/4 - x/16 - x/32 - x/64.
        sin = sin - (sin >> 2) - (sin >> 4) - (sin >> 5) - (sin >> 6);
        cos = cos - (cos >> 2) - (cos >> 4) - (cos >> 5) - (cos >> 6);

        result.DeltaX = cos;
        result.DeltaY = sin;
        return result;
    }

    // Convenience overload that takes a Sprite address inside Memory and writes
    // the result back to the sprite's deltaX, deltaY, direction, fullDirection.
    // Mirrors updateSpriteDirectionAndDeltas (updateSprite.cpp:101-112).
    public static void UpdateSpriteDirectionAndDeltas(int spriteBase)
    {
        int x = Memory.ReadSignedWord(spriteBase + 32);    // x.whole() — high word of Q16.16 (offset +2 = whole pixels)
        int y = Memory.ReadSignedWord(spriteBase + 36);    // y.whole()
        int destX = Memory.ReadSignedWord(spriteBase + 58);
        int destY = Memory.ReadSignedWord(spriteBase + 60);
        int speed = Memory.ReadSignedWord(spriteBase + 44);

        var result = CalculateDeltaXAndY(speed, x, y, destX, destY);

        Memory.WriteDword(spriteBase + 46, result.DeltaX);  // deltaX
        Memory.WriteDword(spriteBase + 50, result.DeltaY);  // deltaY
        Memory.WriteWord(spriteBase + 82, result.Direction); // fullDirection
        // updateSprite.cpp:111 — direction = ((fullDirection + 16) & 0xff) >> 5
        // gives 8-direction quantisation (0..7) from full 0..255 angle.
        // The C++ applies this UNCONDITIONALLY: for direction == -1 (no
        // movement) it yields (15 & 0xff) >> 5 = 0, NOT -1 — writing -1 here
        // would poison every dir*4 table index downstream. Match it exactly.
        int dir8 = ((result.Direction + 16) & 0xff) >> 5;
        Memory.WriteWord(spriteBase + 42, dir8);
    }

    // ===================================================================
    // movePlayers — updateSprite.cpp:145-155
    // ===================================================================
    //
    // Per-tick sprite delta → position integration for the 22 player slots.
    // Called from gameLoop coreGameUpdate AFTER updatePlayers (which sets
    // each sprite's deltaX/deltaY) and AFTER updateBall.
    //
    // C++ original (updateSprite.cpp:145-155):
    //     void movePlayers() {
    //         auto sprite = getPlayerSprites();              // points at 22 sprites
    //         auto sentinelSprite = sprite + 2 * kNumPlayersInLineup;
    //         for (; sprite < sentinelSprite; sprite++) {
    //             A0 = *sprite;
    //             SetNextPlayerFrame();                       // animation frame step
    //             moveSprite(**sprite);                       // x += dx; y += dy; clamp to dest
    //             updateAnimationTableAndDestinationReached(**sprite);
    //         }
    //     }
    //
    // SetNextPlayerFrame (animation index walk) is NOT YET PORTED — it only
    // affects which sprite frame is drawn, not movement. We skip it.
    public static void MoveAllPlayers()
    {
        // updateSprite.cpp:147-149 — iterate all 22 player slots
        // (kNumPlayersInLineup == 11 per team × 2 teams).
        for (int slot = 0; slot < PlayerSprite.TotalSlots; slot++)
        {
            int spriteBase = PlayerSprite.Base(slot);
            // updateSprite.cpp:151 / swos.asm:102811 — SetNextPlayerFrame.
            // Ported in this file below; advances animation and applies
            // frameOffset + goal-cheer logic on top of UpdateSpriteAnimation.
            SetNextPlayerFrame(spriteBase);
            MoveSprite(spriteBase);
            UpdateAnimationTableAndDestinationReached(spriteBase);
        }
    }

    // ===================================================================
    // SetNextPlayerFrame — swos.asm:102834-102971
    // ===================================================================
    //
    // Per-tick animation tick + goal-cheer overlay for one player sprite.
    // The C++ port (updateSprite.cpp:114-143) split this into a stripped-down
    // `updateSpriteAnimation` that drops the frameOffset add and the
    // goal-celebration variant. We need the full asm version because:
    //
    //   1. Without frameOffset, Main.cs's renderer applies frameOffset itself
    //      every draw, but the per-tick walker also has to bake it into
    //      imageIndex so downstream logic that reads imageIndex (e.g. ball
    //      contact / referee draw) sees the correct value (matches asm
    //      102906-102907 verbatim).
    //   2. Goal-scored cheer (asm 102908-102960) makes players celebrate
    //      after a goal — currently dropped.
    //
    // Additionally we add a direction-change re-bind: when sprite.direction
    // differs from sprite.startingDirection (stamped by SetPlayerAnimationTable
    // when the animation was last installed), the frameIndicesTable is bound
    // to the previous direction's stream. In real SWOS the various
    // updatePlayers paths each call SetPlayerAnimationTable themselves after
    // changing direction — we haven't ported all those sites yet, so without
    // a central re-bind walking sprites are stuck on whatever direction was
    // active at the time the standing/walking animation table was installed.
    // The re-bind is a no-op once those callers are ported (they will just
    // re-stamp startingDirection == direction).
    public static void SetNextPlayerFrame(int spriteBase)
    {
        // 102836-102838 — if (!sprite.onScreen) return.
        short onScreen = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffOnScreen);
        if (onScreen == 0) return;

        // --- Direction-change re-bind (OpenSWOS addition, not in asm) -------
        // Detect direction change since the animation table was last installed
        // and re-bind frameIndicesTable to the new direction's stream so the
        // renderer cycles the right walk frames.
        short curDir = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffDirection);
        short startDir = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffStartingDirection);
        // Bug #178/#179 (keeper dive/catch/deflect frozen on the standing pose):
        // the goalkeeper save states install a DIRECTION-SPARSE animation table
        // exactly ONCE — GoalkeeperJumping installs leftGoalieJumping*/
        // rightGoalieJumping* (only dirs 2/6 populated, updatePlayers.cpp:10929+)
        // and goalkeeperCaughtTheBall installs the catch table
        // (updatePlayers.cpp:11048). The original SetNextPlayerFrame
        // (swos.asm:102834) NEVER re-binds on a direction change, so those
        // streams walk to completion regardless of how the keeper's facing
        // moves. The port-only re-bind below re-indexed the sparse dive table
        // every tick the diving keeper's facing jittered (RecomputeSpriteDeltas
        // rewrites direction each tick, faithfully), hit a NULL slot, and
        // SetPlayerAnimationTable zeroed frameIndicesTable → the walker bailed
        // and the renderer fell back to the LEGACY standing pose (the reported
        // "keeper stays standing while diving / punching a shot to the corner").
        // Suppress the re-bind for animation tables that are installed ONCE and
        // must run to completion regardless of facing:
        //  - goalie save states (kGoalieCatchingBall=4, kGoalieDivingHigh=6,
        //    kGoalieDivingLow=7, kGoalieClaimed=11) — bug #178/#179;
        //  - PL_ROLLING_INJURED=13 — the writhe stream is selected from the
        //    fall direction at the injury instant (updatePlayers.cpp:7489-7492 /
        //    swos.asm:116198-116203); RecomputeSpriteDeltas keeps turning the
        //    stationary injured player to face the ball, and re-binding on that
        //    drift flipped the TopLeft/TopRight writhe streams (user repro:
        //    "writhe faces a different way than the fall + flips over time").
        byte rebindState = Memory.ReadByte(spriteBase + PlayerSprite.OffPlayerState);
        bool suppressRebind = rebindState == 4 || rebindState == 6
            || rebindState == 7 || rebindState == 11   // goalie saves (#178/#179)
            || rebindState == 13;                       // PL_ROLLING_INJURED writhe
        if (curDir != startDir && !suppressRebind)
        {
            int animTable = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffAnimTablePtr);
            // Only re-bind if we have a real animation table installed.
            if (animTable > 0 && animTable < 0x60000)
            {
                PlayerActions.SetPlayerAnimationTable(spriteBase, animTable);
                // SetPlayerAnimationTable resets frameIndex=-1, cycleFramesTimer=1,
                // frameSwitchCounter=-1, and stamps startingDirection=curDir, so
                // the rest of this function will pick up the fresh stream.
            }
        }

        // 102840-102841 — if (--sprite.cycleFramesTimer != 0) return.
        short timer = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffCycleFramesTimer);
        timer = (short)(timer - 1);
        Memory.WriteWord(spriteBase + PlayerSprite.OffCycleFramesTimer, timer);
        if (timer != 0) return;

        // 102842-102847 — sprite.frameIndex++; cycleFramesTimer = frameDelay.
        short frameIndex = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffFrameIndex);
        frameIndex = (short)(frameIndex + 1);
        short frameDelay = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffFrameDelay);
        Memory.WriteWord(spriteBase + PlayerSprite.OffCycleFramesTimer, frameDelay);

        // 102848-102850 — A1 = sprite.frameIndicesTable.
        int fitPtr = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffFrameIndicesTable);
        if (fitPtr <= 0 || fitPtr > 0x60000)
        {
            // No frame table wired up — match the defensive bail pattern used
            // by SetPlayerAnimationTableAndPictureIndex (PlayerActions.cs:1514).
            Memory.WriteWord(spriteBase + PlayerSprite.OffFrameIndex, frameIndex);
            return;
        }

        // 102852-102893 — @@next_frame_index loop: walk through opcode stream
        // until we land on a non-negative image index OR hit the @@pause_frame
        // terminator. D0 is the opcode value, A1 is the table base.
        const int kLastFrameLoopMarker  = -999;
        const int kLastFrameHoldMarker  = -101;
        const int kFrameLoopbackMarker  = -100;

        int d0;
        int iter = 0;
        bool exitToOut = false;
        while (true)
        {
            // Safety bound — bad table would loop forever.
            if (++iter > 16) break;

            // 102854-102861 — D0 = *(word*)(A1 + frameIndex*2).
            d0 = Memory.ReadSignedWord(fitPtr + (frameIndex & 0xFFFF) * 2);

            // 102862-102863 — jns @@index_positive (non-negative → real frame).
            if (d0 >= 0) break;

            // 102864-102865 — cmp -999, jz @@reset_index.
            if (d0 == kLastFrameLoopMarker)
            {
                // 102882-102885 — sprite.frameIndex = 0; loop.
                frameIndex = 0;
                continue;
            }

            // 102866-102867 — cmp -101, jz @@pause_frame.
            if (d0 == kLastFrameHoldMarker)
            {
                // 102896-102899 — sprite.frameIndex -= 1; jmp @@out.
                frameIndex = (short)(frameIndex - 1);
                exitToOut = true;
                break;
            }

            // 102868-102869 — cmp -100, jle @@add_negative_offset.
            if (d0 <= kFrameLoopbackMarker)
            {
                // 102888-102893 — D0 += 100 (so -100→0, -101→-1, ...).
                // sprite.frameIndex += D0; loop.
                int offset = d0 - kFrameLoopbackMarker;
                frameIndex = (short)(frameIndex + offset);
                continue;
            }

            // 102870-102879 — variable-pause opcode (-1..-99):
            //   D0 = -D0; sprite.frameDelay = D0; cycleFramesTimer = D0;
            //   sprite.frameIndex += 1; loop.
            int newDelay = -d0;
            Memory.WriteWord(spriteBase + PlayerSprite.OffFrameDelay,       (short)newDelay);
            Memory.WriteWord(spriteBase + PlayerSprite.OffCycleFramesTimer, (short)newDelay);
            frameIndex = (short)(frameIndex + 1);
        }

        // Persist updated frameIndex (also stored on the @@pause_frame path).
        Memory.WriteWord(spriteBase + PlayerSprite.OffFrameIndex, frameIndex);
        if (exitToOut) return;

        // 102902-102907 — @@index_positive: sprite.frameSwitchCounter += 1;
        // D0 += sprite.frameOffset (so D0 is now the final picture index).
        short fsCounter = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffFrameSwitchCounter);
        fsCounter = (short)(fsCounter + 1);
        Memory.WriteWord(spriteBase + PlayerSprite.OffFrameSwitchCounter, fsCounter);

        d0 = Memory.ReadSignedWord(fitPtr + (frameIndex & 0xFFFF) * 2);
        short frameOffset = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffFrameOffset);
        d0 = (short)(d0 + frameOffset);

        // 102908-102960 — goal-cheer overlay.
        //   if (!goalScored) → skip.
        //   if (sprite.playerState != PL_NORMAL) → skip.
        //   if (direction != 0 && direction != 4) → skip (only up/down faces cheer).
        //   if (sprite.teamNumber != lastTeamScoredNumber) → skip.
        //   if (sprite.playerOrdinal == 1) → skip (keepers don't cheer).
        //   if (sprite == lastPlayerScored) → cheer 78.9% of time
        //       (currentGameTick & 0x7F) <= 100.
        //   else → cheer 50% of time
        //       ((playerOrdinal << 2 + currentGameTick) & 0x3F) <= 31.
        //   On cheer: D0 = D0 + 365 - 341 = D0 + 24 (cheer-frame offset).
        bool cheer = false;
        short goal = Memory.ReadSignedWord(Memory.Addr.goalScored);
        if (goal != 0)
        {
            byte playerState = Memory.ReadByte(spriteBase + PlayerSprite.OffPlayerState);
            if (playerState == 0) // PL_NORMAL
            {
                // 102914-102920 — direction == 0 (up) OR direction == 4 (down).
                short dir = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffDirection);
                if (dir == 0 || dir == 4)
                {
                    short lastTeamScored = Memory.ReadSignedWord(Memory.Addr.lastTeamScoredNumber);
                    short teamNum = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffTeamNumber);
                    if (lastTeamScored == teamNum)
                    {
                        short ord = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffPlayerOrdinal);
                        if (ord != 1) // not a keeper
                        {
                            int lastPlayerScored = Memory.ReadSignedDword(Memory.Addr.lastPlayerScored);
                            ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
                            if (spriteBase == lastPlayerScored)
                            {
                                // 102939-102943 — cheering 78.90625% of time.
                                if ((tick & 0x7F) <= 100) cheer = true;
                            }
                            else
                            {
                                // 102948-102955 — cheering 50% of time.
                                int d1 = ((ord & 0xFFFF) << 2) + tick;
                                if ((d1 & 0x3F) <= 31) cheer = true;
                            }
                        }
                    }
                }
            }
        }
        if (cheer)
        {
            // 102959-102960 — add 365, sub 341  →  +24.
            d0 = (short)(d0 + 24);
        }

        // 102962-102966 — @@set_picture_index: sprite.imageIndex = D0.
        Memory.WriteWord(spriteBase + PlayerSprite.OffImageIndex, (short)d0);
    }

    // ===================================================================
    // updateSpriteAnimation — updateSprite.cpp:114-143
    // ===================================================================
    //
    // Per-tick animation advance. Walks the frameIndicesTable stream, which
    // is a sequence of int16 image indices terminated by a negative opcode:
    //
    //   frame >= 0                      → setImage(frame); frameSwitchCounter++
    //   frame == -999 (kLastFrameLoop)  → frameIndex = 0   (loop back)
    //   frame == -101 (kLastFrameHold)  → frameIndex--; break (stay on last frame)
    //   frame <= -100 (kFrameLoopback)  → frameIndex += (frame - (-100))  (relative jump back)
    //   otherwise (negative-but-other)  → frameDelay = -frame; cycleFramesTimer = -frame; frameIndex++
    //
    // The do/while loop re-reads after a non-image opcode so the same tick
    // can both jump and consume the next entry (mirrors C++).
    //
    // Source kLastFrameLoopMarker = -999, kLastFrameHoldMarker = -101,
    // kFrameLoopbackMarker = -100 (updateSprite.cpp:5-7).
    public static void UpdateSpriteAnimation(int spriteBase)
    {
        const int kLastFrameLoopMarker = -999;
        const int kLastFrameHoldMarker = -101;
        const int kFrameLoopbackMarker = -100;

        // updateSprite.cpp:116 — gate on onScreen && --cycleFramesTimer == 0.
        // Note pre-decrement semantics: timer is decremented even when test
        // skips the body, so always store back. Production SWOS uses a uint16
        // timer; allow it to wrap if onScreen==0 (matches C++ behaviour: the
        // C++ short-circuit means timer is NOT decremented when onScreen==0).
        short onScreen = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffOnScreen);
        if (onScreen == 0) return;

        short timer = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffCycleFramesTimer);
        timer = (short)(timer - 1);
        Memory.WriteWord(spriteBase + PlayerSprite.OffCycleFramesTimer, timer);
        if (timer != 0) return;

        // updateSprite.cpp:117-118 — advance frameIndex; reload timer from frameDelay.
        short frameIndex = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffFrameIndex);
        frameIndex = (short)(frameIndex + 1);
        short frameDelay = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffFrameDelay);
        Memory.WriteWord(spriteBase + PlayerSprite.OffCycleFramesTimer, frameDelay);

        int fitPtr = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffFrameIndicesTable);
        // Defensive: if no frame table wired up yet, skip — match port-wide
        // pattern (PlayerActions.cs:1514, 1522, 1529).
        if (fitPtr <= 0 || fitPtr > 0x60000)
        {
            Memory.WriteWord(spriteBase + PlayerSprite.OffFrameIndex, frameIndex);
            return;
        }

        // updateSprite.cpp:122-141 — do/while(frame < 0): consume opcodes
        // until we land on a non-negative image index (or hit a break opcode).
        int frame;
        int iter = 0;
        do
        {
            // Safety bound — a malformed table could loop forever; cap at 16
            // opcodes per tick (each anim stream is < 16 entries).
            if (++iter > 16) break;

            frame = Memory.ReadSignedWord(fitPtr + (frameIndex & 0xFFFF) * 2);

            if (frame >= 0)
            {
                // updateSprite.cpp:124-126 — real frame: bump frameSwitchCounter,
                // store new imageIndex. SWOS's `setImage` writes raw (no
                // frameOffset add — see Sprite.h:125). `frameOffset` is added by
                // SetPlayerAnimationTableAndPictureIndex (PlayerActions.cs:1535)
                // when the table is *installed*, not on each tick.
                short fsCounter = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffFrameSwitchCounter);
                fsCounter = (short)(fsCounter + 1);
                Memory.WriteWord(spriteBase + PlayerSprite.OffFrameSwitchCounter, fsCounter);
                Memory.WriteWord(spriteBase + PlayerSprite.OffImageIndex, (short)frame);
            }
            else if (frame == kLastFrameLoopMarker)
            {
                // updateSprite.cpp:127-128 — wrap to start of stream.
                frameIndex = 0;
            }
            else if (frame == kLastFrameHoldMarker)
            {
                // updateSprite.cpp:129-131 — back off one and freeze on last frame.
                frameIndex = (short)(frameIndex - 1);
                break;
            }
            else if (frame <= kFrameLoopbackMarker)
            {
                // updateSprite.cpp:132-134 — relative jump back by (frame - (-100)).
                // frame is in (-998, -101], offset is negative.
                int offset = frame - kFrameLoopbackMarker;
                frameIndex = (short)(frameIndex + offset);
            }
            else
            {
                // updateSprite.cpp:135-140 — variable-pause opcode (-1..-99):
                // reset frameDelay and timer to -frame, then step past opcode.
                int newDelay = -frame;
                Memory.WriteWord(spriteBase + PlayerSprite.OffFrameDelay,       (short)newDelay);
                Memory.WriteWord(spriteBase + PlayerSprite.OffCycleFramesTimer, (short)newDelay);
                frameIndex = (short)(frameIndex + 1);
            }
        } while (frame < 0);

        Memory.WriteWord(spriteBase + PlayerSprite.OffFrameIndex, frameIndex);
    }

    // ===================================================================
    // moveSprite — updateSprite.cpp:157-186
    // ===================================================================
    //
    // Integrates the sprite's Q16.16 position by its Q16.16 delta:
    //     if (deltaX) x += deltaX; if (overshot destX) x = destX, deltaX = 0
    //     if (deltaY) y += deltaY; if (overshot destY) y = destY, deltaY = 0
    //     stopSpriteIfReachedDestination(sprite)  // belt-and-braces
    //
    // FixedPoint comparison semantics (FixedPoint.h:73, 79-83):
    //   `destX <= sprite.x` (int <= FixedPoint) → !(sprite.x > destX), i.e.
    //   `whole(x) < destX || (whole(x) == destX && fraction(x) == 0)`.
    // So `destX <= sprite.x` means "x has reached or passed destX with no
    // remainder fraction". We model that with Q16.16 raw compares: convert
    // destX into Q16.16 by `<< 16`, then `(destX << 16) <= xRaw` does exactly
    // the same compare (whole + fraction).
    //
    // On reach: production path (#ifndef SWOS_TEST) does `sprite.x = destX`
    // which uses FixedPoint::operator=(int) → `m_value = destX << 16` (clears
    // fraction).
    public static void MoveSprite(int spriteBase)
    {
        int destX = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffDestX);
        int destY = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffDestY);
        int destXq = destX << 16;
        int destYq = destY << 16;

        // updateSprite.cpp:159-170 — X integration.
        int deltaX = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffDeltaX);
        if (deltaX != 0)
        {
            int xRaw = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffX);
            xRaw += deltaX;
            // updateSprite.cpp:161 — reachedDestination test, sign-aware.
            bool reachedX = deltaX > 0 ? destXq <= xRaw : destXq >= xRaw;
            if (reachedX)
            {
                // updateSprite.cpp:166 — sprite.x = destX (FixedPoint(int) — clears fraction).
                xRaw = destXq;
                deltaX = 0;
                Memory.WriteDword(spriteBase + PlayerSprite.OffDeltaX, 0);
            }
            Memory.WriteDword(spriteBase + PlayerSprite.OffX, xRaw);
        }

        // updateSprite.cpp:172-183 — Y integration.
        int deltaY = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffDeltaY);
        if (deltaY != 0)
        {
            int yRaw = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffY);
            yRaw += deltaY;
            bool reachedY = deltaY > 0 ? destYq <= yRaw : destYq >= yRaw;
            if (reachedY)
            {
                yRaw = destYq;
                deltaY = 0;
                Memory.WriteDword(spriteBase + PlayerSprite.OffDeltaY, 0);
            }
            Memory.WriteDword(spriteBase + PlayerSprite.OffY, yRaw);
        }

        // updateSprite.cpp:185 — stopSpriteIfReachedDestination — belt-and-braces
        // for cases where deltaX/Y were already 0 on entry but the sprite is at
        // the dest. (The C++ does the same compare again — we just inline it.)
        StopSpriteIfReachedDestination(spriteBase, destX, destY);
    }

    // ===================================================================
    // stopSpriteIfReachedDestination — updateSprite.cpp:188-213
    // ===================================================================
    //
    // Final clamp: if deltaX/Y is non-zero and x/y has overshot dest, snap
    // and zero the delta. Production path (#ifndef SWOS_TEST) compares
    // `destX <= sprite.x` (FixedPoint compare — see above).
    private static void StopSpriteIfReachedDestination(int spriteBase, int destX, int destY)
    {
        int deltaX = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffDeltaX);
        if (deltaX != 0)
        {
            int xRaw = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffX);
            int destXq = destX << 16;
            bool stop = (deltaX > 0 && destXq <= xRaw) || (deltaX < 0 && destXq >= xRaw);
            if (stop)
            {
                Memory.WriteDword(spriteBase + PlayerSprite.OffX, destXq);
                Memory.WriteDword(spriteBase + PlayerSprite.OffDeltaX, 0);
            }
        }

        int deltaY = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffDeltaY);
        if (deltaY != 0)
        {
            int yRaw = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffY);
            int destYq = destY << 16;
            bool stop = (deltaY > 0 && destYq <= yRaw) || (deltaY < 0 && destYq >= yRaw);
            if (stop)
            {
                Memory.WriteDword(spriteBase + PlayerSprite.OffY, destYq);
                Memory.WriteDword(spriteBase + PlayerSprite.OffDeltaY, 0);
            }
        }
    }

    // ===================================================================
    // updateAnimationTableAndDestinationReached — updateSprite.cpp:215-229
    // ===================================================================
    //
    // After integrating motion, if the sprite is on-screen (or game not in
    // progress) AND in Normal state AND stationary (deltaX==deltaY==0),
    // optionally promote destReachedState to kReached (when game stopped +
    // breakCameraMode == 3) and switch the animation table to the normal
    // standing pose if it isn't already.
    //
    // PlayerState::kNormal = 0 (Sprite.h:16). DestinationState (Sprite.h:36-42):
    // kNotSet=0, kStarting=1, kTraveling=2, kReached=3. The 2 → 3 promotion
    // here is what tells the breakCameraMode ladder (gameLoop.cpp bcm3 stage)
    // that this sprite has finished its walk back to formation. The 1 → 2
    // promotion happens in UpdatePlayers (updatePlayers.cpp:8885-8900).
    //
    // We DO call SetPlayerAnimationTable when needed — wired through
    // PlayerActions.SetPlayerAnimationTable (already ported).
    private static void UpdateAnimationTableAndDestinationReached(int spriteBase)
    {
        // updateSprite.cpp:217 — onScreen || gameStatePl != kInProgress.
        short onScreen = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffOnScreen);
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        const short kStInProgress = 100;
        bool onScreenOrStopped = (onScreen != 0) || (gameStatePl != kStInProgress);

        // updateSprite.cpp:218 — state == kNormal && stationary().
        byte state = Memory.ReadByte(spriteBase + PlayerSprite.OffPlayerState);
        int dx = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffDeltaX);
        int dy = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffDeltaY);
        bool stationary = (dx == 0 && dy == 0);

        if (!(onScreenOrStopped && state == 0 && stationary)) return;

        // updateSprite.cpp:220-222 — promote destReachedState when game
        // stopped AND breakCameraMode == 3 AND currently traveling.
        //
        // BUG FIX (2026-07-02): the enum values were off by one — this used
        // to test ==1 and write 2. Per Sprite.h:36-42 DestinationState is
        // kNotSet=0, kStarting=1, kTraveling=2, kReached=3, so the correct
        // transition is 2 (kTraveling) → 3 (kReached). With the old values
        // the bcm ladder's "all players reached formation" poll (which waits
        // for 3) could never complete.
        short breakCameraMode = Memory.ReadSignedWord(Memory.Addr.breakCameraMode);
        if (gameStatePl != kStInProgress && breakCameraMode == 3)
        {
            short reachedState = Memory.ReadSignedWord(spriteBase + PlayerSprite.OffDestReachedState);
            if (reachedState == 2) // kTraveling (Sprite.h:40)
                Memory.WriteWord(spriteBase + PlayerSprite.OffDestReachedState, 3); // kReached (Sprite.h:41)
        }

        // updateSprite.cpp:223-227 — switch to standing anim table if not
        // already there. animTablePtr stored at OffAnimTablePtr (+6).
        int currentAnimTable = Memory.ReadSignedDword(spriteBase + PlayerSprite.OffAnimTablePtr);
        if (currentAnimTable != Memory.Addr.kPlayerStandingAnimTableAddr)
        {
            PlayerActions.SetPlayerAnimationTable(spriteBase, Memory.Addr.kPlayerStandingAnimTableAddr);
        }
    }
}
