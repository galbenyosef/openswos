using System;
using System.Collections.Generic;

namespace OpenSwos.Competition.Career;

// ============================================================================
// WEEKLY TRAINING — user directive, 2026-08-26.
//
// "fajnie by bylo tez dodac trening jak w fifie 18 - taki co tydzien ze wybiera
//  sie zawodnikow i mozna pomalu im ladowac statsy w zaleznosci od tego jak im
//  poszedl trening, trudnosci treningu, potencjalu zawodnika, trenera ktory ma
//  mu pomagac."
//
// NOTE FOR THE NEXT SESSION: 03-career-depth-plan.md used to list "deep tactics
// and TRAINING SCHEDULES" under "what we deliberately do NOT build". The user
// has overruled that, and he is right that the two are different things: SWOS
// does TACTICS with the pad during play, and a training form would indeed be a
// worse copy of that — but DEVELOPMENT is not tactics, it is the part of a
// career the manager currently has no lever on at all. It stays opt-in: skip
// every session for twenty seasons and the career still completes (test 3).
//
// ---------------------------------------------------------------------------
// WHAT WAS RESEARCHED (2026-08-26), and what we took from each
//
// FIFA 18 career mode (gamespot.com "Career Mode's New Features Revealed";
// fifacareermodetips.com; Operation Sports "Training and player progression"):
//   * a weekly session where you assign SPECIFIC players to a drill;
//   * drills come in bronze / silver / gold difficulty — the harder the drill,
//     the bigger the improvement, and the harder it is to come out of it well;
//   * the session is GRADED and the grade is what feeds development;
//   * growth is driven by the gap between current ability and POTENTIAL (a
//     55-rated player with 85 potential grows faster than a 68 with 88), by
//     age, and by whether the player also plays;
//   * trained youngsters visibly outgrow untrained ones — that is the reason
//     the feature is loved, and it is the reason to have it here;
//   * a player who outperforms his ceiling can have that ceiling raised.
//
// Championship Manager 01/02 (fm-gamer.blogspot.com schedules; champman0102.net):
//   * five categories — FITNESS / TACTICS / SHOOTING / SKILLS / GOALKEEPING;
//   * four intensities — None / Light / Medium / Intensive;
//   * schedules are per POSITION (a striker trains shooting intensively, a
//     keeper trains goalkeeping intensively), and youths get an even spread;
//   * intensity is a real trade-off: it is what tires and injures players.
//
// So: FIFA 18's weekly, hand-picked, graded session; CM 01/02's intensity dial
// and position-shaped drills. Both mapped onto SWOS's seven 0..7 skills rather
// than onto attributes SWOS does not have.
//
// ---------------------------------------------------------------------------
// RULES
//
//  * ONE session per fixture round. Skipping it loses it — there is no backlog
//    to clear, because a backlog is a chore.
//  * The manager picks a DRILL (what is trained), an INTENSITY (how hard) and a
//    GROUP (who does it, max MaxGroup). Everyone else does a light team session
//    that keeps them ticking over and costs nothing.
//  * Every player in the group is graded 0..3 (POOR / OK / GOOD / EXCELLENT)
//    from: coach quality, how well the drill suits his position, his age, how
//    much headroom he has left, how fresh he is, and a deterministic roll.
//  * The grade becomes development in the drill's skills, through the SAME
//    GrowthCarry mechanism season growth uses, so a skill point lands exactly
//    when the fractional carry crosses 1.0 and nothing double-counts.
//  * Potential is the ceiling, as everywhere else in the career — EXCEPT that
//    an EXCELLENT session by a player already at his ceiling can lift it very
//    slightly (FIFA 18's "outperform your potential"), capped at 7.
//  * INTENSE costs fatigue and can injure. That is the whole trade-off; without
//    it "train everyone intensively every week" would be a free lunch.
//
// DETERMINISM: CareerRng only, seeded from (season, round, player id) — never
// System.Random and never the competition RNG, so running a session cannot move
// the fixture-simulation stream and a reloaded save trains identically.
// ============================================================================

