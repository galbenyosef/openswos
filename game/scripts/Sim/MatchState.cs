namespace OpenSwos.Sim;

// Match phases. Drives what Main.cs does each tick: sim during Play, freeze sim and
// show overlay during everything else.
public enum MatchPhase
{
    PreKickoff,   // brief countdown / position freeze before play resumes (after goal too)
    Play,         // normal gameplay
    PostGoal,     // "GOAL!" overlay, players + ball frozen at kickoff positions
    HalfTime,     // "HALF TIME" overlay, swap sides
    FullTime,     // "FULL TIME N-M" overlay, press accept to return to menu
    // Set pieces. SWOS GameState equivalents:
    //   ThrowIn    ← kThrowInForwardRight..kThrowInBackLeft (15..20) — six variants by
    //                  side × pitch-third; we collapse to one phase (which side is taking
    //                  is stored in MatchState.TakingTeamIsPlayer).
    //   CornerKick ← kCornerLeft / kCornerRight (4, 5).
    //   GoalKick   ← effectively SWOS's kKeeperHoldsTheBall (3) — original SWOS makes the
    //                  keeper pick up and throw/kick out the ball. We stub that as a
    //                  static-position goal-kick set piece.
    // See external/swos-port/src/swos/swos.h:568-595 for the SWOS enum.
    ThrowIn,
    CornerKick,
    GoalKick,
}

// One match's lifetime state. Ticks at 25 Hz alongside the rest of the sim.
//
// MatchTick counts only ticks where Phase == Play (so the clock pauses during overlays).
// PhaseTick counts ticks since the current phase started — used for overlay timeouts.
// Half = 1 in the first half, 2 in the second.
public struct MatchState
{
    public MatchPhase Phase;
    public int PhaseTick;
    public int MatchTick;
    public byte Half;
    public byte ScorePlayer;
    public byte ScoreOpponent;
    // Match stats — cheap counters, shown on the Full Time overlay.
    public ushort KicksPlayer;
    public ushort KicksOpponent;
    public ushort PossessionTicksPlayer;
    public ushort PossessionTicksOpponent;

    // Last team to kick the ball. Drives set-piece awards: the OTHER team takes the
    // throw-in/corner. Keeper kicks must NOT update this — otherwise a routine
    // goal-mouth clearance would award the opponent a corner. Mirrors SWOS
    // `lastTeamPlayed` (g_memByte[523092]) — see ball.cpp:3796.
    public bool LastTouchedByPlayer;

    // Which side is taking the current set piece. true = player/home, false = opponent.
    public bool TakingTeamIsPlayer;

    // Auto-release vector for the current set piece. After PhaseTick reaches the
    // configured hold duration, BallSim is given an empty influence span and the
    // ball gets these (Q24.8 raw) velocities applied + a small Z loft so the
    // throw/corner lobs visibly. Real SWOS would have the nearest player walk in
    // and kick — see docs/porting/set-pieces.md for the stub note.
    public int SetPieceKickVxRaw;
    public int SetPieceKickVyRaw;
    public int SetPieceKickVzRaw;

    // Per-keeper ball-hold counters. Increment each tick the ball is in possession
    // range AND on the ground. When >= MatchTimings.KeeperHoldDurationTicks the keeper
    // auto-punts the ball upfield (SWOS-style clearance). Reset when ball leaves the
    // keeper's possession range. Maps roughly to SWOS's kKeeperHoldsTheBall=3 timer.
    public byte HomeKeeperHoldTicks;
    public byte AwayKeeperHoldTicks;

    public static MatchState NewMatch() => new()
    {
        Phase = MatchPhase.PreKickoff,
        PhaseTick = 0,
        MatchTick = 0,
        Half = 1,
        LastTouchedByPlayer = true,  // home kicks off
    };

    public void EnterPhase(MatchPhase next)
    {
        Phase = next;
        PhaseTick = 0;
    }
}

public static class MatchTimings
{
    // 70 ticks/sec = SWOS PC native rate (timer.h kTargetFpsPC = 70). All durations
    // below are in ticks so multiplying by 70 / 50 = 1.4 keeps the same wall-clock time
    // when we switched from Amiga 50 Hz. Ball physics constants also switched to PC.
    public const int TicksPerSecond = 70;
    public const int PreKickoffTicks = 140;       // 2.0 s pre-kickoff freeze
    public const int PostGoalTicks = 210;         // 3.0 s GOAL! overlay
    public const int HalfTimeTicks = 350;         // 5.0 s HALF TIME overlay
    public const int FullTimeAcceptDelay = 140;   // accept ignored for 2.0 s after full-time
    public const int HalfDurationTicks = 2100;    // 30 s per half → 60 s match for fast iteration

    // Set-piece auto-release timers (in 70 Hz ticks). Real SWOS waits on the nearest
    // player to walk into position then on the human pressing kick; we don't have a
    // walks-to-ball state machine yet so we just freeze the ball and release after a
    // fixed delay. See docs/porting/set-pieces.md for the TODO list.
    public const int ThrowInHoldTicks = 140;      // 2.0 s
    public const int CornerHoldTicks = 210;       // 3.0 s
    public const int GoalKickHoldTicks = 140;     // 2.0 s
    // Keeper holds the ball this long during open play before punting it upfield.
    public const int KeeperHoldDurationTicks = 140;  // 2.0 s
}
