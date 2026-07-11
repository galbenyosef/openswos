namespace OpenSwos.Sim;

public struct BallState
{
    public Fixed X;
    public Fixed Y;
    public Fixed Z;          // height above ground (0 = on the pitch, >0 = airborne)
    public Fixed VelocityX;
    public Fixed VelocityY;
    public Fixed VelocityZ;  // positive = ball rising
    // Kicker exclusion: while > 0 the player at KickerExclusionIdx can't possess the
    // ball, so it can escape the kicker's foot. Mirrors SWOS post-kick state where
    // the ball has destX/destY committed and the kicker can't immediately re-grab.
    public sbyte KickerExclusionIdx;
    public byte KickerExclusionTicks;
    // Dribble tracking — used by BallSim.Tick to detect "turn while dribbling"
    // events that trigger Ball Control loss probability rolls. Persists across
    // ticks (PlayerInfluence is readonly so we can't store this on PlayerState).
    public sbyte CurrentDribblerIdx;       // -1 = no dribbler this tick
    public byte LastDribbleFacing;         // 255 = uninitialised
    public byte DribbleTickCount;          // 0..6, capped

    public static BallState At(int x, int y) => new()
    {
        X = Fixed.FromInt(x),
        Y = Fixed.FromInt(y),
        KickerExclusionIdx = -1,
        CurrentDribblerIdx = -1,
        LastDribbleFacing = 255,
    };
}

public static class BallSim
{
    // === CONSTANTS PORTED FROM SWOS ============================================
    // Source: external/swos-port/swos/swos.asm (real values from SWS.EXE) plus
    // external/swos-port/src/game/amigaMode.cpp for the Amiga-vs-PC split.
    //
    // SWOS uses Q8.8 fixed-point for ball "speed" (16-bit word) — kBallKickingSpeed
    // 2208 = 8.625 px/tick. Our Fixed is Q24.8, so the low byte matches; raw values
    // from SWOS plug straight into Fixed.FromRaw() for velocity magnitudes.
    //
    // SWOS uses Q16.16 for deltaZ / gravity (32-bit dword). To map into our Q24.8 we
    // shift right by 8 (drop 8 bits of fractional precision).
    //
    // We target the **PC** numbers — swos-port boots in PC mode by default
    // (swos.ini:15 gameStyle=0, options.cpp:100 default = kPcGameStyle). Earlier audit
    // 2026-05-19 assumed Amiga values but the running swos-port reference uses PC.
    // CLAUDE.md says "Amiga is reference target" but we'll fix that drift later — for
    // now the priority is matching what swos-port actually does on user's machine.

    // Linear friction — ported from external/swos-port/src/game/ball/ball.cpp:240-297.
    // SWOS scalar speed model: each tick speed -= constant.
    //   amigaMode.cpp:35,53  kBallGroundConstant  Amiga=16   PC=13
    //   amigaMode.cpp:36,54  kBallAirConstant     Amiga=10   PC=4
    public const int GroundFrictionPerTick = 13;
    public const int AirFrictionPerTick = 4;

    // Possession capture radius — outfielder default. Keepers override via
    // PlayerInfluence.CatchRadius (24 px). 8 = small enough that the kicker doesn't
    // re-glue the ball after kicker-exclusion expires (user request — was 14, ball
    // kept sticking back to foot after short kicks).
    public const int InteractionRadius = 8;
    // Dribble offset — ported from external/swos-port/swos/swos.asm:245818
    //   kBallPlOffsets dw 0,-1, 1,-1, 1,0, 1,1, 0,1, -1,1, -1,0, -1,-1
    // These are *unit vectors per direction* and the comment in player.cpp:196 says
    // "Ball is always about a pixel in front of player, wherever he may be turned."
    // Earlier value of 10 was vibe-coded — ball appeared way too far ahead of the
    // dribbling player.
    public const int DribbleOffset = 1;

