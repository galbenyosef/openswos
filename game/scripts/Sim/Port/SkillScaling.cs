namespace OpenSwos.Sim.Port;

using OpenSwos.Assets;
using OpenSwos.SwosVm;

// Original SWOS player-price + skill-scaling pipeline (mechanical port).
//
// At match start the original runs, per starting player (ApplyTeamTactics →
// AdjustPlayerSkills, swos.asm:37618-37877):
//
//   1. GetPlayerPrice (swos.asm:129720-129952; teams.txt:834-998) — a
//      35-quadrant simulation of how well the player's skills fit the spots
//      his TACTICS send him to. Produces a price in the 0..49 price-CODE
//      space (same space as the TEAM.* player record byte +0x20).
//   2. pricePercent = computedPrice * 100 / filePrice (swos.asm:37670-37690)
//      — how over/under-priced the file says he is — plus a random spread
//      (GetPlayerPricePercentageChange, swos.asm:37883-37982).
//   3. ScaleSkill (swos.asm:37986-38054) — each raw file skill (nibble mod 8)
//      is multiplied by clamp(pricePercent, 75..125) and by the team's
//      "average player price" percent, with probabilistic rounding of the
//      fraction; the caller clamps the result to 7 (swos.asm:37766-37769).
//
// PRICE-SPACE DECISION: every quantity here lives in the 0..49 price-CODE
// space, never in currency. GetPlayerPrice clamps its result to [0..49]
// (swos.asm:129924-129936) and compares/adds it directly against the raw
// file price byte (swos.asm:129937-129952); GetAveragePlayerPrice sums the
// raw price bytes (swos.asm:81150-81176). The K/M money table
// (teams.txt:222+) is a DISPLAY mapping only.
//
// Everything below is paraphrased from the asm (constants copied verbatim,
// license rule per CLAUDE.md). No code was copied from swos-port.
public static class SkillScaling
{
    // Master toggle (menu-wired). true = faithful original pipeline;
    // false = write raw mod-8 file skills (legacy OpenSWOS behaviour).
    public static bool Enabled = true;

    // The original's "all player teams equal" option gate. When ON (and the
    // game is not a career), a player-coach team's skills are equalized via
    // TeamAppPercent below (InitInGameTeamStructure, swos.asm:35769-35779).
    // Original menu default is OFF → TeamAppPercent == 100.
    public static bool AllPlayerTeamsEqual = false;

    // averagePlayerPrice (swos.asm:219861): the average player price CODE
    // across the pool of selected teams, computed by
    // GetAveragePlayerPriceInSelectedTeams (swos.asm:48458-48495) as
    //   (sum of per-team averages) / numTeams.
    // Main sets this once at boot from the master team list (our stand-in
    // for "selected teams" in exhibition play). 0 = unknown → TeamAppPercent
    // degrades to 100.
    public static int LeagueAvgTeamValue;

    // ------------------------------------------------------------------
    // Data tables — constants copied from swos.asm / teams.txt.
    // ------------------------------------------------------------------

    // skillValuePonders (swos.asm:247062; teams.txt:876): triangular-ish
    // growth so a ponder of 5 values a skill ~15x more than ponder 1.
    // Indexed by the 0..7 ponder nibble from PriceSkillPonders.
    private static readonly byte[] SkillValuePonders = { 0, 1, 3, 6, 10, 15, 21, 28 };

    // plPricesIncreaseTable (swos.asm:247065-247067; teams.txt:993-998):
    // when the computed price exceeds the file price by `diff` (1..49), the
    // final price is filePrice + table[diff] — a soft, saturating increase
    // that keeps the result inside 0..49.
    private static readonly byte[] PlPricesIncreaseTable =
    {
        0, 1, 2, 3, 4, 4, 5, 5, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 9,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
    };

