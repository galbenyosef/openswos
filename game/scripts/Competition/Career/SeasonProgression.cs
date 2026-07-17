namespace OpenSwos.Competition.Career;

/// <summary>Applies deterministic yearly ageing and retirement to a career world.</summary>
public static class SeasonProgression
{
    /// <summary>
    /// Ages every player once and removes players who have reached their
    /// deterministic retirement threshold from their current roster.
    /// </summary>
    public static SeasonAgeingSummary AgeAndRetire(CareerWorld world)
    {
        if (world is null) throw new System.ArgumentNullException(nameof(world));

        SeasonAgeingSummary summary = new()
        {
            RetiredPlayers = new System.Collections.Generic.List<CareerPlayer>()
        };
        uint seasonSeed = unchecked((uint)world.Season);

        var clubIds = new System.Collections.Generic.List<ushort>(world.Clubs.Keys);
        clubIds.Sort();
        foreach (ushort clubId in clubIds)
            AgeAndRetire(world.Clubs[clubId].Squad, seasonSeed, ref summary);

        AgeAndRetire(world.FreeAgents, seasonSeed, ref summary);
        return summary;
    }

    private static void AgeAndRetire(
        System.Collections.Generic.List<CareerPlayer> players,
        uint seasonSeed,
        ref SeasonAgeingSummary summary)
    {
        for (int i = players.Count - 1; i >= 0; i--)
        {
            CareerPlayer player = players[i];
            player.Age++;
            summary.Aged++;

            CareerRng rng = new(seasonSeed, player.Id);
            int hardCap = 36 + rng.NextInt(5);
            // Kind deviation from the original (which never force-retires): a
            // severity-7 injury never heals (swos.asm:33657-33786), so at season
            // end the career-ending injury retires the player rather than leaving
            // a permanently unavailable name on the books.
            if (!player.Retired && player.Age < hardCap && player.InjurySeverity < 7
                && (player.Age < 33 || PotentialModel.OverallOf(player) >= 2.5))
            {
                continue;
            }

            player.Retired = true;
            players.RemoveAt(i);
            summary.Retired++;
            summary.RetiredPlayers.Add(player);
        }
    }
}

public struct SeasonAgeingSummary
{
    public int Aged;
    public int Retired;
    public System.Collections.Generic.List<CareerPlayer> RetiredPlayers;
}
