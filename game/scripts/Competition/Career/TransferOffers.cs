namespace OpenSwos.Competition.Career;

// ============================================================================
// ORIGINAL SWOS career transfer flow, reconstructed from swos.asm (constants
// copied — the algorithm paraphrased into our CareerWorld model).
//
// Rival clubs periodically bid for the manager's players. Each bid carries up
// to two pre-authorised ESCALATIONS: when you "demand more" the club walks its
// bid up to the next escalation (or walks away). Transfer-listing a player makes
// him far more likely to attract offers. Selling and buying are both gated by
// soft per-season quotas, and buying additionally spends a TimeToNegotiate
// budget (6 per season).
//
// Determinism: every Tick draws a fresh CareerRng keyed from the career seed and
// a monotonic tick salt, so results replay identically and the competition
// match/draw RNG stream is never touched. NO System.Random anywhere.
//
// Cited constants (swos.asm):
//   :127226  TimeToNegotiate = 6 per season
//   :238026  offer expiry table {1,1,1,2,2,2,2,2,2,3,3,3,3,4,5,6}, index rng&15
//   :78060   per-player offer chance: listed 11/256, otherwise 1/256
//   :78230   bid amount   = PlayerValue * (0x55 + rng*0x23/256)% = 85..119 %
//   :78270   escalation   = previous  * (0x69 + rng*0x14/256)% = 105..124 %, 50%
// ============================================================================

/// <summary>A rival club's standing bid for one of the manager's players.</summary>
public sealed class TransferOffer
{
    public ushort BidderClubId { get; set; }   // TEAM.* GlobalId of the bidding club
    public int PlayerId { get; set; }          // CareerPlayer.Id being bid for
    public long Amount { get; set; }           // current bid
    public long Escalation1 { get; set; }      // next improved bid, 0 = none
    public long Escalation2 { get; set; }      // final improved bid, 0 = none
    public int ExpiryRounds { get; set; }      // rounds remaining before withdrawal
    public bool Seen { get; set; }             // false = unseen (dashboard "!" flag)
}

public enum DemandOutcome { NotFound, Improved, Withdrawn, Refused }

public static class TransferOffers
{
    public const int MaxPendingOffers = 5;
    public const int MaxTransferListed = 5;
    public const int SellQuotaPerSeason = 6;
    public const int BuyQuotaPerSeason = 6;
    public const int TimeToNegotiatePerSeason = 6;   // swos.asm:127226

    // swos.asm:238026 — expiry seed table, indexed by rng & 15.
    private static readonly int[] ExpiryTable =
        { 1, 1, 1, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 4, 5, 6 };

