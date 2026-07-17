namespace OpenSwos.Competition.Career;

/// <summary>
/// Deterministic, season-boundary career finances. Amounts are whole money
/// units; no simulation RNG or competition RNG state is used here.
/// </summary>
public static class Finance
{
    private const long MinimumBudget = -20_000_000L;
    private const long MaximumBudget = 2_000_000_000L;
    private const long MaximumClubValue = 50_000_000_000L;
    private const long MaximumPlayerValue = 500_000_000L;
    private const long BaseSeasonIncome = 2_000_000L;

    /// <summary>
    /// Returns a player's deterministic market value. The base is the original
    /// SWOS price code inflated to today's market (<see cref="PriceTable"/>);
    /// modest age and form multipliers shape it around that anchor without
    /// breaking the scale (total multiplier stays within roughly [0.4, 2.0]).
    /// </summary>
    public static long PlayerValue(CareerPlayer p)
    {
        if (p is null) throw new System.ArgumentNullException(nameof(p));

        // Authentic ladder -> modern market. ValueCode is the source of a
        // keeper's ability and, for outfield players, tracks their skills.
        double baseValue = PriceTable.ModernValue(PriceTable.Swos1997Price(p.ValueCode));

        double overall = p.EffectiveOverall();
        double potential = FiniteClamp(p.Potential, 0.0, 7.0);
        double headroom = Math.Max(0.0, potential - overall);
        double multiplier = AgeValueMultiplier(p.Age, headroom) * FormNudge(p.Form);
        multiplier = Math.Clamp(multiplier, 0.4, 2.0);

        double value = baseValue * multiplier;
        return (long)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0.0, (double)MaximumPlayerValue);
    }

    /// <summary>Returns the annual player wage bill for a club's squad.</summary>
    public static long SquadWageBill(CareerClub club)
    {
        if (club is null) throw new System.ArgumentNullException(nameof(club));

        // Wages remain intentionally light-weight until individual contracts
        // exist: a squad costs two percent of its current aggregate value.
        return ClubValue(club) / 50L;
    }

    /// <summary>Returns a bounded aggregate market value for a club's squad.</summary>
    public static long ClubValue(CareerClub club)
    {
        if (club is null) throw new System.ArgumentNullException(nameof(club));

        long total = 0;
        foreach (CareerPlayer player in club.Squad)
        {
            long value = PlayerValue(player);
            if (total >= MaximumClubValue - value)
                return MaximumClubValue;
            total += value;
        }

        return total;
    }

    /// <summary>Seeds every club's opening budget from its squad value.</summary>
    public static void SeedBudgets(CareerWorld world)
    {
        if (world is null) throw new System.ArgumentNullException(nameof(world));

        foreach (CareerClub club in world.Clubs.Values)
            club.Budget = Math.Clamp(ClubValue(club) / 5L, 0L, MaximumBudget);
    }

    /// <summary>Applies one deterministic year of income and expenses to all clubs.</summary>
    public static void ApplySeasonFinances(CareerWorld world)
    {
        if (world is null) throw new System.ArgumentNullException(nameof(world));

        // A stable order keeps this safe if future finance seams add keyed RNG.
        var clubIds = new System.Collections.Generic.List<ushort>(world.Clubs.Keys);
        clubIds.Sort();

        foreach (ushort clubId in clubIds)
        {
            CareerClub club = world.Clubs[clubId];
            long clubValue = ClubValue(club);
            long income = BaseSeasonIncome + clubValue / 25L;
            long wages = SquadWageBill(club);
            long coachWages = 0L;
            foreach (Coach coach in club.Coaches)
                coachWages += Math.Max(0L, coach.Wage);

            // TODO: Add league-position and cup-run prize money from the completed
            // career-season results when that result data is supplied to this seam.
            long staffAndScoutingCosts = coachWages;
            long currentBudget = Math.Clamp(club.Budget, MinimumBudget, MaximumBudget);
            long nextBudget = currentBudget + income - wages - staffAndScoutingCosts;
            club.Budget = Math.Clamp(nextBudget, MinimumBudget, MaximumBudget);
        }
    }

    // Age curve anchored on the inflated ladder base. Prime players hold a
    // small premium; the over-32 decline is firm; an under-21 with real
    // potential headroom can reach up to x1.6 for his upside.
    private static double AgeValueMultiplier(int age, double headroom)
    {
        if (age <= 20)
        {
            // 1.0 with no headroom, scaling to 1.6 for a maximal-upside prospect.
            double upside = Math.Clamp(headroom, 0.0, 7.0) / 7.0;
            return 1.0 + 0.6 * upside;
        }
        return age switch
        {
            <= 23 => 1.0,
            <= 29 => 1.15,   // prime
            <= 32 => 0.7,
            _ => 0.4,        // 33+
        };
    }

    // A small form nudge: +/-10% at the extremes of the -3..+3 form scale.
    private static double FormNudge(int form)
        => 1.0 + Math.Clamp(form, -3, 3) * (0.10 / 3.0);

    private static double FiniteClamp(double value, double minimum, double maximum)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
}
