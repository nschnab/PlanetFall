using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;
using Xunit;

namespace Planetfall.Sim.Tests;

public class SimulationTests
{
    private static readonly Leader Commander = Leader.Parse("""{ "id": "commander", "name": "Field Commander" }""");

    private static Leader Fast => Leader.Parse("""
        { "id": "fast", "name": "Fast", "modifiers": [ { "key": "move.speed", "op": "pct", "value": 500 } ] }
        """);

    private static readonly MapData Map = MapGenerator.Generate(1);

    private static GameState NewGame(Leader? p0 = null) => GameState.New(Map, p0 ?? Commander, Commander);

    private static readonly IReadOnlyList<Command> None = Array.Empty<Command>();

    private static void Run(GameState s, int ticks)
    {
        for (int i = 0; i < ticks; i++) Simulation.Step(s, None);
    }

    private static (int a, int b) NeighbourPair(GameState s)
    {
        int a = s.Players[0].Capital;
        return (a, s.Map.Neighbors[a][0]);
    }

    [Fact]
    public void HopTakesThreeSeconds()
    {
        var s = NewGame();
        var (a, b) = NeighbourPair(s);
        var st = s.Stacks[0];
        Simulation.Step(s, new Command[] { new MoveCommand(0, st.Id, b) });
        Run(s, 28);                       // 29 ticks so far
        Assert.Equal(a, st.Tile);
        Run(s, 1);                        // 30th tick
        Assert.Equal(b, st.Tile);
    }

    [Fact]
    public void LeaderWithFiftyPercentMoveSpeedHopsOnePointFiveTimesFaster()
    {
        var s = NewGame(Fast);
        var (a, b) = NeighbourPair(s);
        var st = s.Stacks[0];
        Simulation.Step(s, new Command[] { new MoveCommand(0, st.Id, b) });
        Run(s, 18);                       // 19 ticks
        Assert.Equal(a, st.Tile);
        Run(s, 1);                        // 20 ticks = 30 / 1.5
        Assert.Equal(b, st.Tile);
    }

    [Fact]
    public void ArrivalCapturesTheTileImmediately()
    {
        var s = NewGame();
        var (_, b) = NeighbourPair(s);
        var st = s.Stacks[0];
        Assert.Equal(-1, s.TileOwner[b]);
        Simulation.Step(s, new Command[] { new MoveCommand(0, st.Id, b) });
        Run(s, 28);
        Assert.Equal(-1, s.TileOwner[b]);
        Run(s, 1);
        Assert.Equal(0, s.TileOwner[b]);
    }

    [Fact]
    public void NoCaptureWhileAnEnemyStackIsOnTheTile()
    {
        var s = NewGame();
        int t = s.Map.TileCount / 2;
        s.AddStack(0, t, 10);
        s.AddStack(1, t, 10);
        Run(s, 1);
        Assert.Equal(-1, s.TileOwner[t]);
    }

    [Fact]
    public void FightStartsWhenTheOrderBeginsNotOnArrival()
    {
        var s = NewGame();
        var (a, b) = NeighbourPair(s);
        var attacker = s.Stacks[0];
        var defender = s.AddStack(1, b, 10);
        long attackerHp = attacker.Hp, defenderHp = defender.Hp;

        Simulation.Step(s, new Command[] { new MoveCommand(0, attacker.Id, b) });

        Assert.Equal(a, attacker.Tile);          // has not moved yet
        Assert.True(attacker.Hp < attackerHp);
        Assert.True(defender.Hp < defenderHp);
    }

    [Fact]
    public void HopsInAFightRunAtFortyPercent()
    {
        var s = NewGame();
        var (_, b) = NeighbourPair(s);
        var attacker = s.Stacks[0];
        s.AddStack(1, b, 10);
        Simulation.Step(s, new Command[] { new MoveCommand(0, attacker.Id, b) });
        Run(s, 9);                               // 10 ticks in a fight
        Assert.Equal(10 * Rules.TickMs * 400, attacker.Timer);   // vs 10 * 100 * 1000 unengaged
    }

    [Fact]
    public void BadlyHurtStackRetreatsToAFriendlyNeighbour()
    {
        var s = NewGame();
        int a = s.Map.TileCount / 2;
        int safe = s.Map.Neighbors[a][0];
        s.TileOwner[a] = 0;
        s.TileOwner[safe] = 0;
        var weak = s.AddStack(0, a, 10);
        s.AddStack(1, a, 60);

        StackRetreating? seen = null;
        for (int i = 0; i < 60 && seen == null; i++)
            seen = Simulation.Step(s, None).OfType<StackRetreating>().FirstOrDefault();

        Assert.NotNull(seen);
        Assert.Equal(weak.Id, seen!.StackId);
        Assert.Equal(safe, seen.ToTile);
        Assert.True(weak.Hp * 4 < weak.MaxHp);
    }

