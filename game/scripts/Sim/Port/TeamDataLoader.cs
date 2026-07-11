namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;
using OpenSwos.Assets;

// Loads per-team PlayerInfo records + name + shotChanceTable into SwosVm
// memory at match start. Called once from Main.cs's InitSwosVmFromMatchSetup.
//
// Sources:
//   - external/swos-port/src/swos/swos.h:162   (PlayerInfo struct, 61 bytes)
//   - external/swos-port/src/swos/swos.h:296   (TeamGame struct, 1704 bytes)
//   - external/swos-port/src/game/team.cpp:5-13 (kGoalieSkillTables data)
//   - external/swos-port/src/game/team.cpp:15-17 (kPlayerShotChanceTable data)
//
// PlayerInfo layout (sizeof = 61, pack=1):
//   +0    byte    substituted
//   +1    byte    index
//   +2    byte    goalsScored
//   +3    byte    shirtNumber
//   +4    byte    position (PlayerPosition enum)
//   +5    byte    face
//   +6    byte    isInjured
//   +7..11        filler (5 bytes — fields 7..0xB in swos.h)
//   +12..26       shortName[15]
//   +27   byte    passing
//   +28   byte    shooting
//   +29   byte    heading
//   +30   byte    tackling
//   +31   byte    ballControl
//   +32   byte    speed
//   +33   byte    finishing
//   +34   byte    goalieSkill
//   +35   byte    injuriesBitfield
//   +36   byte    halfPlayed
//   +37   byte    face2
//   +38..60       fullName[23]
public static class TeamDataLoader
{
    public const int PlayerInfoSize = 61;

    public const int OffSubstituted    = 0;
    public const int OffIndex          = 1;
    public const int OffGoalsScored    = 2;
    public const int OffShirtNumber    = 3;
    public const int OffPosition       = 4;
    public const int OffFace           = 5;
    public const int OffIsInjured      = 6;
    public const int OffCards          = 10;  // GameTime.cs:540 reads cards @ +10
    public const int OffShortName      = 12;  // 15 bytes
    public const int OffPassing        = 27;
    public const int OffShooting       = 28;
    public const int OffHeading        = 29;
    public const int OffTackling       = 30;
    public const int OffBallControl    = 31;
    public const int OffSpeed          = 32;
    public const int OffFinishing      = 33;
    public const int OffGoalieSkill    = 34;
    public const int OffInjuriesBits   = 35;
    public const int OffHalfPlayed     = 36;
    public const int OffFace2          = 37;
    public const int OffFullName       = 38;  // 23 bytes

    // PlayerPosition enum (swos.h:149-160).
    public const int PosGoalkeeper    = 0;
    public const int PosRightBack     = 1;
    public const int PosLeftBack      = 2;
    public const int PosDefender      = 3;
    public const int PosRightWing     = 4;
    public const int PosLeftWing      = 5;
    public const int PosMidfielder    = 6;
    public const int PosAttacker      = 7;

