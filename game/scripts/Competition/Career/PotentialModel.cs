namespace OpenSwos.Competition.Career;

/// <summary>Assigns the hidden, deterministic career ceiling for a player.</summary>
public static class PotentialModel
{
    public static void AssignPotential(CareerPlayer p, uint baseSeed)
    {
        CareerRng rng = new(baseSeed ^ 0xA5u, p.Id);
        double overall = OverallOf(p);
        double potential;

        if (p.Age <= 19)
        {
            potential = overall + System.Math.Pow(rng.NextDouble(), 2) * 2.5;
        }
        else if (p.Age <= 23)
        {
            potential = overall + System.Math.Pow(rng.NextDouble(), 2) * 1.5;
        }
        else if (p.Age <= 28)
        {
            potential = overall + System.Math.Pow(rng.NextDouble(), 2) * 0.6;
        }
        else
        {
            potential = overall - 0.3 + rng.NextDouble() * 0.5;
        }

        // The young-player floor preserves a ceiling at or above their present
        // ability; older players may already be past their ceiling.
        double minimum = p.Age <= 23 ? overall : 0.0;
        p.Potential = System.Math.Clamp(potential, minimum, 7.0);
    }

    /// <summary>
    /// Returns the ability used for potential, scouting and valuation. Keepers
    /// use their TEAM.* value code; their seven outfield skills are zero.
    /// </summary>
    public static double OverallOf(CareerPlayer p)
    {
        if (string.Equals(p.Position, "G", System.StringComparison.OrdinalIgnoreCase))
            return p.EffectiveOverall();

        return (p.Passing + p.Shooting + p.Heading + p.Tackling
            + p.Control + p.Speed + p.Finishing) / 7.0;
    }
}