    // Kick speeds — SWOS-authentic raw values from swos.asm. Earlier session had
    // these scaled × 0.75 ("halved then +50 %") as a vibe-coded tuning, but now we
    // pivot to swos-port as primary reference and revert to source-of-truth values.
    //   swos.asm:203952  kBallKickingSpeed     2208  (default kick)
    //   swos.asm:203956  kHighKickBallSpeed    2688  (high shot, hold + back)
    //   swos.asm:203961  kNormalKickBallSpeed  2560  (used by ApplyBallAfterTouch)
    public const int KickPower = 2208;
    public const int HighKickPower = 2688;
    public const int NormalKickPower = 2560;

    // SWOS post-kick speed reduction (game.txt:86):
    //   "Nakon suta umanji brzinu lopte na 87.5% ako je sut u dijagonalnom pravcu,
    //    na 75% za ostale pravce." — cardinal × 0.75, diagonal × 0.875.
    public const int PostKickCardinalPct = 192;   // 192/256 = 0.75
    public const int PostKickDiagonalPct = 224;   // 224/256 = 0.875

    // Slide tackle punch — uses swos.asm:203924 kPlayerTacklingSpeed.
    public const int SlidePunchPower = 1792;
    public const int SlideKickLoft = 256;         // small hop — not from SWOS

    // Vertical loft applied on every kick.
    //   swos.asm:203950  kBallKickingDeltaZ    0x14000 = 81920 (Q16.16) → 320 (Q24.8) = 1.25 px/tick
    //   amigaMode.cpp:37 kGravityConstant      4608    (Q16.16) → 18  (Q24.8) = 0.07 px/tick²
    //   swos.asm:203810  kBallJumpHeaderDeltaZ 0xA000  = 40960 (Q16.16) → 160 (Q24.8) = 0.625 px/tick
    // Peak height ≈ 320² / (2 × 18) = 2844 raw = ~11 px, reached in ~18 ticks (~0.7 s).
    // Lower lob than our earlier vibe value (1024 → 4 px/tick) — matches SWOS feel.
    public const int KickLoft = 320;              // kBallKickingDeltaZ (Q16.16 → Q24.8)
    public const int HeaderJumpZ = 160;           // kBallJumpHeaderDeltaZ (Q16.16 → Q24.8)
    // PC gravity 3291 (Q16.16) → 12.9 Q24.8, round to 13.
    //   amigaMode.cpp:37,55  kGravityConstant  Amiga=4608(→18)  PC=3291(→13)
    public const int Gravity = 13;

    // PC sprite delta damping factor (41/64 = 0.640625). swos-port updateSprite.cpp:323-328:
    // in PC mode every sprite movement delta (player AND ball XY) is multiplied by 41/64
    // before being added to position. Friction still operates on the un-damped velocity
    // magnitude. WITHOUT this damping the game runs ~1.55× too fast — exactly matches
    // user's earlier empirical x0.65 setting that made everything "feel right".
    // The multiplier uses integer shifts in the original (sin - sin/4 - sin/16 - sin/32
    // - sin/64) for bit-exact compatibility; we use the equivalent fraction.
    public const int PcSpriteDeltaNum = 41;
    public const int PcSpriteDeltaDen = 64;

    // === PER-PITCH BOUNCE / FRICTION TABLES =====================================
    // Ported from swos-port. Pitch CONDITION (not the visual variant) is indexed
    // 0=Frozen, 1=Muddy, 2=Wet, 3=Soft, 4=Normal, 5=Dry, 6=Hard. swos.ini
    // default = 4 (Normal). Current condition stored in CurrentPitchType so a future
    // pre-match menu can vary it; for netcode safety all peers must agree.
    //
    //   swos.asm:203822  kBallSpeedBounceFactorTable
    //   swos.asm:203824  kBallBounceFactorTable
    //   game.cpp:1389-1400  kPitchBallSpeedInfluence (PC variant)
    //   ball.cpp:494-550  bounce mechanics
    public static readonly int[] BallSpeedBounceFactors = { 24, 80, 80, 72, 64, 40, 32 };
    public static readonly int[] BallBounceFactors      = { 88, 112, 104, 104, 96, 88, 80 };
    public static readonly int[] PitchBallSpeedInfluence = { -3, 4, 1, 0, 0, -1, -1 };  // PC values
    public static int CurrentPitchType = 4;  // Normal (default in swos.ini)