    [Fact]
    public void IdleFriendlyStacksMerge()
    {
        var s = NewGame();
        int cap = s.Players[0].Capital;
        s.AddStack(0, cap, 5);
        var events = Simulation.Step(s, None);
        Assert.Single(events.OfType<StacksMerged>());
        var mine = s.Stacks.Where(x => x.Owner == 0).ToList();
        Assert.Single(mine);
        Assert.Equal(15 * Rules.HpUnit, mine[0].Hp);
        Assert.Equal(15 * Rules.HpUnit, mine[0].MaxHp);
    }

    [Fact]
    public void MovingStacksDoNotMerge()
    {
        var s = NewGame();
        var (_, b) = NeighbourPair(s);
        var first = s.Stacks[0];
        var second = s.AddStack(0, first.Tile, 5);
        Simulation.Step(s, new Command[] { new MoveCommand(0, second.Id, b) });
        Assert.Equal(2, s.Stacks.Count(x => x.Owner == 0));
    }

    [Fact]
    public void SupplyBuysAStackAtTheCapital()
    {
        var s = NewGame();
        s.Players[0].Supply = Rules.StackCostMilli - 10;      // one tile earns 20 per tick
        var events = Simulation.Step(s, None);
        var spawn = Assert.Single(events.OfType<StackSpawned>());
        Assert.Equal(0, spawn.Owner);
        Assert.Equal(s.Players[0].Capital, spawn.Tile);
        Assert.Equal(10, s.Players[0].Supply);
        Assert.Equal(Rules.NewStackStrength * Rules.HpUnit, s.FindStack(spawn.StackId)!.Hp);
    }

    [Fact]
    public void DepositsPayFiveTimesAsMuch()
    {
        var s = NewGame();
        int dep = Array.IndexOf(s.Map.Deposit, true);
        s.TileOwner[dep] = 0;
        long before = s.Players[0].Supply;
        Simulation.Step(s, None);
        Assert.Equal(Rules.TileYieldMilli * (1 + Rules.DepositMultiplier), s.Players[0].Supply - before);
    }

    [Fact]
    public void ShiftQueueAppendsAfterTheCurrentRoute()
    {
        var s = NewGame();
        var (a, b) = NeighbourPair(s);
        int c = s.Map.Neighbors[b].First(x => x != a && !s.Map.Neighbors[a].Contains(x));
        var st = s.Stacks[0];
        Simulation.Step(s, new Command[] { new MoveCommand(0, st.Id, b) });
        Simulation.Step(s, new Command[] { new MoveCommand(0, st.Id, c, Queue: true) });
        Run(s, 28 + 30);                         // first hop done at tick 30, second at tick 60
        Assert.Equal(c, st.Tile);
        Assert.Equal(0, s.TileOwner[b]);
        Assert.Equal(0, s.TileOwner[c]);
    }

    [Fact]
    public void StopCancelsTheRoute()
    {
        var s = NewGame();
        var (a, b) = NeighbourPair(s);
        var st = s.Stacks[0];
        Simulation.Step(s, new Command[] { new MoveCommand(0, st.Id, b) });
        Simulation.Step(s, new Command[] { new StopCommand(0, st.Id) });
        Run(s, 60);
        Assert.Equal(a, st.Tile);
    }

    [Fact]
    public void PlayersCannotCommandEnemyStacks()
    {
        var s = NewGame();
        var (a, b) = NeighbourPair(s);
        var mine = s.Stacks[0];
        Simulation.Step(s, new Command[] { new MoveCommand(1, mine.Id, b) });
        Run(s, 40);
        Assert.Equal(a, mine.Tile);
    }

    // ---- determinism ----------------------------------------------------------------------

    private static ulong ScriptedRun(ulong scriptSeed, int ticks)
    {
        var s = NewGame();
        var rng = new Pcg32(scriptSeed);
        for (int t = 0; t < ticks; t++)
        {
            var cmds = new List<Command>();
            if (t % 20 == 0)
            {
                foreach (var st in s.Stacks.ToList())
                    cmds.Add(new MoveCommand(st.Owner, st.Id, rng.NextInt(0, s.Map.TileCount - 1), rng.NextInt(0, 3) == 0));
            }
            Simulation.Step(s, cmds);
        }
        return s.Hash();
    }

    [Fact]
    public void SameCommandLogGivesTheSameStateHash()
    {
        Assert.Equal(ScriptedRun(77, 1500), ScriptedRun(77, 1500));
    }

    [Fact]
    public void DifferentCommandLogsGiveDifferentHashes()
    {
        Assert.NotEqual(ScriptedRun(77, 1500), ScriptedRun(78, 1500));
    }

    [Fact]
    public void HashChangesAsTheMatchProgresses()
    {
        var s = NewGame();
        ulong h0 = s.Hash();
        Run(s, 1);
        Assert.NotEqual(h0, s.Hash());
    }
}