    // Wires a single team's data into the VM. `playersBase` is the absolute
    // address where the 16 × 61-byte PlayerInfo records will be written
    // (matches topTeamInGame / bottomTeamInGame as used by existing port code).
    // `team.Players` holds 1..16 player records loaded from the PC TEAM.* file
    // (see Assets/TeamRecord.cs); for slots beyond Players.Count we emit a
    // substituted=1 placeholder.
    //
    // `isHumanControlled` mirrors the original's TeamFile.teamControls !=
    // TEAM_COMPUTER: it gates the price-feedback parts of the skill pipeline
    // (GetPlayerPrice crowding/nearness, swos.asm:129786-129789/129886-129905,
    // and the CPU pin of the price percent to 100, swos.asm:37691-37695).
    // Defaults to false (CPU) so existing callers keep CPU-vs-CPU semantics.
    public static void WritePlayerInfos(int playersBase, TeamRecord team,
                                        bool isHumanControlled = false)
    {
        if (team is null) return;

        // Which side is this? The goalie-skill random bonus uses a different
        // gameRandValue bit per side (swos.asm:37811-37819 compares A4 against
        // `offset topTeamInGame`). team1 == top by SeedTeamData convention.
        bool top = playersBase == Memory.Addr.team1InGameTeamPlayers;

        int playerCount = team.Players?.Count ?? 0;

        // team{1,2}AppPercent — computed once per team by
        // InitInGameTeamStructure (swos.asm:35761-35825), read per skill by
        // ScaleSkill (swos.asm:38000-38010). 100 unless the "all player
        // teams equal" option is active (see SkillScaling.TeamAppPercent).
        int teamAppPercent = SkillScaling.Enabled ? SkillScaling.TeamAppPercent(team) : 100;

        // Original lineup construction: InitInGameTeamStructure
        // (swos.asm:35824-35923) loops in-game slot D2 = 0..15 and calls
        // GetPlayerAtIndex (swos.asm:25572-25607), which resolves the ROSTER
        // index through the TEAM.* header table TeamFile.playerNumbers[slot]
        // (swos.asm:343 / swos.h:257, header offset +0x3C). Slot 0 is the
        // starting goalkeeper, 1..10 the field lineup in tactics-position
        // order, 11..15 the bench (incl. the reserve keeper). The roster
        // order is NOT the in-game order.
        byte[] order = team.LineupOrder;
        bool orderValid = order is { Length: 16 };
        if (orderValid)
        {
            for (int i = 0; i < 16; i++)
                if (order![i] >= playerCount) { orderValid = false; break; }
        }

        for (int i = 0; i < 16; i++)
        {
            int slotAddr = playersBase + i * PlayerInfoSize;

            // Zero the slot first — every field is byte-sized.
            for (int b = 0; b < PlayerInfoSize; b++)
                Memory.WriteByte(slotAddr + b, 0);

            int rosterIdx = orderValid ? order![i] : i;
            if (rosterIdx >= playerCount)
            {
                // Empty slot — mark substituted so port code (PlayerInfo.wasSubstituted)
                // skips it. cards remain 0.
                Memory.WriteByte(slotAddr + OffSubstituted, 1);
                continue;
            }

            PlayerRecord p = team.Players![rosterIdx];

            Memory.WriteByte(slotAddr + OffSubstituted, 0);
            // PlayerInfo.index = index in the team FILE, not the in-game slot
            // (InitInGamePlayer, swos.asm:36013-36015: playerIndex = D1, where
            // D1 was translated by GetPlayerAtIndex before the call).
            Memory.WriteByte(slotAddr + OffIndex,       (byte)rosterIdx);
            Memory.WriteByte(slotAddr + OffGoalsScored, 0);
            Memory.WriteByte(slotAddr + OffShirtNumber, p.ShirtNumber);

            // Position comes straight from the file record's positionAndFace
            // bits 5..7 (InitInGamePlayer, swos.asm:36048-36055). With the
            // playerNumbers permutation applied, slot 0 lands on a GK entry
            // naturally — the original never forces it.
            byte positionByte = (byte)MapPositionString(p.Position);
            Memory.WriteByte(slotAddr + OffPosition, positionByte);

            // Skills (0..7 mod-8 file nibbles from the TEAM.* parse; layout
            // per external/swos-port/src/swos/swos.h:177-185).
            //
            // Faithful path (SkillScaling.Enabled): reproduce ApplyTeamTactics
            // → AdjustPlayerSkills (swos.asm:37618-37877) for the starting XI
            // (slots 0..10 — the original's loop runs D2 = 0..10 only):
            //   - slot 0 (keeper): price = raw file price (swos.asm:37620-37628);
            //   - slots 1..10: price = GetPlayerPrice with playerNo = in-game
            //     slot (swos.asm:37630-37668);
            //   - percent = price*100/filePrice + rand spread, CPU pinned to
            //     100 (swos.asm:37670-37727, 37959-37981);
            //   - each skill scaled by ScaleSkill then clamped to 7 max
            //     (swos.asm:37746-37772), in the asm's passing→finishing order
            //     so the Rand2 stream is consumed identically.
            // Bench slots 11..15 keep the raw file skills — the original only
            // rescales them via a fresh AdjustPlayerSkills when tactics are
            // (re)applied; our port loads them once here (see task #196 notes).
            if (SkillScaling.Enabled && i <= 10)
            {
                int computedPrice = i == 0
                    ? p.ValueCode
                    : SkillScaling.ComputePlayerPrice(p, team, i, isHumanControlled);
                int pricePercent = SkillScaling.ComputePricePercent(
                    computedPrice, p.ValueCode, isHumanControlled);

                Memory.WriteByte(slotAddr + OffPassing,
                    Clamp7(SkillScaling.ScaleSkill(Clamp7(p.Passing),   pricePercent, teamAppPercent)));
                Memory.WriteByte(slotAddr + OffShooting,
                    Clamp7(SkillScaling.ScaleSkill(Clamp7(p.Shooting),  pricePercent, teamAppPercent)));
                Memory.WriteByte(slotAddr + OffHeading,
                    Clamp7(SkillScaling.ScaleSkill(Clamp7(p.Heading),   pricePercent, teamAppPercent)));
                Memory.WriteByte(slotAddr + OffTackling,
                    Clamp7(SkillScaling.ScaleSkill(Clamp7(p.Tackling),  pricePercent, teamAppPercent)));
                Memory.WriteByte(slotAddr + OffBallControl,
                    Clamp7(SkillScaling.ScaleSkill(Clamp7(p.Control),   pricePercent, teamAppPercent)));
                Memory.WriteByte(slotAddr + OffSpeed,
                    Clamp7(SkillScaling.ScaleSkill(Clamp7(p.Speed),     pricePercent, teamAppPercent)));
                Memory.WriteByte(slotAddr + OffFinishing,
                    Clamp7(SkillScaling.ScaleSkill(Clamp7(p.Finishing), pricePercent, teamAppPercent)));
            }
            else
            {
                // Legacy / bench path: raw mod-8 file skills, defensively
                // clamped (TEAM.* corruption could push them out of range).
                Memory.WriteByte(slotAddr + OffPassing,     Clamp7(p.Passing));
                Memory.WriteByte(slotAddr + OffShooting,    Clamp7(p.Shooting));
                Memory.WriteByte(slotAddr + OffHeading,     Clamp7(p.Heading));
                Memory.WriteByte(slotAddr + OffTackling,    Clamp7(p.Tackling));
                Memory.WriteByte(slotAddr + OffBallControl, Clamp7(p.Control));
                Memory.WriteByte(slotAddr + OffSpeed,       Clamp7(p.Speed));
                Memory.WriteByte(slotAddr + OffFinishing,   Clamp7(p.Finishing));
            }

            // Goalkeeper-specific: goalieSkill (0..7, indexes kGoalieSkillTables
            // via TeamPort.UpdatePlayerShotChanceTable) is derived from the
            // keeper's VALUE/PRICE byte, NOT from any skill nibble — keepers'
            // TEAM.* skill nibbles are all-zero filler (parse back as 0s).
            // swos.asm:37783-37789 (AdjustPlayerSkills) gates on position ==
            // goalkeeper; non-keepers keep goalieSkill = 0 (swos.asm:37781-37782,
            // covered by the zero-fill above). Applies to the bench keeper too.
            if (positionByte == PosGoalkeeper)
            {
                Memory.WriteByte(slotAddr + OffGoalieSkill,
                                 GoalieSkillFromPrice(p.ValueCode, top));
            }

            // Names — truncate + ASCII-encode + null-terminate.
            WriteAsciiTrunc(slotAddr + OffShortName, p.Name, 14);   // 15 bytes incl null
            WriteAsciiTrunc(slotAddr + OffFullName,  p.Name, 22);   // 23 bytes incl null
        }

        // One-line load probe: total of the 70 outfield skill bytes actually
        // written for the starting XI (slots 1..10). Lets a headless run verify
        // the ScaleSkill path lands near the raw sums (no systematic nerf).
        {
            int sum = 0;
            for (int s = 1; s <= 10; s++)
            {
                int b = playersBase + s * PlayerInfoSize;
                sum += Memory.ReadByte(b + OffPassing) + Memory.ReadByte(b + OffShooting)
                     + Memory.ReadByte(b + OffHeading) + Memory.ReadByte(b + OffTackling)
                     + Memory.ReadByte(b + OffBallControl) + Memory.ReadByte(b + OffSpeed)
                     + Memory.ReadByte(b + OffFinishing);
            }
            Godot.GD.Print($"[skills] {(top ? "top" : "bottom")} '{team.Name?.Trim()}' XI skill sum = {sum} (scaling={(SkillScaling.Enabled ? "ON" : "OFF")})");
        }
    }