    // priceSkillPonders (swos.asm:209363-209367; teams.txt:860-865): indexed
    // by the player's DESTINATION quadrant (0..34). Each dword packs 8
    // nibbles, high→low: 0, passing, shooting, heading, tackling,
    // ballControl, speed, finishing — how much each skill is valued when the
    // player's job places him on that quadrant (defensive quadrants value
    // tackling, attacking ones shooting/finishing, ...). Ponder values 0..5.
    private static readonly uint[] PriceSkillPonders =
    {
        0x00005020, 0x00024010, 0x00034000, 0x00024010, 0x00005020, 0x01003030, 0x01013020,
        0x00024010, 0x01013020, 0x01003030, 0x02002030, 0x02013010, 0x01013000, 0x02013010,
        0x02002030, 0x02010130, 0x02013000, 0x03011000, 0x02013000, 0x02010130, 0x02001230,
        0x03000220, 0x04100200, 0x03000220, 0x02001230, 0x03000240, 0x02210220, 0x03230312,
        0x02210220, 0x03000240, 0x03000330, 0x02200221, 0x01220124, 0x02200221, 0x03000330,
    };

    // skillsPerQuadrant (swos.asm:208896-208902; teams.txt:898-905): 7 rows
    // of 35, row = position - 1 in RB, LB, D, RW, LW, M, A order (the
    // goalkeeper never reaches GetPlayerPrice — AdjustPlayerSkills slot 0
    // short-circuits, swos.asm:37620-37628). Indexed by DESTINATION quadrant
    // (swos.asm:129863-129871); values 1..4 = how much that quadrant matters
    // for the position (defenders own-goal-side, attackers the other end).
    private static readonly byte[] SkillsPerQuadrant =
    {
        // RB (position 1)
        4, 4, 4, 3, 3, 4, 4, 4, 3, 3, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 3, 3, 2, 2, 2, 3, 2, 1, 1, 2, 3, 1, 1, 1, 2,
        // LB (position 2)
        3, 3, 4, 4, 4, 3, 3, 4, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4, 2, 2, 2, 3, 3, 2, 1, 1, 2, 3, 2, 1, 1, 1, 3,
        // D (position 3)
        3, 4, 4, 4, 3, 3, 4, 4, 4, 3, 2, 4, 4, 4, 2, 1, 3, 4, 3, 1, 1, 2, 3, 2, 1, 1, 1, 3, 1, 1, 1, 1, 4, 1, 1,
        // RW (position 4)
        3, 3, 1, 2, 2, 3, 3, 2, 2, 2, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3,
        // LW (position 5)
        2, 2, 1, 3, 3, 2, 2, 2, 3, 3, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4, 3, 3, 3, 4, 4,
        // M (position 6)
        1, 1, 2, 1, 1, 2, 3, 4, 3, 2, 3, 4, 4, 4, 3, 3, 4, 4, 4, 3, 3, 4, 4, 4, 3, 1, 2, 3, 2, 1, 1, 1, 1, 1, 1,
        // A (position 7)
        1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 2, 3, 2, 1, 2, 3, 4, 3, 2, 3, 4, 4, 4, 3, 3, 4, 4, 4, 3, 3, 4, 4, 4, 3,
    };

    // quadrantCoordinatesToBallQuadrant (swos.asm:209369-209383): maps a
    // packed tactics-coordinate byte (as stored in TeamTactics.positions) to
    // a quadrant index 0..34. High nibble 0..14 selects a row, low nibble
    // 0..15 a column. 15 x 16 = 240 entries.
    private static readonly byte[] CoordToQuadrant =
    {
        0, 0, 0, 5, 5, 10, 10, 15, 15, 20, 20, 25, 25, 30, 30, 30,
        0, 0, 0, 5, 5, 10, 10, 15, 15, 20, 20, 25, 25, 30, 30, 30,
        0, 0, 0, 5, 5, 10, 10, 15, 15, 20, 20, 25, 25, 30, 30, 30,
        1, 1, 1, 6, 6, 11, 11, 16, 16, 21, 21, 26, 26, 31, 31, 31,
        1, 1, 1, 6, 6, 11, 11, 16, 16, 21, 21, 26, 26, 31, 31, 31,
        1, 1, 1, 6, 6, 11, 11, 16, 16, 21, 21, 26, 26, 31, 31, 31,
        2, 2, 2, 7, 7, 12, 12, 17, 17, 22, 22, 27, 27, 32, 32, 32,
        2, 2, 2, 7, 7, 12, 12, 17, 17, 22, 22, 27, 27, 32, 32, 32,
        2, 2, 2, 7, 7, 12, 12, 17, 17, 22, 22, 27, 27, 32, 32, 32,
        3, 3, 3, 8, 8, 13, 13, 18, 18, 23, 23, 28, 28, 33, 33, 33,
        3, 3, 3, 8, 8, 13, 13, 18, 18, 23, 23, 28, 28, 33, 33, 33,
        3, 3, 3, 8, 8, 13, 13, 18, 18, 23, 23, 28, 28, 33, 33, 33,
        4, 4, 4, 9, 9, 14, 14, 19, 19, 24, 24, 29, 29, 34, 34, 34,
        4, 4, 4, 9, 9, 14, 14, 19, 19, 24, 24, 29, 29, 34, 34, 34,
        4, 4, 4, 9, 9, 14, 14, 19, 19, 24, 24, 29, 29, 34, 34, 34,
    };

