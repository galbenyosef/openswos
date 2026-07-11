namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Team utility functions ported from external/swos-port/src/game/team.cpp.
//
// stopAllPlayers — called when play stops (goal, half time, set piece). For both
// teams, parks each non-sent-off player at their current X/Y as their dest, and
// clears all "ball / passing / fire" bookkeeping. Note the SWOS_TEST quirk:
// goalkeeperPlaying is intentionally NOT reset for the top team (matches the
// original SWOS bug); we preserve the bug since this is a mechanical port.
//
// initPlayerShotChanceTables + updatePlayerShotChanceTable allocate the goalie
// skill table + (for a given player) point a team's shotChanceTable at either
// kPlayerShotChanceTable or the goalie's skill row. Allocation lives at fixed
// Memory addresses since we don't model SWOS's VM `allocateMemory` runtime —
// we just inline the tables in our static class and treat callers' shotChanceTable
// pointer (TeamData +24) as the offset of the row to use.
public static class TeamPort
{
    // PlayerPosition enum — only goalkeeper checked here.
    // swos.h:PlayerPosition::kGoalkeeper = 0.
    public const int kPlayerPositionGoalkeeper = 0;

    // Player struct (swos.h) — used by updatePlayerShotChanceTable.
    // Layout subset:
    //   position  @ +X (byte)
    //   goalieSkill @ +Y (byte)
    // Until the full PlayerInfo struct view is in, we accept the raw addresses
    // through the caller. The Referee.cs port assumes PlayerInfo records are
    // 61 bytes; we use the same here. For now the function is a stub that the
    // host wires up later.

    // team.cpp:5-13 — kGoalieSkillTables[8][30]: per-skill-level goalie tables.
    // Each row is 30 int16s. Indexed by playerInfo.goalieSkill (0..7).
    public static readonly short[][] kGoalieSkillTables = new short[8][]
    {
        new short[] { 7, 424, -50, 832, 160, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 3, 5, 8, 5, 11, 2, 6, 8, 5 },
        new short[] { 6, 588, -4, 864, 176, 3, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 4, 5, 7, 6, 10, 3, 6, 7, 6 },
        new short[] { 5, 752, 42, 896, 192, 2, 3, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 7, 7, 5, 5, 6, 7, 9, 4, 6, 6, 7 },
        new short[] { 4, 916, 88, 928, 208, 1, 2, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 6, 7, 6, 5, 5, 8, 8, 5, 6, 5, 8 },
        new short[] { 3, 1080, 134, 960, 224, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 6, 6, 4, 9, 7, 6, 6, 4, 9 },
        new short[] { 2, 1244, 180, 992, 240, 0, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 4, 5, 7, 6, 3, 10, 6, 7, 6, 3, 10 },
        new short[] { 1, 1408, 226, 1024, 256, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 4, 8, 6, 2, 11, 5, 8, 6, 2, 11 },
        new short[] { 99, 1408, 226, 1024, 256, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 9, 6, 1, 12, 4, 9, 6, 1, 12 },
    };

    // team.cpp:15-17 — kPlayerShotChanceTable[30]: outfielder table.
    public static readonly short[] kPlayerShotChanceTable = new short[]
    {
        8, 1024, 112, 800, 144, 7, 7, 7, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 1, 6, 9, 4, 12, 1, 6, 9, 4,
    };

