using System;
using System.Collections.Generic;

namespace OpenSwos.Competition.Career;

/// <summary>
/// Deterministic player development. Skill changes are retained as fractional
/// carry until they become a visible 0..7 skill step.
/// </summary>
public static class GrowthModel
{
    private const int SkillCount = 7;

    /// <summary>
    /// Applies one season of development to every active player in the career
    /// world. The RNG stream is isolated per season and player, so neither
    /// traversal order nor the competition RNG state can affect the result.
    /// </summary>
    public static void ApplySeasonGrowth(CareerWorld world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        List<ushort> clubIds = new(world.Clubs.Keys);
        clubIds.Sort();
        uint seasonSeed = unchecked((uint)world.Season);

        foreach (ushort clubId in clubIds)
            ApplySeasonGrowth(world.Clubs[clubId], seasonSeed);

        ApplySeasonGrowth(world.FreeAgents, seasonSeed);
    }

    /// <summary>
    /// Applies a fine, deterministic development increment for one player's
    /// match participation. Callers may pass extra-time minutes; negative
    /// minutes are treated as no participation and coach bonuses below one do
    /// not reduce the baseline rate.
    /// </summary>
    public static void ApplyMatchGrowth(CareerPlayer p, int minutes, double coachBonus, uint seed)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        if (minutes <= 0) return;

        double ageFactor = AgeFactor(p.Age);
        if (ageFactor == 0.0) return;

        double participation = minutes / 90.0;
        double bonus = double.IsFinite(coachBonus) ? Math.Max(1.0, coachBonus) : 1.0;
        CareerRng rng = new(seed, p.Id);
        if (IsGoalkeeper(p))
        {
            ApplyGoalkeeperGrowth(p, ageFactor, participation * bonus, rng);
            return;
        }

        double overall = PotentialModel.OverallOf(p);
        double declineTerm = DeclineTerm(overall);
        double[] weights = PositionWeights(p.Position);
        double[] skills = SkillsOf(p);
        double[] carry = EnsureGrowthCarry(p);

        for (int i = 0; i < SkillCount; i++)
        {
            double target = ageFactor > 0.0
                ? Math.Clamp(p.Potential - skills[i], 0.0, 7.0)
                : declineTerm;
            double noise = 0.6 + rng.NextDouble() * 0.8;
            double delta = ageFactor * 0.025 * participation * bonus * weights[i] * target * noise;
            ApplyDelta(skills, carry, i, delta);
        }