/// <summary>One drill: what it trains and which coach helps with it.</summary>
public sealed class TrainingDrill
{
    public int Index { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Indices into the seven SWOS skills, in GrowthModel order.</summary>
    public int[] Skills { get; init; } = [];
    /// <summary>Coach specialty that helps: "ATTACK" / "DEFENCE" / "YOUTH" / "".</summary>
    public string Coach { get; init; } = "";
    /// <summary>Position lines this drill suits: G / D / M / W / A.</summary>
    public string Suits { get; init; } = "";
    /// <summary>Recovery drill: restores condition instead of developing skill.</summary>
    public bool Recovery { get; init; }
    /// <summary>Goalkeeper drill: develops the keeper's ValueCode ability.</summary>
    public bool Keeper { get; init; }
}

/// <summary>One player's line in the session report, straight to both clients.</summary>
public sealed class TrainingResultRow
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    /// <summary>0 POOR, 1 OK, 2 GOOD, 3 EXCELLENT.</summary>
    public int Grade { get; set; }
    /// <summary>"SHOOTING 4-5" style gains, already resolved to skill names.</summary>
    public List<string> Gains { get; set; } = new();
    /// <summary>Condition change (negative = tired out by the session).</summary>
    public int FitnessDelta { get; set; }
    public int SharpnessDelta { get; set; }
    /// <summary>Injury picked up in training, 0 = none.</summary>
    public int Injury { get; set; }
    /// <summary>His ceiling went up — the rarest and best line in the report.</summary>
    public bool PotentialUp { get; set; }
}

public static class TrainingModel
{
    // ---- tuning, all in one place ----------------------------------------
    /// <summary>How many players one session can carry.</summary>
    public const int MaxGroup = 6;
    /// <summary>Development a grade is worth, in fractional skill points.</summary>
    private static readonly double[] GradeGain = [0.010, 0.035, 0.070, 0.115];
    /// <summary>Multiplier per intensity: LIGHT / NORMAL / INTENSE.</summary>
    private static readonly double[] IntensityGain = [0.55, 1.00, 1.60];
    /// <summary>Condition cost per intensity (negative = the player recovers).</summary>
    private static readonly int[] IntensityFatigue = [-6, 5, 14];
    /// <summary>Injury chance per 1000 at each intensity, before modifiers.</summary>
    private static readonly int[] IntensityInjuryPerMille = [2, 9, 28];
    /// <summary>Sharpness a session is worth per intensity.</summary>
    private static readonly int[] IntensitySharpness = [4, 8, 13];
    /// <summary>Chance per 1000 that an EXCELLENT session lifts the ceiling.</summary>
    private const int PotentialBreakPerMille = 60;
    /// <summary>How much it lifts it by.</summary>
    private const double PotentialBreakStep = 0.15;
    /// <summary>Development the players NOT in the group get, for free.</summary>
    private const double TeamSessionGain = 0.006;

    public static readonly string[] IntensityNames = ["LIGHT", "NORMAL", "INTENSE"];
    public static readonly string[] GradeNames = ["POOR", "OK", "GOOD", "EXCELLENT"];

    /// <summary>
    /// GrowthModel's skill order: Passing, Shooting, Heading, Tackling, Control,
    /// Speed, Finishing. Kept here as names so a report line reads in English.
    /// </summary>
    public static readonly string[] SkillNames =
        ["PASSING", "SHOOTING", "HEADING", "TACKLING", "CONTROL", "SPEED", "FINISHING"];