    // Selected pitch *variant* (0..3) — the visual map that loads. Picked by
    // Pitch.SetPitchNumber (pitch.cpp:259-284). 0..3 maps onto SWCPICH{1..4}.MAP
    // via kPitchNumberProbabilities[16]. Persists between matches until reset.
    public static int CurrentPitchNumber = 0;

    // Sticky-ball threshold (Q24.8 raw, = 0.625 px/tick). After a bounce, if reflected
    // Z velocity is below this the ball settles instead of micro-bouncing forever.
    // SWOS uses 40960 Q16.16 → 160 Q24.8. ball.cpp:545-550.
    public const int StickyBallVelocityZ = 160;

    // One sim tick. The closest player within InteractionRadius takes possession:
    //   - If they're tapping kick: ball gets a hard velocity in their facing direction.
    //   - Otherwise: ball is glued 10 px in front of them with their velocity (dribble).
    // No player in possession → normal physics (move + friction).
    public static void Tick(ref BallState ball, System.ReadOnlySpan<PlayerInfluence> influences)
    {
        // Decrement kicker-exclusion cooldown — once it lapses the recent kicker
        // may possess the ball again.
        if (ball.KickerExclusionTicks > 0) ball.KickerExclusionTicks--;
        if (ball.KickerExclusionTicks == 0) ball.KickerExclusionIdx = -1;

        // Possession is decided in TWO passes:
        //   1. Keepers (CatchRadius > 0) get FIRST refusal in their wider catch range
        //      AND can catch balls up to Z<32 (jump/dive). Mirrors SWOS keeper priority.
        //   2. Outfielders only get the ball if no keeper has it AND ball.Z < 8 (on ground).
        // The recent kicker (KickerExclusionIdx) is excluded from BOTH passes for
        // KickerExclusionTicks frames so the ball can escape his own foot.
        int possessor = -1;
        int closestSq = int.MaxValue;

        // Pass 1: keepers, allow higher Z for catch.
        if (ball.Z.Raw < 32 * Fixed.One)
        {
            for (int i = 0; i < influences.Length; i++)
            {
                if (i == ball.KickerExclusionIdx) continue;
                if (influences[i].CatchRadius == 0) continue;
                int dx = ball.X.ToInt() - influences[i].Player.X.ToInt();
                int dy = ball.Y.ToInt() - influences[i].Player.Y.ToInt();
                int sq = dx * dx + dy * dy;
                int radius = influences[i].CatchRadius;
                if (sq < radius * radius && sq < closestSq)
                {
                    possessor = i;
                    closestSq = sq;
                }
            }
        }

        // Pass 2: outfielders, only if no keeper got the ball and ball is grounded.
        if (possessor < 0 && ball.Z.Raw < 8 * Fixed.One)
        {
            for (int i = 0; i < influences.Length; i++)
            {
                if (i == ball.KickerExclusionIdx) continue;
                if (influences[i].CatchRadius != 0) continue;  // skip keepers
                int dx = ball.X.ToInt() - influences[i].Player.X.ToInt();
                int dy = ball.Y.ToInt() - influences[i].Player.Y.ToInt();
                int sq = dx * dx + dy * dy;
                if (sq < InteractionRadius * InteractionRadius && sq < closestSq)
                {
                    possessor = i;
                    closestSq = sq;
                }
            }
        }

        if (possessor >= 0)
        {
            ref readonly var inf = ref influences[possessor];
            (int dx, int dy) = DirectionUnit(inf.Player.Facing);
            if (inf.Player.SlideTicks > 0)
            {
                // Slide tackle — punch the ball forward, drop possession.
                int diagS = (dx != 0 && dy != 0) ? 181 : 256;
                ball.VelocityX = Fixed.FromRaw(dx * SlidePunchPower * diagS / 256);
                ball.VelocityY = Fixed.FromRaw(dy * SlidePunchPower * diagS / 256);
                ball.VelocityZ = Fixed.FromRaw(SlideKickLoft);
                // PC mode applies 41/64 damping to XY sprite deltas (Z unaffected).
                ball.X += Fixed.FromRaw(ball.VelocityX.Raw * PcSpriteDeltaNum / PcSpriteDeltaDen);
                ball.Y += Fixed.FromRaw(ball.VelocityY.Raw * PcSpriteDeltaNum / PcSpriteDeltaDen);
            }
            else if (inf.Kick)
            {
                // SWOS three kick types — see KickType enum docs and game.txt:34, 84-86.
                int basePower;
                int baseLoft;
                switch (inf.KickType)
                {
                    case KickType.Pass:
                        basePower = (inf.KickPower > 0 ? inf.KickPower : KickPower) * 9 / 8; // +12.5 %
                        baseLoft = 0;                  // flat pass, no Z lift
                        break;
                    case KickType.HighShot:
                        basePower = HighKickPower;     // 1344 raw
                        baseLoft = KickLoft * 2;       // doubled lob arc
                        break;
                    default:                            // Shot
                        basePower = inf.KickPower > 0 ? inf.KickPower : KickPower;
                        baseLoft = KickLoft;
                        break;
                }
                // SWOS post-kick reduction: cardinal × 0.75, diagonal × 0.875.
                int postKickPct = (dx != 0 && dy != 0) ? PostKickDiagonalPct : PostKickCardinalPct;
                int power = basePower * postKickPct / 256;
                // Diagonal normalisation so magnitude == power regardless of direction.
                int diagK = (dx != 0 && dy != 0) ? 181 : 256;
                ball.VelocityX = Fixed.FromRaw(dx * power * diagK / 256);
                ball.VelocityY = Fixed.FromRaw(dy * power * diagK / 256);
                ball.VelocityZ = Fixed.FromRaw(baseLoft);
                // PC mode applies 41/64 damping to XY sprite deltas (Z unaffected).
                ball.X += Fixed.FromRaw(ball.VelocityX.Raw * PcSpriteDeltaNum / PcSpriteDeltaDen);
                ball.Y += Fixed.FromRaw(ball.VelocityY.Raw * PcSpriteDeltaNum / PcSpriteDeltaDen);
                // Lock the kicker out so the ball can escape before re-possession.
                ball.KickerExclusionIdx = (sbyte)possessor;
                ball.KickerExclusionTicks = 12;
                // Kick ends dribble — reset state.
                ball.CurrentDribblerIdx = -1;
                ball.LastDribbleFacing = 255;
                ball.DribbleTickCount = 0;
            }
            else
            {
                // Dribble — Ball Control probability check.
                // SWOS varies dribble retention by per-player Ball Control stat (1-7);
                // we don't yet track per-player stats (port pending B6). For now a
                // uniform probabilistic loss kicks in when:
                //   - player TURNS while dribbling (direction changed)  → 25% nudge chance
                //   - or just ticking for a while  → 3% per tick after 4 ticks of dribble
                // State persistence lives on BallState (LastDribbleFacing, DribbleTickCount)
                // because PlayerInfluence is readonly. CurrentDribblerIdx tracks who's on
                // the ball this tick — if it changes the timer resets.
                bool sameDribbler = ball.CurrentDribblerIdx == (sbyte)possessor;
                bool turning = sameDribbler && ball.LastDribbleFacing != 255
                            && ball.LastDribbleFacing != (byte)inf.Player.Facing;
                int tickCount = sameDribbler ? ball.DribbleTickCount : 0;

                // 0..99 from a cheap deterministic hash — no System.Random (lockstep).
                int hash = (inf.Player.X.ToInt() * 31 + inf.Player.Y.ToInt() * 17
                          + tickCount * 13 + possessor * 7) & 0x7FFFFFFF;
                int roll = hash % 100;
                int threshold = turning ? 25 : (tickCount >= 4 ? 3 : 0);

                if (roll < threshold)
                {
                    // Ball nudged perpendicular to facing direction — escapes the foot.
                    // Re-possession blocked for 14 ticks (~0.2s @ 70Hz).
                    int pdx = -dy;  // 90° rotation
                    int pdy = dx;
                    ball.X = Fixed.FromInt(inf.Player.X.ToInt() + dx * DribbleOffset);
                    ball.Y = Fixed.FromInt(inf.Player.Y.ToInt() + dy * DribbleOffset);
                    ball.VelocityX = Fixed.FromRaw(pdx * 384);  // ~1.5 px/tick perpendicular
                    ball.VelocityY = Fixed.FromRaw(pdy * 384);
                    ball.Z = Fixed.FromInt(0);
                    ball.VelocityZ = Fixed.FromInt(0);
                    ball.KickerExclusionIdx = (sbyte)possessor;
                    ball.KickerExclusionTicks = 14;
                    // Dribble interrupted — reset state.
                    ball.CurrentDribblerIdx = -1;
                    ball.LastDribbleFacing = 255;
                    ball.DribbleTickCount = 0;
                }
                else
                {
                    // Normal dribble pin — ball at player FOOT + 1-px unit-vector offset.
                    ball.X = Fixed.FromInt(inf.Player.X.ToInt() + dx * DribbleOffset);
                    ball.Y = Fixed.FromInt(inf.Player.Y.ToInt() + dy * DribbleOffset);
                    ball.VelocityX = inf.Player.VelocityX;
                    ball.VelocityY = inf.Player.VelocityY;
                    ball.Z = Fixed.FromInt(0);
                    ball.VelocityZ = Fixed.FromInt(0);
                    // Update dribble tracking on ball state.
                    ball.CurrentDribblerIdx = (sbyte)possessor;
                    ball.LastDribbleFacing = (byte)inf.Player.Facing;
                    if (ball.DribbleTickCount < 6) ball.DribbleTickCount++;
                }
            }
        }
        else
        {
            // PC mode applies 41/64 damping to XY sprite deltas (Z unaffected).
            ball.X += Fixed.FromRaw(ball.VelocityX.Raw * PcSpriteDeltaNum / PcSpriteDeltaDen);
            ball.Y += Fixed.FromRaw(ball.VelocityY.Raw * PcSpriteDeltaNum / PcSpriteDeltaDen);
            // No dribbler this tick (free ball).
            if (ball.CurrentDribblerIdx != -1)
            {
                ball.CurrentDribblerIdx = -1;
                ball.LastDribbleFacing = 255;
                ball.DribbleTickCount = 0;
            }
        }

        // Z-axis: apply VZ, then gravity. Detect bounce moment (transition airborne →
        // ground) so XY-speed and Z-velocity damping fires EXACTLY once per impact, not
        // every tick while Z ≤ 0. swos-port ball.cpp:494-550.
        bool wasAirborne = ball.Z.Raw > 0;
        ball.Z += ball.VelocityZ;
        ball.VelocityZ = Fixed.FromRaw(ball.VelocityZ.Raw - Gravity);
        if (ball.Z.Raw <= 0)
        {
            ball.Z = Fixed.FromInt(0);
            if (wasAirborne && ball.VelocityZ.Raw < 0)
            {
                // BOUNCE MOMENT. Lookup per-pitch factors (0..6, clamped).
                int pitch = CurrentPitchType;
                if (pitch < 0) pitch = 0; else if (pitch > 6) pitch = 6;
                // XY speed reduction — SWOS speed -= (speed * bounceFactor) >> 8.
                // Normal pitch: factor=64 → 75% retained. Frozen: 91% retained. Hard: 87%.
                int speedBounce = BallSpeedBounceFactors[pitch];
                int keepNum = 256 - speedBounce;
                ball.VelocityX = Fixed.FromRaw((int)((long)ball.VelocityX.Raw * keepNum / 256));
                ball.VelocityY = Fixed.FromRaw((int)((long)ball.VelocityY.Raw * keepNum / 256));
                // Z velocity reflection + per-pitch damping.
                int zKeep = BallBounceFactors[pitch];
                int reflected = (int)((long)(-ball.VelocityZ.Raw) * zKeep / 256);
                if (reflected < StickyBallVelocityZ)
                {
                    // Sticky-ball: small bounce → settle on ground. Stops micro-bounce
                    // loop that previously kept ball in air-friction mode forever.
                    ball.VelocityZ = Fixed.FromInt(0);
                }
                else
                {
                    // | 1 mirrors swos-port ball.cpp:536 — guarantees non-zero so the
                    // bounce never collapses to 0 right above the sticky threshold.
                    ball.VelocityZ = Fixed.FromRaw(reflected | 1);
                }
            }
            else if (ball.VelocityZ.Raw < 0)
            {
                // Already on ground (not a fresh impact) — clamp any negative VZ from
                // gravity to 0 so the ball stays put.
                ball.VelocityZ = Fixed.FromInt(0);
            }
        }

        ApplyLinearFriction(ref ball);
    }