    // quadrantDistanceFactor (swos.asm:208943-208977; teams.txt "tablica
    // poredjana po pod-matricama"): 35 x 35, [ballQuadrant * 35 + quadrant]
    // = proximity weight {32, 24, 16, 8, 2, 1, 0} — 32 on the diagonal,
    // falling off with distance. Used three ways: GetPlayerPrice weights the
    // destination quadrant by [ballQ][dest] (swos.asm:129872-129884);
    // CountNearnessFactor uses [ballQ][q] >= 8 as "q is near the ball" and
    // [dest][destOfQ] >= 4 as "that destination is near ours"
    // (swos.asm:129971-130039).
    private static readonly byte[] QuadrantDistanceFactor =
    {
        32,24,16, 8, 2,24,24,16, 8, 2,16,16,16, 8, 2, 8, 8, 8, 8, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
        24,32,24,16, 8,24,24,24,16, 8,16,16,16,16, 8, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
        16,24,32,24,16,16,24,24,24,16,16,16,16,16,16, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
         8,16,24,32,24, 8,16,24,24,24, 8,16,16,16,16, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
         2, 8,16,24,32, 2, 8,16,24,24, 2, 8,16,16,16, 2, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
        24,24,16, 8, 2,32,24,16, 8, 2,24,24,16, 8, 2,16,16,16, 8, 2, 8, 8, 8, 8, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        24,24,24,16, 8,24,32,24,16, 8,24,24,24,16, 8,16,16,16,16, 8, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        16,24,24,24,16,16,24,32,24,16,16,24,24,24,16,16,16,16,16,16, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
         8,16,24,24,24, 8,16,24,32,24, 8,16,24,24,24, 8,16,16,16,16, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
         2, 8,16,24,24, 2, 8,16,24,32, 2, 8,16,24,24, 2, 8,16,16,16, 2, 8, 8, 8, 8, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        16,16,16, 8, 2,24,24,16, 8, 2,32,24,16, 8, 2,24,24,16, 8, 2,16,16,16, 8, 2, 8, 8, 8, 8, 2, 2, 2, 2, 2, 2,
        16,16,16,16, 8,24,24,24,16, 8,24,32,24,16, 8,24,24,24,16, 8,16,16,16,16, 8, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2,
        16,16,16,16,16,16,24,24,24,16,16,24,32,24,16,16,24,24,24,16,16,16,16,16,16, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2,
         8,16,16,16,16, 8,16,24,24,24, 8,16,24,32,24, 8,16,24,24,24, 8,16,16,16,16, 8, 8, 8, 8, 8, 2, 2, 2, 2, 2,
         2, 8,16,16,16, 2, 8,16,24,24, 2, 8,16,24,32, 2, 8,16,24,24, 2, 8,16,16,16, 2, 8, 8, 8, 8, 2, 2, 2, 2, 2,
         8, 8, 8, 8, 2,16,16,16, 8, 2,24,24,16, 8, 2,32,24,16, 8, 2,24,24,16, 8, 2,16,16,16, 8, 2, 8, 8, 8, 8, 2,
         8, 8, 8, 8, 8,16,16,16,16, 8,24,24,24,16, 8,24,32,24,16, 8,24,24,24,16, 8,16,16,16,16, 8, 8, 8, 8, 8, 8,
         8, 8, 8, 8, 8,16,16,16,16,16,16,24,24,24,16,16,24,32,24,16,16,24,24,24,16,16,16,16,16,16, 8, 8, 8, 8, 8,
         8, 8, 8, 8, 8, 8,16,16,16,16, 8,16,24,24,24, 8,16,24,32,24, 8,16,24,24,24, 8,16,16,16,16, 8, 8, 8, 8, 8,
         2, 8, 8, 8, 8, 2, 8,16,16,16, 2, 8,16,24,24, 2, 8,16,24,32, 2, 8,16,24,24, 2, 8,16,16,16, 2, 8, 8, 8, 8,
         2, 2, 2, 2, 2, 8, 8, 8, 8, 2,16,16,16, 8, 2,24,24,16, 8, 2,32,24,16, 8, 2,24,24,16, 8, 2,16,16,16, 8, 2,
         2, 2, 2, 2, 2, 8, 8, 8, 8, 8,16,16,16,16, 8,24,24,24,16, 8,24,32,24,16, 8,24,24,24,16, 8,16,16,16,16, 8,
         2, 2, 2, 2, 2, 8, 8, 8, 8, 8,16,16,16,16,16,16,24,24,24,16,16,24,32,24,16,16,24,24,24,16,16,16,16,16,16,
         2, 2, 2, 2, 2, 8, 8, 8, 8, 8, 8,16,16,16,16, 8,16,24,24,24, 8,16,24,32,24, 8,16,24,24,24, 8,16,16,16,16,
         2, 2, 2, 2, 2, 2, 8, 8, 8, 8, 2, 8,16,16,16, 2, 8,16,24,24, 2, 8,16,24,32, 2, 8,16,24,24, 2, 8,16,16,16,
         1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 2,16,16,16, 8, 2,24,24,16, 8, 2,32,24,16, 8, 2,24,24,16, 8, 2,
         1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 8,16,16,16,16, 8,24,24,24,16, 8,24,32,24,16, 8,24,24,24,16, 8,
         1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 8,16,16,16,16,16,16,24,24,24,16,16,24,32,24,16,16,24,24,24,16,
         1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 8, 8,16,16,16,16, 8,16,24,24,24, 8,16,24,32,24, 8,16,24,24,24,
         1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 8, 8, 8, 8, 2, 8,16,16,16, 2, 8,16,24,24, 2, 8,16,24,32, 2, 8,16,24,24,
         0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 2,16,16,16, 8, 2,24,24,16, 8, 2,32,24,16, 8, 2,
         0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 8,16,16,16,16, 8,24,24,24,16, 8,24,32,24,16, 8,
         0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 8,16,16,16,16,16,16,24,24,24,16,16,24,32,24,16,
         0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 8, 8, 8, 8, 8, 8,16,16,16,16, 8,16,24,24,24, 8,16,24,32,24,
         0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 8, 8, 8, 8, 2, 8,16,16,16, 2, 8,16,24,24, 2, 8,16,24,32,
    };