    /// <summary>
    /// The drills. Deliberately few: nine entries fit one screen, which is the
    /// CM 01/02 bar ("a completionist still finishes in finite time").
    /// </summary>
    public static readonly TrainingDrill[] Drills =
    [
        new() { Index = 0, Key = "train.drill_shooting", Name = "SHOOTING PRACTICE",
                Skills = [1, 6], Coach = "ATTACK", Suits = "AW" },
        new() { Index = 1, Key = "train.drill_finishing", Name = "FINISHING DRILL",
                Skills = [6], Coach = "ATTACK", Suits = "A" },
        new() { Index = 2, Key = "train.drill_passing", Name = "PASSING AND CROSSING",
                Skills = [0, 4], Coach = "", Suits = "MW" },
        new() { Index = 3, Key = "train.drill_control", Name = "DRIBBLING CIRCUIT",
                Skills = [4, 5], Coach = "", Suits = "MWA" },
        new() { Index = 4, Key = "train.drill_pace", Name = "SPRINTS AND SHUTTLES",
                Skills = [5], Coach = "", Suits = "DMWA" },
        new() { Index = 5, Key = "train.drill_heading", Name = "HEADING AND AERIAL",
                Skills = [2], Coach = "DEFENCE", Suits = "DA" },
        new() { Index = 6, Key = "train.drill_defending", Name = "DEFENDING AND TACKLING",
                Skills = [3, 2], Coach = "DEFENCE", Suits = "DM" },
        new() { Index = 7, Key = "train.drill_keeper", Name = "GOALKEEPING",
                Skills = [], Coach = "DEFENCE", Suits = "G", Keeper = true },
        new() { Index = 8, Key = "train.drill_recovery", Name = "RECOVERY AND FITNESS",
                Skills = [], Coach = "", Suits = "GDMWA", Recovery = true },
    ];

    public static TrainingDrill DrillAt(int index)
        => Drills[Math.Clamp(index, 0, Drills.Length - 1)];

    // ------------------------------------------------------------------
    // Availability
    // ------------------------------------------------------------------

    /// <summary>
    /// A session is available once per competition round. Between seasons there
    /// is nothing to train for, so a finished season closes the gym.
    /// </summary>
    public static bool CanTrain(CompetitionState? s)
    {
        var career = s?.Career;
        if (s is null || career is null || career.Retired || career.Sacked) return false;
        if (s.Finished) return false;
        return !AlreadyTrained(s);
    }

    public static bool AlreadyTrained(CompetitionState? s)
    {
        var career = s?.Career;
        if (s is null || career is null) return false;
        return career.TrainingLastSeason == career.Season && career.TrainingLastRound == s.CurrentRound;
    }

    /// <summary>
    /// Suggests a drill for a squad: the one that suits the most players in the
    /// current group, or a position-shaped default, exactly as CM 01/02's
    /// per-position schedules do. Used to fill an empty selection.
    /// </summary>
    public static int SuggestDrill(CareerClub? club, IReadOnlyList<int>? group)
    {
        if (club?.Squad is null) return 0;
        int[] score = new int[Drills.Length];
        foreach (var p in club.Squad)
        {
            if (p is null) continue;
            if (group is not null && group.Count > 0 && !group.Contains(p.Id)) continue;
            string line = ScorerModel.LineOf(p.Position);
            for (int d = 0; d < Drills.Length; d++)
                if (Drills[d].Suits.Contains(line, StringComparison.Ordinal)) score[d]++;
        }
        int best = 0;
        for (int d = 1; d < Drills.Length; d++)
            if (score[d] > score[best]) best = d;
        return best;
    }

    /// <summary>
    /// Fills the group automatically: the players with the most headroom left
    /// who are fit enough to work. FIFA 18's lesson is that training pays most
    /// on a young player with a long way to go.
    /// </summary>
    public static List<int> AutoGroup(CareerClub? club, int drillIndex)
    {
        var picked = new List<int>();
        if (club?.Squad is null) return picked;
        var drill = DrillAt(drillIndex);
        var candidates = new List<CareerPlayer>();
        foreach (var p in club.Squad)
        {
            if (p is null || p.InjurySeverity >= 2) continue;
            if (drill.Keeper != IsKeeper(p)) continue;      // keepers only in the keeper drill
            candidates.Add(p);
        }
        candidates.Sort((a, b) =>
        {
            double ha = Math.Max(0.0, a.Potential - PotentialModel.OverallOf(a));
            double hb = Math.Max(0.0, b.Potential - PotentialModel.OverallOf(b));
            int byHeadroom = hb.CompareTo(ha);
            if (byHeadroom != 0) return byHeadroom;
            int byAge = a.Age.CompareTo(b.Age);
            return byAge != 0 ? byAge : a.Id.CompareTo(b.Id);
        });
        for (int i = 0; i < candidates.Count && picked.Count < MaxGroup; i++)
            picked.Add(candidates[i].Id);
        return picked;
    }

