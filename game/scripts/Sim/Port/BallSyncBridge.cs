namespace OpenSwos.Sim.Port;

using OpenSwos.Sim;
using OpenSwos.SwosVm;

// Bridge between BallState (Q24.8, velocity-based) and BallSprite (Q16.16,
// destX/destY-based). Used by Main.cs when `_useSwosPort` toggle is ON to
// route per-tick ball physics through the SwosVm port instead of BallSim.
//
// **Architectural compromise (B4.7)**: BallSim and SWOS use different motion
// models — BallSim applies velocity directly each tick, SWOS recomputes
// velocity each tick from destX/destY+speed. Our bridge bypasses SWOS's
// direction recalc (Section2 of updateBall) by calling `TickPhysicsOnly()`,
// which keeps DeltaX/Y/Z intact and only applies position + bounce.
//
// Once the rest of updatePlayers / AI lands in port form, the full `Tick()`
// pipeline becomes correct (destX/Y set by port AI, Section2 computes velocity).
// Until then, this compromise gives us a working toggle for A/B testing.
//
// Fidelity notes (audit risk hotspots from 2026-05-23):
// - Sync uses FULL 32-bit X/Y/Z (Q16.16), NOT whole-pixel `XPixels` accessors —
//   preserves sub-pixel fraction across tick.
// - `gameState` (ST_KEEPER_HOLDS_BALL=3) propagated from caller — without this
//   the keeper-z branch in Section3 is dead code.
// - `gameStatePl` always set; barrier-bounce (set pieces) needs != 100 to fire.
public static class BallSyncBridge
{
    // Sync C# state INTO Memory before calling BallUpdate.TickPhysicsOnly().
    //
    // gameStateForPort: which SWOS gameState to advertise this tick. Caller
    //   passes 3 (ST_KEEPER_HOLDS_BALL) when keeper is in Claimed state, else 0.
    // gameStatePlForPort: 100 (kInProgress) for live play, 101 (kStopped) for
    //   set pieces / paused.
    public static void Push(in BallState ball,
                            short gameStateForPort,
                            short gameStatePlForPort)
    {
        // Position Q24.8 → Q16.16: shift left 8 (raw value scales by 256).
        // Use full 32-bit accessors to preserve sub-pixel fraction.
        BallSprite.X = ball.X.Raw << 8;
        BallSprite.Y = ball.Y.Raw << 8;
        BallSprite.Z = ball.Z.Raw << 8;

        // Velocity Q24.8 → Q16.16: shift + PC 41/64 damping on XY.
        // The PC sprite damping (41/64 ≈ 0.640625) normally lives inside
        // Section2/CalculateDeltaXAndY which we skip in TickPhysicsOnly. To
        // get the same per-tick motion magnitude as the full SWOS pipeline,
        // we apply it manually here before push. Mirrors updateSprite.cpp:327-328:
        //   result = x - x/4 - x/16 - x/32 - x/64
        // Z is NOT damped — gravity applies in raw Q16.16.
        int rawVx = ball.VelocityX.Raw;
        int rawVy = ball.VelocityY.Raw;
        int dampedVx = rawVx - (rawVx >> 2) - (rawVx >> 4) - (rawVx >> 5) - (rawVx >> 6);
        int dampedVy = rawVy - (rawVy >> 2) - (rawVy >> 4) - (rawVy >> 5) - (rawVy >> 6);
        BallSprite.DeltaX = dampedVx << 8;
        BallSprite.DeltaY = dampedVy << 8;
        BallSprite.DeltaZ = ball.VelocityZ.Raw << 8;

        // Speed = |velocity| in Q8.8. Used by Section3 ground-bounce for XY
        // damping (newSpeed = speed - speed*bounceFactor/256). If we set Speed=0
        // the bounce damping is a no-op; setting it to magnitude makes bounce
        // act on speed magnitude correctly.
        //
        // Approximation: sqrt(dx² + dy²) where dx, dy are Q24.8 pixels-per-tick.
        // Speed is Q8.8 so result must be in same scale. dx.ToInt() is pixels/tick;
        // mul by 256 gives Q8.8. Integer sqrt ≈ sqrt(dx²+dy²) without floats.
        int dxPix = ball.VelocityX.ToInt();
        int dyPix = ball.VelocityY.ToInt();
        int magSq = dxPix * dxPix + dyPix * dyPix;
        int speed = IntSqrt(magSq) << 8;  // Q8.8 scale (whole pixels * 256)
        if (speed > 32767) speed = 32767; // clamp to int16
        BallSprite.Speed = (short)speed;

        // DestX/Y: set to current position so direction calc (if it ever runs)
        // would compute zero motion. We use TickPhysicsOnly which skips Section2
        // so this is mostly cosmetic — but updateBall internally reads destX/Y
        // in Section1 (frame index) so keep it consistent.
        BallSprite.DestX = (short)ball.X.ToInt();
        BallSprite.DestY = (short)ball.Y.ToInt();

        // Game state propagation — risk hotspot #2/#3 from audit.
        Memory.WriteWord(Memory.Addr.gameState, gameStateForPort);
        Memory.WriteWord(Memory.Addr.gameStatePl, gameStatePlForPort);
    }

    // Pull state OUT of Memory after BallUpdate.TickPhysicsOnly().
    public static void Pull(ref BallState ball)
    {
        // Position Q16.16 → Q24.8: shift right 8 (raw value scales by 1/256).
        ball.X = Fixed.FromRaw(BallSprite.X >> 8);
        ball.Y = Fixed.FromRaw(BallSprite.Y >> 8);
        ball.Z = Fixed.FromRaw(BallSprite.Z >> 8);

        // Velocity from the port-applied DeltaX/Y/Z (Section3 modified these
        // during bounce; in non-bounce ticks they pass through unchanged).
        ball.VelocityX = Fixed.FromRaw(BallSprite.DeltaX >> 8);
        ball.VelocityY = Fixed.FromRaw(BallSprite.DeltaY >> 8);
        ball.VelocityZ = Fixed.FromRaw(BallSprite.DeltaZ >> 8);
    }

    // Integer sqrt — borrowed from BallSim.IntSqrt pattern. Babylonian method,
    // deterministic (no float). For sync purposes precision isn't critical.
    private static int IntSqrt(int value)
    {
        if (value <= 0) return 0;
        int x = value;
        int y = (x + 1) >> 1;
        while (y < x)
        {
            x = y;
            y = (x + value / x) >> 1;
        }
        return x;
    }
}