    // Stand-in for the zero-initialised USER_A..F tactics slots — a team
    // whose file tactics index points at a user slot resolves every packed
    // coordinate to 0 (→ quadrant 0), exactly like the original's
    // InitUserTactics-zeroed pool before any user tactics are loaded.
    private static readonly byte[] ZeroTactics = new byte[TacticsLoader.TacticsStructSize];

    // ------------------------------------------------------------------
    // GetPlayerPrice (swos.asm:129720-129952; teams.txt:834-998).
    // ------------------------------------------------------------------
    //
    // `playerNo` is the in-game slot 1..10 (the original's D7 — the loop
    // index of ApplyTeamTactics' starting-XI walk, which is also the row in
    // the tactics position table). `humanControlled` mirrors the original's
    // TeamFile.teamControls != TEAM_COMPUTER: for CPU teams the crowding
    // division and the nearness factor are skipped (swos.asm:129786-129789,
    // 129886-129905).
    //
    // Returns the player's computed price in CODE space (0..49-ish; the
    // price-increase feedback can only keep it inside 0..49).
    public static int ComputePlayerPrice(PlayerRecord p, TeamRecord team,
                                         int playerNo, bool humanControlled)
    {
        // Position 1..7 (RB..A). The keeper never reaches GetPlayerPrice in
        // the original (AdjustPlayerSkills slot 0 short-circuits to the file
        // price, swos.asm:37620-37628); a stray keeper in an outfield slot
        // would index before skillsPerQuadrant in the asm — we return the
        // file price instead of reproducing that out-of-bounds read.
        int pos = TeamDataLoader.MapPositionString(p.Position);
        if (pos < 1 || pos > 7 || playerNo < 1 || playerNo > 10 || team is null)
            return p.ValueCode;

        // Packed skills, low→high nibble: finishing, speed, ballControl,
        // tackling, heading, shooting, passing. Masked to 0..7 then +1 each
        // → 1..8 (swos.asm:129737-129745: `and 7777777h` / `add 1111111h`).
        // PlayerRecord skills are already mod-8 file nibbles (TeamFile.cs).
        uint skills = PackSkills(p) + 0x1111111u;

        // Tactics positions: g_tacticsTable[team.tactics] + 9 → 10 rows of
        // 35 packed coordinates (swos.asm:129746-129769). "Deduce tactics
        // from player's team" — the team FILE tactics byte.
        byte[] tact = (uint)team.Tactics < (uint)TacticsLoader.BuiltinTactics.Length
            ? TacticsLoader.BuiltinTactics[team.Tactics]
            : ZeroTactics;
        int posRow  = 9 + (playerNo - 1) * 35;   // this player's positions
        int rowBase = (pos - 1) * 35;            // skillsPerQuadrant row (swos.asm:129728-129735)

        // totalSkillsFactorSum is a 16-bit word in the original
        // (swos.asm:214497) — keep the 16-bit wrap.
        int sum = 0;
        for (int ballQ = 34; ballQ >= 0; ballQ--)   // swos.asm:129774-129912
        {
            int dest = CoordToQuadrant[tact[posRow + ballQ]];   // swos.asm:129778-129785

            // How many of the 10 field players run to the same destination
            // quadrant when the ball is here. Skipped for CPU teams; also
            // skipped (forced 1) for the goal rows (ballQ < 5, ballQ >= 30)
            // and the central pre-penalty-area quadrant 7
            // (swos.asm:129786-129828).
            int numPlayersAtQuadrant = 1;
            if (humanControlled && ballQ >= 5 && ballQ < 30 && ballQ != 7)
            {
                int count = 0;
                for (int pl = 0; pl < 10; pl++)
                    if (CoordToQuadrant[tact[9 + pl * 35 + ballQ]] == dest)
                        count++;
                numPlayersAtQuadrant = count;   // includes this player → >= 1
            }

            // Weighted skill accumulator: for each of the 7 skills, if the
            // destination quadrant's ponder nibble is non-zero, add
            // skill(1..8) * skillValuePonders[ponder] (swos.asm:129829-129861;
            // per teams.txt:878-890 the result lands in 8..200).
            uint sk = skills;
            uint ponders = PriceSkillPonders[dest];
            int acc = 0;
            for (int k = 0; k < 7; k++)
            {
                int ponder = (int)(ponders & 0xF);
                if (ponder != 0)
                    acc += (int)(sk & 0xF) * SkillValuePonders[ponder];
                sk >>= 4;
                ponders >>= 4;
            }

            acc >>= 1;                                        // halve → 4..100 (swos.asm:129862)
            acc *= SkillsPerQuadrant[rowBase + dest];         // position fit (swos.asm:129863-129871)
            acc *= QuadrantDistanceFactor[ballQ * 35 + dest]; // proximity, x8 scaled (swos.asm:129872-129884)
            acc >>= 3;                                        // /8 → real 0..4 factor (swos.asm:129885)

            if (humanControlled)
            {
                // Share the value with the other players crowding the same
                // destination (swos.asm:129886-129898).
                if (numPlayersAtQuadrant - 1 > 0)
                    acc /= numPlayersAtQuadrant - 1;
                // Nearness factor (<= 1) — skipped for CPU teams
                // (swos.asm:129900-129905).
                acc = CountNearnessFactor(ballQ, dest, acc, tact, posRow);
            }

            sum = (sum + acc) & 0xFFFF;   // 16-bit accumulate (swos.asm:129907-129909)
        }

        // sum / 350, then -8, clamp [0..49] (swos.asm:129913-129936).
        // Original quirk kept: the word sum is SIGN-extended (cwde) before an
        // UNSIGNED 32-bit divide, and the -8/clamp then runs on the low WORD
        // of the quotient — a sum >= 0x8000 takes a chaotic-but-deterministic
        // path. Realistic sums stay well below it (final prices are 0..49).
        uint dividend = (uint)(int)(short)sum;
        int price = (short)(ushort)(dividend / 350);
        price -= 8;
        if (price < 0) price = 0;
        else if (price > 49) price = 49;

        // Price feedback vs the file (swos.asm:129937-129952;
        // teams.txt:986-998): a computed price below the file price is
        // returned as-is; above it, the file price grows by the saturating
        // increase table instead of jumping straight to the computed value.
        int diff = price - p.ValueCode;
        if (diff < 0) return price;
        return PlPricesIncreaseTable[diff] + p.ValueCode;
    }

