namespace OpenSwos.Sim;

// SWOS goalkeeper logic — ported from swos-port src/game/updatePlayers/updatePlayers.cpp
// (~lines 10485-10962 dive, 11045-11098 catch, 16656-17190 hold + auto-release) and
// amigaMode.cpp:9-14 (dive deltas).
//
// Pipeline per sim tick (called from Main.TickPlay before BallSim.Tick):
//   1. KeeperSim.Update(ref keeper, ball, faceTeamY)
//      - Advances state machine: dive recovery → CatchingBall → Normal → Claimed → Normal
//      - Triggers a dive if shouldGoalkeeperDive() passes (Y proximity + reach check)
//      - Moves the keeper toward predicted ball arrival (ballNextGroundX) or home spot
//   2. BallSim.Tick uses PlayerInfluence.CatchRadius (still 16) for the dive grab and
//      the wider 72 px secondary radius when keeper is committed to a dive.
public static class KeeperSim
{
    // === SWOS CONSTANTS ========================================================
    // PC mode. Amiga values differ — see comments.

    // kGoalkeeperDiveDeltas — fixed-point Q16.16 horizontal dive distance per frame,
    // indexed by keeper Goalkeeper skill 0..7. PC: 2.5..6.0 px/frame; Amiga: 3.0..6.5.
    //   amigaMode.cpp:12-13
    public static readonly int[] DiveDeltaByPcSkill =
    {
        0x28000, 0x30000, 0x38000, 0x40000, 0x48000, 0x50000, 0x58000, 0x60000,
    };

    // Catch radius (kKeeperSaveDistance) — used for dive trigger Y proximity check.
    //   amigaMode.cpp:34 (Amiga=24) / amigaMode.cpp:52 (PC=16)
    public const int SaveDistance = 16;

    // Active catch radius once the ball is close enough. Wider than SaveDistance so a
    // dive that ALMOST reached still scoops the ball at the end of the arc.
    //   updatePlayers.cpp:2804 ballDistance ≤ 72 (squared 5184)
    public const int CatchRadius = 72;

    // Ball Z threshold for catch — keeper grabs only balls below this height.
    //   updatePlayers.cpp:2809 ballAbove17
    public const int CatchZMax = 17;

    // Dive recovery duration. After this the keeper transitions to CatchingBall or
    // Normal depending on whether he reached the ball.
    //   updatePlayers.cpp:10962 playerDownTimer = 75
    public const int DiveRecoveryTicks = 75;

    // "Catch within dive" — if remaining downTimer is ≥ this the keeper catches
    // cleanly, otherwise he can only parry. updatePlayers.cpp:3141 vs :3144.
    public const int CatchVsParryThreshold = 60;

    // Catch state animation duration.
    //   updatePlayers.cpp:11051 playerDownTimer = 15
    public const int CatchAnimationTicks = 15;

    // Auto-release while keeper is Claimed. After this many ticks the CPU keeper
    // looks for a teammate and kicks. updatePlayers.cpp:16759 stoppageTimerActive ≥ 150
    // (at 70 Hz = ~2.14 s).
    // Auto-release timer — was 150 (matched swos-port stoppageTimerActive at 50Hz Amiga
    // = 3.0s). At our 70Hz PC tick rate that's 2.14s. User reported "keeper holds too long
    // and doesn't kick out". Reduced 2026-05-24 to ~1.3s (90 ticks @ 70Hz) which feels
    // closer to SWOS PC behaviour. Real fix is porting findClosestPlayerToBallFacing
    // + playerKickingBall (B5 follow-up) which run before 150-tick auto-fire.
    public const int ClaimAutoReleaseTicks = 90;

    // Goal mouth X bounds (pitch coordinates). updatePlayers.cpp:1287-1350.
    public const int GoalMouthXMin = 193;
    public const int GoalMouthXMax = 478;

    // Speeds in Q8.8 raw units (un-damped — Main applies 41/64 sprite delta damping
    // when adding velocity to position, same as outfielders).
    public const int GameSpeed = 1024;          // kGoalkeeperGameSpeed (swos.asm:202379)
    public const int CatchSpeed = 768;          // kGoalkeeperCatchSpeed (swos.asm:202377)

    // === STATE MACHINE TICK ====================================================
    // Decrements per-tick timers and transitions states. Doesn't touch position —
    // movement is handled separately so the caller can still apply PC sprite-delta
    // damping on position updates.
    public static void AdvanceTimers(ref PlayerState k)
    {
        switch (k.GoalieState)
        {
            case KeeperState.DivingHigh:
            case KeeperState.DivingLow:
                if (k.GoaliePlayerDownTimer > 0) k.GoaliePlayerDownTimer--;
                if (k.GoaliePlayerDownTimer == 0)
                    k.GoalieState = KeeperState.Normal;
                break;
            case KeeperState.CatchingBall:
                if (k.GoaliePlayerDownTimer > 0) k.GoaliePlayerDownTimer--;
                if (k.GoaliePlayerDownTimer == 0)
                    k.GoalieState = KeeperState.Claimed;  // catch animation done → hold
                break;
            case KeeperState.Claimed:
                if (k.GoalieClaimedTimer < 255) k.GoalieClaimedTimer++;
                // Auto-release decision is taken by the caller (it needs the team-mate
                // list to find a target). Just bump the timer here.
                break;
        }
    }

