using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;
using Xunit;
using Xunit.Abstractions;

namespace Planetfall.Sim.Tests;

public class MatchTests
{
    private static readonly Leader Commander = Leader.Parse("""{ "id": "commander", "name": "Field Commander" }""");
    private static readonly IReadOnlyList<Command> None = Array.Empty<Command>();
    private readonly ITestOutputHelper _out;

    public MatchTests(ITestOutputHelper output) => _out = output;

    private static GameState NewMatch(ulong seed)
    {
        var map = MapGenerator.Generate(seed);
        return GameState.NewWithMinor(map, GameState.PickMinorTile(map), Rules.MinorStrength, Commander, Commander);
    }

    private static void Run(GameState s, int ticks)
    {
        for (int i = 0; i < ticks; i++) Simulation.Step(s, None);
    }

    [Fact]
    public void MinorPowerHoldsItsTileAndNeverMoves()
    {
        var s = NewMatch(3);
        int minorTile = s.Players[2].Capital;
        Assert.Equal(2, s.TileOwner[minorTile]);
        Run(s, 600);
        var minorStack = s.Stacks.Single(x => x.Owner == 2);
        Assert.Equal(minorTile, minorStack.Tile);
        Assert.Equal(2, s.TileOwner[minorTile]);
        Assert.Equal(0, s.Players[2].Supply);
    }

    [Fact]
    public void HoldingTheEnemyCapitalForSixtySecondsWinsTheMatch()
    {
        var s = NewMatch(3);
        int cap1 = s.Players[1].Capital;
        s.Stacks.RemoveAll(x => x.Owner == 1);
        s.TileOwner[cap1] = 0;
        s.TileOwner[s.Map.Neighbors[cap1][0]] = 1;   // keep them alive on another tile
        s.AddStack(0, cap1, 10);

        Run(s, Rules.CapitulationTicks - 1);
        Assert.False(s.Over);
        var events = Simulation.Step(s, None);
        Assert.Equal(0, s.Winner);
        Assert.Contains(events, e => e is PlayerDefeated { Player: 1, Reason: "capital held" });
        Assert.Contains(events, e => e is MatchOver { Winner: 0 });
    }

    [Fact]
    public void RetakingTheCapitalResetsTheClock()
    {
        var s = NewMatch(3);
        int cap1 = s.Players[1].Capital;
        s.Stacks.RemoveAll(x => x.Owner == 1);
        s.TileOwner[cap1] = 0;
        s.TileOwner[s.Map.Neighbors[cap1][0]] = 1;
        Run(s, 300);
        Assert.True(s.CapitalHeld[1] > 0);
        s.TileOwner[cap1] = 1;
        Run(s, 1);
        Assert.Equal(0, s.CapitalHeld[1]);
    }

    [Fact]
    public void SurrenderEndsTheMatchAndHandsOverTheTiles()
    {
        var s = NewMatch(3);
        int theirs = s.Players[1].Capital;
        Simulation.Step(s, new Command[] { new SurrenderCommand(1) });
        Assert.Equal(0, s.Winner);
        Assert.Equal(0, s.TileOwner[theirs]);
        Assert.DoesNotContain(s.TileOwner, o => o == 1);
        Assert.DoesNotContain(s.Stacks, x => x.Owner == 1);
    }

    [Fact]
    public void NothingHappensAfterTheMatchIsOver()
    {
        var s = NewMatch(3);
        Simulation.Step(s, new Command[] { new SurrenderCommand(1) });
        ulong h = s.Hash();
        Run(s, 50);
        Assert.Equal(h, s.Hash());
    }

    [Fact]
    public void AiSendsStacksTowardForeignTiles()
    {
        var s = NewMatch(3);
        var ai = new RivalAi(0);
        Simulation.Step(s, ai.Think(s));
        Assert.True(s.Stacks.First(x => x.Owner == 0).Target != -1);
    }

    private static (GameState state, int ticks, int captures) PlayOut(ulong seed, int maxTicks)
    {
        var s = NewMatch(seed);
        var ais = new[] { new RivalAi(0), new RivalAi(1) };
        int captures = 0;
        while (!s.Over && s.Tick < maxTicks)
        {
            var cmds = ais.SelectMany(a => a.Think(s)).ToList();
            captures += Simulation.Step(s, cmds).Count(e => e is TileCaptured);
        }
        return (s, s.Tick, captures);
    }

    [Fact]
    public void AiVersusAiMatchesFinishWithinThirtyMinutes()
    {
        const int Limit = 30 * 60 * 10;
        var unfinished = new List<ulong>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (s, ticks, captures) = PlayOut(seed, Limit);
            _out.WriteLine($"seed {seed}: winner={s.Winner} ticks={ticks} ({ticks / 600.0:F1} min) captures={captures} tiles={s.OwnedTiles(0)}/{s.OwnedTiles(1)}");
            if (!s.Over) unfinished.Add(seed);
        }
        Assert.Empty(unfinished);
    }

    [Fact]
    public void AiMatchesAreDeterministic()
    {
        Assert.Equal(PlayOut(5, 6000).state.Hash(), PlayOut(5, 6000).state.Hash());
    }

    [Fact]
    public void DifferentSeedsGiveDifferentMaps()
    {
        var hashes = Enumerable.Range(1, 20).Select(i => MapGenerator.Generate((ulong)i).Hash()).ToList();
        Assert.Equal(20, hashes.Distinct().Count());
    }

    [Fact]
    public void StackCapLimitsHowManyStacksAPlayerFields()
    {
        var s = NewMatch(3);
        s.Players[0].MaxStacks = 2;
        s.Players[0].Supply = Rules.StackCostMilli * 10;
        int most = 0;
        for (int i = 0; i < 100; i++)
        {
            Simulation.Step(s, None);
            most = Math.Max(most, s.Stacks.Count(x => x.Owner == 0));
        }
        Assert.Equal(2, most);
    }
}
