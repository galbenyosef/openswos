namespace OpenSwos.Sim;

// Deterministic fixed-point math for the gameplay simulation.
//
// Stored as Int32 with implicit shift = 8 (Q24.8). Range: ±8 388 608.99 with 1/256
// granularity — way more than enough for SWOS positions in a 672×848 pitch.
//
// Why fixed point: cross-platform determinism. `float`/`double` differ subtly between
// .NET runtimes on Windows/Linux/wasm/Android (Math.Sin/Cos drift, FMA usage, etc.),
// which breaks lockstep netplay. The simulation tick must produce byte-identical state
// on every platform, so the whole sim uses Fixed; floats are only allowed in the
// render layer.
public readonly struct Fixed : System.IEquatable<Fixed>
{
    public const int FractionBits = 8;
    public const int One = 1 << FractionBits;

    public readonly int Raw;

    public Fixed(int raw) => Raw = raw;

    public static Fixed FromInt(int v) => new(v << FractionBits);
    public static Fixed FromRaw(int raw) => new(raw);

    public int ToInt() => Raw >> FractionBits;
    public float ToFloat() => Raw / (float)One;

    public static Fixed operator +(Fixed a, Fixed b) => new(a.Raw + b.Raw);
    public static Fixed operator -(Fixed a, Fixed b) => new(a.Raw - b.Raw);
    public static Fixed operator *(Fixed a, int b) => new(a.Raw * b);
    public static Fixed operator /(Fixed a, int b) => new(a.Raw / b);
    public static Fixed operator -(Fixed a) => new(-a.Raw);

    public static bool operator ==(Fixed a, Fixed b) => a.Raw == b.Raw;
    public static bool operator !=(Fixed a, Fixed b) => a.Raw != b.Raw;
    public static bool operator <(Fixed a, Fixed b) => a.Raw < b.Raw;
    public static bool operator >(Fixed a, Fixed b) => a.Raw > b.Raw;
    public static bool operator <=(Fixed a, Fixed b) => a.Raw <= b.Raw;
    public static bool operator >=(Fixed a, Fixed b) => a.Raw >= b.Raw;

    public bool Equals(Fixed other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fixed f && Equals(f);
    public override int GetHashCode() => Raw;
    public override string ToString() => ToFloat().ToString("F2");
}