    // CountNearnessFactor (swos.asm:129971-130039): scales `acc` by
    // (nearby quadrants whose destination is near ours) / (nearby quadrants),
    // a ratio <= 1. "Nearby" = distance factor >= 8 from the ball quadrant;
    // "destination near ours" = distance factor >= 4 from our destination.
    private static int CountNearnessFactor(int ballQ, int dest, int acc,
                                           byte[] tact, int posRow)
    {
        int near = 0, nearDest = 0;
        for (int q = 34; q >= 0; q--)
        {
            if (q == ballQ) continue;                              // swos.asm:130001-130003
            if (QuadrantDistanceFactor[ballQ * 35 + q] < 8) continue; // too far (swos.asm:130004-130008)
            near++;
            int dq = CoordToQuadrant[tact[posRow + q]];            // our dest when ball at q
            if (QuadrantDistanceFactor[dest * 35 + dq] < 4) continue; // swos.asm:130018-130022
            nearDest++;
        }
        // Every quadrant has >= 8-distance neighbours so near > 0 in the
        // original; guard the division anyway.
        if (near == 0) return acc;
        return acc * nearDest / near;                              // swos.asm:130027-130038
    }

    // ------------------------------------------------------------------
    // AdjustPlayerSkills price → percent step (swos.asm:37670-37727) +
    // GetPlayerPricePercentageChange (swos.asm:37883-37982), friendly path.
    // ------------------------------------------------------------------
    //
    // Consumes ONE Rand2 byte per call — call once per player, not per skill.
    //
    // Omitted original branches, both inert for OpenSWOS exhibition matches:
    //   - career-only bottom-team -12 handicap (swos.asm:37697-37714): gated
    //     on plg_D0_param == 0, and plg_D0_param is TRUE for friendlies
    //     (InitializeInGameTeamsAndStartGame doc, swos.asm:34608-34623);
    //   - career morale/comparative terms in GetPlayerPricePercentageChange
    //     (swos.asm:37888-37957): skipped when isGameFriendly != 0.
    public static int ComputePricePercent(int computedPrice, int filePrice,
                                          bool humanControlled)
    {
        // File price byte is sign-extended (cbw) and zero → 1 to protect the
        // division (swos.asm:37670-37680). Real value codes are 0..47.
        int file = (sbyte)filePrice;
        if (file == 0) file = 1;

        // percent = computed * 100 / file (swos.asm:37682-37690)...
        int percent = computedPrice * 100 / file;

        // ...but CPU teams are pinned to 100 — the price feedback only ever
        // moves PLAYER-controlled teams (swos.asm:37691-37695).
        if (!humanControlled) percent = 100;

        // Random spread: Rand2 → 0..255, *24 >> 8 → 0..23, -12 → [-12..+11]
        // (swos.asm:37959-37981).
        percent += (Rng.NextByte2() * 24 >> 8) - 12;
        return percent;
    }

