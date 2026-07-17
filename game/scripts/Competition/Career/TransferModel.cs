namespace OpenSwos.Competition.Career;

/// <summary>
/// Deterministic roster movement for the career transfer market.  This model
/// deliberately has no UI or engine dependencies, so a failed transaction can
/// leave the saved world exactly as it was.
/// </summary>
public static class TransferModel
{
    public const int SortValue = 0;
    public const int SortEffectiveOverall = 1;
    public const int SortAge = 2;

    /// <summary>
    /// The selling club's asking price.  Every player starts at a 25% premium;
    /// promising young, high-overall players attract a small additional premium.
    /// </summary>
    public static long AskingPrice(CareerPlayer p)
    {
        if (p is null) throw new System.ArgumentNullException(nameof(p));

        long value = Finance.PlayerValue(p);
        int youthAndAbilityPremium = p.Age <= 23
            ? (int)System.Math.Clamp(24L - p.Age, 0L, 7L) + p.EffectiveOverall()
            : 0;
        int premiumPercent = 25 + youthAndAbilityPremium;
        return value + value * premiumPercent / 100L;
    }

    public static bool Buy(CareerWorld world, ushort buyerId, int playerId, long? overridePrice = null)
    {
        if (world?.Clubs is null || buyerId == 0
            || !world.Clubs.TryGetValue(buyerId, out CareerClub? buyer)
            || IsNationalTeam(world, buyerId)
            || buyer.Squad is null || buyer.Squad.Count >= 22)
            return false;

        CareerPlayer? player = null;
        CareerClub? seller = null;
        ushort sellerId = 0;
        bool freeAgent = false;
        var freeAgents = world.FreeAgents;

        // A player must be present in the collection their ClubId declares.
        // This avoids repairing corrupt saves by silently moving an orphan.
        if (freeAgents is not null)
        {
            foreach (CareerPlayer? candidate in freeAgents)
            {
                if (candidate?.Id != playerId || candidate.ClubId != 0) continue;
                player = candidate;
                freeAgent = true;
                break;
            }
        }

        if (player is null)
        {
            foreach (var entry in world.Clubs)
            {
                CareerClub? candidateClub = entry.Value;
                if (candidateClub?.Squad is null) continue;
                foreach (CareerPlayer? candidate in candidateClub.Squad)
                {
                    if (candidate?.Id != playerId || candidate.ClubId != entry.Key) continue;
                    player = candidate;
                    seller = candidateClub;
                    sellerId = entry.Key;
                    break;
                }
                if (player is not null) break;
            }
        }

        if (player is null || (!freeAgent && (seller is null || sellerId == buyerId
            || IsNationalTeam(world, sellerId))))
            return false;

        // A caller can negotiate a specific price (the accepted bid); otherwise
        // the seller's standard asking price applies.
        long price = overridePrice ?? AskingPrice(player);
        if (price < 0 || buyer.Budget < price) return false;

        byte shirt = FindFreeShirtNumber(buyer.Squad);
        if (shirt == 0) return false;

        // Complete every overflow-sensitive calculation before mutating a list.
        long newBuyerBudget;
        long newSellerBudget = 0;
        try
        {
            newBuyerBudget = checked(buyer.Budget - price);
            if (!freeAgent) newSellerBudget = checked(seller!.Budget + price);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (freeAgent) freeAgents!.Remove(player);
        else seller!.Squad.Remove(player);

        buyer.Budget = newBuyerBudget;
        if (!freeAgent) seller!.Budget = newSellerBudget;
        player.ClubId = buyerId;
        player.ShirtNumber = shirt;
        buyer.Squad.Add(player);
        return true;
    }

    public static bool Sell(CareerWorld world, ushort sellerId, int playerId)
    {
        if (world?.Clubs is null || world.FreeAgents is null || sellerId == 0
            || !world.Clubs.TryGetValue(sellerId, out CareerClub? seller)
            || IsNationalTeam(world, sellerId)
            || seller.Squad is null || seller.Squad.Count <= 12)
            return false;

        CareerPlayer? player = null;
        foreach (CareerPlayer? candidate in seller.Squad)
        {
            if (candidate?.Id == playerId && candidate.ClubId == sellerId)
            {
                player = candidate;
                break;
            }
        }
        if (player is null) return false;

        long value = Finance.PlayerValue(player);
        long newBudget;
        try { newBudget = checked(seller.Budget + value); }
        catch (OverflowException) { return false; }

        seller.Squad.Remove(player);
        seller.Budget = newBudget;
        player.ClubId = 0;
        player.ShirtNumber = 0;
        world.FreeAgents.Add(player);
        return true;
    }

    /// <summary>
    /// Moves a player from one club to another at a negotiated amount (used by
    /// the transfer-offer flow when the manager accepts a rival's bid). The
    /// selling club is credited; the buying club is debited. Leaves the world
    /// untouched on any failure.
    /// </summary>
    public static bool SellToClub(CareerWorld world, ushort sellerId, ushort buyerId, int playerId, long amount)
    {
        if (world?.Clubs is null || sellerId == 0 || buyerId == 0 || sellerId == buyerId
            || IsNationalTeam(world, sellerId) || IsNationalTeam(world, buyerId)
            || !world.Clubs.TryGetValue(sellerId, out CareerClub? seller)
            || !world.Clubs.TryGetValue(buyerId, out CareerClub? buyer)
            || seller?.Squad is null || buyer?.Squad is null
            || seller.Squad.Count <= 12 || buyer.Squad.Count >= 22)
            return false;

        CareerPlayer? player = null;
        foreach (CareerPlayer? candidate in seller.Squad)
            if (candidate?.Id == playerId && candidate.ClubId == sellerId) { player = candidate; break; }
        if (player is null || amount < 0 || buyer.Budget < amount) return false;

        byte shirt = FindFreeShirtNumber(buyer.Squad);
        if (shirt == 0) return false;

        long newSellerBudget, newBuyerBudget;
        try
        {
            newSellerBudget = checked(seller.Budget + amount);
            newBuyerBudget = checked(buyer.Budget - amount);
        }
        catch (OverflowException) { return false; }

        seller.Squad.Remove(player);
        seller.Budget = newSellerBudget;
        buyer.Budget = newBuyerBudget;
        player.ClubId = buyerId;
        player.ShirtNumber = shirt;
        buyer.Squad.Add(player);
        return true;
    }

    /// <summary>
    /// Releases a player from a club on a free transfer (no fee): the player
    /// leaves the squad and joins the free-agent pool.
    /// </summary>
    public static bool Release(CareerWorld world, ushort sellerId, int playerId)
    {
        if (world?.Clubs is null || world.FreeAgents is null || sellerId == 0
            || IsNationalTeam(world, sellerId)
            || !world.Clubs.TryGetValue(sellerId, out CareerClub? seller)
            || seller?.Squad is null || seller.Squad.Count <= 12)
            return false;

        CareerPlayer? player = null;
        foreach (CareerPlayer? candidate in seller.Squad)
            if (candidate?.Id == playerId && candidate.ClubId == sellerId) { player = candidate; break; }
        if (player is null) return false;

        seller.Squad.Remove(player);
        player.ClubId = 0;
        player.ShirtNumber = 0;
        world.FreeAgents.Add(player);
        return true;
    }

    public static System.Collections.Generic.List<CareerPlayer> Market(
        CareerWorld world, ushort excludeClub, int sortMode,
        long minPrice = 0, long maxPrice = long.MaxValue)
    {
        var result = new System.Collections.Generic.List<CareerPlayer>();
        if (world is null) return result;

        if (world.FreeAgents is not null)
        {
            foreach (CareerPlayer? player in world.FreeAgents)
            {
                if (player is null || player.ClubId != 0 || player.Retired) continue;
                long ask = AskingPrice(player);
                if (ask >= minPrice && ask < maxPrice) result.Add(player);
            }
        }

        if (world.Clubs is not null)
        {
            var ids = new System.Collections.Generic.List<ushort>(world.Clubs.Keys);
            ids.Sort();
            foreach (ushort clubId in ids)
            {
                if (clubId == excludeClub || IsNationalTeam(world, clubId)) continue;
                CareerClub? club = world.Clubs[clubId];
                if (club?.Squad is null) continue;
                foreach (CareerPlayer? player in club.Squad)
                {
                    if (player is null || player.ClubId != clubId || player.Retired) continue;
                    long ask = AskingPrice(player);
                    if (ask >= minPrice && ask < maxPrice) result.Add(player);
                }
            }
        }

        // Decorate-sort-undecorate: compute each player's expensive sort key
        // (Finance.PlayerValue / EffectiveOverall) ONCE, then sort the
        // lightweight (key, player) pairs. The old comparator recomputed those
        // for every comparison — ~1.6M calls across the ~27.6k-player pool —
        // which froze the transfer/scout screens. Name/Id remain cheap
        // tie-breaks so market pages stay reproducible across loads.
        int n = result.Count;
        var keyed = new (long primary, CareerPlayer player)[n];
        for (int i = 0; i < n; i++)
        {
            CareerPlayer p = result[i];
            long primary = sortMode switch
            {
                SortEffectiveOverall => -(long)p.EffectiveSkillSum(),
                SortAge => p.Age,
                _ => -Finance.PlayerValue(p),
            };
            keyed[i] = (primary, p);
        }
        System.Array.Sort(keyed, static (a, b) =>
        {
            int cmp = a.primary.CompareTo(b.primary);
            if (cmp != 0) return cmp;
            int name = string.Compare(a.player.Name, b.player.Name, System.StringComparison.Ordinal);
            return name != 0 ? name : a.player.Id.CompareTo(b.player.Id);
        });

        result.Clear();
        for (int i = 0; i < n; i++) result.Add(keyed[i].player);
        return result;
    }

    private static bool IsNationalTeam(CareerWorld world, ushort teamId)
        => world.NationalTeamIds?.Contains(teamId) == true;

    private static byte FindFreeShirtNumber(System.Collections.Generic.List<CareerPlayer> squad)
    {
        var used = new bool[100];
        foreach (CareerPlayer? player in squad)
            if (player is not null && player.ShirtNumber is >= 1 and <= 99)
                used[player.ShirtNumber] = true;
        for (int number = 1; number <= 99; number++)
            if (!used[number]) return (byte)number;
        return 0;
    }
}
