using OpenSwos.Assets;

namespace OpenSwos.Competition.Career;

/// <summary>
/// Creates the persistent career-world copy of the read-only TEAM.* roster data.
/// </summary>
public static class CareerWorldBuilder
{
    /// <summary>
    /// Materializes every distinct master team and its players into the career save state.
    /// </summary>
    public static CareerWorld BuildWorld(
        CompetitionState career,
        System.Collections.Generic.IReadOnlyList<TeamRecord> masterTeams,
        System.IProgress<float>? progress = null)
    {
        if (career?.Career is null)
        {
            throw new System.ArgumentException("CAREER STATE REQUIRED", nameof(career));
        }

        CareerWorld world = new() { Season = career.Career.Season };
        uint baseSeed = CareerRng.SeedFrom(career);

        // Real 1996/97 ages come from the committed offline table (Wikidata, CC0)
        // when a player's name matches; otherwise we fall back to skill-derived
        // ages. KnownAges is pure, so we resolve the res:// path here (Godot-aware).
        KnownAges.ResolvePath ??= static () =>
        {
            try { return Godot.ProjectSettings.GlobalizePath("res://data/known_ages_1997.csv"); }
            catch { return null; }
        };
        int agesMatched = 0, agesTotal = 0;

        for (int i = 0; i < masterTeams.Count; i++)
        {
            TeamRecord teamRecord = masterTeams[i];
            HarvestNames(world.YouthFirstNamePools, world.YouthSurnamePools, teamRecord);
            // TEAM.* header byte 0 is the source-file nation.  SWOS reserves
            // 80..85 for its continental national-team files (TEAM.080..085).
            if (teamRecord.Nation is >= 80 and <= 85)
                world.NationalTeamIds.Add(teamRecord.GlobalId);
            if (!world.Clubs.ContainsKey(teamRecord.GlobalId))
            {
                CareerClub club = new() { GlobalId = teamRecord.GlobalId };

                foreach (PlayerRecord playerRecord in teamRecord.Players)
                {
                    CareerPlayer player = new()
                    {
                        Id = world.NextPlayerId++,
                        ClubId = teamRecord.GlobalId,
                        Name = playerRecord.Name,
                        Position = playerRecord.Position,
                        Nationality = playerRecord.Nationality,
                        ShirtNumber = playerRecord.ShirtNumber,
                        Face = playerRecord.Face,
                        ValueCode = playerRecord.ValueCode,
                        Passing = playerRecord.Passing,
                        Shooting = playerRecord.Shooting,
                        Heading = playerRecord.Heading,
                        Tackling = playerRecord.Tackling,
                        Control = playerRecord.Control,
                        Speed = playerRecord.Speed,
                        Finishing = playerRecord.Finishing
                    };
                    club.Squad.Add(player);
                    // Real age first (offline table), skill-derivation as fallback.
                    agesTotal++;
                    // Player nationality byte uses the PLAYER-nation numbering,
                    // so the country hint comes from PlayerNationNames.
                    string nationName = OpenSwos.Assets.PlayerNationNames.FullName(player.Nationality);
                    if (KnownAges.TryGetAge(player.Name, nationName, out int realAge))
                    {
                        player.Age = realAge;
                        agesMatched++;
                    }
                    else
                    {
                        AgeModel.AssignInitialAge(player, baseSeed);
                    }
                    StaminaModel.AssignStamina(player, baseSeed);
                    PotentialModel.AssignPotential(player, baseSeed);
                }

                world.Clubs[teamRecord.GlobalId] = club;
            }

            progress?.Report((i + 1) / (float)masterTeams.Count);
        }

        int pct = agesTotal > 0 ? (int)(agesMatched * 100L / agesTotal) : 0;
        Godot.GD.Print($"[ages] matched {agesMatched} of {agesTotal} players ({pct}%)" +
            $" from known_ages table ({KnownAges.RowCount} rows)");

        Finance.SeedBudgets(world);
        career.Career.World = world;
        return world;
    }

    // Splits each real player's name into a FIRST-name token and a SURNAME token,
    // pooling them separately per nationality.  A single-token name (a nickname
    // such as CADU / CHICO / LUISAO) is a surname only — it never seeds a first
    // name, so generated youths built from these pools are always two real tokens.
    private static void HarvestNames(
        System.Collections.Generic.Dictionary<byte, System.Collections.Generic.List<string>> firstPools,
        System.Collections.Generic.Dictionary<byte, System.Collections.Generic.List<string>> surnamePools,
        TeamRecord team)
    {
        foreach (PlayerRecord player in team.Players)
        {
            if (string.IsNullOrWhiteSpace(player.Name)) continue;
            string trimmed = player.Name.Trim();
            int separator = trimmed.LastIndexOf(' ');
            if (separator < 0)
            {
                AddToPool(surnamePools, player.Nationality, trimmed);
                continue;
            }

            string first = trimmed[..separator].Trim();
            string surname = trimmed[(separator + 1)..].Trim();
            // Keep only the leading token as the first name so "JEAN PIERRE PAPIN"
            // pools JEAN + PAPIN rather than a compound first name.
            int firstBreak = first.IndexOf(' ');
            if (firstBreak >= 0) first = first[..firstBreak];
            if (first.Length > 0) AddToPool(firstPools, player.Nationality, first);
            if (surname.Length > 0) AddToPool(surnamePools, player.Nationality, surname);
        }
    }

    private static void AddToPool(
        System.Collections.Generic.Dictionary<byte, System.Collections.Generic.List<string>> pools,
        byte nationality, string token)
    {
        if (!pools.TryGetValue(nationality, out System.Collections.Generic.List<string>? pool))
        {
            pool = new System.Collections.Generic.List<string>();
            pools[nationality] = pool;
        }
        pool.Add(token);
    }

    /// <summary>Returns the total number of club and free-agent players.</summary>
    public static int CountPlayers(CareerWorld world)
    {
        int count = world.FreeAgents.Count;
        foreach (CareerClub club in world.Clubs.Values)
        {
            count += club.Squad.Count;
        }

        return count;
    }
}
