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
//   +0x00  u8   nationality
//   +0x01  u8   reserved (0)
//   +0x02  u8   shirt number
//   +0x03  23   name (ASCII, null-padded)
//   +0x1A  u8   position + appearance byte (high nibble = position, rounded even)
//   +0x1C  u8   skill byte 1: bits 0..2 = passing - 1
//   +0x1D  u8   skill byte 2: bits 4..6 = shooting - 1, bits 0..2 = heading - 1
//   +0x1E  u8   skill byte 3: bits 4..6 = tackling - 1, bits 0..2 = control - 1
//   +0x1F  u8   skill byte 4: bits 4..6 = speed - 1,    bits 0..2 = finishing - 1
//   +0x20  u8   value code (0..47, lookup table to monetary value in thousands)

public sealed record Player(
    byte Nationality,
    byte ShirtNumber,
    string Name,
    byte PositionByte,
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
        byte division = block[0x19];
        byte tactics = block[0x1A];
        // Verified empirically against TEAM.008 ARSENAL — coach starts at +0x24 (not +0x26 as
        // ysoccer claims). Kit layout: home 5 bytes at +0x1C..+0x20, then a short away
        // segment, then coach. 3-byte away kit best fits the data.
        var homeKit = block.Slice(0x1C, 5).ToArray();
        var awayKit = block.Slice(0x21, 3).ToArray();
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

        // 7 skills packed in bytes 0x1C..0x1F as 3-bit nibbles (mask 0x07), +1 -> range 1..8.
        int passing  =  (rec[0x1C]       & 0x07) + 1;
        int shooting = ((rec[0x1D] >> 4) & 0x07) + 1;
        int heading  =  (rec[0x1D]       & 0x07) + 1;
        int tackling = ((rec[0x1E] >> 4) & 0x07) + 1;
        int control  =  (rec[0x1E]       & 0x07) + 1;
        int speed    = ((rec[0x1F] >> 4) & 0x07) + 1;
        int finishing=  (rec[0x1F]       & 0x07) + 1;

        byte valueCode = rec[0x20];

        return new Player(nationality, shirt, name, posByte,
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