    // SWOS-style linear decay: compute scalar speed from velocity vector, subtract
    // the per-tick constant (ground or air), then rescale velocity to the new speed
    // (preserving direction). Deterministic — IntSqrt is integer-only for netcode.
    private static void ApplyLinearFriction(ref BallState ball)
    {
        long vxRaw = ball.VelocityX.Raw;
        long vyRaw = ball.VelocityY.Raw;
        if (vxRaw == 0 && vyRaw == 0) return;

        long speedSq = vxRaw * vxRaw + vyRaw * vyRaw;
        int speed = IntSqrt(speedSq);
        int decay;
        if (ball.Z.Raw > 0)
        {
            decay = AirFrictionPerTick;
        }
        else
        {
            // SWOS adds pitchBallSpeedInfluence to ground friction only when ball is
            // free (no possessor). ApplyLinearFriction runs only in the free branch of
            // BallSim.Tick, so we apply it unconditionally here. Normal pitch (4) gives
            // +0 — no behaviour change. Muddy +4 = stronger decay, Frozen -3 = weaker.
            // swos-port ball.cpp:258-264.
            int pitch = CurrentPitchType;
            if (pitch < 0) pitch = 0; else if (pitch > 6) pitch = 6;
            decay = GroundFrictionPerTick + PitchBallSpeedInfluence[pitch];
            if (decay < 1) decay = 1;
        }
        int newSpeed = speed - decay;
        if (newSpeed <= 0)
        {
            ball.VelocityX = Fixed.FromInt(0);
            ball.VelocityY = Fixed.FromInt(0);
        }
        else
        {
            ball.VelocityX = Fixed.FromRaw((int)(vxRaw * newSpeed / speed));
            ball.VelocityY = Fixed.FromRaw((int)(vyRaw * newSpeed / speed));
        }
    }

