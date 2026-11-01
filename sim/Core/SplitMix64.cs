namespace Planetfall.Sim.Core;

/// <summary>Seed expander; also derives per-system sub-seeds from a match seed.</summary>
public static class SplitMix64
{
    public static ulong Next(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Derives an independent sub-seed so adding rolls to one system never shifts another.</summary>
    public static ulong Derive(ulong matchSeed, string stream)
    {
        ulong s = matchSeed;
        foreach (char c in stream)
        {
            s ^= c;
            Next(ref s);
        }
        return Next(ref s);
    }
}
