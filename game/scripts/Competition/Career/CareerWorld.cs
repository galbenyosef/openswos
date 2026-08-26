namespace OpenSwos.Competition.Career;

// ============================================================================
// Persistent, evolving career roster stored in the save (never in the read-only
// TeamRecord/PlayerRecord). It is seeded from PlayerRecord when a career is
// created, then evolves independently. Skills remain doubles so growth can
// accumulate finely, and are quantized to int 0..7 only when a match or UI
// reads them. The full graph round-trips through System.Text.Json: it contains
// only primitives, strings, arrays, List and Dictionary<ushort, ...>.
// See docs/design/05-career-mode-DETAILED-PLAN.md, "Data-model policy".
// ============================================================================

public sealed class CareerPlayer
{
    public int Id { get; set; }                 // stable, unique within a CareerWorld
    public ushort ClubId { get; set; }          // owning club TEAM.* GlobalId; 0 = free agent
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public byte Nationality { get; set; }
    public byte ShirtNumber { get; set; }
    public int Face { get; set; }                // 0 WHITE, 1 GINGER, 2 BLACK (as PlayerRecord.Face)
    // SWOS price code, now a fractional double (0..49+, may exceed the ladder
    // for grown stars). For goalkeepers this is the source of their ability;
    // their seven outfield skills are deliberately zero. For outfield players it
    // is seeded from the TEAM.* code and then drifts with their development so
    // valuation tracks ability. Legacy int codes deserialize into this natively.
    public double ValueCode { get; set; }

    // Skills are 0..7 SWOS scale, held as doubles for fine career growth.
    public double Passing { get; set; }
    public double Shooting { get; set; }
    public double Heading { get; set; }
    public double Tackling { get; set; }
    public double Control { get; set; }
    public double Speed { get; set; }
    public double Finishing { get; set; }
    public int Age { get; set; }
    public double Potential { get; set; }        // hidden true ceiling, <= 7
    public int Stamina { get; set; }             // 0..7 fatigue drain/recovery rate
    public double[] GrowthCarry { get; set; } = new double[7];
    public int Form { get; set; }                // -3..+3 simple form
    public int FatigueCarry { get; set; }        // 0..100 tiredness carried between matches
    /// <summary>
    /// Goals scored across the whole career, never reset — the original's
    /// "CAREER TOTAL" (asm:283055). The per-SEASON count lives on the
    /// competition (CompetitionState.Scorers), which is rebuilt every season.
    /// </summary>
    public int CareerGoals { get; set; }

    // ---- CAREER COUNTERS (career depth plan feature #8) --------------------
    // Two integers are what turn an anonymous striker into YOUR striker, and
    // what makes selling him hurt. Credited by CareerRecords from the XI of
    // EVERY fixture in the competition (played or simulated), so a club legend
    // is built the same way whether or not the manager watched the match.
    /// <summary>Matches started, across the whole career, never reset.</summary>
    public int Appearances { get; set; }
    /// <summary>
    /// Appearances and goals for the CURRENT club only. Reset lazily when
    /// <see cref="ClubStatsClubId"/> stops matching <see cref="ClubId"/>, so a
    /// transfer needs no hook at any of the four places a club changes hands.
    /// </summary>
    public int ClubAppearances { get; set; }
    public int ClubGoals { get; set; }
    public ushort ClubStatsClubId { get; set; }
    /// <summary>Appearances in the season now being played; reset at rollover.</summary>
    public int SeasonAppearances { get; set; }

    // ---- TRAINING (user directive 2026-08-26) ------------------------------
    /// <summary>
    /// Training sessions this player has completed, career-long. Purely
    /// informational — the development itself lands in GrowthCarry/skills.
    /// </summary>
    public int TrainingSessions { get; set; }
    /// <summary>
    /// Sharpness 0..100, the FIFA-18-style "match fitness" a player builds by
    /// training and by playing. It is a small match-time nudge on top of Form
    /// and it decays when he neither trains nor plays, which is what stops
    /// "pick the same XI for ever" from being free.
    /// </summary>
    public int Sharpness { get; set; } = 50;

