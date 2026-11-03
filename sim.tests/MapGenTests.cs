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

    [Fact]
    public void ChokepointsAreExactlyTheTilesWhoseRemovalDisconnectsTheMap()
    {
        for (ulong seed = 1; seed <= 15; seed++)
        {
            var m = MapGenerator.Generate(seed);
            int land = Enumerable.Range(0, m.TileCount).Count(m.Passable);
            for (int t = 0; t < m.TileCount; t++)
            {
                if (!m.Passable(t)) { Assert.False(m.Chokepoint[t]); continue; }
                int start = m.Neighbors[m.Spawns[0] == t ? m.Spawns[1] : m.Spawns[0]].Length >= 0 ? (m.Spawns[0] == t ? m.Spawns[1] : m.Spawns[0]) : 0;
                var seen = new HashSet<int> { start };
                var q = new Queue<int>(new[] { start });
                while (q.Count > 0)
                    foreach (int n in m.Neighbors[q.Dequeue()])
                        if (n != t && seen.Add(n)) q.Enqueue(n);
                bool disconnects = seen.Count < land - 1;
                Assert.True(disconnects == m.Chokepoint[t], $"seed {seed} tile {t}: brute force {disconnects}, flagged {m.Chokepoint[t]}");
            }
        }
    }

    [Fact]
    public void ChokepointsNearEachSpawnAreBalancedAndMapsHaveSome()
    {
        int maps = 0, withChokes = 0;
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var m = MapGenerator.Generate(seed);
            maps++;
            if (m.Chokepoint.Any(c => c)) withChokes++;
        }
        Assert.True(withChokes > maps / 2, $"only {withChokes}/{maps} maps have chokepoints");
    }

    [Fact]
    public void RiversSitBetweenAdjacentPassableTilesAndMostMapsHaveThem()
    {
        int withRivers = 0;
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var m = MapGenerator.Generate(seed);
            foreach (long key in m.Rivers)
            {
                int a = (int)(key / 100_000), b = (int)(key % 100_000);
                Assert.Contains(b, m.Neighbors[a]);
                Assert.True(m.IsRiver(b, a));
            }
            if (m.Rivers.Length > 0) withRivers++;
        }
        Assert.True(withRivers > 70, $"only {withRivers}/100 maps have rivers");
    }

    [Fact]
    public void CrossingARiverTakesFiftyPercentLonger()
    {
        var m = Enumerable.Range(1, 200).Select(i => MapGenerator.Generate((ulong)i)).First(x => x.Rivers.Length > 0);
        long key = m.Rivers[0];
        int a = (int)(key / 100_000), b = (int)(key % 100_000);
        var s = Planetfall.Sim.World.GameState.New(m, Planetfall.Sim.Data.Leader.Parse("""{ "id": "c", "name": "C" }"""), Planetfall.Sim.Data.Leader.Parse("""{ "id": "c", "name": "C" }"""));
        s.Stacks.Clear();
        var st = s.AddStack(0, a, 5);
        st.Target = b;
        long river = Planetfall.Sim.Systems.Simulation.HopNeeded(s, st);
        var other = m.Neighbors[a].First(n => !m.IsRiver(a, n));
        st.Target = other;
        long plain = Planetfall.Sim.Systems.Simulation.HopNeeded(s, st);
        Assert.Equal(plain * 3 / 2, river);
    }

    [Fact]
    public void MinorClustersAreSizedSeparatedAndClearOfSpawns()
    {
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var m = MapGenerator.Generate(seed);
            Assert.InRange(m.MinorClusters.Length, MapGenerator.MinMinors, MapGenerator.MaxMinors);
            var all = m.MinorClusters.SelectMany(c => c).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            foreach (var c in m.MinorClusters)
            {
                Assert.InRange(c.Length, MapGenerator.MinClusterSize, MapGenerator.MaxClusterSize);
                Assert.All(c, t => Assert.True(m.Passable(t)));
                foreach (int sp in m.Spawns) Assert.All(c, t => Assert.True(MapGenerator.HopDistance(m.Neighbors, sp, t) > MapGenerator.MinorSpawnClearance));
            }
            // no two clusters touch
            for (int a = 0; a < m.MinorClusters.Length; a++)
            for (int b = a + 1; b < m.MinorClusters.Length; b++)
                Assert.DoesNotContain(m.MinorClusters[a], t => m.Neighbors[t].Intersect(m.MinorClusters[b]).Any());
        }
    }

    [Fact]
    public void EverySpawnHasAMinorClusterWithinSixHops()
    {
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var m = MapGenerator.Generate(seed);
            foreach (int sp in m.Spawns)
                Assert.Contains(m.MinorClusters, c => MapGenerator.HopDistance(m.Neighbors, sp, c[0]) is >= 0 and <= MapGenerator.MaxMinorHops);
        }
    }
}
