using System;
using System.Collections.Generic;
using System.Linq;
using OpenSwos.Assets;

namespace OpenSwos.Competition.Career;

// Builds a match-ready TeamRecord from the LIVE CareerWorld squad so that
// career fixtures play with exactly the players the manager currently owns
// (bought, sold, injured, developed) rather than the read-only TEAM.* master
// roster. Identity/appearance fields are cloned from the master record; the
// 16-slot lineup and player skills come from the CareerClub squad.
//
// Slot layout mirrors SWOS' in-game order (TeamDataLoader.WritePlayerInfos):
//   slot  0        = starting goalkeeper (best keeper by ValueCode)
//   slots 1..10    = field lineup, ordered by position group then ability
//   slots 11..15   = bench (reserve keeper first, then best remaining)
// LineupOrder is the identity map because Players is already emitted in slot
// order.
public static class CareerMatchTeam
{
    public static TeamRecord Build(TeamRecord baseRecord, CareerClub club)
    {
        baseRecord ??= new TeamRecord();
        var ordered = BuildOrder(club, baseRecord);

        var players = new List<PlayerRecord>(ordered.Count);
        foreach (CareerPlayer p in ordered) players.Add(ToPlayerRecord(p));

        var lineupOrder = new byte[16];
        for (int i = 0; i < 16; i++) lineupOrder[i] = (byte)i;

        return new TeamRecord
        {
            Nation = baseRecord.Nation,
            GlobalId = baseRecord.GlobalId,
            Name = baseRecord.Name,
            Coach = baseRecord.Coach,
            Tactics = baseRecord.Tactics,
            Division = baseRecord.Division,
            HomeKit = baseRecord.HomeKit,
            AwayKit = baseRecord.AwayKit,
            GoalkeeperKit = baseRecord.GoalkeeperKit,
            LineupOrder = lineupOrder,
            Players = players,
        };
    }

    // Returns the live squad ordered into the 16 in-game slots (0 keeper, 1..10
    // XI, 11..15 bench). Priority:
    //   1. the club's PreferredLineup (user's custom order) when it validates;
    //   2. otherwise the club's ORIGINAL TeamRecord lineup, projected from
    //      baseRecord.LineupOrder onto the live squad (default = the real XI);
    //   3. otherwise the automatic ability/position ordering (no base record,
    //      or the original keeper is gone with no replacement).
    // Exposed so the lineup-editor UI can display and swap the exact slots the
    // match will use.
    public static List<CareerPlayer> BuildOrder(CareerClub? club, TeamRecord? baseRecord = null)
    {
        // Availability: severity >= 2 is unavailable and excluded like the
        // original's init (position=-1, substituted=1, swos.asm:36103-36107).
        // Severity 0/1 stay selectable (1 = carrying a knock). Guard: if the fit
        // pool cannot field 11, re-admit the least-injured sidelined players so we
        // never field fewer than 11 when bodies exist (the original fields the
        // fittest available rather than an incomplete XI).
        var nonRetired = new List<CareerPlayer>();
        if (club?.Squad is not null)
            foreach (CareerPlayer? p in club.Squad)
                if (p is not null && !p.Retired) nonRetired.Add(p);

        var squad = new List<CareerPlayer>();
        var sidelined = new List<CareerPlayer>();
        foreach (CareerPlayer p in nonRetired)
            (p.InjurySeverity >= 2 ? sidelined : squad).Add(p);
        if (squad.Count < 11 && sidelined.Count > 0)
        {
            sidelined.Sort((a, b) => a.InjurySeverity.CompareTo(b.InjurySeverity));
            foreach (CareerPlayer p in sidelined)
            {
                if (squad.Count >= 11) break;
                squad.Add(p);
            }
        }

        return BuildFromPreferred(squad, club?.PreferredLineup)
            ?? BuildFromBaseLineup(squad, baseRecord)
            ?? BuildAuto(squad);
    }

