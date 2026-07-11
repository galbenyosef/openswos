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
        HomeKit = t.HomeKit,
        AwayKit = t.AwayKit,
        GoalkeeperKit = SynthesizeGoalkeeperKit(t.HomeKit),
        LineupOrder = t.PlayerDisplayOrder,
        Players = t.Players.Select(From).ToList(),
    };

    // SWOS keeper colour rule (swos-port colorizeSprites.cpp:270-275): keeper SHORTS
    // and SOCKS use the team's primary shorts/socks colours. The kSkinColor[face] /
    // kHairColor[face] entries in that same layer list recolour the keeper's SKIN and
    // HAIR from his face type (KitPalette.ApplyFace with the keeper's
    // PlayerRecord.Face), not his jersey — the jersey colour is baked into the keeper
    // art, which we approximate with solid yellow (9), the dominant SWOS keeper colour.
    //
    // homeKit is now the canonical SWOS layout {shirtType, stripesColor,
    // basicColor, shortsColor, socksColor} (parser fixed for task #185), so
    // shorts = homeKit[3], socks = homeKit[4].
    private static byte[] SynthesizeGoalkeeperKit(byte[] homeKit)
    {
        const byte Yellow = 9;
        byte shorts = (homeKit.Length > 3) ? homeKit[3] : Yellow;
        byte socks  = (homeKit.Length > 4) ? homeKit[4] : Yellow;
        // type=1 (sleeves): KitPalette makes body=stripes, sleeves=basic — both
        // yellow here → solid yellow torso. shorts/socks from the team primary.
        return new byte[] { 1, Yellow, Yellow, shorts, socks };
    }

    public static PlayerRecord From(Player p) => new()
    {
        Nationality = p.Nationality,
        ShirtNumber = p.ShirtNumber,
        Name = p.Name,
        Position = TeamFile.DecodePosition(p.PositionByte),
        Face = p.Face,
        Passing = p.Passing,
        Shooting = p.Shooting,
        Heading = p.Heading,
        Tackling = p.Tackling,
        Control = p.Control,
        Speed = p.Speed,
        Finishing = p.Finishing,
        ValueCode = p.ValueCode,
    };
}