    // Writes the team's display name + computes/wires the per-team
    // shotChanceTable + sets various TeamGeneralInfo fields that the port
    // code reads each tick.
    public static void WireTeamFields(bool top, TeamRecord team,
                                       int playersBaseAddr,
                                       int shotChanceTableAddr,
                                       int nameStorageAddr,
                                       bool isHumanControlled,
                                       int defaultTacticsIndex = 5 /* 4-3-3 */)
    {
        int teamBase = TeamData.Base(top);
        int inGameTeamBase = playersBaseAddr;  // existing port convention: players[0]

        // inGameTeamPtr (TeamData +10) — points at players[0]. Same convention
        // used by GameTime.ForEachPlayer and TeamPort.UpdatePlayerShotChanceTable.
        Memory.WriteDword(teamBase + TeamData.OffInGameTeamPtr, inGameTeamBase);

        // topTeamInGame / bottomTeamInGame (Memory.Addr.* globals) — same pointer.
        Memory.WriteDword(top ? Memory.Addr.topTeamInGame : Memory.Addr.bottomTeamInGame,
                          inGameTeamBase);

        // teamNumber (TeamData +18) — 1 for top, 2 for bottom (swos.h:339).
        Memory.WriteWord(teamBase + TeamData.OffTeamNumber, top ? 1 : 2);

        // tactics (TeamData +28) — index into g_tacticsTable. Default 5 = 4-3-3.
        // setPlayerWithNoBallDestination derefs g_tacticsTable[tactics] for
        // formation positions. updatePlayers.cpp:15382/15438.
        Memory.WriteWord(teamBase + TeamData.OffTactics, defaultTacticsIndex);

        // playerNumber (TeamData +4) — human controller. swos.h:334.
        // 0 = AI, 1 = Player1 controller, 2 = Player2 controller.
        // InputControls.UpdateTeamControls (gameControls.cpp:74) reads this to
        // dispatch which ic_pl{1,2}Events slot drives this team — top team
        // gets P1 controls; bottom team gets P2 controls (if both human).
        int playerNumberValue = isHumanControlled ? (top ? 1 : 2) : 0;
        Memory.WriteWord(teamBase + TeamData.OffPlayerNumber, playerNumberValue);

        // shotChanceTable (TeamData +24) — pre-load with kPlayerShotChanceTable
        // bytes; per-frame code in PlayerUpdate.cs:466/700 reads +10/+52/+54.
        // (Once UpdatePlayerShotChanceTable runs per-player, this will be
        // re-pointed at the goalie row when the keeper has the ball.)
        WriteShortArray(shotChanceTableAddr, TeamPort.kPlayerShotChanceTable);
        Memory.WriteDword(teamBase + TeamData.OffShotChanceTable, shotChanceTableAddr);

        // teamStatsPtr (+14) — already wired by TeamData.Init() to point at
        // top/bottomTeamStatsData. Leave alone.

        // Display name — copy into VM memory + write pointer into result.cpp's
        // m_team1Name / m_team2Name slot. Result UI reads this when drawing
        // the post-match screen.
        string name = team?.Name ?? "";
        WriteAsciiTrunc(nameStorageAddr, name, 17);
        Memory.WriteDword(top ? Memory.Addr.res_team1Name : Memory.Addr.res_team2Name,
                          nameStorageAddr);

        if (top)
            Memory.WriteWord(Memory.Addr.res_team1NameLength, System.Math.Min(name.Length, 17));
    }

