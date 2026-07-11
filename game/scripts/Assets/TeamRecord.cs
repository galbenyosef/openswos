namespace OpenSwos.Assets;

// Variant-neutral team data. SWOS team files have an identical 76 + 16*38-byte
// structure across PC and Amiga, so both sources produce these same records.
public sealed class TeamRecord
{
    public byte Nation { get; init; }
    public ushort GlobalId { get; init; }
    public string Name { get; init; } = "";
    public string Coach { get; init; } = "";
    public byte Tactics { get; init; }
    public byte Division { get; init; }
    // Kit bytes from the TEAM.* record. BOTH are the canonical 5-byte SWOS
    // layout {shirtType, stripesColor, basicColor, shortsColor, socksColor}
    // (swos.h:245-254): Home = primary kit (+0x1A), Away = secondary/change kit
    // (+0x1F). Away is used when the two teams' kits clash (KitClash.Select).
    public byte[] HomeKit { get; init; } = System.Array.Empty<byte>();
    public byte[] AwayKit { get; init; } = System.Array.Empty<byte>();
    // Synthesized 5-byte keeper kit using SWOS convention from swos-port
    // colorizeSprites.cpp:270-275: keeper SHORTS/SOCKS come from the team's
    // prShortsCol/prSocksCol; kSkinColor[face]/kHairColor[face] there recolour the
    // keeper's SKIN and HAIR (same per-face rule as outfield players — handled by
    // KitPalette.ApplyFace using the keeper's own PlayerRecord.Face), NOT his shirt.
    // The keeper jersey colour is baked into the keeper art; our solid-yellow body
    // is an approximation of the classic SWOS keeper look.
    // Layout: { type, shirt1, shirt2, shorts, socks }.
    public byte[] GoalkeeperKit { get; init; } = System.Array.Empty<byte>();
    // TEAM.* header +0x3C: playerNumbers[16] (swos.asm:343, swos.h:257 TeamFileHeader).
    // Maps in-game slot (0..15) -> roster index in Players. Slot 0 is the starting
    // goalkeeper, slots 1..10 the field lineup in tactics-position order, slots
    // 11..15 the bench (incl. the reserve keeper). GetPlayerAtIndex
    // (swos.asm:25572-25607) resolves in-game player i via this table.
    public byte[] LineupOrder { get; init; } = System.Array.Empty<byte>();
    public List<PlayerRecord> Players { get; init; } = new();
}

public sealed class PlayerRecord
{
    public byte Nationality { get; init; }
    public byte ShirtNumber { get; init; }
    public string Name { get; init; } = "";
    public string Position { get; init; } = "";
    // Face (skin + hair colour) type: 0 = WHITE, 1 = GINGER, 2 = BLACK
    // (swos-port swos.h:436-442 enum FaceTypes). Decoded from TEAM.* player
    // byte +0x1A bits 3-4 (docs/SWOS/teams.txt:192-200) by TeamFile.ReadPlayer.
    // Drives the per-player skin/hair palette recolour: KitPalette.ApplyFace.
    public int Face { get; init; }
    // 7 skills, range 0..7: the file nibble (0..15) reduced modulo 8, exactly
    // as the original engine consumes it (`and 7777777h`, swos.asm:37754 /
    // 129744; teams.txt:848 — nibble 8 plays as 0, F as 7). See
    // tools/team-decode/TeamFile.cs ReadPlayer. These are the RAW file
    // skills; the on-pitch values additionally go through the price/team
    // feedback in Sim/Port/SkillScaling.cs when that path is enabled.
    public int Passing { get; init; }
    public int Shooting { get; init; }
    public int Heading { get; init; }
    public int Tackling { get; init; }
    public int Control { get; init; }
    public int Speed { get; init; }
    public int Finishing { get; init; }
    // TEAM.* player record byte +0x20 — "value code" (0..47), index into the
    // price table. Source of the goalkeeper's effective skill in the original
    // (AdjustPlayerSkills, swos.asm:37783-37867: (value+3)/7 + rand bonus).
    public int ValueCode { get; init; }
}