    /// <summary>
    /// Called ONCE per player-round advance (career only). Ages existing offers,
    /// prunes withdrawn/stale ones, then — unless the sell quota is spent — sweeps
    /// the manager's squad and may generate new bids from rival league clubs.
    /// </summary>
    public static void Tick(CompetitionState c)
    {
        if (c is null || c.Kind != CompetitionKind.Career) return;
        var career = c.Career;
        var world = career?.World;
        if (career is null || world?.Clubs is null) return;
        ushort clubId = career.ClubGlobalId;
        if (clubId == 0 || !world.Clubs.TryGetValue(clubId, out var club) || club?.Squad is null)
            return;

        career.PendingOffers ??= new System.Collections.Generic.List<TransferOffer>();
        career.TransferListedPlayerIds ??= new System.Collections.Generic.List<int>();

        // --- age + prune ------------------------------------------------------
        for (int i = career.PendingOffers.Count - 1; i >= 0; i--)
        {
            var o = career.PendingOffers[i];
            o.ExpiryRounds--;
            bool stale = o.ExpiryRounds <= 0
                || !world.Clubs.ContainsKey(o.BidderClubId)
                || club.Squad.Find(p => p is not null && p.Id == o.PlayerId) is null;
            if (stale) career.PendingOffers.RemoveAt(i);
        }

        // Sell quota spent -> no new offers this season.
        if (career.SellsThisSeason >= SellQuotaPerSeason) return;
        if (career.PendingOffers.Count >= MaxPendingOffers) return;

        // Fresh deterministic stream for this tick.
        var rng = new CareerRng(CareerRng.SeedFrom(c), unchecked(0x0FFE5 ^ career.TransferTicks));
        career.TransferTicks++;

        int n = club.Squad.Count;
        if (n == 0) return;
        int start = rng.NextInt(n);
        for (int step = 0; step < n && career.PendingOffers.Count < MaxPendingOffers; step++)
        {
            var player = club.Squad[(start + step) % n];
            if (player is null || player.Retired) continue;
            // One live offer per player at a time keeps the list readable.
            if (career.PendingOffers.Exists(o => o.PlayerId == player.Id)) continue;

            bool listed = career.TransferListedPlayerIds.Contains(player.Id);
            int threshold = listed ? 11 : 1;                 // swos.asm:78060
            if (rng.NextInt(256) >= threshold) continue;

            long value = Finance.PlayerValue(player);
            if (value <= 0) continue;

            // amount = value * (85..119)%   (swos.asm:78230)
            int amtPct = 0x55 + rng.NextInt(256) * 0x23 / 256;
            long amount = value * amtPct / 100L;
            if (amount <= 0) continue;

            ushort bidder = PickBidder(c, world, clubId, amount, ref rng);
            if (bidder == 0) continue;

            // escalations: 50% each, previous * (105..124)%  (swos.asm:78270)
            long esc1 = 0, esc2 = 0;
            if ((rng.NextInt(2)) == 1)
            {
                int e1Pct = 0x69 + rng.NextInt(256) * 0x14 / 256;
                esc1 = amount * e1Pct / 100L;
                if (esc1 <= amount) esc1 = amount + 1;
                if ((rng.NextInt(2)) == 1)
                {
                    int e2Pct = 0x69 + rng.NextInt(256) * 0x14 / 256;
                    esc2 = esc1 * e2Pct / 100L;
                    if (esc2 <= esc1) esc2 = esc1 + 1;
                }
            }

            career.PendingOffers.Add(new TransferOffer
            {
                BidderClubId = bidder,
                PlayerId = player.Id,
                Amount = amount,
                Escalation1 = esc1,
                Escalation2 = esc2,
                ExpiryRounds = ExpiryTable[rng.NextInt(16)],   // swos.asm:238026
                Seen = false,
            });
        }
    }

    // Random rival club from the COMPETITION's entrants (in-league bidders),
    // value-sane: the bidder must be able to afford the amount.
    private static ushort PickBidder(
        CompetitionState c, CareerWorld world, ushort ownClub, long amount, ref CareerRng rng)
    {
        var candidates = new System.Collections.Generic.List<ushort>();
        if (c.Teams is not null)
            foreach (var t in c.Teams)
            {
                ushort gid = t.GlobalId;
                if (gid == 0 || gid == ownClub) continue;
                if (world.NationalTeamIds?.Contains(gid) == true) continue;
                if (!world.Clubs.TryGetValue(gid, out var rival) || rival is null) continue;
                if (rival.Budget < amount) continue;
                if (rival.Squad is null || rival.Squad.Count >= 22) continue;
                candidates.Add(gid);
            }
        if (candidates.Count == 0) return 0;
        return candidates[rng.NextInt(candidates.Count)];
    }

    /// <summary>
    /// Accepts a standing bid: moves the player to the bidder, credits the amount,
    /// spends one TimeToNegotiate, bumps the sell quota, and removes the offer.
    /// </summary>
    public static bool Accept(CompetitionState c, CareerWorld world, TransferOffer offer)
    {
        var career = c?.Career;
        if (career is null || world is null || offer is null) return false;
        if (!TransferModel.SellToClub(world, career.ClubGlobalId, offer.BidderClubId, offer.PlayerId, offer.Amount))
            return false;
        career.PendingOffers?.Remove(offer);
        career.TransferListedPlayerIds?.Remove(offer.PlayerId);
        if (career.TimeToNegotiate > 0) career.TimeToNegotiate--;
        career.SellsThisSeason++;
        return true;
    }

