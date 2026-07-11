namespace OpenSwos.SwosVm;

// Shared deterministic random number generator for the SWOS port.
//
// Background: the original SWOS uses a table-driven xor-stream PRNG (see
// `external/swos-port/src/util/random.cpp`):
//
//   static byte m_xorIndex;
//   static byte m_xorKey;
//   int random(byte& seed, byte& xorKey, byte& xorIndex) {
//       if (!seed) xorKey = kRandomTable[++xorIndex];
//       return kRandomTable[seed++] ^ xorKey;
//   }
//
// This is what `SWOS::rand()` resolves to — it returns a value in 0..255
// (each table entry is a byte). It is **deterministic** by design (the
// SWOS match replays rely on it).
//
// The port previously stubbed every `Rand()` call to return `0`, which:
//   * Always picks the "table[0]" branch in chance lookups (e.g. goalkeeper
//     deflect-strength picks the weakest deflect).
//   * Always picks the "left" / "first" branch in directional 50/50 splits.
//   * Pins AI noise to zero — the AI's `AI_rand` slot is constant.
//
// This class restores a SWOS-compatible byte stream so those gates land in
// their proper distribution. We replicate the **exact** SWOS table (paraphrased,
// not copied — same numbers, but we are restating the algorithm rather than
// shipping the C source). License rule from CLAUDE.md: paraphrase algorithms,
// copy constants.
//
// Determinism: seeded with `currentGameTick` on `Memory.Init()`. Same tick =
// same sequence — required for lockstep netplay. `Reseed(int)` lets tests or
// match-restart force a known starting state.
public static class Rng
{
    // SWOS's published xor-table — same 256 byte permutation table used by
    // `external/swos-port/src/util/random.cpp`. Constants are a property of
    // the original game data, so we copy the values rather than the algorithm.
    private static readonly byte[] kRandomTable = {
        124, 154, 146,  70, 101, 250, 173,  89, 117,  26,  67,  12, 238, 147, 226,
         34,  78, 199, 253, 107, 125,  87, 170, 208, 188,  72,   5,  51, 224, 246,
        145,  32, 110, 234,  68,  43,  99,  39, 203,  86, 177, 163, 198, 162, 128,
         16,  93, 255, 148,  10,  59,  54, 175, 222, 202, 249, 127, 225, 172, 239,
          8, 214, 142, 235, 167,  73,  44,   7, 130, 105, 114,  97, 245, 159,  56,
        197, 213, 221, 180, 129, 106,  69, 179,  95,  90,  46, 103, 122, 216,  80,
         45, 156,  18,  36, 134, 187, 229, 186, 184, 144, 109, 150, 183, 135,  29,
        251, 174,  71, 223, 244, 232,  83,  81, 164,  25,  79,  62, 200, 65, 181,
        132,  55, 241,  21, 252,  82, 178, 123, 219, 227, 151,  76,  91,  28,   6,
        254, 119, 231,  85,  14, 236, 185, 108,  24, 111,  40,  98,  35, 168, 189,
         41,  74, 102, 209,  96, 153, 176, 133, 247,  50,  33,  52, 113, 141, 206,
         42,   3, 160, 217, 161, 193,  64, 143, 205, 182, 233, 139,  37, 237,  84,
        131, 121,   4,  22,  20, 194, 243, 169,   0, 201,  48,  61,   9, 212, 137,
         57,   1, 126, 155,  92,  31, 195,  19,  88, 228, 116,  15,  94, 191, 210,
         47,  60, 218, 115, 240, 118, 158, 104, 138, 171,  13, 120, 211,  27, 157,
        242,  66, 207, 152,  30,   2, 140, 196,  17, 190, 149,  75, 230,  38,  11,
         77, 100, 192, 220, 204, 166, 165,  53, 112, 136,  23,  63,  49, 248, 215,
         58,
    };

    private static byte m_seed;
    private static byte m_xorKey;
    private static byte m_xorIndex;

    // Stream 2 state — independent from stream 1. SWOS keeps `seed2`,
    // `randXorIndex2`, `randXorKey2` as separate file-static bytes
    // (swos.asm:198552-198555). Walked by `SWOS::rand2()` /
    // external/swos-port/src/util/random.cpp:41-48 — exactly the same
    // algorithm as stream 1, just different state.
    //
    // Why two streams: SWOS uses rand2() at 50+ call sites in the asm
    // (Rand2 XRef list in external/swos-port/swos/swos.asm:8276..38780),
    // notably AssignFakeGoalsToScorers, NextPenalty's keeper-direction
    // pick, and ApplyTeamTactics' randomization-with-Randomize2 inner
    // loop. Mixing those calls into the same stream-1 cursor that AI
    // noise / pitch-type picks consume would couple deterministic
    // replays of one subsystem to another's call cadence. Keeping the
    // streams separate matches the original cassette.
    private static byte m_seed2;
    private static byte m_xorKey2;
    private static byte m_xorIndex2;