    /// <summary>Adds or removes one squad player from the session group.</summary>
    public static bool ToggleGroup(CompetitionState? s, int playerId, out string refusal)
    {
        refusal = "";
        var career = s?.Career;
        var club = ClubOf(career);
        if (career is null || club?.Squad is null) { refusal = "NO SQUAD"; return false; }
        career.TrainingGroup ??= new List<int>();
        if (career.TrainingGroup.Remove(playerId)) return true;
        if (!club.Squad.Exists(p => p is not null && p.Id == playerId)) { refusal = "PLAYER NOT AVAILABLE"; return false; }
        if (career.TrainingGroup.Count >= MaxGroup) { refusal = "GROUP FULL"; return false; }
        career.TrainingGroup.Add(playerId);
        return true;
    }

    // ------------------------------------------------------------------
    // The session
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs one session and writes the report. Returns false with a reason when
    /// there is nothing to run — both clients show the reason rather than
    /// silently doing nothing.
    /// </summary>
    public static bool RunSession(CompetitionState? s, out string refusal)
    {
        refusal = "";
        var career = s?.Career;
        var club = ClubOf(career);
        if (s is null || career is null || club?.Squad is null) { refusal = "NO SQUAD"; return false; }
        if (AlreadyTrained(s)) { refusal = "ALREADY TRAINED THIS WEEK"; return false; }
        if (!CanTrain(s)) { refusal = "NO TRAINING BETWEEN SEASONS"; return false; }

        var drill = DrillAt(career.TrainingDrill);
        int intensity = Math.Clamp(career.TrainingIntensity, 0, 2);
        var group = new List<int>(career.TrainingGroup ?? new List<int>());
        if (group.Count == 0) group = AutoGroup(club, drill.Index);

        // One RNG stream for the whole session, keyed by where in the career it
        // happens, so re-running a loaded save reproduces it exactly.
        uint seed = unchecked((uint)(0x54524E47u ^ (career.Season * 8191) ^ (s.CurrentRound * 131)));

        var report = new List<TrainingResultRow>();
        int coachQuality = BestCoachQuality(club, drill);
        int breakthroughs = 0;

        foreach (int id in group)
        {
            CareerPlayer? p = null;
            foreach (var q in club.Squad) if (q is not null && q.Id == id) { p = q; break; }
            if (p is null) continue;
            if (p.InjurySeverity >= 2) continue;             // injured players do not train
            report.Add(TrainOne(s, career, club, p, drill, intensity, coachQuality, seed, ref breakthroughs));
        }

        // Everyone else does the light team session: a fraction of the gain, no
        // fatigue, no risk. It exists so that ignoring training entirely is a
        // small loss rather than a cliff.
        foreach (var p in club.Squad)
        {
            if (p is null || group.Contains(p.Id) || p.InjurySeverity >= 2) continue;
            ApplyTeamSession(p);
        }

        career.TrainingLastRound = s.CurrentRound;
        career.TrainingLastSeason = career.Season;
        career.TrainingSessionsRun++;
        career.TrainingGroup = group;
        career.TrainingReport = report;

        Chronicle.Add(career, "chron.training",
            "TRAINING: %a AT %b INTENSITY", "training", 0,
            a: drill.Name, b: IntensityNames[intensity],
            round: s.CurrentRound);
        if (breakthroughs > 0)
            Chronicle.Add(career, "chron.training_break",
                "%0 PLAYER(S) BROKE THROUGH IN TRAINING", "training", 2,
                n: breakthroughs, round: s.CurrentRound);
        return true;
    }

