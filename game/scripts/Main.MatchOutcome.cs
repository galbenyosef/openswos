using Godot;
using OpenSwos.Sim;

namespace OpenSwos;

// ============================================================================
// What a PLAYED fixture hands back to the career layer.
//
// Split out of Main.LiveMatch.cs on 2026-08-26 because it is SHARED: the
// locally played desktop match and the streamed browser match must derive the
// score, the energy spent, the injuries and the scorers identically, or
// watching a fixture would have different career consequences from playing it.
// The live-streaming driver itself is part of the optional web module and is
// not in this repository; this file is not.
// ============================================================================
public partial class Main : Node2D
{
    /// <summary>Everything a played fixture hands back to the career layer.</summary>
    /// <param name="scorers">
    /// Who actually scored (career depth plan feature #5). One entry per scorer
    /// slot on the result panel: <c>playerTeam</c> is the human's club (the sim
    /// always seats it on the TOP slots), <c>slot</c> is that club's in-game
    /// PlayerInfo index so the caller can resolve it to a CareerPlayer through
    /// the same CareerMatchTeam.BuildOrder the match launched with, and
    /// <c>ownGoal</c> marks the original's shirt+1000 entries — the goal counts
    /// for that club but belongs to nobody's tally.
    /// </param>
    internal readonly record struct MatchOutcome(
        int player, int opponent, int distance,
        System.Collections.Generic.List<(int slot, int severity)>? injuries,
        System.Collections.Generic.List<(bool playerTeam, int slot, int goals, bool ownGoal)>? scorers);

    /// <summary>
    /// The consequences of a played fixture, read straight out of live sim
    /// memory. Extracted from the FullTime branch of _PhysicsProcess so the
    /// streamed web match and the locally played match derive them identically
    /// — a spectated fixture must age, tire and injure the squad the same way.
    /// </summary>
    internal MatchOutcome CaptureMatchOutcome()
    {
        // The human always sims on the top/home slots (0..10). Average their
        // consumed energy and map to the 0..10 distance scale MatchEffects uses
        // (its surrogate is a flat 5).
        int homeConsumed = 0;
        for (int slot = 0; slot <= 10; slot++)
            homeConsumed += OpenSwos.Sim.Port.PlayerEnergy.Max
                - OpenSwos.Sim.Port.PlayerEnergy.ReadEnergy(slot);
        homeConsumed /= 11;
        int distance = homeConsumed * 10 / OpenSwos.Sim.Port.PlayerEnergy.Max;

        // Post-match injuries (UpdatePlayerInjuries, swos.asm:35651-35701). The
        // human always plays the home/top physical store (team1InGameTeamPlayers),
        // which never moves (half-time swaps only pointer fields). Scan all 16
        // slots (a starter subbed off keeps his record in the 11..15 range) and
        // record the severity of every slot that finished injured and was NOT
        // substituted off — the original skips subbed-off and CPU players.
        var injuries = new System.Collections.Generic.List<(int slot, int severity)>();
        int injBase = OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers;
        for (int islot = 0; islot < 16; islot++)
        {
            int rec = injBase + islot * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
            if (OpenSwos.SwosVm.Memory.ReadByte(rec + OpenSwos.Sim.Port.TeamDataLoader.OffSubstituted) != 0)
                continue;
            if (OpenSwos.SwosVm.Memory.ReadByte(rec + OpenSwos.Sim.Port.TeamDataLoader.OffIsInjured) == 0)
                continue;
            int severity = (OpenSwos.SwosVm.Memory.ReadByte(
                rec + OpenSwos.Sim.Port.TeamDataLoader.OffInjuriesBits) >> 5) & 7;
            if (severity <= 0) continue;
            int index = OpenSwos.SwosVm.Memory.ReadByte(
                rec + OpenSwos.Sim.Port.TeamDataLoader.OffIndex);
            injuries.Add((index, severity));
        }

        // Feature #5: the real scorers, straight off the result panel's own
        // structured store (Result.cs ScorerInfo). team1 == topTeamInGame ==
        // the human's club, team2 == the opponent (RegisterScorer's team1=top /
        // team2=bottom convention, result.cpp:165-172).
        int topRoster    = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.topTeamInGame);
        int bottomRoster = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.bottomTeamInGame);
        var scorers = new System.Collections.Generic.List<(bool, int, int, bool)>();
        CollectScorers(OpenSwos.Sim.Port.Result.GetTeam1Scorers(), true,  bottomRoster, topRoster, scorers);
        CollectScorers(OpenSwos.Sim.Port.Result.GetTeam2Scorers(), false, topRoster, bottomRoster, scorers);

        return new MatchOutcome(_match.ScorePlayer, _match.ScoreOpponent, distance,
                                injuries.Count > 0 ? injuries : null,
                                scorers.Count > 0 ? scorers : null);
    }

    /// <summary>
    /// Turns one team's result-panel scorer slots into (slot, goals) pairs. The
    /// slots hold SHIRT numbers only, so resolve each to its PlayerInfo index the
    /// way the result renderer resolves the surname (ScorerSurname in Main.cs):
    /// scan the 16 in-game records for the shirt. Own-goal entries carry
    /// shirt+1000 and belong to the OTHER roster (result.cpp:181) — they need no
    /// slot at all, since the goal goes to the club's anonymous OWN GOALS row.
    /// </summary>
    private static void CollectScorers(
        OpenSwos.Sim.Port.Result.ScorerInfo[] slots, bool playerTeam,
        int ownGoalRoster, int roster,
        System.Collections.Generic.List<(bool, int, int, bool)> into)
    {
        if (slots is null) return;
        foreach (var info in slots)
        {
            if (info.ShirtNum == 0) break;              // slots fill contiguously
            if (info.NumGoals <= 0) continue;
            bool ownGoal = info.ShirtNum >= 1000;
            if (ownGoal)
            {
                into.Add((playerTeam, -1, info.NumGoals, true));
                continue;
            }
            int slot = SlotOfShirt(roster, info.ShirtNum);
            into.Add((playerTeam, slot, info.NumGoals, false));
        }
    }

    /// <summary>PlayerInfo.index of the shirt-holder in an in-game roster, or -1.</summary>
    private static int SlotOfShirt(int rosterBase, int shirtNum)
    {
        if (rosterBase == 0) return -1;
        for (int i = 0; i < 16; i++)                    // TeamGame: 16 players
        {
            int pi = rosterBase + i * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
            if (OpenSwos.SwosVm.Memory.ReadByte(pi + OpenSwos.Sim.Port.TeamDataLoader.OffShirtNumber) != shirtNum)
                continue;
            return OpenSwos.SwosVm.Memory.ReadByte(pi + OpenSwos.Sim.Port.TeamDataLoader.OffIndex);
        }
        return -1;
    }
}
