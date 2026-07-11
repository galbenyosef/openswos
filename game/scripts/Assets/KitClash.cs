namespace OpenSwos.Assets;

// Faithful port of SWOS's change-kit (kit-clash) selection: when two teams'
// kits look too similar, the away team switches to its alternate/change kit.
//
// Port of external/swos-port/swos/swos.asm:
//   SetInGameTeamsPrimaryColors  (36165-36246) — the 4-combination selection,
//   MeasureTeamKitsSimilarity    (36664-36810) — computes the 4 factors,
//   GetTeamKitsSimilarityFactor  (36827-37237) — the similarity predicate,
//   AreBasicColorsConflicting /
//   AreShortsAndStripeColorsConflicting (37248-37286),
//   conflictingBasicTeamColorsTable / conflictingShortsTeamColorsTable /
//   dseg_130942 / teamKitTieBreakColors  (data tables, 217863-217938).
//
// Kit byte layout (every arg): {shirtType, stripesColor, basicColor,
// shortsColor, socksColor} — the canonical SWOS TeamFileHeader order
// (swos.h:245-249). Colours are 0..9 (gray/white/black/orange/red/blue/brown/
// lightblue/green/yellow). shirtType: 0 ordinary, 1 coloured-sleeves, 2
// vertical stripes, 3 horizontal stripes. Only bytes 0..3 (type/stripes/basic/
// shorts) feed the similarity test — socks is not compared.
public static class KitClash
{
    private const int ShirtOrdinary = 0;
    private const int ShirtColoredSleeves = 1;
    private const int ShirtVerticalStripes = 2;
    private const int ShirtHorizontalStripes = 3;

    // swos.asm:36186 — a factor >= this counts as a clash ("too similar").
    private const int kClashThreshold = 11;

    // swos.asm:217863 / 217878 — per-colour conflict bitmasks. Index by the
    // FIRST colour; if bit `secondColour` is set the pair conflicts.
    private static readonly ushort[] ConflictingBasic =
        { 0x1A1, 0x002, 0x164, 0x218, 0x058, 0x0A5, 0x054, 0x0A1, 0x105, 0x208 };
    private static readonly ushort[] ConflictingShorts =
        { 0x1A3, 0x283, 0x164, 0x218, 0x058, 0x0E5, 0x074, 0x0A3, 0x105, 0x20A };

    // swos.asm:217891-217893 — striped-kit → equivalent-solid-colour remap.
    // Triples {a, b, c}: a striped shirt whose (stripes,basic) is {a,b} or {b,a}
    // is treated as a solid shirt of colour c for a re-check. -1 terminates.
    private static readonly sbyte[] StripedToSolid =
    {
        1,5,7,  1,2,0,  4,5,6,  4,9,3,  1,9,9,  1,7,7,  1,0,0,  4,3,3,
        4,6,6,  4,2,6,  0,5,7,  6,3,4,  2,7,5,  9,5,8,  9,7,8,  -1
    };

    // swos.asm:217938 — solid tie-break colours tried for the away team when
    // every home/away kit combination still clashes. -1 terminates.
    private static readonly sbyte[] TieBreakColors = { 4, 5, 1, 7, 9, 0, 3, 6, 8, 2, -1 };

    // ---- Public API ---------------------------------------------------------

