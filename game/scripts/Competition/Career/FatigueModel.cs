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
    /// How much of the CARRIED fatigue a week's rest sheds on top of the flat
    /// per-day rate. This term is what gives the model an equilibrium.
    ///
    /// With flat recovery only, a player whose per-match gain exceeds his weekly
    /// rest ratchets to 100 and stays there for the rest of his career — which
    /// is exactly what every regular in a career was doing until 2026-08-26
    /// (the training screen's CON column made it visible: first XI 0, bench 96).
    /// Because the shed grows with the fatigue, every player now settles at
    /// gain-over-recovery instead of saturating: a stamina-3 professional lands
    /// around 15 fatigue, a stamina-0 one around 79, and nobody sits at 100
    /// unless he is genuinely being run into the ground.
    /// </summary>
    private const double ProportionalShed = 0.35;

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
        if (restDays > 0) remaining -= fatigue * ProportionalShed;
        p.FatigueCarry = System.Math.Clamp(
            (int)System.Math.Floor(remaining),
            0,
            100);
    }

    /// <summary>
    /// The close season. Everybody reports back fit — which is what a summer is
    /// — but a player who was run into the ground still starts a little behind.
    /// Settles the plan's open question ("should fatigue recover between
    /// seasons?", 03-career-depth-plan.md) with a YES: it has to, now that
    /// condition is a number the manager reads before every training session.
    /// </summary>
    public static void PreSeason(CareerPlayer p)
    {
        ArgumentNullException.ThrowIfNull(p);
        p.FatigueCarry = System.Math.Clamp(p.FatigueCarry, 0, 100) / 5;
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
