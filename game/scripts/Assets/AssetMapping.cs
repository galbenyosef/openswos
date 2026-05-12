using OpenSwos.Tools.TeamDecode;

namespace OpenSwos.Assets;

// Maps raw-format records (from the shared parsers in tools/) into the
// variant-neutral types game code consumes. Lives in one place so PC and Amiga
// sources stay in lockstep when the public TeamRecord shape evolves.
internal static class AssetMapping
{
    public static TeamRecord From(Team t) => new()
    {
        Nation = t.Nation,
        GlobalId = t.GlobalTeamId,
        Name = t.Name,
        Coach = t.Coach,
        Tactics = t.Tactics,
        Division = t.Division,
        Players = t.Players.Select(From).ToList(),
    };

    public static PlayerRecord From(Player p) => new()
    {
        Nationality = p.Nationality,
        ShirtNumber = p.ShirtNumber,
        Name = p.Name,
        Position = TeamFile.DecodePosition(p.PositionByte),
        Passing = p.Passing,
        Shooting = p.Shooting,
        Heading = p.Heading,
        Tackling = p.Tackling,
        Control = p.Control,
        Speed = p.Speed,
        Finishing = p.Finishing,
    };
}
