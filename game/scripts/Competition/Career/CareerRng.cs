namespace OpenSwos.Competition.Career;

/// <summary>
/// Small deterministic random-number generator keyed to a career seed and a
/// stable player id. It never advances the competition match/draw RNG stream.
/// </summary>
public struct CareerRng
{
    private uint _state;

    public CareerRng(uint baseSeed, int playerId)
    {
        _state = baseSeed ^ SplitMix(unchecked((uint)playerId));
        if (_state == 0) _state = 0x9E3779B9u;
    }

    /// <summary>Returns the next xorshift32 value.</summary>
    public uint NextU()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    public int NextInt(int maxExclusive)
        => maxExclusive <= 1 ? 0 : (int)(NextU() % (uint)maxExclusive);

    /// <summary>Returns a 24-bit-mantissa uniform value in [0, 1).</summary>
    public double NextDouble()
        => (NextU() >> 8) * (1.0 / 16777216.0);

    public int Range(int loInclusive, int hiInclusive)
    {
        if (hiInclusive < loInclusive)
            throw new System.ArgumentException("INVALID RANGE");
        return loInclusive + NextInt(hiInclusive - loInclusive + 1);
    }

    /// <summary>Returns the non-zero seed used to key career attributes.</summary>
    public static uint SeedFrom(CompetitionState s)
        => s.RngState == 0 ? 0x9E3779B9u : s.RngState;

    private static uint SplitMix(uint value)
    {
        value += 0x9E3779B9u;
        value = (value ^ (value >> 16)) * 0x85EBCA6Bu;
        value = (value ^ (value >> 13)) * 0xC2B2AE35u;
        return value ^ (value >> 16);
    }
}
