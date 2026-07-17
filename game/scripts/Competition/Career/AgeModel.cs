namespace OpenSwos.Competition.Career;

/// <summary>Assigns deterministic starting ages from a player's skill tier.</summary>
public static class AgeModel
{
    public static void AssignInitialAge(CareerPlayer p, uint baseSeed)
    {
        CareerRng rng = new(baseSeed, p.Id);
        double overall = PotentialModel.OverallOf(p);
        int age;

        if (overall >= 5.0)
        {
            // Established strong players are normally in their prime, with a
            // modest veteran tail so the world is not uniformly youthful.
            age = rng.NextInt(10) == 0 ? rng.Range(31, 34) : rng.Range(23, 30);
        }
        else if (overall >= 3.0)
        {
            age = rng.Range(19, 31);
        }
        else
        {
            age = rng.NextInt(2) == 0 ? rng.Range(17, 22) : rng.Range(31, 36);
        }

        p.Age = System.Math.Clamp(age, 16, 38);
    }
}
