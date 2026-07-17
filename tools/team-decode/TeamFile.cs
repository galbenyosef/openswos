using System.Buffers.Binary;
using System.Text;

namespace OpenSwos.Tools.TeamDecode;

// SWOS PC 96/97 TEAM file format.
//
// File: 2-byte BE team count, followed by N x 684-byte team blocks.
//
// Team block (684 bytes):
//   76-byte header + 16 x 38-byte player records.
//
// Team header:
//   +0x00  u8   nation code (matches file suffix in decimal: TEAM.057 -> nation 57)
//   +0x01  u8   team index within this file
//   +0x02  u16  global team ID (BIG-ENDIAN)
//   +0x04  u8   team skill total
//   +0x05  19   team name (ASCII, null-padded)
//   +0x19  u8   division (0=Premier, 1=Div1, ..., 4=Non-league)
//   +0x1A  u8   default tactics (0=4-4-2, 1=5-4-1, ...)
//   +0x1C  5    home kit (type, shirt1, shirt2, shorts, socks)
//   +0x21  5    away kit (same layout)
//   +0x26  22   coach name (ASCII, null-padded)
//   +0x3C  16   player display order (each byte is a player index 0..15)
//
// Player record (38 bytes):
//   +0x00  u8   nationality — uses SWOS's PLAYER-nation numbering
//                (0=ALB..152=CUS; 18=ITA, 46=IRL, 70=BRA), NOT the TEAM.*
//                filename-suffix numbering. See game PlayerNationNames.cs
//                (teams.txt:109-161, swos.asm:186797-186806).
//   +0x01  u8   reserved (0)
//   +0x02  u8   shirt number
//   +0x03  23   name (ASCII, null-padded)
//   +0x1A  u8   bit-packed position + face: bits 5-7 = position (0=G .. 7=A),
//                bits 3-4 = face/skin+hair type (0=white, 1=ginger, 2=black)
//                (swos-port docs/SWOS/teams.txt:192-200; swos.asm:84815-84832
//                extracts the face with `and 18h` / `shr 3` and cycles it mod 3)
//   +0x1C  u8   skill byte 1: high nibble = ???,      low nibble = passing
//   +0x1D  u8   skill byte 2: high nibble = shooting, low nibble = heading
//   +0x1E  u8   skill byte 3: high nibble = tackling, low nibble = control
//   +0x1F  u8   skill byte 4: high nibble = speed,    low nibble = finishing
//                Skill nibbles hold 0..15 in the file (swos-port
//                docs/SWOS/teams.txt:214-221) but the ENGINE reads them
//                modulo 8 — see ReadPlayer below. We decode to 0..7.
//   +0x20  u8   value code (0..47, lookup table to monetary value in thousands)

public sealed record Player(
    byte Nationality,
    byte ShirtNumber,
    string Name,
    byte PositionByte,
    int Face,
    int Passing,
    int Shooting,
    int Heading,
    int Tackling,
    int Control,
    int Speed,
    int Finishing,
    byte ValueCode);

public sealed record Team(
    byte Nation,
    byte IndexInFile,
    ushort GlobalTeamId,
    byte TeamSkillTotal,
    string Name,
    byte Division,
    byte Tactics,
    byte[] HomeKit,
    byte[] AwayKit,
    string Coach,
    byte[] PlayerDisplayOrder,
    Player[] Players);

public sealed class TeamFileException : Exception
{
    public TeamFileException(string message) : base(message) { }
}

public static class TeamFile
{
    public const int TeamBlockSize = 684;
    public const int HeaderSize = 76;
    public const int PlayerRecordSize = 38;
    public const int PlayerCount = 16;

