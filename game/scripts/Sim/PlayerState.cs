namespace OpenSwos.Sim;

// SWOS goalkeeper state machine — defs.h:592-606. Outfielders only use Normal (and
// SlideTicks for tackle); keepers cycle through the rest. Numeric values match SWOS
// PlayerState enum so sprite-selector code can index directly.
public enum KeeperState : byte
{
    Normal = 0,
    CatchingBall = 4,  // 15-tick recovery after grab, then back to Normal
    DivingHigh = 6,    // Z >= 5 at dive trigger (ball above ground level)
    DivingLow = 7,     // Z <  5 at dive trigger
    Claimed = 11,      // holding ball + gameplay frozen
}

// One player's per-tick gameplay state. Lives inside the deterministic simulation —
// no floats here, no Godot types. Render layer reads this via accessors that convert
// to Vector2.
public struct PlayerState
{
    public Fixed X;
    public Fixed Y;
    public Fixed VelocityX;
    public Fixed VelocityY;
    public Direction Facing;
    public bool IsMoving;
    public byte AnimationTick;   // counts physics ticks since the current move started
    // Slide tackle progress. >0 means the player is mid-slide; ticks down each frame.
    // During a slide we lock facing/direction and the sprite picks the Slide cell.
    public byte SlideTicks;
    // Effective movement speed in Q8.8 raw units (set each tick by PlayerSim.Tick).
    // 1024 = exactly walking pace = 4 px/tick. SWOS uses 928..1250 for skill 0..7.
    public ushort EffectiveSpeed;

    // === KEEPER-ONLY FIELDS ====================================================
    // Outfielders leave Goalie* at default. Only the two keepers' instances drive
    // these. Centralising on PlayerState avoids a parallel keeper struct.
    public KeeperState GoalieState;
    // Counts down while in DivingHigh/DivingLow (initial 75) and CatchingBall
    // (initial 15). Mirrors SWOS playerDownTimer (updatePlayers.cpp:10962, 11051).
    public byte GoaliePlayerDownTimer;
    // While in Claimed: ticks since the catch. At 150 the keeper auto-kicks to the
    // closest team-mate (updatePlayers.cpp:16759 stoppageTimerActive ≥ 150).
    public byte GoalieClaimedTimer;
    // Goalkeeper stat 0..7 from TeamRecord. Indexes kGoalkeeperDiveDeltas so a
    // skilled keeper reaches further. NOT used for catch radius (that stays fixed).
    public byte GoalieSkill;
    // Y axis (-1 north end, +1 south end). North-end keeper Y < ball ⇒ shot incoming.
    public sbyte GoalieFaceTeamY;

    // === DRIBBLE / BALL-CONTROL STATE ============================================
    // Outfielders only. Tracks previous facing for detecting "turn while dribbling"
    // events — SWOS triggers Ball Control loss rolls when player changes direction
    // with the ball. We simulate this without full updatePlayers port via a per-tick
    // probability check in BallSim. byte 255 = uninitialised (no last facing).
    public byte LastDribbleFacing;
    // 0..6: how many ticks the current dribble has lasted (capped to 6). Higher
    // values increase loss probability slightly (proxy for "running with ball").
    public byte DribbleTickCount;

    public static PlayerState At(int x, int y) => new()
    {
        X = Fixed.FromInt(x),
        Y = Fixed.FromInt(y),
        Facing = Direction.South,
        EffectiveSpeed = 1024,
    };
}

public enum Direction : byte
{
    North = 0,
    NorthEast = 1,
    East = 2,
    SouthEast = 3,
    South = 4,
    SouthWest = 5,
    West = 6,
    NorthWest = 7,
}

public readonly struct InputState
{
    public readonly sbyte DirX; // -1, 0, +1
    public readonly sbyte DirY; // -1, 0, +1
    public readonly bool Action;   // kick
    public readonly bool Slide;    // sliding tackle
    public InputState(sbyte dx, sbyte dy, bool action, bool slide = false)
    {
        DirX = dx;
        DirY = dy;
        Action = action;
        Slide = slide;
    }

    public static readonly InputState Idle = new(0, 0, false);
}

// Ported from SWOS: source = external/swos-port/docs/SWOS/game.txt "Player speed" section.
// Speed values are Q8.8 fixed-point pixels per tick (compatible with our Fixed.FromRaw).
// 1024 = 4 px/tick = 280 px/s @ 70 Hz.
public static class PlayerSpeedTable
{
    // dw 928, 974, 1020, 1066, 1112, 1158, 1204, 1250  ; game in progress
    public static readonly ushort[] InProgress = { 928, 974, 1020, 1066, 1112, 1158, 1204, 1250 };
    // dw 1136, 1152, 1168, 1184, 1200, 1216, 1232, 1248 ; game stopped
    public static readonly ushort[] Stopped = { 1136, 1152, 1168, 1184, 1200, 1216, 1232, 1248 };
    public const int MaxSpeed = 1280;
    // 87.5% of capacity for the player carrying the ball (game.txt).
    public const int BallCarrierMul = 224;
    // 62.5% when running back to position after a goal.
    public const int RunbackMul = 160;
    // 78.125% during a human slide; 50% during a CPU slide.
    public const int HumanSlideMul = 200;
    public const int CpuSlideMul = 128;
}

