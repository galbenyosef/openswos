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
        {
            club.Budget = Math.Clamp(ClubValue(club) / 5L, 0L, MaximumBudget);
            // Open the first season's books (career depth plan feature #1).
            club.SeasonLedgerActive = true;
            club.SeasonOpeningBalance = club.Budget;
            club.SeasonPlayerSales = 0L;
            club.SeasonPlayerPurchases = 0L;
            club.SeasonStaffSpend = 0L;
        }
    }

    /// <summary>
    /// Applies one deterministic year of income and expenses to all clubs.
    /// Legacy entry point: no club played an accounted competition, so nobody
    /// earns prize money. Kept so callers that have no season results (and old
    /// saves reloaded mid-rollover) behave exactly as before this seam existed.
    /// </summary>
    public static void ApplySeasonFinances(CareerWorld world)
        => ApplySeasonFinances(world, null, 0, 0);

    /// <summary>
    /// Applies one deterministic year of income and expenses to all clubs,
    /// crediting league-position and cup-run prize money to the clubs that
    /// played the season described by <paramref name="results"/>.
    ///
    /// Career depth plan feature #1. The per-club statement is built by
    /// <see cref="SeasonFinances"/> and follows the ORIGINAL game's line items
    /// (see the fidelity note at the top of that file). Every club still pays
    /// wages and staff, so a club that wins nothing genuinely falls behind —
    /// which is the point.
    /// </summary>
    /// <param name="results">
    /// Per-club season outcome, keyed by club GlobalId. Clubs absent from the
    /// map are accounted as unattached (crowd and wages, no prize money).
    /// </param>
    /// <param name="season">The season that has just finished.</param>
    /// <param name="playerClubId">
    /// The managed club; only this club may receive the "NEW CHAIRMAN" opening
    /// investment, and only in season 1.
    /// </param>
    /// <returns>The managed club's statement, or null when it has none.</returns>
    public static SeasonAccount? ApplySeasonFinances(
        CareerWorld world,
        System.Collections.Generic.IReadOnlyDictionary<ushort, SeasonResultInput>? results,
        int season,
        ushort playerClubId)
    {
        if (world is null) throw new System.ArgumentNullException(nameof(world));

        // A stable order keeps this safe if future finance seams add keyed RNG.
        var clubIds = new System.Collections.Generic.List<ushort>(world.Clubs.Keys);
        clubIds.Sort();

        SeasonAccount? playerAccount = null;

        foreach (ushort clubId in clubIds)
        {
            CareerClub club = world.Clubs[clubId];
            // The books open at the balance the club STARTED the season with,
            // not the balance left after a season of trading — otherwise the
            // 'BALANCE AT START OF SEASON' line is a lie and the sheet cannot
            // reconcile. Careers saved before the ledger existed fall back to
            // the live budget (SeasonLedgerActive == false).
            long liveBudget = Math.Clamp(club.Budget, MinimumBudget, MaximumBudget);
            long openingBalance = club.SeasonLedgerActive
                ? Math.Clamp(club.SeasonOpeningBalance, MinimumBudget, MaximumBudget)
                : liveBudget;

            SeasonAccount account;
            if (results is not null && results.TryGetValue(clubId, out SeasonResultInput result))
            {
                bool newChairman = clubId == playerClubId && season <= 1;
                account = SeasonFinances.Compute(club, result, season, openingBalance, newChairman);
            }
            else
            {
                account = SeasonFinances.ComputeUnattached(club, season, openingBalance);
            }

            // The original had no coaching staff, so its statement has no line
            // for it. Coach annual wages, coach signing fees and scouting
            // upgrades are all folded into PLAYER WAGES BILL — the nearest
            // truthful home — rather than inventing a line item the original
            // never had.
            long coachWages = 0L;
            foreach (Coach coach in club.Coaches)
                coachWages += Math.Max(0L, coach.Wage);
            long staffSpend = Math.Max(0L, club.SeasonStaffSpend);
            account.WageBill += coachWages + staffSpend;

            // TWO independent figures, deliberately:
            //
            //   statementClosing — what the printed sheet adds up to, starting
            //     from the balance the club had at the START of the season.
            //   actualClosing    — what the club really has: its LIVE budget
            //     (which already reflects the season's transfers and staff
            //     spending) plus this rollover's income, minus the wages
            //     charged now.
            //
            // They agree only if every path that moved money reported to the
            // season ledger. The club is credited with the ACTUAL figure — never
            // with a number derived from the sheet, or a forgotten hook would
            // quietly mint money. The difference is recorded and asserted.
            long incomeArrivingNow = account.GateReceipts + account.CompetitionBonuses
                + account.Sponsorship + account.ChairmanInvestment;
            long statementClosing = openingBalance + account.TotalIncome - account.TotalExpenditure;
            long actualClosing = liveBudget + incomeArrivingNow - Finance.SquadWageBill(club) - coachWages;

            club.Budget = Math.Clamp(actualClosing, MinimumBudget, MaximumBudget);
            account.ClosingBalance = club.Budget;
            account.Unreconciled = statementClosing - account.ClosingBalance;

            // Open the next season's books.
            club.SeasonLedgerActive = true;
            club.SeasonOpeningBalance = club.Budget;
            club.SeasonPlayerSales = 0L;
            club.SeasonPlayerPurchases = 0L;
            club.SeasonStaffSpend = 0L;

            if (clubId == playerClubId && playerClubId != 0)
                playerAccount = account;
        }

        return playerAccount;
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
