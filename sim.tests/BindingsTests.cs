using Planetfall.Sim.Core;
using Xunit;

namespace Planetfall.Sim.Tests;

public class BindingsTests
{
    [Fact]
    public void DefaultsAreUnique()
    {
        var keys = Bindings.Actions.Select(a => a.Default).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void RebindingToAFreeKeyWorks()
    {
        var b = new Bindings();
        Assert.True(b.TryRebind("pause", "P", out var c));
        Assert.Null(c);
        Assert.Equal("P", b.KeyOf("pause"));
        Assert.Equal("pause", b.ActionOf("P"));
    }

    [Fact]
    public void RebindingToAUsedKeyIsRefusedAndNamesTheOtherAction()
    {
        var b = new Bindings();
        Assert.False(b.TryRebind("pause", "Z", out var c));
        Assert.Equal("Build depot", c);
        Assert.Equal("Space", b.KeyOf("pause"));
    }

    [Fact]
    public void RebindingToItsOwnKeyIsFine()
    {
        var b = new Bindings();
        Assert.True(b.TryRebind("pause", "Space", out _));
    }

    [Fact]
    public void RoundTripKeepsChanges()
    {
        var b = new Bindings();
        b.TryRebind("pause", "P", out _);
        var back = Bindings.FromJson(b.ToJson());
        Assert.Equal("P", back.KeyOf("pause"));
        Assert.Equal("Z", back.KeyOf("build_depot"));
    }

    [Fact]
    public void ClashingOrGarbageFilesFallBackToDefaults()
    {
        Assert.Equal("Space", Bindings.FromJson("{\"pause\":\"Z\"}").KeyOf("pause"));     // Z is still the depot default
        Assert.Equal("Space", Bindings.FromJson("not json").KeyOf("pause"));
        Assert.Equal("Space", Bindings.FromJson(null).KeyOf("pause"));
        Assert.Equal("Space", Bindings.FromJson("{\"pause\":\"Q\",\"stop\":\"Q\"}").KeyOf("pause"));
    }

    [Fact]
    public void SwappingTwoKeysInTheFileIsAccepted()
    {
        var b = Bindings.FromJson("{\"pause\":\"S\",\"stop\":\"Space\"}");
        Assert.Equal("S", b.KeyOf("pause"));
        Assert.Equal("Space", b.KeyOf("stop"));
    }
}
