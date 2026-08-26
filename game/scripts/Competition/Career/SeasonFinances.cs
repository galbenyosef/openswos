namespace OpenSwos.Competition.Career;

using System;
using System.Collections.Generic;

// ============================================================================
// Season finance statement — career depth plan feature #1
// (docs/decisions/03-career-depth-plan.md).
//
// FIDELITY NOTE. The LINE ITEMS and their wording are taken verbatim from the
// original game's text pool, recovered from
// external/original-amiga-swos/original-amiga-swos.asm:
//
//     'BALANCE AT START OF SEASON:'      'BALANCE AT END OF LAST SEASON:'
//     'GATE RECEIPTS:'
//     'COMPETITION BONUSES + TV RIGHTS:'
//     'SPONSORSHIP FOR THE CLUB FOR THIS' / 'SEASON FROM SENSISOFT PLC:'
//     'ADDITIONAL INVESTMENT FROM THE CHAIRMAN:'
//     'ADDITIONAL INVESTMENT FROM THE NEW CHAIRMAN:'
//     'PLAYER WAGES BILL:'
//     'TOTAL PROFIT:'                    'TOTAL LOSS:'
//     'NEW BALANCE:'                     'OLD BALANCE:'
//
// The COEFFICIENTS below are OURS, not the original's. The disassembly we hold
// carries the strings as raw data with no symbol names, so the routine that
// computed them is not identifiable, and the original's money scale is not our
// money scale anyway (our values come from PriceTable's inflation of the SWOS
// 1997 price ladder). So: the statement a player reads is 1:1, the arithmetic
// behind it is our own and is documented here rather than pretended to be
// authentic. If the original formulas are ever recovered, only this file
// changes.
//
// Everything here is deterministic integer arithmetic over data the engine
// already holds. No RNG at all — the same season always produces the same
// statement, which is what the save format and the future network mode need.
// ============================================================================

/// <summary>One club's income and expenditure for one completed season.</summary>
public sealed class SeasonAccount
{
    /// <summary>Season this statement covers (the season that just finished).</summary>
    public int Season { get; set; }

    // ---- the original's line items, in the original's order ----------------
    public long OpeningBalance { get; set; }
    public long GateReceipts { get; set; }
    public long CompetitionBonuses { get; set; }     // prize money + TV rights
    public long Sponsorship { get; set; }
    public long ChairmanInvestment { get; set; }
    /// <summary>Fees received for players sold this season ('PLAYER SALES:').</summary>
    public long PlayerSales { get; set; }
    /// <summary>Fees paid for players bought this season ('PLAYER PURCHASES:').</summary>
    public long PlayerPurchases { get; set; }
    /// <summary>Selects 'FROM THE NEW CHAIRMAN' over 'FROM THE CHAIRMAN'.</summary>
    public bool NewChairman { get; set; }
    public long WageBill { get; set; }
    public long ClosingBalance { get; set; }

    // ---- context, so a client can caption the statement --------------------
    public int LeaguePosition { get; set; }          // 1-based; 0 = did not play a league
    public int LeagueTeams { get; set; }
    public string CupResult { get; set; } = "";      // "WINNER" / "RUNNER UP" / "OUT IN SF" / ""
    public int HomeGames { get; set; }
    public int Attendance { get; set; }              // average home attendance

    /// <summary>
    /// Diagnostic: money the statement cannot account for. MUST be zero — a
    /// non-zero value means some code path moved the club's budget without
    /// telling the season ledger (see CareerClub.Season* fields). Asserted by
    /// --competition-test rather than hidden.
    /// </summary>
    public long Unreconciled { get; set; }

    /// <summary>Positive = profit, negative = loss. Derived, never stored.</summary>
    public long Profit => ClosingBalance - OpeningBalance;

    public long TotalIncome
        => GateReceipts + CompetitionBonuses + Sponsorship + ChairmanInvestment + PlayerSales;
    public long TotalExpenditure => PlayerPurchases + WageBill;
}

/// <summary>What a club achieved in the season being accounted for.</summary>
public struct SeasonResultInput
{
    public int LeaguePosition;      // 1-based; 0 when the club played no league
    public int LeagueTeams;
    public int HomeLeagueGames;
    public int HomeCupGames;
    public int CupRoundsWon;
    public bool CupWinner;
    public bool CupRunnerUp;
    public bool LeagueChampion;
    public string CupResult;
    /// <summary>Division the club played in, 0 = top flight.</summary>
    public int Division;
    /// <summary>Set for the club that came up this season (chairman rewards it).</summary>
    public bool Promoted;
}