    // swos.asm:37783-37867 — AdjustPlayerSkills, goalkeeper branch. The keeper's
    // effective skill is derived from his PRICE byte (TEAM.* player record +0x20,
    // "value code" 0..47), not from skill nibbles:
    //
    //   D0 = (price + 3) / 7            ; swos.asm:37792-37810
    //                                   ; "add 3 for rounded division"
    //                                   ; "D0 = goalkeeper price in 0..7 range"
    //   D1 = gameRandValue & 1          ; top team    — swos.asm:37811-37815
    //   D1 = (gameRandValue & 2) >> 1   ; bottom team — swos.asm:37816-37819
    //   D0 += D1 + 1                    ; "D1 = 1 or 2" — swos.asm:37822-37824
    //   D0 += 1 if opponent is TEAM_PLAYER_COACH — swos.asm:37826-37829.
    //       OpenSWOS has no player-coach mode, so that boost never applies
    //       and is omitted here.
    //
    // Quirk being patched: the original only clamps D0 into 0..7 on the code
    // paths involving a player-coach team (@@limit_max_skill_to_7 /
    // @@eliminate_negative_values, swos.asm:37851-37861); every other mode
    // (including CPU vs CPU) skips the clamp, so a top-priced keeper could
    // yield goalieSkill 8..9 and index past the end of kGoalieSkillTables.
    // swos-port hooks FixTwoCPUsGameCrash into AdjustPlayerSkills
    // (external/swos-port/src/game/game.cpp:1508-1516, swos/symbols.txt:775)
    // to clamp D0 into 0..7 unconditionally. We do the same.
    public static byte GoalieSkillFromPrice(int priceCode, bool top)
    {
        // asm sign-extends the price byte (cbw) before the arithmetic.
        int d0 = ((sbyte)priceCode + 3) / 7;
        int rand = Memory.ReadWord(Memory.Addr.gameRandValue);
        int d1 = top ? (rand & 1) : ((rand & 2) >> 1);
        d0 += d1 + 1;
        // game.cpp:1510-1516 FixTwoCPUsGameCrash — unconditional 0..7 clamp.
        if (d0 < 0) d0 = 0;
        if (d0 > 7) d0 = 7;
        return (byte)d0;
    }