    /// <summary>
    /// "Demand more": if the club has an unused escalation it improves its bid;
    /// otherwise it either withdraws or refuses — the offer is removed either way.
    /// </summary>
    public static DemandOutcome RejectDemandMore(CompetitionState c, TransferOffer offer)
    {
        var career = c?.Career;
        if (career?.PendingOffers is null || offer is null || !career.PendingOffers.Contains(offer))
            return DemandOutcome.NotFound;

        if (offer.Escalation1 != 0)
        {
            offer.Amount = offer.Escalation1;
            offer.Escalation1 = offer.Escalation2;
            offer.Escalation2 = 0;
            return DemandOutcome.Improved;
        }
        if (offer.Escalation2 != 0)
        {
            offer.Amount = offer.Escalation2;
            offer.Escalation2 = 0;
            return DemandOutcome.Improved;
        }

        // No more room to move: 50/50 walk away vs flat refusal — both remove it.
        var rng = new CareerRng(CareerRng.SeedFrom(c!), unchecked(offer.PlayerId ^ (int)offer.BidderClubId));
        bool withdrawn = rng.NextInt(2) == 0;
        career.PendingOffers.Remove(offer);
        return withdrawn ? DemandOutcome.Withdrawn : DemandOutcome.Refused;
    }

    /// <summary>Transfer-lists a squad player (cap 5).</summary>
    public static bool ListPlayer(CompetitionState c, int playerId)
    {
        var career = c?.Career;
        if (career is null) return false;
        career.TransferListedPlayerIds ??= new System.Collections.Generic.List<int>();
        if (career.TransferListedPlayerIds.Contains(playerId)) return true;
        if (career.TransferListedPlayerIds.Count >= MaxTransferListed) return false;
        career.TransferListedPlayerIds.Add(playerId);
        return true;
    }

    /// <summary>Removes a player from the transfer list.</summary>
    public static bool UnlistPlayer(CompetitionState c, int playerId)
        => c?.Career?.TransferListedPlayerIds?.Remove(playerId) == true;

    public static bool IsListed(CompetitionState c, int playerId)
        => c?.Career?.TransferListedPlayerIds?.Contains(playerId) == true;

    /// <summary>Releases a player on a free transfer: no fee, squad -> free agents.</summary>
    public static bool FreeTransfer(CompetitionState c, CareerWorld world, int playerId)
    {
        var career = c?.Career;
        if (career is null || world is null) return false;
        if (!TransferModel.Release(world, career.ClubGlobalId, playerId)) return false;
        career.TransferListedPlayerIds?.Remove(playerId);
        for (int i = (career.PendingOffers?.Count ?? 0) - 1; i >= 0; i--)
            if (career.PendingOffers![i].PlayerId == playerId) career.PendingOffers.RemoveAt(i);
        return true;
    }

    /// <summary>Season reset: clear offers/list and refill the negotiation budget.</summary>
    public static void ResetForNewSeason(CareerState career)
    {
        if (career is null) return;
        career.PendingOffers ??= new System.Collections.Generic.List<TransferOffer>();
        career.TransferListedPlayerIds ??= new System.Collections.Generic.List<int>();
        career.PendingOffers.Clear();
        career.TransferListedPlayerIds.Clear();
        career.TimeToNegotiate = TimeToNegotiatePerSeason;
        career.SellsThisSeason = 0;
        career.BuysThisSeason = 0;
    }

    /// <summary>True when any pending offer has not yet been viewed.</summary>
    public static bool HasUnseenOffers(CompetitionState c)
    {
        var offers = c?.Career?.PendingOffers;
        if (offers is null) return false;
        foreach (var o in offers) if (o is not null && !o.Seen) return true;
        return false;
    }
}