public static class PlayerSim
{
    // Slide duration in ticks. Hand-tuned scaled for 70 Hz (was 24 @ 50 Hz → 34 @ 70 Hz);
    // SWOS original value not yet traced from sub-state machine.
    public const int SlideDurationTicks = 34;

    // Movement scale multiplier. 256 = 100% SWOS-authentic player speed (matches
    // swos-port behaviour). Earlier session used 128 (50%) as user-requested tuning,
    // reverted 2026-05-20 to align with swos-port reference.
    public const int MovementScaleNum = 256;

    // effectiveSpeed is Q8.8 (SWOS-correct, un-scaled). PlayerSpeedTable.InProgress[skill]
    // possibly multiplied by BallCarrierMul / RunbackMul. slideMul (Q8) chooses human
    // vs CPU slide. MovementScaleNum is applied internally so animation can read the
    // raw value.
    public static void Tick(ref PlayerState p, InputState input, int effectiveSpeed,
        int slideMul = PlayerSpeedTable.HumanSlideMul)
    {
        // Save the RAW SWOS-correct speed — the animation-pace formula needs the
        // un-scaled value so cycle timing stays SWOS-authentic regardless of how much
        // we scale movement.
        p.EffectiveSpeed = (ushort)System.Math.Clamp(effectiveSpeed, 0, PlayerSpeedTable.MaxSpeed);

        // Apply movement scale once, here. Velocity calculations below use the scaled
        // value; animation uses the raw stored EffectiveSpeed.
        int moveSpeed = effectiveSpeed * MovementScaleNum / 256;

        // (1) Start a new slide if requested and not already sliding.
        if (input.Slide && p.SlideTicks == 0)
        {
            if (input.DirX != 0 || input.DirY != 0)
                p.Facing = DirectionFrom(input.DirX, input.DirY);
            p.SlideTicks = SlideDurationTicks;
        }

        // (2) Mid-slide: facing locked, speed = moveSpeed * slideMul / 256.
        if (p.SlideTicks > 0)
        {
            var (fx, fy) = FacingUnit(p.Facing);
            int diagSlide = (fx != 0 && fy != 0) ? 181 : 256;
            int slideSpeed = moveSpeed * slideMul / 256;
            p.VelocityX = Fixed.FromRaw(fx * slideSpeed * diagSlide / 256);
            p.VelocityY = Fixed.FromRaw(fy * slideSpeed * diagSlide / 256);
            // PC mode 41/64 damping on sprite delta (swos-port updateSprite.cpp:323-328).
            p.X += Fixed.FromRaw(p.VelocityX.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);
            p.Y += Fixed.FromRaw(p.VelocityY.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);
            p.SlideTicks--;
            p.IsMoving = true;
            p.AnimationTick = 0;
            return;
        }

        // (3) Normal movement.
        int sx = input.DirX;
        int sy = input.DirY;
        int diag = (sx != 0 && sy != 0) ? 181 : 256;

        p.VelocityX = Fixed.FromRaw(sx * moveSpeed * diag / 256);
        p.VelocityY = Fixed.FromRaw(sy * moveSpeed * diag / 256);
        // PC mode 41/64 damping on sprite delta (swos-port updateSprite.cpp:323-328).
        p.X += Fixed.FromRaw(p.VelocityX.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);
        p.Y += Fixed.FromRaw(p.VelocityY.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);

        bool moving = sx != 0 || sy != 0;
        if (moving)
        {
            p.Facing = DirectionFrom(sx, sy);
            p.AnimationTick++;
        }
        else
        {
            p.AnimationTick = 0;
        }
        p.IsMoving = moving;
    }

    // Frame in [0..2] picked from AnimationTick. Returns -1 if standing, -2 if sliding.
    // Frame-delay formula from SWOS (game.txt): delay = (1280 - speed) / 128 + 6.
    // Faster players → smaller delay → faster animation cycle.
    public static int AnimationPhase(in PlayerState p)
    {
        if (p.SlideTicks > 0) return -2;
        if (!p.IsMoving) return -1;
        int speed = p.EffectiveSpeed > 0 ? p.EffectiveSpeed : 1024;
        int delay = (PlayerSpeedTable.MaxSpeed - speed) / 128 + 6;
        if (delay < 1) delay = 1;
        return (p.AnimationTick / delay) % 3;
    }

    public static (int x, int y) FacingUnit(Direction d) => d switch
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

    private static Direction DirectionFrom(int sx, int sy)
    {
        // sx, sy ∈ {-1, 0, 1}. Map (sx, sy) to one of 8 compass directions.
        return (sx, sy) switch
        {
            ( 0, -1) => Direction.North,
            ( 1, -1) => Direction.NorthEast,
            ( 1,  0) => Direction.East,
            ( 1,  1) => Direction.SouthEast,
            ( 0,  1) => Direction.South,
            (-1,  1) => Direction.SouthWest,
            (-1,  0) => Direction.West,
            (-1, -1) => Direction.NorthWest,
            _ => Direction.South,
        };
    }
}