    // Places the user's PreferredLineup ids in slot order, then appends any
    // squad players not listed by the auto rules. Returns null (caller falls
    // back to full auto) when the preferred list is empty, references an id not
    // in the squad, repeats an id, or would put an outfielder in goal (slot 0).
    private static List<CareerPlayer>? BuildFromPreferred(List<CareerPlayer> squad, List<int>? preferred)
    {
        if (preferred is null || preferred.Count == 0) return null;
        var byId = new Dictionary<int, CareerPlayer>(squad.Count);
        foreach (CareerPlayer p in squad) byId[p.Id] = p;

        var ordered = new List<CareerPlayer>(16);
        var used = new HashSet<int>();
        foreach (int id in preferred)
        {
            // Missing id = an injured (now-unavailable) or sold player: skip it and
            // compact rather than discarding the whole custom lineup, so a stale
            // PreferredLineup degrades gracefully (the auto rest-fill below still
            // completes the XI + bench). Genuine duplicates remain a hard fall-back.
            if (!byId.TryGetValue(id, out CareerPlayer? p)) continue;      // injured/sold -> skip
            if (!used.Add(id)) return null;                                // duplicate
            ordered.Add(p);
        }
        if (ordered.Count == 0 || !IsKeeper(ordered[0])) return null;       // slot 0 keeper-only

        // Anything not explicitly placed is appended by the auto priority:
        // remaining outfield (by position group then ability) first, reserve
        // keepers last, so the bench fills sensibly.
        var rest = squad.Where(p => !used.Contains(p.Id))
            .OrderBy(p => IsKeeper(p) ? 1 : 0)
            .ThenBy(p => PositionGroupOrder(p.Position))
            .ThenByDescending(p => p.EffectiveOverall());
        foreach (CareerPlayer p in rest)
        {
            if (ordered.Count >= 16) break;
            ordered.Add(p);
        }
        if (ordered.Count > 16) ordered = ordered.GetRange(0, 16);
        return ordered;
    }

    // Projects the club's ORIGINAL TeamRecord lineup onto the live squad, so an
    // untouched career club defaults to its real XI (in tactics order) rather
    // than a position/ability resort. baseRecord.LineupOrder maps in-game slot
    // -> roster index; each roster PlayerRecord is matched to the CareerPlayer
    // seeded from it (CareerWorldBuilder seeds squads in roster order) by
    // ShirtNumber+Name, falling back to Name. Sold/missing base players are
    // skipped (compacting the slots); career-added players (transfers, regens)
    // not in the base roster are appended by the auto priority. Returns null
    // when there is no usable base lineup (caller falls back to full auto).
    private static List<CareerPlayer>? BuildFromBaseLineup(List<CareerPlayer> squad, TeamRecord? baseRecord)
    {
        var order = baseRecord?.LineupOrder;
        var roster = baseRecord?.Players;
        if (order is null || roster is null || order.Length == 0 || roster.Count == 0) return null;

        var used = new HashSet<int>();               // CareerPlayer ids already placed
        var ordered = new List<CareerPlayer>(16);
        foreach (byte rosterIndex in order)
        {
            if (rosterIndex >= roster.Count) continue;
            PlayerRecord pr = roster[rosterIndex];
            CareerPlayer? match = MatchRosterPlayer(squad, pr, used);
            if (match is null) continue;             // sold/missing base player -> skip (compact)
            used.Add(match.Id);
            ordered.Add(match);
            if (ordered.Count >= 16) break;
        }
        if (ordered.Count == 0) return null;         // no base player survived -> full auto

        // Keeper enforcement: slot 0 must be a keeper. If the original slot-0
        // player is gone (or somehow not a keeper), promote the best keeper.
        if (!IsKeeper(ordered[0]))
        {
            int bestKeeper = -1;
            for (int i = 0; i < ordered.Count; i++)
                if (IsKeeper(ordered[i]) &&
                    (bestKeeper < 0 || ordered[i].ValueCode > ordered[bestKeeper].ValueCode))
                    bestKeeper = i;
            if (bestKeeper > 0)
            {
                CareerPlayer k = ordered[bestKeeper];
                ordered.RemoveAt(bestKeeper);
                ordered.Insert(0, k);
            }
            else if (bestKeeper < 0)
            {
                // No keeper survived in the projected list — pull the best keeper
                // from the wider squad into goal so slot 0 is always a keeper.
                CareerPlayer? squadKeeper = squad.Where(p => IsKeeper(p) && !used.Contains(p.Id))
                    .OrderByDescending(p => p.ValueCode)
                    .ThenByDescending(p => p.EffectiveOverall())
                    .FirstOrDefault();
                if (squadKeeper is not null)
                {
                    used.Add(squadKeeper.Id);
                    ordered.Insert(0, squadKeeper);
                    if (ordered.Count > 16) ordered.RemoveAt(ordered.Count - 1);
                }
            }
        }

        // Append career-added players not in the base lineup (bought/regens) by
        // the auto priority: outfield (position group then ability) first,
        // reserve keepers last, so the bench fills sensibly.
        var rest = squad.Where(p => !used.Contains(p.Id))
            .OrderBy(p => IsKeeper(p) ? 1 : 0)
            .ThenBy(p => PositionGroupOrder(p.Position))
            .ThenByDescending(p => p.EffectiveOverall());
        foreach (CareerPlayer p in rest)
        {
            if (ordered.Count >= 16) break;
            ordered.Add(p);
        }
        if (ordered.Count > 16) ordered = ordered.GetRange(0, 16);
        return ordered;
    }

