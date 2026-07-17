namespace OpenSwos.Competition.Career;

/// <summary>Assigns deterministic stamina correlated with speed and age.</summary>
public static class StaminaModel
{
    public static void AssignStamina(CareerPlayer p, uint baseSeed)
    {
        CareerRng rng = new(baseSeed ^ 0x5Au, p.Id);
        // TEAM.* keepers have zeroed outfield speed; use their price-code
        // ability so their stamina remains meaningful and deterministic.
        int stamina = string.Equals(p.Position, "G", System.StringComparison.OrdinalIgnoreCase)
            ? p.EffectiveOverall()
            : (int)System.Math.Round(p.Speed, System.MidpointRounding.AwayFromZero);
        if (p.Age < 24) stamina++;
        if (p.Age > 31) stamina--;
        stamina += rng.Range(-1, 1);
        p.Stamina = System.Math.Clamp(stamina, 0, 7);
    }
}
