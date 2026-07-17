namespace OpenSwos.Competition.Career;

/// <summary>
/// The original SWOS 96/97 player price ladder plus a progressive "modern
/// market" inflation curve. Pure, deterministic, no engine dependencies.
///
/// The 50-step ladder (price CODE 0..49 -> pounds) is transcribed verbatim from
/// the reverse-engineered player-record documentation:
///   external/swos-port/tools/swospp/doc/teams.txt:197-221  (byte +32, "player
///   price": 0 -> 25K, ... 49 -> 15M+).
/// SWOS stores only this 0..49 code per player; the pound figure is looked up
/// from the ladder. We keep the exact figures (constants may be copied) and add
/// our own paraphrased interpolation / inflation on top.
/// </summary>
public static class PriceTable
{
    // teams.txt:197-221 — CODE -> 1997 GBP. Index is the price code.
    private static readonly double[] Ladder =
    {
        25_000,    25_000,    30_000,    40_000,    50_000,    65_000,    75_000,
        85_000,    100_000,   110_000,   130_000,   150_000,   160_000,   180_000,
        200_000,   250_000,   300_000,   350_000,   450_000,   500_000,   550_000,
        600_000,   650_000,   700_000,   750_000,   800_000,   850_000,   950_000,
        1_000_000, 1_100_000, 1_300_000, 1_500_000, 1_600_000, 1_800_000, 1_900_000,
        2_000_000, 2_250_000, 2_750_000, 3_000_000, 3_500_000, 4_500_000, 5_000_000,
        6_000_000, 7_000_000, 8_000_000, 9_000_000, 10_000_000, 12_000_000,
        15_000_000, 15_000_000,
    };

    private const int MaxCode = 49;

    // Progressive-inflation power curve, ModernValue(v) = a * v^b, solved once
    // from two anchors so the coefficients are auditable rather than magic:
    //   150K(1997) -> 350K(today)   (a small-club player roughly doubles)
    //   15M (1997) -> 150M(today)   (a world star roughly x10)
    private static readonly double InflationExponent;   // b
    private static readonly double InflationCoefficient; // a

    static PriceTable()
    {
        const double lowFrom = 150_000.0, lowTo = 350_000.0;
        const double highFrom = 15_000_000.0, highTo = 150_000_000.0;

        // b = ln(highTo/lowTo) / ln(highFrom/lowFrom)
        InflationExponent =
            System.Math.Log(highTo / lowTo) / System.Math.Log(highFrom / lowFrom);
        // a chosen so the low anchor is hit exactly: a = lowTo / lowFrom^b
        InflationCoefficient = lowTo / System.Math.Pow(lowFrom, InflationExponent);
    }

    /// <summary>
    /// The 1997 SWOS price for a (possibly fractional) price code. Fractional
    /// codes linearly interpolate between ladder steps. Codes below 0 clamp to
    /// the floor; codes above 49 extrapolate geometrically at the ladder's own
    /// 46->49 per-step growth so future codes beyond the table stay monotonic.
    /// </summary>
    public static double Swos1997Price(double code)
    {
        if (double.IsNaN(code) || code <= 0.0) return Ladder[0];
        if (code >= MaxCode)
        {
            // 46 -> 49 spans three steps (10M -> 15M): recover the per-step ratio.
            double perStep = System.Math.Pow(Ladder[MaxCode] / Ladder[46], 1.0 / 3.0);
            return Ladder[MaxCode] * System.Math.Pow(perStep, code - MaxCode);
        }

        int lo = (int)System.Math.Floor(code);
        double frac = code - lo;
        return Ladder[lo] + (Ladder[lo + 1] - Ladder[lo]) * frac;
    }

    /// <summary>
    /// Inflates a 1997 pound value to today's market with the progressive power
    /// curve (small clubs ~x2-3, world stars ~x10). See the anchors above.
    /// </summary>
    public static double ModernValue(double v1997)
    {
        if (double.IsNaN(v1997) || v1997 <= 0.0) return 0.0;
        return InflationCoefficient * System.Math.Pow(v1997, InflationExponent);
    }

    /// <summary>
    /// The fractional price code implied by a SWOS 0..7 overall, inverting the
    /// keeper ability mapping overall = (code + 3) / 7  =>  code = overall*7 - 3.
    /// Clamped to a generous [0, 60] so grown stars can drift above the ladder.
    /// </summary>
    public static double CodeFromOverall(double overall)
    {
        if (double.IsNaN(overall)) return 0.0;
        return System.Math.Clamp(overall * 7.0 - 3.0, 0.0, 60.0);
    }
}
