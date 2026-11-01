namespace Planetfall.Sim.Core;

/// <summary>
/// Our own PCG32 (XSH-RR). Engine RNGs do not promise stable sequences across versions,
/// so shared seeds and saves depend on this implementation staying byte-for-byte fixed.
/// The golden-output test in Pcg32Tests pins it.
/// </summary>
public sealed class Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;
    private ulong _state;
    private ulong _inc;

    public Pcg32(ulong seed, ulong sequence = 54)
    {
        _state = 0;
        _inc = (sequence << 1) | 1UL;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        ulong old = _state;
        _state = unchecked(old * Multiplier + _inc);
        uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
    }

    /// <summary>Unbiased integer in [0, bound).</summary>
    public uint NextBounded(uint bound)
    {
        if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound));
        uint threshold = unchecked((uint)(-(int)bound)) % bound;
        while (true)
        {
            uint r = NextUInt();
            if (r >= threshold) return r % bound;
        }
    }

    /// <summary>Inclusive range.</summary>
    public int NextInt(int minInclusive, int maxInclusive)
    {
        if (maxInclusive < minInclusive) throw new ArgumentOutOfRangeException(nameof(maxInclusive));
        return minInclusive + (int)NextBounded((uint)(maxInclusive - minInclusive + 1));
    }

    /// <summary>Double in [0, 1). Presentation/map-gen only; sim rules use integers.</summary>
    public double NextDouble() => NextUInt() / 4294967296.0;
}