    // Persistent post-match injury severity, 0..7 — the original's per-player
    // cardsInjuries bits 5-7 (struct swos.asm:387-408; IsPlayerInjured
    // swos.asm:46743-46801). 0 = fit; 1 = "carrying a knock" (still selectable,
    // doubled re-injury risk + in-game speed handicap, swos.asm:245866); >= 2 =
    // unavailable, excluded from the XI as the original does at init
    // (position=-1, substituted=1, swos.asm:36103-36107); 7 never heals.
    // Recovered once per fixture for the whole squad (swos.asm:33657-33786) and
    // OR-persisted after a played match (UpdatePlayerInjuries, swos.asm:35651-
    // 35701) — human club only. JSON default 0 keeps legacy saves safe.
    public int InjurySeverity { get; set; }

    // Scouting knowledge.
    public bool Scouted { get; set; }
    public int ScoutAccuracy { get; set; }       // 0 = unseen; higher = better estimate
    public double EstLow { get; set; }
    public double EstHigh { get; set; }
    public bool Retired { get; set; }

    // True for youths created by RegenModel (not seeded from a real TEAM.* record).
    // Real players may legitimately share a surname (e.g. several GARCIA); the
    // regen name uniqueness rule only applies to GENERATED players, so it needs a
    // way to tell them apart when scanning the world. Defaults false for real
    // players and legacy saves.
    public bool Generated { get; set; }

    public int[] QuantizedSkills()
    {
        return
        [
            Math.Clamp((int)Math.Round(Passing, MidpointRounding.AwayFromZero), 0, 7),
            Math.Clamp((int)Math.Round(Shooting, MidpointRounding.AwayFromZero), 0, 7),
            Math.Clamp((int)Math.Round(Heading, MidpointRounding.AwayFromZero), 0, 7),
            Math.Clamp((int)Math.Round(Tackling, MidpointRounding.AwayFromZero), 0, 7),
            Math.Clamp((int)Math.Round(Control, MidpointRounding.AwayFromZero), 0, 7),
            Math.Clamp((int)Math.Round(Speed, MidpointRounding.AwayFromZero), 0, 7),
            Math.Clamp((int)Math.Round(Finishing, MidpointRounding.AwayFromZero), 0, 7)
        ];
    }

    /// <summary>
    /// Returns the display and valuation ability on the SWOS 0..7 scale.
    /// Goalkeepers derive this from their TEAM.* price code, as the original
    /// AdjustPlayerSkills routine does before its match-specific random bonus.
    /// </summary>
    public int EffectiveOverall()
    {
        if (string.Equals(Position, "G", StringComparison.OrdinalIgnoreCase))
            return (int)Math.Clamp(
                Math.Round((ValueCode + 3.0) / 7.0, MidpointRounding.AwayFromZero), 0, 7);

        // Allocation-free: sum the seven clamped skills directly rather than
        // materialising QuantizedSkills(). This runs inside the transfer-market
        // sort key computation (~27.6k players), so avoiding an int[7] per call
        // matters (see TransferModel.Market decorate-sort-undecorate).
        int total = ClampSkill(Passing) + ClampSkill(Shooting) + ClampSkill(Heading)
                  + ClampSkill(Tackling) + ClampSkill(Control) + ClampSkill(Speed)
                  + ClampSkill(Finishing);
        return Math.Clamp((int)Math.Round(total / 7.0, MidpointRounding.AwayFromZero), 0, 7);
    }

    /// <summary>
    /// Display SKILL total (0..49): the SUM of the seven quantized skills, so a
    /// weak team differentiates (14 vs 19) instead of every player reading the
    /// same 0..7 average. Goalkeepers have no outfield skills, so use their
    /// derived overall scaled by 7 to keep them on the same 0..49 scale.
    /// </summary>
    public int EffectiveSkillSum()
    {
        if (string.Equals(Position, "G", StringComparison.OrdinalIgnoreCase))
            return EffectiveOverall() * 7;
        return ClampSkill(Passing) + ClampSkill(Shooting) + ClampSkill(Heading)
             + ClampSkill(Tackling) + ClampSkill(Control) + ClampSkill(Speed)
             + ClampSkill(Finishing);
    }

    private static int ClampSkill(double v)
        => Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 7);
}