        WriteSkills(p, skills);
    }

    /// <summary>
    /// Returns the growth profile for a SWOS position. All profiles keep a
    /// baseline in every skill, while specialist strengths receive more of the
    /// available development. Unknown positions deliberately use a flat
    /// profile so imported or future labels remain safe.
    /// </summary>
    public static double[] PositionWeights(string position)
    {
        string key = position?.Trim().ToUpperInvariant() ?? string.Empty;
        return key switch
        {
            "G" => [0.35, 0.35, 0.35, 0.45, 0.45, 0.35, 0.35],
            "RB" or "LB" or "D" => [0.65, 0.55, 1.30, 1.45, 0.65, 0.65, 0.55],
            "M" => [1.45, 0.70, 0.60, 0.70, 1.45, 0.90, 0.70],
            "RW" or "LW" or "A" => [0.65, 1.35, 0.70, 0.55, 0.75, 1.20, 1.45],
            _ => [0.75, 0.75, 0.75, 0.75, 0.75, 0.75, 0.75],
        };
    }

    private static void ApplySeasonGrowth(CareerClub club, uint seasonSeed)
        => ApplySeasonGrowth(club.Squad, seasonSeed, club);

    private static void ApplySeasonGrowth(List<CareerPlayer> players, uint seasonSeed, CareerClub? club = null)
    {
        foreach (CareerPlayer player in players)
        {
            double ageFactor = AgeFactor(player.Age);
            if (ageFactor == 0.0) continue;

            CareerRng rng = new(seasonSeed, player.Id);
            double staffMultiplier = ageFactor > 0.0 && club is not null
                ? StaffModel.GrowthMultiplier(club, player)
                : 1.0;
            if (IsGoalkeeper(player))
            {
                ApplyGoalkeeperGrowth(player, ageFactor, staffMultiplier, rng);
                continue;
            }

            double overall = PotentialModel.OverallOf(player);
            double declineTerm = DeclineTerm(overall);
            double[] weights = PositionWeights(player.Position);
            double[] skills = SkillsOf(player);
            double[] carry = EnsureGrowthCarry(player);

            for (int i = 0; i < SkillCount; i++)
            {
                double target = ageFactor > 0.0
                    ? Math.Clamp(player.Potential - skills[i], 0.0, 7.0)
                    : declineTerm;
                double noise = 0.6 + rng.NextDouble() * 0.8;
                double delta = ageFactor * weights[i] * target * noise * staffMultiplier;
                ApplyDelta(skills, carry, i, delta);
            }

            WriteSkills(player, skills);
            DriftValueCode(player, skills);
        }
    }

    private static double AgeFactor(int age) => age switch
    {
        // A prospect with substantial headroom needs visibly rapid early
        // development: roughly 2 -> 5 by age 25 for a potential-6 outfielder.
        // The headroom term below naturally tapers this rate near the ceiling.
        <= 19 => 0.20,
        <= 23 => 0.14,
        <= 28 => 0.012,
        <= 31 => -0.06,
        _ => -0.14 - Math.Min(age - 32, 4) * 0.015,
    };

    private static double DeclineTerm(double overall)
        => Math.Clamp(0.8 + overall / 14.0, 0.8, 1.3);

    private static bool IsGoalkeeper(CareerPlayer p)
        => string.Equals(p.Position, "G", StringComparison.OrdinalIgnoreCase);

    private static void ApplyGoalkeeperGrowth(
        CareerPlayer player,
        double ageFactor,
        double multiplier,
        CareerRng rng)
    {
        // Goalkeeper ability is represented by ValueCode, whose seven-code
        // buckets map to EffectiveOverall. With ValueCode now a double we adjust
        // it directly (dropping the integer-step carry dance): the same age
        // curve and potential headroom drive it, converting a skill-level delta
        // into value-code units at seven codes per level.
        double ability = player.EffectiveOverall();
        double target = ageFactor > 0.0
            ? Math.Clamp(player.Potential - ability, 0.0, 7.0)
            : DeclineTerm(ability);
        double noise = 0.6 + rng.NextDouble() * 0.8;
        double codeDelta = ageFactor * target * noise * multiplier * 7.0;
        player.ValueCode = Math.Clamp(player.ValueCode + codeDelta, 0.0, 49.0);
    }

    // After a season's development, drift an outfield player's ValueCode 30% of
    // the way toward the price code his current ability implies. Deterministic
    // (no RNG): a grown youth becomes more valuable, a declined veteran cheaper.
    private static void DriftValueCode(CareerPlayer p, double[] skills)
    {
        double overall = 0.0;
        for (int i = 0; i < SkillCount; i++) overall += skills[i];
        overall /= SkillCount;
        double targetCode = PriceTable.CodeFromOverall(overall);
        p.ValueCode = Math.Clamp(p.ValueCode + 0.3 * (targetCode - p.ValueCode), 0.0, 60.0);
    }

    private static double[] SkillsOf(CareerPlayer p) =>
    [
        Math.Clamp(p.Passing, 0.0, 7.0),
        Math.Clamp(p.Shooting, 0.0, 7.0),
        Math.Clamp(p.Heading, 0.0, 7.0),
        Math.Clamp(p.Tackling, 0.0, 7.0),
        Math.Clamp(p.Control, 0.0, 7.0),
        Math.Clamp(p.Speed, 0.0, 7.0),
        Math.Clamp(p.Finishing, 0.0, 7.0),
    ];

    private static double[] EnsureGrowthCarry(CareerPlayer p)
    {
        if (p.GrowthCarry is { Length: SkillCount }) return p.GrowthCarry;

        double[] carry = new double[SkillCount];
        if (p.GrowthCarry is not null)
            Array.Copy(p.GrowthCarry, carry, Math.Min(p.GrowthCarry.Length, SkillCount));
        p.GrowthCarry = carry;
        return carry;
    }

    private static void ApplyDelta(double[] skills, double[] carry, int index, double delta)
    {
        carry[index] += delta;
        while (carry[index] >= 1.0)
        {
            skills[index] = Math.Min(7.0, skills[index] + 1.0);
            carry[index] -= 1.0;
        }
        while (carry[index] <= -1.0)
        {
            skills[index] = Math.Max(0.0, skills[index] - 1.0);
            carry[index] += 1.0;
        }
    }

    private static void WriteSkills(CareerPlayer p, double[] skills)
    {
        p.Passing = skills[0];
        p.Shooting = skills[1];
        p.Heading = skills[2];
        p.Tackling = skills[3];
        p.Control = skills[4];
        p.Speed = skills[5];
        p.Finishing = skills[6];
    }
}