    private static TrainingResultRow TrainOne(
        CompetitionState s, CareerState career, CareerClub club, CareerPlayer p,
        TrainingDrill drill, int intensity, int coachQuality, uint seed, ref int breakthroughs)
    {
        var row = new TrainingResultRow
        {
            PlayerId = p.Id,
            Name = ScorerModel.CleanName(p.Name),
            Position = p.Position ?? "",
        };
        var rng = new CareerRng(seed, p.Id);

        // ---- the grade -------------------------------------------------
        // Everything that FIFA 18 says should matter, expressed as one 0..100
        // score that is then banded. Written out rather than folded into one
        // expression so the next person can see WHY a player trained badly.
        double score = 42.0;
        score += coachQuality * 3.4;                          // the coach helping him
        if (drill.Suits.Contains(ScorerModel.LineOf(p.Position), StringComparison.Ordinal))
            score += 9.0;                                     // the drill suits him
        else
            score -= 6.0;
        double headroom = Math.Clamp(p.Potential - PotentialModel.OverallOf(p), 0.0, 7.0);
        score += headroom * 5.0;                              // a long way still to go
        score += AgeScore(p.Age);                             // young legs learn faster
        int condition = Math.Clamp(100 - p.FatigueCarry, 0, 100);
        score += (condition - 60) * 0.25;                     // a tired player learns nothing
        score += (Math.Clamp(p.Sharpness, 0, 100) - 50) * 0.08;
        score += Math.Clamp(p.Form, -3, 3) * 1.5;
        score += intensity * 4.0;                             // harder drill, bigger prize
        score += rng.Range(-14, 14);

        int grade = score >= 82 ? 3 : score >= 62 ? 2 : score >= 40 ? 1 : 0;
        row.Grade = grade;

        // ---- what he gets out of it ------------------------------------
        double gain = GradeGain[grade] * IntensityGain[intensity];
        // A tired player barely benefits, whatever the drill says. This is the
        // reason RECOVERY exists as a drill at all.
        gain *= 0.45 + 0.55 * (condition / 100.0);

        if (drill.Recovery)
        {
            int before = Math.Clamp(100 - p.FatigueCarry, 0, 100);
            p.FatigueCarry = Math.Clamp(p.FatigueCarry - (14 + grade * 6), 0, 100);
            // Recovery work heals a knock faster than sitting still does.
            if (p.InjurySeverity == 1 && grade >= 2 && rng.NextInt(3) == 0) p.InjurySeverity = 0;
            p.Sharpness = Math.Clamp(p.Sharpness + 2, 0, 100);
            row.FitnessDelta = Math.Clamp(100 - p.FatigueCarry, 0, 100) - before;
            row.SharpnessDelta = 2;
            p.TrainingSessions++;
            return row;
        }

        if (drill.Keeper)
        {
            if (IsKeeper(p))
            {
                double ability = p.EffectiveOverall();
                double room = Math.Clamp(p.Potential - ability, 0.0, 7.0);
                double before = ability;
                // Seven price codes to a skill level, the same conversion
                // GrowthModel uses for keepers.
                p.ValueCode = Math.Clamp(p.ValueCode + gain * (0.35 + room) * 7.0, 0.0, 60.0);
                double after = p.EffectiveOverall();
                if (after > before)
                    row.Gains.Add(NameOfSkill(-1) + " " + (int)before + "-" + (int)after);
            }
        }
        else
        {
            double[] skills = SkillsOf(p);
            double[] carry = EnsureCarry(p);
            foreach (int i in drill.Skills)
            {
                if (i < 0 || i >= 7) continue;
                double room = Math.Clamp(p.Potential - skills[i], 0.0, 7.0);
                // The FIFA 18 curve: development is proportional to how much
                // room is left, so a finished player barely moves and a raw
                // youngster climbs quickly.
                double delta = gain * (0.25 + room);
                int before = (int)Math.Round(skills[i], MidpointRounding.AwayFromZero);
                carry[i] += delta;
                while (carry[i] >= 1.0 && skills[i] < 7.0)
                {
                    // Potential is the ceiling everywhere else in the career and
                    // it is the ceiling here too.
                    if (skills[i] + 1.0 > Math.Max(p.Potential, 1.0)) break;
                    skills[i] += 1.0;
                    carry[i] -= 1.0;
                }
                int after = (int)Math.Round(skills[i], MidpointRounding.AwayFromZero);
                if (after > before) row.Gains.Add(SkillNames[i] + " " + before + "-" + after);
                else
                {
                    // A whole SWOS skill point is a big step and lands rarely, so
                    // the report shows how far along the carry is. Without it the
                    // manager sees "-" week after week and concludes that
                    // training does nothing.
                    //
                    // But only where there is something to report: a player at
                    // his ceiling cannot improve, and printing "FINISHING 7 2%"
                    // against him promises progress that will never arrive.
                    int pct = Math.Clamp((int)Math.Round(carry[i] * 100.0), 0, 99);
                    bool capped = skills[i] + 1.0 > Math.Max(p.Potential, 1.0) || skills[i] >= 7.0;
                    if (!capped && pct >= 1)
                        row.Gains.Add(SkillNames[i] + " " + before + " " + pct + "%");
                }
            }
            WriteSkills(p, skills);
        }

        // ---- breaking the ceiling --------------------------------------
        // FIFA 18: a player who outperforms his potential has it raised. Rare on
        // purpose — it is the best line the report can print.
        if (grade == 3 && p.Age <= 26 && rng.NextInt(1000) < PotentialBreakPerMille
            && p.Potential < 7.0)
        {
            p.Potential = Math.Min(7.0, p.Potential + PotentialBreakStep);
            row.PotentialUp = true;
            breakthroughs++;
            Chronicle.Add(career, "chron.breakthrough",
                "%a IS TRAINING ABOVE HIMSELF", "training", 2,
                a: row.Name, round: s.CurrentRound);
        }

        // ---- what it costs ---------------------------------------------
        int fitBefore = Math.Clamp(100 - p.FatigueCarry, 0, 100);
        int fatigue = IntensityFatigue[intensity];
        // Stamina is exactly what a hard week is about: a stamina-7 professional
        // shrugs an intense session off, a stamina-1 one does not.
        if (fatigue > 0) fatigue = Math.Max(1, fatigue - Math.Clamp(p.Stamina, 0, 7) / 2);
        p.FatigueCarry = Math.Clamp(p.FatigueCarry + fatigue, 0, 100);
        row.FitnessDelta = Math.Clamp(100 - p.FatigueCarry, 0, 100) - fitBefore;

        int sharp = IntensitySharpness[intensity] + (grade - 1);
        p.Sharpness = Math.Clamp(p.Sharpness + sharp, 0, 100);
        row.SharpnessDelta = sharp;
        p.TrainingSessions++;

        // ---- the risk ---------------------------------------------------
        int risk = IntensityInjuryPerMille[intensity];
        if (condition < 40) risk *= 2;                        // working a tired player
        if (p.Age >= 31) risk += risk / 2;                    // and an old one
        if (p.InjurySeverity == 1) risk *= 2;                 // already carrying a knock
        risk -= Math.Clamp(p.Stamina, 0, 7);
        if (risk > 0 && rng.NextInt(1000) < risk)
        {
            // Training injuries are the mild end of the ladder: 1..3, never the
            // career-ending 7 that only a match can inflict.
            int sev = 1 + rng.NextInt(3);
            if (sev > p.InjurySeverity) p.InjurySeverity = sev;
            row.Injury = p.InjurySeverity;
            Chronicle.Add(career, "chron.train_injury",
                "%a PICKED UP AN INJURY IN TRAINING", "injury", 1,
                a: row.Name, round: s.CurrentRound);
        }
        return row;
    }

