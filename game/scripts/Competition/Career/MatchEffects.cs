namespace OpenSwos.Competition.Career;

// ============================================================================
// Per-match career effects for a REAL played fixture: form, growth and fatigue
// applied to both clubs' players right after full time (the season-level pass
// in AdvanceCareerSeason keeps handling the rest of the world).
//
// Participation model until real minutes are wired from the sim: the first 11
// of the squad play 90', the bench rests. Fatigue uses a flat per-match
// distance surrogate — TODO(balance/netplay session): replace with the real
// integer distance-covered metric from the match tick once the in-match
// fatigue toggle is wired (docs/design/05-career-mode-DETAILED-PLAN.md, step 15).
// Deterministic: seeded by (fixture round, player id) via the models' CareerRng.
// ============================================================================

public static class MatchEffects
{
    // Flat distance surrogate for a full match; gain = surrogate * (8 - stamina)
    // -> stamina 7 gains 5, stamina 0 gains 40 (clamped to 0..100 carry).
    // The real integer distance-covered metric from the in-match energy model
    // (Sim/Port/PlayerEnergy) is now optionally supplied via ApplyFixture's
    // realDrainClubId/realDrainDistance params for the human's club; every
    // other club keeps using this flat surrogate.
    private const int kMatchDistanceSurrogate = 5;
    private const int kRestDaysBetweenFixtures = 7;

    /// Applies form / growth / fatigue to both clubs of a played fixture.
    /// goalsA/goalsB are in the SAME orientation as clubA/clubB GlobalIds.
    /// When realDrainClubId is non-zero and realDrainDistance >= 0, that club
    /// uses the real in-match drain metric instead of the flat surrogate.
    public static void ApplyFixture(
        CareerWorld world, ushort clubA, ushort clubB, int goalsA, int goalsB, int round,
        ushort realDrainClubId = 0, int realDrainDistance = -1)
    {
        ApplyClub(world, clubA, System.Math.Sign(goalsA - goalsB), round, realDrainClubId, realDrainDistance);
        ApplyClub(world, clubB, System.Math.Sign(goalsB - goalsA), round, realDrainClubId, realDrainDistance);
    }

    private static void ApplyClub(CareerWorld world, ushort clubId, int result, int round,
                                  ushort realDrainClubId, int realDrainDistance)
    {
        if (!world.Clubs.TryGetValue(clubId, out var club)) return;
        uint seed = unchecked((uint)(0x4D45 ^ (round * 2654435761))); // 'ME' ^ round hash
        // Injury recovery draws from a distinct stream so it never correlates
        // with the form/growth rolls that reuse `seed` (both build CareerRng(seed,
        // id) internally). 'INJU'.
        uint injurySeed = seed ^ 0x494E4A55u;
        for (int i = 0; i < club.Squad.Count; i++)
        {
            var p = club.Squad[i];
            bool played = i < 11;
            // A week passes between fixtures: everyone recovers first. Injury
            // recovery happens BEFORE any new injuries are OR-persisted by
            // ApplyFixtureResult, matching the original order (recover at
            // swos.asm:35207, new injuries written afterwards at 35651).
            RecoverInjury(p, injurySeed);
            FatigueModel.RecoverBetweenMatches(p, kRestDaysBetweenFixtures);
            FormModel.UpdateFormAfterMatch(p, played ? result : 0, played ? 90 : 0, seed);
            if (!played) continue;
            double coachBonus = StaffModel.GrowthMultiplier(club, p);
            GrowthModel.ApplyMatchGrowth(p, 90, coachBonus, seed);
            int distance = (clubId == realDrainClubId && realDrainClubId != 0 && realDrainDistance >= 0)
                ? realDrainDistance
                : kMatchDistanceSurrogate;
            int gain = FatigueModel.MatchFatigueGain(distance, p.Stamina);
            p.FatigueCarry = System.Math.Clamp(p.FatigueCarry + gain, 0, 100);
        }
    }

    // One fixture's injury recovery for a single player. Paraphrases the original
    // per-player heal walk (swos.asm:33657-33786), applied once per fixture:
    //   sev 1     -> 50% chance to fully heal;
    //   sev 2..5  -> decrement one step, and if that lands on 1, an immediate 50%
    //                chance to fully heal;
    //   sev 6     -> 1/8 chance to re-roll down to a random 1..5, else unchanged;
    //   sev 7     -> never heals.
    // Deterministic: a per-player CareerRng keyed by (injurySeed, id) — never the
    // competition match/draw RNG. Public so the headless career test can drive it.
    internal static void RecoverInjury(CareerPlayer p, uint injurySeed)
    {
        if (p is null) return;
        int sev = p.InjurySeverity;
        if (sev <= 0 || sev == 7) return;           // fit, or the permanent injury

        var rng = new CareerRng(injurySeed, p.Id);
        if (sev == 6)
        {
            if (rng.NextInt(8) == 0) p.InjurySeverity = rng.Range(1, 5);
            return;
        }
        if (sev == 1)
        {
            if (rng.NextInt(2) == 0) p.InjurySeverity = 0;   // 50% heal
            return;
        }
        // sev 2..5: step down; landing on 1 gets an immediate 50% full heal.
        sev--;
        if (sev == 1 && rng.NextInt(2) == 0) sev = 0;
        p.InjurySeverity = sev;
    }

    // Per-fixture injury seed for a given round, so tests (and any external
    // driver) reproduce ApplyClub's recovery stream exactly.
    internal static uint InjurySeedForRound(int round)
        => unchecked((uint)(0x4D45 ^ (round * 2654435761))) ^ 0x494E4A55u;
}
