using Planetfall.Sim.Core;
using Xunit;

namespace Planetfall.Sim.Tests;

public class Pcg32Tests
{
    // Reference vector from the PCG paper's demo: seed 42, sequence 54.
    private static readonly uint[] Golden =
    {
        0xA15C02B7, 0x7B47F409, 0xBA1D3330, 0x83D2F293,
        0xBFA4784B, 0xCBED606E,
    };

    [Fact]
    public void First_outputs_match_reference_vector()
    {
        var rng = new Pcg32(42, 54);
        foreach (uint expected in Golden)
            Assert.Equal(expected, rng.NextUInt());
    }

    [Fact]
    public void Same_seed_same_sequence()
    {
        var a = new Pcg32(12345);
        var b = new Pcg32(12345);
        for (int i = 0; i < 1000; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void Different_seeds_diverge()
    {
        Assert.NotEqual(new Pcg32(1).NextUInt(), new Pcg32(2).NextUInt());
    }

    [Fact]
    public void Bounded_stays_in_range()
    {
        var rng = new Pcg32(7);
        for (int i = 0; i < 10000; i++)
        {
            int v = rng.NextInt(-3, 5);
            Assert.InRange(v, -3, 5);
        }
    }

    [Fact]
    public void Derived_streams_are_stable_and_independent()
    {
        ulong map = SplitMix64.Derive(99, "mapgen");
        ulong ai = SplitMix64.Derive(99, "ai");
        Assert.NotEqual(map, ai);
        Assert.Equal(map, SplitMix64.Derive(99, "mapgen"));
    }
}
