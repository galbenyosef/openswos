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
    public List<PlayerRecord> Players { get; init; } = new();
}

public sealed class PlayerRecord
{
    public byte Nationality { get; init; }
    public byte ShirtNumber { get; init; }
    public string Name { get; init; } = "";
    public string Position { get; init; } = "";
    public int Passing { get; init; }
    public int Shooting { get; init; }
    public int Heading { get; init; }
    public int Tackling { get; init; }
    public int Control { get; init; }
    public int Speed { get; init; }
    public int Finishing { get; init; }
}