    /// <summary>
    /// The rest of the squad's light session: a token amount of development in
    /// the skills their position cares about, and a point of sharpness.
    /// </summary>
    private static void ApplyTeamSession(CareerPlayer p)
    {
        p.Sharpness = Math.Clamp(p.Sharpness + 1, 0, 100);
        if (IsKeeper(p)) return;
        double[] weights = GrowthModel.PositionWeights(p.Position);
        double[] skills = SkillsOf(p);
        double[] carry = EnsureCarry(p);
        for (int i = 0; i < 7; i++)
        {
            double room = Math.Clamp(p.Potential - skills[i], 0.0, 7.0);
            if (room <= 0.0) continue;
            carry[i] += TeamSessionGain * weights[i] * room;
            while (carry[i] >= 1.0 && skills[i] < 7.0)
            {
                if (skills[i] + 1.0 > Math.Max(p.Potential, 1.0)) break;
                skills[i] += 1.0;
                carry[i] -= 1.0;
            }
        }
        WriteSkills(p, skills);
    }

    // ------------------------------------------------------------------
    // Match-time effect of sharpness
    // ------------------------------------------------------------------

    /// <summary>
    /// The quantized skill nudge sharpness is worth, on the same scale as
    /// <see cref="FormModel.FormSkillDelta"/>. Deliberately small: training is a
    /// development lever, not a cheat code, and the two together can move a
    /// player by at most one level in each direction.
    /// </summary>
    public static int SharpnessSkillDelta(CareerPlayer p)
    {
        int sharp = Math.Clamp(p.Sharpness, 0, 100);
        return sharp >= 80 ? 1 : sharp <= 25 ? -1 : 0;
    }