    // team.cpp:26-44 — stopAllPlayers.
    // Iterates both teams. Per team: stops each non-sent-away player at their
    // current X/Y, clears ball/passing/keeper bookkeeping. Note SWOS_TEST quirk
    // (line 40-41): goalkeeperPlaying isn't reset for the TOP team (original bug).
    // We replicate the bug for parity — `if (team == &bottomTeamData)` guards it.
    public static void StopAllPlayers()
    {
        for (int t = 0; t < 2; t++)
        {
            bool top = t == 0;
            int teamBase = TeamData.Base(top);
            StopPlayers(top);

            Memory.WriteWord(teamBase + TeamData.OffBallInPlay,        0);
            Memory.WriteWord(teamBase + TeamData.OffBallOutOfPlay,     0);
            // team.cpp:32-33 — controlledPlayer.reset() / passToPlayerPtr.reset() write 0.
            Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, 0);
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr,  0);
            Memory.WriteWord(teamBase + TeamData.OffPassingBall,       0);
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer,   0);
            Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 0);
            Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);

            // team.cpp:38-43 — SWOS_TEST: skip top-team keeper reset (matches
            // original bug). We have no SWOS_TEST flag, so default to production
            // behaviour (always reset).
            Memory.WriteWord(teamBase + TeamData.OffGoalkeeperPlaying, 0);
        }
    }

    // team.cpp:46-55 — stopPlayers(team).
    // For each of the team's 11 sprites: if in normal state AND not sent away,
    // set destX/Y to current X/Y (whole pixels) so the sprite stops moving.
    private static void StopPlayers(bool top)
    {
        for (int slotInTeam = 0; slotInTeam < PlayerSprite.TeamSize; slotInTeam++)
        {
            int spriteAddr = TeamData.GetTeamSpriteAddr(top, slotInTeam);
            if (spriteAddr == 0) continue;

            // team.cpp:50 — inNormalState() and !sentAway.
            // inNormalState (Sprite.h:player.h) = PlayerState == kNormal (0) (best-
            // effort here; full PlayerState semantics live in Sprite.h).
            byte plState = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerState);
            short sentAway = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffSentAway);
            if (plState == 0 && sentAway == 0)
            {
                short curX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
                short curY = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
                Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, curX);
                Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, curY);
            }
        }
    }

    // team.cpp:58-64 — initPlayerShotChanceTables.
    // Original allocates SWOS-VM memory for both tables and memcpys
    // kGoalieSkillTables / kPlayerShotChanceTable into the freshly allocated
    // pools so the per-team `team.shotChanceTable` pointer can be reseated to
    // a specific row at runtime.
    //
    // OpenSwos divides the responsibility differently — the per-team backing
    // buffer is allocated once at TeamData.Init() time (the addresses live in
    // Memory.Addr.team{1,2}ShotChanceTable, wired by TeamDataLoader.SeedTeamData
    // at line 175). UpdatePlayerShotChanceTable below copies the right row of
    // the still-managed-side tables into that buffer per-tick.
    //
    // The C++ allocation + memcpy is therefore intentionally a no-op here: the
    // managed-side kGoalieSkillTables / kPlayerShotChanceTable are already
    // available without an explicit init pass, and per-team buffers come from
    // TeamData.Init() rather than from a global pool. Kept for call-site
    // parity with `team.cpp:58-64` once the host migrates to the full
    // shotChance pipeline.
    //
    // Source: external/swos-port/src/game/team.cpp:58-64.
    public static void InitPlayerShotChanceTables()
    {
        // No-op by design — see comment block above.
    }

    // team.cpp:66-75 — updatePlayerShotChanceTable.
    // Picks which row of the goalie table (by goalieSkill) — or the outfield
    // table — applies to this team's controlled player. Writes a row INDEX into
    // TeamData.shotChanceTable (+24). When the consumer of that field is ported,
    // it will resolve the index via kGoalieSkillTables / kPlayerShotChanceTable.
    public static void UpdatePlayerShotChanceTable(bool top, int playerInfoAddr)
    {
        // PlayerInfo struct layout (swos.h:162). Verified offsets:
        //   position    @ +4  (byte) — PlayerPosition enum after substituted/index/
        //                              goalsScored/shirtNumber
        //   goalieSkill @ +34 (byte) — after skills block (passing..finishing)
        // See game/scripts/Sim/Port/TeamDataLoader.cs for the full PlayerInfo
        // offset map. The actual table data is allocated per-team in Memory
        // (Memory.Addr.team1ShotChanceTable / team2ShotChanceTable) and the
        // pointer is stored in TeamData +24. We rewrite the bytes here, then
        // re-point the field at the same backing buffer.
        int position    = playerInfoAddr == 0 ? -1 : Memory.ReadByte(playerInfoAddr + 4);
        int goalieSkill = playerInfoAddr == 0 ? 0  : Memory.ReadByte(playerInfoAddr + 34);

        // team.cpp:69-74. Pick the right row → memcpy into our team's
        // shotChanceTable buffer (allocated at SeedTeamData time by Main.cs).
        int teamBase   = TeamData.Base(top);
        int tableAddr  = Memory.ReadSignedDword(teamBase + TeamData.OffShotChanceTable);
        if (tableAddr == 0)
        {
            // Buffer never allocated — fall back to legacy index encoding so
            // we don't crash. (Should not happen post-SeedTeamData.)
            if (position == kPlayerPositionGoalkeeper)
            {
                if (goalieSkill < 0) goalieSkill = 0;
                if (goalieSkill > 7) goalieSkill = 7;
                Memory.WriteDword(teamBase + TeamData.OffShotChanceTable, goalieSkill);
            }
            else
            {
                Memory.WriteDword(teamBase + TeamData.OffShotChanceTable, -1);
            }
            return;
        }

        short[] row;
        if (position == kPlayerPositionGoalkeeper)
        {
            if (goalieSkill < 0) goalieSkill = 0;
            if (goalieSkill > 7) goalieSkill = 7;
            row = kGoalieSkillTables[goalieSkill];
        }
        else
        {
            row = kPlayerShotChanceTable;
        }
        for (int i = 0; i < row.Length; i++)
            Memory.WriteWord(tableAddr + i * 2, row[i]);
    }

    // team.cpp:78-91 — SWOS_TEST helpers (only compiled in tests).
    // GetPlayerShotChanceTable returns a managed-array reference; the goalie
    // table index lookup is the inverse of UpdatePlayerShotChanceTable.
    public static short[] GetPlayerShotChanceTable() => kPlayerShotChanceTable;

    public static int GetGoalieShotChanceTableIndex(int rowIndex)
    {
        // team.cpp:84-89 — linear search over kGoalieSkillTables. With our
        // index-based encoding, the answer IS the row index when in range.
        if (rowIndex >= 0 && rowIndex < kGoalieSkillTables.Length)
            return rowIndex;
        return -1;
    }
}