/// <summary>
/// One club's all-time record for one player — career depth plan feature #8.
/// Survives the player leaving, which is the whole point of a legends list.
/// </summary>
public sealed class LegendRow
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public int Appearances { get; set; }
    public int Goals { get; set; }
    /// <summary>Season the row was last touched, so "still here" can be shown.</summary>
    public int LastSeason { get; set; }
}

public sealed class Coach
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Quality { get; set; }             // 0..7
    public string Specialty { get; set; } = "";  // "ATTACK" / "DEFENCE" / "YOUTH" / "GENERAL"
    public long Wage { get; set; }
}

public sealed class ScoutingState
{
    public System.Collections.Generic.List<int> Regions { get; set; } = new();
    public System.Collections.Generic.List<int> WatchedPlayerIds { get; set; } = new();
    public int ScoutQuality { get; set; }        // 0..7 club scouting strength
}

public sealed class CareerClub
{
    public ushort GlobalId { get; set; }
    public System.Collections.Generic.List<CareerPlayer> Squad { get; set; } = new();
    // User-chosen pre-match lineup: CareerPlayer.Id values in in-game slot order
    // (0 = keeper, 1..10 = XI, 11..15 = bench). Empty means "auto" — let
    // CareerMatchTeam order the squad by ability/position. Honored by
    // CareerMatchTeam.Build only when valid (all ids present in the squad and
    // slot 0 a keeper); any players not listed are appended by the auto rules.
    public System.Collections.Generic.List<int> PreferredLineup { get; set; } = new();
    public long Budget { get; set; }
    public System.Collections.Generic.List<Coach> Coaches { get; set; } = new();
    public System.Collections.Generic.List<int> TrainingFocusIds { get; set; } = new();

    // ---- CLUB LEGENDS (career depth plan feature #8) -----------------------
    // Kept ONLY for clubs the manager has actually worked at (writing a row for
    // each of ~29 000 players in 1730 clubs would bloat every save for a screen
    // nobody can open). Upserted at the season rollover, so a player who is
    // sold keeps the record he built here.
    public System.Collections.Generic.List<LegendRow> Legends { get; set; } = new();
    public ScoutingState Scouting { get; set; } = new();

    // ---- SEASON LEDGER (career depth plan feature #1) ---------------------
    // Money that moved during the current season, so the end-of-season
    // statement can print the ORIGINAL game's 'PLAYER SALES:' and
    // 'PLAYER PURCHASES:' lines and reconcile exactly against the balance.
    // Reset for every club when the season rolls over (Finance).
    // SeasonLedgerActive distinguishes "nothing happened" from "old save with
    // no ledger", so pre-feature careers fall back to the live budget instead
    // of claiming they started the season at zero.
    public bool SeasonLedgerActive { get; set; }
    public long SeasonOpeningBalance { get; set; }
    public long SeasonPlayerSales { get; set; }
    public long SeasonPlayerPurchases { get; set; }
    /// <summary>Coach signing fees, coach wages paid mid-season, scouting upgrades.</summary>
    public long SeasonStaffSpend { get; set; }
}

public sealed class CareerWorld
{
    // Keyed by club TEAM.* GlobalId.
    public System.Collections.Generic.Dictionary<ushort, CareerClub> Clubs { get; set; } = new();
    // TEAM.* national sides remain in the world for international matches, but
    // their players are not part of the club transfer market.
    public System.Collections.Generic.HashSet<ushort> NationalTeamIds { get; set; } = new();
    public System.Collections.Generic.List<CareerPlayer> FreeAgents { get; set; } = new();
    // First names and surnames harvested SEPARATELY from the read-only TEAM.*
    // master roster at world creation, keyed by nationality.  A source row with a
    // single token (a nickname such as CADU / CHICO) contributes that token to the
    // SURNAME pool only.  Keeping the pools with the save makes subsequent youth
    // intakes independent of whether the source assets are still available on load.
    // A generated youth is always drawn as "FIRST SURNAME" from these two pools.
    public System.Collections.Generic.Dictionary<byte, System.Collections.Generic.List<string>> YouthFirstNamePools { get; set; } = new();
    public System.Collections.Generic.Dictionary<byte, System.Collections.Generic.List<string>> YouthSurnamePools { get; set; } = new();
    public int NextPlayerId { get; set; } = 1;
    public int Season { get; set; } = 1;
}