    // ------------------------------------------------------------------
    // ScaleSkill (swos.asm:37986-38054).
    // ------------------------------------------------------------------
    //
    // rawSkill: the file skill nibble mod 8 (0..7) — the original feeds
    // `and 7777777h` nibbles with NO +1 here (swos.asm:37746-37765).
    // pricePercent: from ComputePricePercent. teamAppPercent: from
    // TeamAppPercent (100 unless the equalizer option is active).
    //
    // Consumes ONE Rand2 byte per call (probabilistic rounding). Returns the
    // UNCLAMPED scaled skill; the caller clamps to 7 max, mirroring
    // AdjustPlayerSkills (swos.asm:37766-37769).
    public static int ScaleSkill(int rawSkill, int pricePercent, int teamAppPercent)
    {
        int pct = pricePercent;
        if (pct < 75) pct = 75;         // swos.asm:37989-37995
        else if (pct > 125) pct = 125;  // swos.asm:37996-37999

        // Fold in the team average-player-price percent (swos.asm:38000-38018).
        pct = pct * teamAppPercent / 100;

        // skill * pct / 100 with the remainder rounded up stochastically:
        // rand2 → 0..255, *100 >> 8 → 0..99; if that is < the remainder, +1
        // ("if decimals greater than random 0..99 give 1 skill point extra",
        // swos.asm:38019-38052).
        int scaled = rawSkill * pct;
        int result = scaled / 100;
        int remainder = scaled % 100;
        if (Rng.NextByte2() * 100 >> 8 < remainder)
            result++;
        return result;
    }