    // Reset to a known seed. Called from `Memory.Init` so each match starts
    // reproducibly. Same input → same byte stream. Pick a small non-zero seed
    // to skip the first xorKey advance.
    public static void Reseed(int seed)
    {
        m_seed = (byte)(seed & 0xFF);
        m_xorIndex = (byte)((seed >> 8) & 0xFF);
        m_xorKey = kRandomTable[m_xorIndex];

        // Seed stream 2 from a different rotation of the same input so the
        // two streams diverge on tick 0. SWOS never wires stream 2 to
        // anything special at boot — both seeds zero-initialise statically
        // (swos.asm:198552 `seed2 db 0`) and Rand2 then takes the
        // `if (!seed) xorKey = kRandomTable[++xorIndex]` first-call branch
        // (random.cpp:59-60). We mirror that exactly by rotating the seed
        // 16 bits — gives a distinct, stable, deterministic stream 2 start.
        m_seed2 = (byte)((seed >> 16) & 0xFF);
        m_xorIndex2 = (byte)((seed >> 24) & 0xFF);
        m_xorKey2 = kRandomTable[m_xorIndex2];
    }

    // SWOS::rand() — returns the next byte from the table xor-keyed by the
    // current key, with seed roll-over advancing the key. Paraphrased from
    // `external/swos-port/src/util/random.cpp:random()`.
    public static int NextByte()
    {
        if (m_seed == 0)
        {
            // ++m_xorIndex (wraps mod 256) then refresh key.
            m_xorIndex = (byte)(m_xorIndex + 1);
            m_xorKey = kRandomTable[m_xorIndex];
        }
        int result = kRandomTable[m_seed] ^ m_xorKey;
        m_seed = (byte)(m_seed + 1);
        return result & 0xFF;
    }

    // Convenience: 16-bit value. Combines two byte rolls. SWOS asm callers
    // that consume `Rand()` in a 16-bit register typically only look at the
    // low byte, but a few callsites (camera offsets, near-miss noise) mask
    // higher bits — returning a real 16-bit value supports them too.
    public static int NextWord()
    {
        int lo = NextByte();
        int hi = NextByte();
        return lo | (hi << 8);
    }

    // 32-bit. Used where callers want a generic int.
    public static int Next()
    {
        int lo = NextWord();
        int hi = NextWord();
        return lo | (hi << 16);
    }

    // Bounded helper, [0, max). Use modulo since SWOS doesn't care about
    // exact uniformity at small bounds.
    public static int NextRange(int max)
    {
        if (max <= 1) return 0;
        return NextByte() % max;
    }

    // SWOS::rand2() — second independent byte stream.
    // Mechanical port of external/swos-port/src/util/random.cpp:41-48 +
    // 57-63 (the `random()` helper is reused with the stream-2 state
    // triple). Same algorithm as NextByte(); separate state.
    //
    // Used (in the original game; not yet wired into any ported C# caller)
    // by:
    //   - AssignFakeGoalsToScorers (swos.asm:8276, 8371) — picks which
    //     player gets a rigged goal when a CPU "wins" a friendly that the
    //     user cancelled mid-game.
    //   - NextPenalty's keeper-direction roll (swos.asm:24771).
    //   - ApplyTeamTactics' Randomize2 fallback (swos.asm:37519, 37961+).
    //   - 60+ further asm sites — see Rand2 XRef block in swos.asm.
    //
    // Exposed publicly now so any future port can immediately call into
    // stream 2 without retrofitting the API. Until then this is dead
    // code from the C# side; the byte sequence stays deterministic.
    public static int NextByte2()
    {
        if (m_seed2 == 0)
        {
            m_xorIndex2 = (byte)(m_xorIndex2 + 1);
            m_xorKey2 = kRandomTable[m_xorIndex2];
        }
        int result = kRandomTable[m_seed2] ^ m_xorKey2;
        m_seed2 = (byte)(m_seed2 + 1);
        return result & 0xFF;
    }

    // Word + bounded helpers for stream 2 — symmetrical to stream 1.
    public static int NextWord2()
    {
        int lo = NextByte2();
        int hi = NextByte2();
        return lo | (hi << 8);
    }

    public static int NextRange2(int max)
    {
        if (max <= 1) return 0;
        return NextByte2() % max;
    }
}