    // Chooses the two teams' on-pitch kits. Mirrors SetInGameTeamsPrimaryColors
    // (swos.asm:36165-36246): try the four home/away × primary/secondary combos
    //   0: home.primary   vs away.primary
    //   1: home.primary   vs away.secondary   (away wears its change kit)
    //   2: home.secondary vs away.primary     (home wears its change kit)
    //   3: home.secondary vs away.secondary
    // and take the FIRST with factor < 11. `combo` is returned for logging
    // (0..3 = the chosen combo; 4 = tie-break solid away kit; 5 = fixed fallback).
    public static (byte[] home, byte[] away, int combo) Select(
        byte[] homePrimary, byte[] homeSecondary,
        byte[] awayPrimary, byte[] awaySecondary)
    {
        if (!Valid(homePrimary) || !Valid(awayPrimary))
            return (homePrimary, awayPrimary, 0);
        byte[] homeSec = Valid(homeSecondary) ? homeSecondary : homePrimary;
        byte[] awaySec = Valid(awaySecondary) ? awaySecondary : awayPrimary;

        int[] factors =
        {
            SimilarityFactor(homePrimary, awayPrimary),
            SimilarityFactor(homePrimary, awaySec),
            SimilarityFactor(homeSec,     awayPrimary),
            SimilarityFactor(homeSec,     awaySec),
        };
        for (int combo = 0; combo < 4; combo++)
        {
            if (factors[combo] < kClashThreshold)
            {
                byte[] h = (combo & 2) != 0 ? homeSec : homePrimary;
                byte[] a = (combo & 1) != 0 ? awaySec : awayPrimary;
                return (h, a, combo);
            }
        }

        // Every combination clashes — keep home on its primary and give the away
        // team a solid tie-break colour that does not clash with home. Simplified
        // port of ResolveTeamKitColorConflict (swos.asm:36203-36433). PORT-VISUAL.
        foreach (sbyte c in TieBreakColors)
        {
            if (c < 0) break;
            byte[] solid = { ShirtOrdinary, (byte)c, (byte)c, 1, (byte)c };  // white shorts
            if (SimilarityFactor(homePrimary, solid) < kClashThreshold)
                return (homePrimary, solid, 4);
        }
        // Ultimate fixed fallback (swos.asm:36417-36429): away all-blue, white shorts.
        byte[] awayBlue = { ShirtOrdinary, 5, 5, 1, 5 };
        return (homePrimary, awayBlue, 5);
    }

    // GetTeamKitsSimilarityFactor (swos.asm:36827-37237). Higher = more similar;
    // >= 11 is a clash. Only kit[0..3] are read.
    public static int SimilarityFactor(byte[] kitA, byte[] kitB)
    {
        int st1 = kitA[0], str1 = kitA[1], bas1 = kitA[2], sho1 = kitA[3];
        int st2 = kitB[0], str2 = kitB[1], bas2 = kitB[2], sho2 = kitB[3];
        bool remapped1 = false, remapped2 = false;
        int factor;

        while (true)  // @@colors_check_loop (36835)
        {
            // 36837-36847 — solid shirt: equalize basic := stripes.
            if (st1 == ShirtOrdinary) bas1 = str1;
            if (st2 == ShirtOrdinary) bas2 = str2;

            factor = ComputeRawFactor(st1, str1, bas1, sho1, st2, str2, bas2, sho2);

            // @@got_conflict (37118-37121): a clash short-circuits.
            if (factor >= kClashThreshold) break;

            // 37122-37226 — striped→solid remap pass; re-loop if anything changed
            // (each team is remapped at most once, exactly as the asm which keeps
            // shirtType==ORDINARY sticky after a remap).
            bool changed = false;
            if (!remapped1 && (st1 == ShirtVerticalStripes || st1 == ShirtHorizontalStripes))
            {
                int solid = StripedSolidLookup(str1, bas1);
                if (solid >= 0) { remapped1 = true; changed = true; st1 = ShirtOrdinary; str1 = solid; bas1 = solid; }
            }
            if (!remapped2 && (st2 == ShirtVerticalStripes || st2 == ShirtHorizontalStripes))
            {
                int solid = StripedSolidLookup(str2, bas2);
                if (solid >= 0) { remapped2 = true; changed = true; st2 = ShirtOrdinary; str2 = solid; bas2 = solid; }
            }
            if (!changed) break;
        }

        // @@return_value (37228-37234): clamp a negative score to 0.
        return factor < 0 ? 0 : factor;
    }

    // ---- Internals ----------------------------------------------------------

