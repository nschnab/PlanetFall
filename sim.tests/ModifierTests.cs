using Planetfall.Sim.Core.Modifiers;
using Planetfall.Sim.Data;
using Xunit;

namespace Planetfall.Sim.Tests;

public class ModifierTests
{
    private sealed class Source(params Modifier[] mods) : IModifierSource
    {
        public IReadOnlyList<Modifier> Modifiers { get; } = mods;
    }

    private static string DataFile(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", relative));

    [Fact]
    public void NoSourcesReturnsBase()
    {
        Assert.Equal(1000, StatResolver.Resolve("capture.rate", 1000, Array.Empty<IModifierSource>()));
    }

    [Fact]
    public void FieldCommanderChangesNothing()
    {
        var leader = Leader.Parse(DataFile("leaders/commander.json"));
        Assert.Equal("commander", leader.Id);
        Assert.Empty(leader.Modifiers);
        Assert.Equal(3000, StatResolver.Resolve("move.delay", 3000, new[] { leader }));
    }

    [Fact]
    public void TestLeaderWithFiftyPercentCaptureRateCapturesOnePointFiveTimesFaster()
    {
        var leader = Leader.Parse("""
            { "id": "test", "name": "Test", "modifiers": [ { "key": "capture.rate", "op": "pct", "value": 500 } ] }
            """);
        long baseRate = 1000;
        long boosted = StatResolver.Resolve("capture.rate", baseRate, new[] { leader });
        Assert.Equal(1500, boosted);
        // time to capture is inversely proportional to the rate
        Assert.Equal(1.5, (double)boosted / baseRate);
    }

    [Fact]
    public void PercentModsMultiplyThenFlatAdds()
    {
        var a = new Source(new Modifier("x", ModifierOp.Pct, 500));    // +50%
        var b = new Source(new Modifier("x", ModifierOp.Pct, 200), new Modifier("x", ModifierOp.Flat, 7));
        // 1000 * 1.5 = 1500; * 1.2 = 1800; + 7 = 1807
        Assert.Equal(1807, StatResolver.Resolve("x", 1000, new IModifierSource[] { a, b }));
    }

    [Fact]
    public void OtherKeysAreIgnored()
    {
        var a = new Source(new Modifier("y", ModifierOp.Pct, 900));
        Assert.Equal(1000, StatResolver.Resolve("x", 1000, new IModifierSource[] { a }));
    }

    [Fact]
    public void NegativePercentReduces()
    {
        var a = new Source(new Modifier("x", ModifierOp.Pct, -250));
        Assert.Equal(750, StatResolver.Resolve("x", 1000, new IModifierSource[] { a }));
    }

    [Fact]
    public void BadLeaderJsonIsRejected()
    {
        Assert.Throws<FormatException>(() => Leader.Parse("""{ "id": "", "name": "n" }"""));
        Assert.Throws<FormatException>(() => Leader.Parse("""{ "id": "a", "name": "n", "modifiers": [ { "key": "k", "op": "wat", "value": 1 } ] }"""));
    }
}