    // ------------------------------------------------------------------
    // Team average player price + "app percent".
    // ------------------------------------------------------------------

    // GetAveragePlayerPrice (swos.asm:81150-81176): sum of the 16 player
    // price CODES / 16 (integer division). Missing roster slots count as 0,
    // like a zeroed player record would.
    public static int ComputeTeamValue(TeamRecord t)
    {
        var players = t?.Players;
        if (players is null) return 0;
        int sum = 0;
        for (int i = 0; i < 16 && i < players.Count; i++)
            sum += (byte)players[i].ValueCode;
        return sum / 16;
    }

    // team{1,2}AppPercent, set by InitInGameTeamStructure
    // (swos.asm:35761-35825): defaults to 100 (swos.asm:35766-35768); only
    // when the game is NOT a career AND "all player teams equal" is on AND
    // the team is player-controlled (TEAM_PLAYER_COACH) does it become
    //
    //   selectedTeamsAvgPlayerPrice * 100 / thisTeamAvgPlayerPrice + 6
    //   (swos.asm:35807-35823)
    //
    // NOTE the ratio direction: LEAGUE average over TEAM average — the
    // option EQUALIZES squads (cheap teams boosted above 100, expensive
    // nerfed below), it does not reward expensive squads. All in price-CODE
    // space. We gate on AllPlayerTeamsEqual only (no player-coach mode in
    // OpenSWOS); default OFF keeps the original friendly behaviour of 100.
    public static int TeamAppPercent(TeamRecord t)
    {
        if (!AllPlayerTeamsEqual || LeagueAvgTeamValue <= 0) return 100;
        int teamAvg = ComputeTeamValue(t);
        if (teamAvg <= 0) return 100;   // division guard (any real squad has priced players)
        return LeagueAvgTeamValue * 100 / teamAvg + 6;
    }

    // Packed skills, low→high nibble: finishing, speed, ballControl,
    // tackling, heading, shooting, passing — matching the register layout
    // GetPlayerPrice builds from the two file words (swos.asm:129737-129744)
    // and the nibble order of PriceSkillPonders. Nibble 7 (the file's
    // unknown high nibble) stays 0 and is never consumed (7-iteration loop).
    private static uint PackSkills(PlayerRecord p) =>
          (uint)(p.Finishing & 7)
        | ((uint)(p.Speed    & 7) << 4)
        | ((uint)(p.Control  & 7) << 8)
        | ((uint)(p.Tackling & 7) << 12)
        | ((uint)(p.Heading  & 7) << 16)
        | ((uint)(p.Shooting & 7) << 20)
        | ((uint)(p.Passing  & 7) << 24);
}