public static class SeasonFinances
{
    // ---- gate receipts ----------------------------------------------------
    /// <summary>Baseline crowd before club size and league form are applied.</summary>
    private const long AttendanceBase = 4_000L;
    /// <summary>One extra seat sold per this much aggregate squad value.</summary>
    private const long ValuePerExtraSeat = 20_000L;
    private const long AttendanceFloor = 1_200L;
    private const long AttendanceCeiling = 60_000L;
    /// <summary>Money taken per head. Flat — SWOS has no ticket pricing.</summary>
    private const long TicketPrice = 20L;

    // ---- competition bonuses + TV rights -----------------------------------
    /// <summary>Paid per place ABOVE the bottom of the table, plus one.</summary>
    private const long LeaguePrizePerPlace = 150_000L;
    /// <summary>Top-flight TV money; halved for each division below.</summary>
    private const long TvRightsTopDivision = 1_200_000L;
    private const long CupRoundBonus = 300_000L;
    private const long CupWinnerBonus = 1_500_000L;
    private const long CupRunnerUpBonus = 600_000L;

    // ---- sponsorship -------------------------------------------------------
    private const long SponsorBase = 500_000L;
    private const long ValuePerSponsorUnit = 80L;

    // ---- chairman investment ----------------------------------------------
    // Each component is a SHARE of what the club earned this season, capped at
    // a flat ceiling. Proportionate matters: a flat two million is noise to a
    // giant and a fifteen-fold windfall to a village side, which would wreck
    // the transfer economy at the bottom of the pyramid.
    private const long InvestmentNewChairmanPercent = 50L;
    private const long InvestmentNewChairmanCap = 1_500_000L;
    private const long InvestmentPromotionPercent = 40L;
    private const long InvestmentPromotionCap = 2_000_000L;
    private const long InvestmentTrophyPercent = 25L;
    private const long InvestmentTrophyCap = 1_000_000L;

    private static long Share(long operatingIncome, long percent, long cap)
        => Math.Min(cap, Math.Max(0L, operatingIncome) * percent / 100L);

    /// <summary>
    /// Builds one club's statement. <paramref name="openingBalance"/> is the
    /// club's budget before the rollover; the caller writes
    /// <see cref="SeasonAccount.ClosingBalance"/> back onto the club.
    /// </summary>
    public static SeasonAccount Compute(
        CareerClub club, SeasonResultInput result, int season, long openingBalance, bool newChairman)
    {
        // Transfer activity is read off the club's own season ledger.
        if (club is null) throw new ArgumentNullException(nameof(club));

        long clubValue = Finance.ClubValue(club);

        // --- GATE RECEIPTS ---------------------------------------------------
        // Crowd = a floor every club draws, plus one seat per slice of squad
        // value, plus up to +30% for finishing high. A club with no league
        // (cup-only entrant) gets the flat crowd.
        long attendance = AttendanceBase + clubValue / ValuePerExtraSeat;
        if (result.LeagueTeams > 1 && result.LeaguePosition >= 1)
        {
            // 1st place -> +30%, last place -> +0%.
            long placesBelow = result.LeagueTeams - result.LeaguePosition;
            attendance += attendance * 30L * placesBelow / (100L * (result.LeagueTeams - 1));
        }
        attendance = Math.Clamp(attendance, AttendanceFloor, AttendanceCeiling);

        int homeGames = Math.Max(0, result.HomeLeagueGames) + Math.Max(0, result.HomeCupGames);
        long gate = attendance * TicketPrice * homeGames;

        // --- COMPETITION BONUSES + TV RIGHTS ---------------------------------
        long bonuses = 0L;
        if (result.LeagueTeams > 0 && result.LeaguePosition >= 1)
            bonuses += LeaguePrizePerPlace * (result.LeagueTeams - result.LeaguePosition + 1);

        int division = Math.Clamp(result.Division, 0, 6);
        bonuses += TvRightsTopDivision >> division;

        bonuses += CupRoundBonus * Math.Max(0, result.CupRoundsWon);
        if (result.CupWinner) bonuses += CupWinnerBonus;
        else if (result.CupRunnerUp) bonuses += CupRunnerUpBonus;

        // --- SPONSORSHIP ------------------------------------------------------
        // Sponsors pay for exposure: club size, damped by how far down the
        // pyramid the club plays.
        long sponsorship = (SponsorBase + clubValue / ValuePerSponsorUnit) >> division;

        // --- ADDITIONAL INVESTMENT FROM THE CHAIRMAN --------------------------
        // A reward, never a bailout. The original's overdraft memo
        // ("CLEAR THE OVERDRAFT IMMEDIATELY OR YOU'RE SACKED") makes clear the
        // board does not cover your losses.
        long operatingIncome = gate + bonuses + sponsorship;
        long investment = 0L;
        if (newChairman)
            investment += Share(operatingIncome, InvestmentNewChairmanPercent, InvestmentNewChairmanCap);
        if (result.Promoted)
            investment += Share(operatingIncome, InvestmentPromotionPercent, InvestmentPromotionCap);
        if (result.LeagueChampion || result.CupWinner)
            investment += Share(operatingIncome, InvestmentTrophyPercent, InvestmentTrophyCap);

        // --- PLAYER WAGES BILL ------------------------------------------------
        long wages = Finance.SquadWageBill(club);

        var account = new SeasonAccount
        {
            Season = season,
            OpeningBalance = openingBalance,
            GateReceipts = gate,
            CompetitionBonuses = bonuses,
            Sponsorship = sponsorship,
            ChairmanInvestment = investment,
            NewChairman = newChairman && investment > 0L,
            WageBill = wages,
            LeaguePosition = result.LeaguePosition,
            LeagueTeams = result.LeagueTeams,
            CupResult = result.CupResult ?? "",
            HomeGames = homeGames,
            Attendance = (int)attendance,
            PlayerSales = Math.Max(0L, club.SeasonPlayerSales),
            PlayerPurchases = Math.Max(0L, club.SeasonPlayerPurchases),
        };
        account.ClosingBalance = openingBalance + account.TotalIncome - account.TotalExpenditure;
        return account;
    }