    // Finds the live CareerPlayer seeded from a base-roster PlayerRecord, matching
    // on ShirtNumber+Name first (unique for an untouched club) then Name alone,
    // skipping ids already placed.
    private static CareerPlayer? MatchRosterPlayer(List<CareerPlayer> squad, PlayerRecord pr, HashSet<int> used)
    {
        foreach (CareerPlayer p in squad)
            if (!used.Contains(p.Id) && p.ShirtNumber == pr.ShirtNumber && NameEquals(p.Name, pr.Name))
                return p;
        foreach (CareerPlayer p in squad)
            if (!used.Contains(p.Id) && NameEquals(p.Name, pr.Name))
                return p;
        return null;
    }

    private static bool NameEquals(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    // Automatic ordering (the original behaviour): best keeper in goal, XI by
    // position group tie-broken by ability, bench = reserve keeper(s) then the
    // best remaining outfielders.
    private static List<CareerPlayer> BuildAuto(List<CareerPlayer> squad)
    {
        // Keepers by ability (ValueCode is a keeper's ability source).
        var keepers = squad.Where(IsKeeper)
            .OrderByDescending(p => p.ValueCode)
            .ThenByDescending(p => p.EffectiveOverall())
            .ToList();
        var outfield = squad.Where(p => !IsKeeper(p)).ToList();

        var ordered = new List<CareerPlayer>(16);

        // slot 0 — best keeper (must be a keeper when one exists).
        CareerPlayer? starterKeeper = keepers.Count > 0 ? keepers[0] : null;
        if (starterKeeper is not null) ordered.Add(starterKeeper);

        // slots 1..10 — field lineup by position group, tie-broken by ability.
        var starters = outfield
            .OrderBy(p => PositionGroupOrder(p.Position))
            .ThenByDescending(p => p.EffectiveOverall())
            .Take(10)
            .ToList();
        ordered.AddRange(starters);
        var startersSet = new HashSet<CareerPlayer>(starters);

        // slots 11..15 — bench: reserve keeper(s) first, then best remaining
        // outfielders by ability. Anything past 16 is the weakest and dropped.
        var bench = new List<CareerPlayer>();
        for (int i = 1; i < keepers.Count; i++) bench.Add(keepers[i]);
        bench.AddRange(outfield.Where(p => !startersSet.Contains(p))
            .OrderByDescending(p => p.EffectiveOverall()));
        foreach (CareerPlayer p in bench)
        {
            if (ordered.Count >= 16) break;
            ordered.Add(p);
        }
        return ordered;
    }

    public static bool IsKeeper(CareerPlayer p)
        => string.Equals(p.Position, "G", StringComparison.OrdinalIgnoreCase);

    // RB,LB,D,RW,LW,M,A — enum order (TeamDataLoader.cs:64-71).
    private static int PositionGroupOrder(string pos) => pos switch
    {
        "RB" => 0,
        "LB" => 1,
        "D" => 2,
        "RW" => 3,
        "LW" => 4,
        "M" => 5,
        "A" => 6,
        _ => 7,
    };

    private static PlayerRecord ToPlayerRecord(CareerPlayer p)
    {
        int[] s = p.QuantizedSkills();  // P,Sh,He,Ta,Co,Sp,Fi
        return new PlayerRecord
        {
            Name = ToAsciiUpper(p.Name),
            Position = p.Position ?? "",
            ShirtNumber = p.ShirtNumber,
            Face = p.Face,
            Nationality = p.Nationality,
            Passing = s[0],
            Shooting = s[1],
            Heading = s[2],
            Tackling = s[3],
            Control = s[4],
            Speed = s[5],
            Finishing = s[6],
            ValueCode = (int)Math.Clamp(Math.Round(p.ValueCode), 0, 47),
            Stamina = System.Math.Clamp(p.Stamina, 0, 7),
            FatigueCarry = System.Math.Clamp(p.FatigueCarry, 0, 100),
            InjurySeverity = System.Math.Clamp(p.InjurySeverity, 0, 7),
        };
    }

    private static string ToAsciiUpper(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.ToUpperInvariant())
            sb.Append(c <= 0x7F ? c : '?');
        return sb.ToString();
    }
}