    // shouldGoalkeeperDive — updatePlayers.cpp:10485 (cascading check).
    // Returns true if the keeper should commit to a dive THIS tick. Caller is
    // responsible for picking DivingHigh vs DivingLow based on ball Z.
    //
    // Conditions (all must hold):
    //   1. Keeper not already diving/catching/claimed (only triggers from Normal)
    //   2. Ball moving toward keeper's goal (signed Y velocity matches GoalieFaceTeamY)
    //   3. Ball Y distance from keeper <= SaveDistance (16 PC)
    //   4. Keeper can reach ball X in ≤ ball-frames-to-reach-goal-Y
    //
    // ballNextGroundX/Y is the predicted ball position when it hits the goal Y line.
    public static bool ShouldDive(in PlayerState k, in BallState ball,
        int ballNextGroundX, int ballNextGroundY)
    {
        if (k.GoalieState != KeeperState.Normal) return false;
        // Ball must be moving toward this keeper's goal.
        int faceY = k.GoalieFaceTeamY;
        if (faceY > 0 && ball.VelocityY.Raw <= 0) return false;
        if (faceY < 0 && ball.VelocityY.Raw >= 0) return false;

        // Y proximity check — the ball must arrive within SaveDistance of keeper's Y.
        int dy = ballNextGroundY - k.Y.ToInt();
        if (dy < 0) dy = -dy;
        if (dy > SaveDistance) return false;

        // Reachability: dive delta indexed by skill. Frames to cover X distance must be
        // ≤ frames the ball needs to reach the keeper's Y. Simplified: if predicted
        // landing X is within (DiveDelta × 75 ticks) of keeper, we commit.
        int skill = (int)System.Math.Clamp((int)k.GoalieSkill, 0, 7);
        // DiveDeltaByPcSkill is Q16.16 — convert to Q24.8 (>>8) px-per-frame.
        int deltaQ24_8 = DiveDeltaByPcSkill[skill] >> 8;
        // Max reach in pixels = deltaQ24_8 / 256 × 75 ticks. Skip the divide by holding
        // the comparison in raw Q24.8 units.
        long maxReachRawQ24_8 = (long)deltaQ24_8 * DiveRecoveryTicks;
        long dxRawQ24_8 = (long)System.Math.Abs(ballNextGroundX - k.X.ToInt()) * 256;
        if (dxRawQ24_8 > maxReachRawQ24_8) return false;

        return true;
    }

    // Trigger a dive in the given direction. Sets state + dive timer + Velocity. Call
    // this when ShouldDive returned true.
    public static void StartDive(ref PlayerState k, int ballNextGroundX, int ballZ)
    {
        // High dive if ball is in the air (Z >= 5 px), low dive otherwise.
        // updatePlayers.cpp:10912,10938.
        k.GoalieState = (ballZ >= 5) ? KeeperState.DivingHigh : KeeperState.DivingLow;
        k.GoaliePlayerDownTimer = DiveRecoveryTicks;

        // Lock facing along the dive direction (east/west). Y velocity is 0 — keeper
        // moves laterally toward predicted ball X. The dive delta determines speed.
        int skill = (int)System.Math.Clamp((int)k.GoalieSkill, 0, 7);
        int deltaQ24_8 = DiveDeltaByPcSkill[skill] >> 8;
        bool diveRight = ballNextGroundX > k.X.ToInt();
        k.Facing = diveRight ? Direction.East : Direction.West;
        k.VelocityX = Fixed.FromRaw(diveRight ? deltaQ24_8 : -deltaQ24_8);
        k.VelocityY = Fixed.FromInt(0);
    }

    // Called by the ball/possession layer when keeper has actually caught (vs only
    // touched) the ball. Triggers the 15-tick catch animation. After it expires
    // AdvanceTimers will transition to Claimed automatically.
    public static void OnCatch(ref PlayerState k)
    {
        k.GoalieState = KeeperState.CatchingBall;
        k.GoaliePlayerDownTimer = CatchAnimationTicks;
        k.GoalieClaimedTimer = 0;
        k.VelocityX = Fixed.FromInt(0);
        k.VelocityY = Fixed.FromInt(0);
    }

    // Called when the keeper releases the ball (either via auto-release timer or
    // human Fire). Returns to Normal so the keeper can move again.
    public static void OnRelease(ref PlayerState k)
    {
        k.GoalieState = KeeperState.Normal;
        k.GoaliePlayerDownTimer = 0;
        k.GoalieClaimedTimer = 0;
    }

    // True if keeper is currently airborne / lying / immobile (diving, catching, or
    // holding). Caller uses this to skip normal movement input.
    public static bool IsBusy(in PlayerState k) => k.GoalieState != KeeperState.Normal;

    // True if catch-vs-parry favours a clean catch (called by BallSim when keeper
    // intersects the ball mid-dive). updatePlayers.cpp:3141 vs :3144.
    public static bool ParryOnlyContact(in PlayerState k)
        => (k.GoalieState == KeeperState.DivingHigh || k.GoalieState == KeeperState.DivingLow)
           && k.GoaliePlayerDownTimer < CatchVsParryThreshold;
}