    public static List<Team> Read(byte[] data)
    {
        if (data.Length < 2)
            throw new TeamFileException("file too short");

        int teamCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0));
        int expected = 2 + teamCount * TeamBlockSize;
        if (expected != data.Length)
            throw new TeamFileException(
                $"size mismatch: header says {teamCount} teams (= {expected} bytes), file is {data.Length} bytes");

        var teams = new List<Team>(teamCount);
        for (int i = 0; i < teamCount; i++)
            teams.Add(ReadTeam(data.AsSpan(2 + i * TeamBlockSize, TeamBlockSize)));
        return teams;
    }

    private static Team ReadTeam(ReadOnlySpan<byte> block)
    {
        byte nation = block[0x00];
        byte indexInFile = block[0x01];
        ushort globalId = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(0x02, 2));
        byte skillTotal = block[0x04];
        string name = ReadNullPadded(block.Slice(0x05, 19));
        // TeamFileHeader (swos.h:234-258, static_assert size==76): tactics @0x18,
        // league @0x19, then TWO 5-byte kits — primary (home) @0x1A..0x1E and
        // secondary (away/change) @0x1F..0x23 — then coachName @0x24 (the +0x24
        // coach start is empirically verified against TEAM.008 ARSENAL, which
        // pins the 10 kit bytes to 0x1A..0x23). Each kit is
        // {shirtType, stripesColor, basicColor, shortsColor, socksColor}.
        // PRIOR BUG: home was read at 0x1C (straddling primary tail + secondary
        // head) and away as only 3 bytes at 0x21, so both kits were scrambled
        // and the change-kit clash test had no valid data (task #185).
        byte division = block[0x19];
        byte tactics = block[0x18];
        var homeKit = block.Slice(0x1A, 5).ToArray();
        var awayKit = block.Slice(0x1F, 5).ToArray();
        string coach = ReadNullPadded(block.Slice(0x24, 24));
        var displayOrder = block.Slice(0x3C, 16).ToArray();

        var players = new Player[PlayerCount];
        for (int p = 0; p < PlayerCount; p++)
        {
            int offset = HeaderSize + p * PlayerRecordSize;
            players[p] = ReadPlayer(block.Slice(offset, PlayerRecordSize));
        }

        return new Team(nation, indexInFile, globalId, skillTotal, name, division, tactics,
            homeKit, awayKit, coach, displayOrder, players);
    }

    private static Player ReadPlayer(ReadOnlySpan<byte> rec)
    {
        byte nationality = rec[0x00];
        byte shirt = rec[0x02];
        string name = ReadNullPadded(rec.Slice(0x03, 23));
        byte posByte = rec[0x1A];

        // Face (skin + hair colour type) shares the position byte: bits 3-4,
        // 0 = white, 1 = ginger, 2 = black (swos-port docs/SWOS/teams.txt:199-200
        // "bits 3,4 - face (00 = white, 10 = black, 01 = ginger)"; matches
        // enum FaceTypes kWhite/kGinger/kBlack, swos.h:436-442). Extraction
        // mirrors EditTeamsChangePlayerFace, swos.asm:84815-84818: `and 18h`,
        // `shr 3`. Position stays in bits 5-7, untouched by this mask.
        int face = (posByte >> 3) & 0x03;

        // 7 skills packed one per NIBBLE in bytes 0x1C..0x1F. The file stores
        // full nibbles 0..15 (swos-port docs/SWOS/teams.txt:214-221), but the
        // original engine consumes every skill MODULO 8: both GetPlayerPrice
        // and AdjustPlayerSkills mask the packed nibbles with `and 7777777h`
        // (swos.asm:129744 / 37754; teams.txt:848). So a hacked nibble of 8
        // plays as 0 and F as 7 — a wrap-around, not a clamp. We decode with
        // the same mod-8 semantics -> range 0..7.
        //
        // PRIOR BUG (task #196): decoded as (v & 0x07) + 1 -> 1..8, which was
        // off by one against the engine's own reading of the same bytes.
        static int Skill(int nibble) => (nibble & 0x0F) % 8;
        int passing  = Skill(rec[0x1C]);
        int shooting = Skill(rec[0x1D] >> 4);
        int heading  = Skill(rec[0x1D]);
        int tackling = Skill(rec[0x1E] >> 4);
        int control  = Skill(rec[0x1E]);
        int speed    = Skill(rec[0x1F] >> 4);
        int finishing= Skill(rec[0x1F]);

        byte valueCode = rec[0x20];

        return new Player(nationality, shirt, name, posByte, face,
            passing, shooting, heading, tackling, control, speed, finishing, valueCode);
    }

    private static string ReadNullPadded(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        return Encoding.Latin1.GetString(bytes[..end]).TrimEnd();
    }

    public static string DecodePosition(byte posByte)
    {
        int pos = posByte >> 4;
        if ((pos & 1) != 0) pos--;
        return pos switch
        {
            0x0 => "G",
            0x2 => "RB",
            0x4 => "LB",
            0x6 => "D",
            0x8 => "RW",
            0xA => "LW",
            0xC => "M",
            0xE => "A",
            _ => "?",
        };
    }

    public static string DecodeTactics(byte t) => t switch
    {
        0x0 => "4-4-2", 0x1 => "5-4-1", 0x2 => "4-5-1", 0x3 => "5-3-2", 0x4 => "3-5-2",
        0x5 => "4-3-3", 0x6 => "4-2-4", 0x7 => "3-4-3", 0x8 => "Sweep", 0x9 => "5-2-3",
        0xA => "Attack", 0xB => "Defend",
        0xC => "User A", 0xD => "User B", 0xE => "User C", 0xF => "User D",
        0x10 => "User E", 0x11 => "User F",
        _ => $"?(0x{t:X2})",
    };

    public static string DecodeDivision(byte d) => d switch
    {
        0 => "Premier", 1 => "First", 2 => "Second", 3 => "Third", 4 => "Non-league",
        _ => $"?({d})",
    };

    public static string DecodeKitColour(byte c) => c switch
    {
        0 => "grey", 1 => "white", 2 => "black", 3 => "orange", 4 => "red",
        5 => "blue", 6 => "brown", 7 => "lightblue", 8 => "green", 9 => "yellow",
        _ => $"?({c})",
    };

    public static string DecodeKitType(byte t) => t switch
    {
        0 => "plain", 1 => "sleeves", 2 => "vertical", 3 => "horizontal",
        _ => $"?({t})",
    };

    public static string FormatKit(byte[] kit)
    {
        if (kit.Length >= 5)
            return $"{DecodeKitType(kit[0])}/{DecodeKitColour(kit[1])}/{DecodeKitColour(kit[2])}/{DecodeKitColour(kit[3])}/{DecodeKitColour(kit[4])}";
        if (kit.Length >= 3)
            return $"{DecodeKitType(kit[0])}/{DecodeKitColour(kit[1])}/{DecodeKitColour(kit[2])}";
        return "(empty)";
    }
}
