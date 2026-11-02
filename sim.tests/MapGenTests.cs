using Planetfall.Sim.MapGen;
using Xunit;

namespace Planetfall.Sim.Tests;

public class MapGenTests
{
    [Fact]
    public void SameSeedGivesIdenticalMap()
    {
        Assert.Equal(MapGenerator.Generate(12345).Hash(), MapGenerator.Generate(12345).Hash());
    }

    [Fact]
    public void DifferentSeedsGiveDifferentMaps()
    {
        Assert.NotEqual(MapGenerator.Generate(1).Hash(), MapGenerator.Generate(2).Hash());
    }

    [Fact]
    public void HasTwoHundredTiles()
    {
        Assert.Equal(200, MapGenerator.Generate(7).TileCount);
    }

    [Fact]
    public void AdjacencyIsSymmetricConnectedAndSane()
    {
        var m = MapGenerator.Generate(99);
        for (int i = 0; i < m.TileCount; i++)
        {
            if (!m.Passable(i)) { Assert.Empty(m.Neighbors[i]); continue; }
            Assert.InRange(m.Neighbors[i].Length, 1, 10);
            Assert.DoesNotContain(i, m.Neighbors[i]);
            foreach (int j in m.Neighbors[i]) Assert.Contains(i, m.Neighbors[j]);
        }
        for (int i = 0; i < m.TileCount; i++)
            if (m.Passable(i))
                Assert.True(MapGenerator.HopDistance(m.Neighbors, m.Spawns[0], i) >= 0, $"tile {i} unreachable");
    }

    [Fact]
    public void AverageNeighbourCountLooksLikeAVoronoiMap()
    {
        var m = MapGenerator.Generate(5);
        double avg = m.Neighbors.Where(n => n.Length > 0).Average(n => n.Length);
        Assert.InRange(avg, 4.5, 6.5);
    }

    [Fact]
    public void DepositsAreScarceAndNotOnSpawns()
    {
        var m = MapGenerator.Generate(31);
        int deposits = m.Deposit.Count(d => d);
        Assert.InRange(deposits, 8, 13);
        foreach (int s in m.Spawns) Assert.False(m.Deposit[s]);
    }

    [Fact]
    public void FairnessHoldsAcrossManySeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var m = MapGenerator.Generate(seed);
            Assert.False(m.UsedFallback, $"seed {seed} exhausted retries");
            Assert.True(m.SpawnValueRatio <= MapGenerator.MaxSpawnValueRatio, $"seed {seed} ratio {m.SpawnValueRatio}");
            Assert.True(MapGenerator.HopDistance(m.Neighbors, m.Spawns[0], m.Spawns[1]) >= MapGenerator.MinSpawnHops, $"seed {seed} spawns too close");
        }
    }

    [Fact]
    public void ThousandSeedsValidateWithoutFallbackAndHaveTerrain()
    {
        int withImpassable = 0;
        for (ulong seed = 1; seed <= 1000; seed++)
        {
            var m = MapGenerator.Generate(seed);
            Assert.False(m.UsedFallback, $"seed {seed} needed the fallback");
            Assert.InRange(m.PassableFraction, MapGenerator.MinPassable, MapGenerator.MaxPassable);
            Assert.True(m.Passable(m.Spawns[0]) && m.Passable(m.Spawns[1]));
            Assert.True(MapGenerator.HopDistance(m.Neighbors, m.Spawns[0], m.Spawns[1]) >= MapGenerator.MinSpawnHops);
            for (int i = 0; i < m.TileCount; i++)
                if (m.Deposit[i]) Assert.True(m.Passable(i));
            if (m.PassableFraction < 1) withImpassable++;
        }
        Assert.True(withImpassable > 900, $"only {withImpassable} maps had mountains or water");
    }

    [Fact]
    public void MapsHaveSeveralBiomes()
    {
        var kinds = new HashSet<Terrain>();
        for (ulong seed = 1; seed <= 20; seed++)
            foreach (var k in MapGenerator.Generate(seed).Terrain) kinds.Add(k);
        Assert.True(kinds.Count == 5, string.Join(",", kinds));
    }

    [Fact]
    public void DerivedSubSeedsDoNotCollideAcrossNearbySeedsAndStreams()
    {
        var seen = new HashSet<ulong>();
        for (ulong seed = 0; seed < 200; seed++)
            for (int attempt = 0; attempt < 50; attempt++)
                Assert.True(seen.Add(Planetfall.Sim.Core.SplitMix64.Derive(seed, "map:" + attempt)), $"collision at seed {seed} attempt {attempt}");
    }
}