    // Integer Newton-Raphson sqrt — deterministic across platforms (no float).
    public static int IntSqrt(long n)
    {
        if (n <= 0) return 0;
        long x = n;
        long y = (x + 1) / 2;
        while (y < x)
        {
            x = y;
            y = (x + n / x) / 2;
        }
        return (int)x;
    }

    private static (int x, int y) DirectionUnit(Direction d) => d switch
    {
        Direction.North     => ( 0, -1),
        Direction.NorthEast => ( 1, -1),
        Direction.East      => ( 1,  0),
        Direction.SouthEast => ( 1,  1),
        Direction.South     => ( 0,  1),
        Direction.SouthWest => (-1,  1),
        Direction.West      => (-1,  0),
        Direction.NorthWest => (-1, -1),
        _ => (0, 1),
    };

    // (FootOffset removed — PlayerState.X/Y now is the foot world position itself, so
    // dribble ball offset is just SWOS's 1-px direction unit vector. The sprite
    // renderer in Main.UpdateSprite shifts the tile by (8 - cx, 8 - cy) so the foot
    // anchor lands on the world position.)
}

// Three SWOS kick styles, picked by Main based on hold time + back-press:
//   Shot     — auto-fired after 4 ticks of hold, normal loft, base power.
//   Pass     — released before 4-tick wind-up, +12.5 % speed (game.txt:34), no loft.
//   HighShot — back-press during wind-up (game.txt:84-86), uses HighKickPower
//              with doubled loft for an over-the-top lob.
public enum KickType : byte
{
    Shot = 0,
    Pass,
    HighShot,
}

public readonly struct PlayerInfluence
{
    public readonly PlayerState Player;
    public readonly bool Kick;
    public readonly short KickPower;  // 0 = use BallSim default per KickType
    public readonly byte CatchRadius; // 0 = use BallSim.InteractionRadius
    public readonly KickType KickType;
    public PlayerInfluence(PlayerState player, bool kick, short kickPower = 0,
        byte catchRadius = 0, KickType kickType = KickType.Shot)
    {
        Player = player;
        Kick = kick;
        KickPower = kickPower;
        CatchRadius = catchRadius;
        KickType = kickType;
    }
}
