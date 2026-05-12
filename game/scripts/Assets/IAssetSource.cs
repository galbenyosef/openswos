namespace OpenSwos.Assets;

// Boundary between gameplay code and variant-specific asset storage. PC and
// Amiga sources implement this; game code must never branch on variant directly.
public interface IAssetSource
{
    GameVariant Variant { get; }

    IEnumerable<TeamRecord> LoadAllTeams();
    IEnumerable<TeamRecord> LoadTeamsForNation(int nation);
}
