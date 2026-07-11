namespace OpenSwos.Sim;

// Slightly smarter match AI for 1v1:
//   - About to get the ball → face the enemy goal and kick.
//   - AI closer to the ball than the human → chase the ball.
//   - Human closer to the ball → drop back to a defensive spot midway between the ball
//     and own goal so the AI is between the human and the goal.
public static class AiSim
{
    public const int DeadZone = 6;                // pixels — within this radius of target, AI stops
    public const int PossessionDistance = 16;     // close enough that BallSim would award possession this tick
    public const int GoalCentreX = 176;           // both goals are along the pitch centre line
    public const int HumanDribbleDistance = 20;   // human within this radius of ball = dribbling
    public const int SlideRange = 24;             // AI within this radius of human → slide tackle

    public static InputState Decide(in PlayerState ai, in PlayerState human, in BallState ball,
        int ownGoalY, int enemyGoalY)
    {
        int aiDx = ball.X.ToInt() - ai.X.ToInt();
        int aiDy = ball.Y.ToInt() - ai.Y.ToInt();
        int aiDistSq = aiDx * aiDx + aiDy * aiDy;

        // (1) Possession range — kick toward enemy goal.
        if (aiDistSq < PossessionDistance * PossessionDistance)
        {
            int gdy = enemyGoalY - ai.Y.ToInt();
            sbyte sgy = (sbyte)System.Math.Sign(gdy);
            if (sgy == 0) sgy = -1;
            return new InputState(0, sgy, action: true);
        }

        int humanDx = ball.X.ToInt() - human.X.ToInt();
        int humanDy = ball.Y.ToInt() - human.Y.ToInt();
        int humanDistSq = humanDx * humanDx + humanDy * humanDy;

        // (1b) Slide tackle: human is dribbling and we're in range. PlayerSim self-blocks
        //      a new slide while one is in progress, so this won't spam the input.
        int aiToHumanDx = human.X.ToInt() - ai.X.ToInt();
        int aiToHumanDy = human.Y.ToInt() - ai.Y.ToInt();
        int aiToHumanSq = aiToHumanDx * aiToHumanDx + aiToHumanDy * aiToHumanDy;
        if (humanDistSq < HumanDribbleDistance * HumanDribbleDistance
            && aiToHumanSq < SlideRange * SlideRange)
        {
            sbyte sx = (sbyte)System.Math.Sign(aiToHumanDx);
            sbyte sy = (sbyte)System.Math.Sign(aiToHumanDy);
            return new InputState(sx, sy, action: false, slide: true);
        }

        // (2) AI is the nearer chaser — go straight at the ball.
        if (aiDistSq <= humanDistSq)
            return StepToward(in ai, ball.X.ToInt(), ball.Y.ToInt());

        // (3) Human is closer to the ball — get between the ball and our goal. Target is
        //     2/3 of the way from ball back to own goal so we have time to set up.
        int targetX = (2 * GoalCentreX + ball.X.ToInt()) / 3;
        int targetY = (2 * ownGoalY + ball.Y.ToInt()) / 3;
        return StepToward(in ai, targetX, targetY);
    }

    private static InputState StepToward(in PlayerState p, int tx, int ty)
    {
        int dx = tx - p.X.ToInt();
        int dy = ty - p.Y.ToInt();
        if (System.Math.Abs(dx) < DeadZone && System.Math.Abs(dy) < DeadZone)
            return InputState.Idle;
        sbyte sx = (sbyte)System.Math.Sign(dx);
        sbyte sy = (sbyte)System.Math.Sign(dy);
        return new InputState(sx, sy, action: false);
    }

    // Legacy single-arg form for the original chase-only AI.
    public static InputState ChaseBall(in PlayerState ai, in BallState ball)
    {
        int dx = ball.X.ToInt() - ai.X.ToInt();
        int dy = ball.Y.ToInt() - ai.Y.ToInt();
        if (System.Math.Abs(dx) < DeadZone && System.Math.Abs(dy) < DeadZone)
            return InputState.Idle;
        sbyte sx = (sbyte)System.Math.Sign(dx);
        sbyte sy = (sbyte)System.Math.Sign(dy);
        return new InputState(sx, sy, action: false);
    }
}
