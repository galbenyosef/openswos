namespace OpenSwos.Competition.Career;

/// <summary>
/// Pure fatigue calculations shared by career match-boundary code and the
/// deterministic match simulation. Match-side methods use integers only.
/// </summary>
public static class FatigueModel
{
    /// <summary>
    /// Returns fatigue accumulated over a match from its integer distance metric.
    /// A positive distance produces strictly more fatigue for every lower valid
    /// stamina value. The result is saturated because callers add it to a
    /// 0..100 fatigue counter.
    /// </summary>
    public static int MatchFatigueGain(int distanceUnits, int stamina)
    {
        if (distanceUnits <= 0)
            return 0;

        // Bound the metric before multiplication so every valid stamina factor
        // remains distinct without integer overflow.
        int boundedDistance = System.Math.Min(distanceUnits, int.MaxValue / 8);
        int clampedStamina = System.Math.Clamp(stamina, 0, 7);
        return boundedDistance * (8 - clampedStamina);
    }

    /// <summary>
    /// Returns the bounded integer skill adjustment caused by fatigue.
    /// </summary>
    public static int SkillPenalty(int tiredness)
    {
        if (tiredness >= 80)
            return -2;
        if (tiredness >= 50)
            return -1;
        return 0;
    }

    /// <summary>
    /// Applies deterministic rest recovery to a career player's persistent
    /// fatigue. The remaining whole-number fatigue is rounded down, ensuring a
    /// positive recovery amount reduces any non-zero carried fatigue.
    /// </summary>
    public static void RecoverBetweenMatches(CareerPlayer p, int daysRest)
    {
        ArgumentNullException.ThrowIfNull(p);

        int fatigue = System.Math.Clamp(p.FatigueCarry, 0, 100);
        int restDays = System.Math.Max(daysRest, 0);
        double remaining = fatigue - (RecoveryPerDay(p) * restDays);
        p.FatigueCarry = System.Math.Clamp(
            (int)System.Math.Floor(remaining),
            0,
            100);
    }

    /// <summary>
    /// Returns recovery points per calendar rest day. Young players recover
    /// faster, and every stamina point raises recovery by 0.35 points per day.
    /// </summary>
    public static double RecoveryPerDay(CareerPlayer p)
    {
        ArgumentNullException.ThrowIfNull(p);

        int stamina = System.Math.Clamp(p.Stamina, 0, 7);
        int age = System.Math.Max(p.Age, 0);
        double ageBonus = age switch
        {
            <= 21 => 1.25,
            <= 27 => 0.75,
            <= 32 => 0.35,
            <= 35 => 0.0,
            _ => -0.20 * (age - 35)
        };

        return System.Math.Max(0.50, 1.00 + ageBonus + (stamina * 0.35));
    }
}