    /// <summary>
    /// The statement for a club that did not take part in the accounted
    /// competition (every other club in the world). It still pays wages and
    /// still draws a crowd, but wins no prize money — which is exactly the
    /// pressure that makes the accounted competition matter.
    /// </summary>
    public static SeasonAccount ComputeUnattached(CareerClub club, int season, long openingBalance)
        => Compute(club, new SeasonResultInput { CupResult = "" }, season, openingBalance, newChairman: false);

    /// <summary>
    /// Formats one statement in the original's line order. Used by BOTH clients
    /// so the desktop menu and the browser print the identical statement.
    ///
    /// Each row carries a stable translation KEY alongside the ORIGINAL English
    /// label. The label is the original game's exact wording and is what the
    /// browser shows; the desktop menu passes the key through Loc.Tr with the
    /// label as its fallback, so a translated build stays readable without the
    /// English ever drifting from the source text.
    ///
    /// Kind is "in" (income), "out" (expenditure) or "total" (ruled-off line).
    /// </summary>
    public static List<(string Key, string Label, long Amount, string Kind)> StatementRows(SeasonAccount a)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        var rows = new List<(string, string, long, string)>
        {
            ("fin.row.opening", "BALANCE AT START OF SEASON:", a.OpeningBalance, "total"),
            ("fin.row.gate", "GATE RECEIPTS:", a.GateReceipts, "in"),
            ("fin.row.bonuses", "COMPETITION BONUSES + TV RIGHTS:", a.CompetitionBonuses, "in"),
            ("fin.row.sponsor", "SPONSORSHIP FROM SENSISOFT PLC:", a.Sponsorship, "in"),
        };
        if (a.ChairmanInvestment != 0L)
        {
            rows.Add(a.NewChairman
                ? ("fin.row.invest_new", "ADDITIONAL INVESTMENT FROM THE NEW CHAIRMAN:", a.ChairmanInvestment, "in")
                : ("fin.row.invest", "ADDITIONAL INVESTMENT FROM THE CHAIRMAN:", a.ChairmanInvestment, "in"));
        }
        if (a.PlayerSales != 0L)
            rows.Add(("fin.row.sales", "PLAYER SALES:", a.PlayerSales, "in"));
        if (a.PlayerPurchases != 0L)
            rows.Add(("fin.row.purchases", "PLAYER PURCHASES:", a.PlayerPurchases, "out"));
        rows.Add(("fin.row.wages", "PLAYER WAGES BILL:", a.WageBill, "out"));
        rows.Add(a.Profit >= 0L
            ? ("fin.row.profit", "TOTAL PROFIT:", a.Profit, "total")
            : ("fin.row.loss", "TOTAL LOSS:", -a.Profit, "total"));
        rows.Add(("fin.row.closing", "NEW BALANCE:", a.ClosingBalance, "total"));
        return rows;
    }
}