    /// <summary>
    /// Between seasons everybody comes back part-way to average: pre-season
    /// exists, and a player should not start a new campaign at 100 because he
    /// finished the last one there.
    /// </summary>
    public static void StartNewSeason(CareerWorld? world)
    {
        if (world?.Clubs is null) return;
        foreach (var kv in world.Clubs)
        {
            var squad = kv.Value?.Squad;
            if (squad is null) continue;
            foreach (var p in squad)
            {
                if (p is null) continue;
                p.Sharpness = 50 + (Math.Clamp(p.Sharpness, 0, 100) - 50) / 3;
                FatigueModel.PreSeason(p);
            }
        }
        if (world.FreeAgents is not null)
            foreach (var p in world.FreeAgents)
            {
                if (p is null) continue;
                p.Sharpness = 50 + (Math.Clamp(p.Sharpness, 0, 100) - 50) / 3;
                FatigueModel.PreSeason(p);
            }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    public static CareerClub? ClubOf(CareerState? career)
    {
        var world = career?.World;
        if (career is null || world?.Clubs is null || career.ClubGlobalId == 0) return null;
        return world.Clubs.TryGetValue(career.ClubGlobalId, out var club) ? club : null;
    }

    /// <summary>The best coach at this club for this drill, 0 if nobody helps.</summary>
    public static int BestCoachQuality(CareerClub? club, TrainingDrill drill)
    {
        int best = 0;
        if (club?.Coaches is null) return 0;
        foreach (var coach in club.Coaches)
        {
            if (coach is null) continue;
            string spec = coach.Specialty?.Trim().ToUpperInvariant() ?? "";
            int q = Math.Clamp(coach.Quality, 0, 7);
            int effective =
                spec == drill.Coach && drill.Coach.Length > 0 ? q :
                spec == "GENERAL" ? q * 3 / 4 :
                spec == "YOUTH" ? q / 2 :
                q / 3;
            if (effective > best) best = effective;
        }
        return best;
    }

    public static bool IsKeeper(CareerPlayer p)
        => string.Equals(p.Position, "G", StringComparison.OrdinalIgnoreCase);

    private static double AgeScore(int age) => age switch
    {
        <= 18 => 14.0,
        <= 21 => 11.0,
        <= 24 => 7.0,
        <= 27 => 2.0,
        <= 30 => -3.0,
        <= 33 => -9.0,
        _ => -15.0,
    };

    private static string NameOfSkill(int i) => i < 0 ? "KEEPING" : SkillNames[i];

    private static double[] SkillsOf(CareerPlayer p) =>
    [
        Math.Clamp(p.Passing, 0.0, 7.0), Math.Clamp(p.Shooting, 0.0, 7.0),
        Math.Clamp(p.Heading, 0.0, 7.0), Math.Clamp(p.Tackling, 0.0, 7.0),
        Math.Clamp(p.Control, 0.0, 7.0), Math.Clamp(p.Speed, 0.0, 7.0),
        Math.Clamp(p.Finishing, 0.0, 7.0),
    ];

    private static void WriteSkills(CareerPlayer p, double[] s)
    {
        p.Passing = s[0]; p.Shooting = s[1]; p.Heading = s[2]; p.Tackling = s[3];
        p.Control = s[4]; p.Speed = s[5]; p.Finishing = s[6];
    }

    private static double[] EnsureCarry(CareerPlayer p)
    {
        if (p.GrowthCarry is { Length: 7 }) return p.GrowthCarry;
        double[] carry = new double[7];
        if (p.GrowthCarry is not null)
            Array.Copy(p.GrowthCarry, carry, Math.Min(p.GrowthCarry.Length, 7));
        p.GrowthCarry = carry;
        return carry;
    }
}