    private static int ComputeRawFactor(int st1, int str1, int bas1, int sho1,
                                        int st2, int str2, int bas2, int sho2)
    {
        // 36849-36877 — factor 99: basic/stripe cross-conflict.
        if ((BasicConflict(str1, str2) && BasicConflict(bas1, bas2)) ||
            (BasicConflict(str1, bas2) && BasicConflict(bas1, str2)))
            return 99;

        // 36879-36908 — factor 88: ordinary-vs-sleeves with stripe/shorts conflict.
        bool sleevesOrdinaryPair =
            (st1 == ShirtColoredSleeves && st2 == ShirtOrdinary) ||
            (st1 == ShirtOrdinary && st2 == ShirtColoredSleeves);
        if (sleevesOrdinaryPair &&
            ShortsStripeConflict(str1, sho2) && ShortsStripeConflict(sho1, str2))
            return 88;

        // 36913-36960 — factor 77: both striped, shorts vs stripe/basic conflicts.
        bool bothStriped =
            (st1 == ShirtVerticalStripes || st1 == ShirtHorizontalStripes) &&
            (st2 == ShirtVerticalStripes || st2 == ShirtHorizontalStripes);
        if (bothStriped &&
            (BasicConflict(sho1, str2) || BasicConflict(sho1, bas2)) &&
            (BasicConflict(sho2, str1) || BasicConflict(sho2, bas1)))
            return 77;

        // 36965-36976 — factor 66: both coloured-sleeves with conflicting stripes.
        if (st1 == ShirtColoredSleeves && st2 == ShirtColoredSleeves &&
            BasicConflict(str1, str2))
            return 66;

        // 36979-37116 — @@no_conflict: weighted score (can go negative).
        int factor = 0;
        if (st1 != st2) factor -= 4;                                   // 36982-36987
        if (st1 == ShirtOrdinary)                                      // 36990-37001
        {
            factor -= 2;
            if (st2 == ShirtColoredSleeves && bas1 == str1 && sho1 == str1)
                factor -= 6;
        }
        if (st2 == ShirtOrdinary)                                      // 37005-37016
        {
            factor -= 2;
            if (st1 == ShirtColoredSleeves && bas2 == str2 && sho2 == str2)
                factor -= 6;
        }
        if ((st1 == ShirtOrdinary && st2 == ShirtColoredSleeves) ||    // 37020-37034
            (st1 == ShirtColoredSleeves && st2 == ShirtOrdinary))
            factor += 1;
        // 37036-37116 — nine cross-colour conflict weights.
        if (BasicConflict(str1, str2)) factor += 5;
        if (BasicConflict(str1, bas2)) factor += 5;
        if (BasicConflict(str1, sho2)) factor += 2;
        if (BasicConflict(bas1, str2)) factor += 5;
        if (BasicConflict(bas1, bas2)) factor += 5;
        if (BasicConflict(bas1, sho2)) factor += 2;
        if (BasicConflict(sho1, str2)) factor += 2;
        if (BasicConflict(sho1, bas2)) factor += 2;
        if (BasicConflict(sho1, sho2)) factor += 5;
        return factor;
    }

    // swos.asm:37139-37167 — return the solid colour a striped (stripes,basic)
    // pair maps to, or -1 if the pair isn't in the remap table.
    private static int StripedSolidLookup(int stripes, int basic)
    {
        for (int i = 0; i + 2 < StripedToSolid.Length && StripedToSolid[i] >= 0; i += 3)
        {
            int a = StripedToSolid[i], b = StripedToSolid[i + 1], c = StripedToSolid[i + 2];
            if ((stripes == a && basic == b) || (stripes == b && basic == a))
                return c;
        }
        return -1;
    }

    // AreBasicColorsConflicting (swos.asm:37248-37264).
    private static bool BasicConflict(int c1, int c2) => ColorConflict(ConflictingBasic, c1, c2);
    // AreShortsAndStripeColorsConflicting (swos.asm:37270-37286).
    private static bool ShortsStripeConflict(int c1, int c2) => ColorConflict(ConflictingShorts, c1, c2);

    private static bool ColorConflict(ushort[] table, int c1, int c2)
    {
        c1 &= 0xFF; c2 &= 0xFF;
        if (c1 >= table.Length) return false;   // colours are 0..9; guard defensively
        return ((table[c1] >> c2) & 1) != 0;
    }

    private static bool Valid(byte[] kit) => kit != null && kit.Length >= 5;
}