    // Helper: clamp a value into 0..7 (PlayerRecord skills come back as 0..7
    // already, but PC TEAM.* corruption could push them out of range).
    private static byte Clamp7(int v)
    {
        if (v < 0) return 0;
        if (v > 7) return 7;
        return (byte)v;
    }

    // Helper: map a position string back to the PlayerPosition enum byte.
    // TeamFile.DecodePosition (tools/team-decode/TeamFile.cs:152) emits
    // "G","RB","LB","D","RW","LW","M","A" from positionAndFace bits 5..7 —
    // the enum value is exactly those bits (swos.asm:36048-36052: and 0xE0,
    // shr 5), i.e. G=0 RB=1 LB=2 D=3 RW=4 LW=5 M=6 A=7 (swos.h:149-160).
    // internal: SkillScaling.ComputePlayerPrice uses the same mapping to pick
    // its skillsPerQuadrant row.
    internal static int MapPositionString(string s) => s switch
    {
        "G"  => PosGoalkeeper,
        "RB" => PosRightBack,
        "LB" => PosLeftBack,
        "D"  => PosDefender,
        "RW" => PosRightWing,
        "LW" => PosLeftWing,
        "M"  => PosMidfielder,
        "A"  => PosAttacker,
        _    => PosMidfielder,
    };

    // Helper: write a string as fixed-width ASCII into VM memory, null-padded.
    private static void WriteAsciiTrunc(int addr, string s, int maxLen)
    {
        s ??= "";
        int n = System.Math.Min(s.Length, maxLen);
        for (int i = 0; i < n; i++)
        {
            char c = s[i];
            byte b = c <= 0x7F ? (byte)c : (byte)'?';
            Memory.WriteByte(addr + i, b);
        }
        // Null-pad the remainder up to maxLen + 1 byte (terminator).
        for (int i = n; i <= maxLen; i++)
            Memory.WriteByte(addr + i, 0);
    }

    // Helper: write a managed short[] into VM memory as a packed int16_t[].
    private static void WriteShortArray(int addr, short[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            Memory.WriteWord(addr + i * 2, arr[i]);
    }
}
