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

    // ---- encirclement, entrenchment, attackers, flanking ------------------------------------

    private static int FarFromBoth(GameState s)
    {
        for (int t = 0; t < s.Map.TileCount; t++)
        {
            if (!s.Map.Passable(t) || s.Players[1].Capital == t || s.Players[0].Capital == t || s.Players[2].Capital == t) continue;
            if (s.Map.Neighbors[t].Any(n => n == s.Players[1].Capital || n == s.Players[0].Capital || n == s.Players[2].Capital)) continue;
            if (s.Players.Any(p => s.Map.Neighbors[t].Any(n => s.Map.Neighbors[n].Contains(p.Capital)))) continue;
            return t;
        }
        throw new InvalidOperationException();
    }

    private static GameState Surrounded(out int pocket)
    {
        var s = NewMatch(3);
        pocket = FarFromBoth(s);
        s.Stacks.Clear();
        for (int t = 0; t < s.Map.TileCount; t++)
            if (s.Map.Passable(t) && t != pocket && t != s.Players[1].Capital && t != s.Players[2].Capital) s.TileOwner[t] = 0;
        s.TileOwner[pocket] = -1;
        s.AddStack(1, s.Players[1].Capital, 10);   // keeps player 1 alive
        return s;
    }

    [Fact]
    public void EncircledTilesConnectedToTheCapitalAreCaptured()
    {
        var s = Surrounded(out int pocket);
        Run(s, 1);
        Assert.Equal(0, s.TileOwner[pocket]);
        Assert.NotEqual(0, s.TileOwner[s.Players[1].Capital]);   // a capital is never swallowed
    }

    [Fact]
    public void EncirclementDoesNothingWithAnEnemyInside()
    {
        var s = Surrounded(out int pocket);
        s.AddStack(1, pocket, 5);
        Run(s, 1);
        Assert.NotEqual(0, s.TileOwner[pocket]);
    }

    [Fact]
    public void EncirclementNeedsTheRingConnectedToTheCapital()
    {
        var s = NewMatch(3);
        s.Stacks.Clear();
        s.AddStack(1, s.Players[1].Capital, 10);
        int pocket = FarFromBoth(s);
        foreach (int n in s.Map.Neighbors[pocket]) s.TileOwner[n] = 0;   // ring only, cut off from capital
        Run(s, 1);
        Assert.Equal(-1, s.TileOwner[pocket]);
    }

    [Fact]
    public void StackEntrenchesTenPercentEveryTenSecondsUpToFifty()
    {
        var s = NewMatch(3);
        var st = s.Stacks.First(x => x.Owner == 0);
        Run(s, 99);
        Assert.Equal(0, st.Entrench);
        Run(s, 1);
        Assert.Equal(100, st.Entrench);
        Run(s, 100);
        Assert.Equal(200, st.Entrench);
        Run(s, 600);
        Assert.Equal(500, st.Entrench);
    }

    [Fact]
    public void MovingRemovesEntrenchment()
    {
        var s = NewMatch(3);
        var st = s.Stacks.First(x => x.Owner == 0);
        Run(s, 250);
        Assert.Equal(200, st.Entrench);
        Simulation.Step(s, new Command[] { new MoveCommand(0, st.Id, s.Map.Neighbors[st.Tile][0]) });
        Assert.Equal(0, st.Entrench);
        Assert.Equal(0, st.StillTicks);
    }

    [Fact]
    public void BeingAttackedPausesTheEntrenchTimerButKeepsTheBonus()
    {
        var s = NewMatch(3);
        var st = s.Stacks.First(x => x.Owner == 0);
        Run(s, 150);
        Assert.Equal(100, st.Entrench);
        int still = st.StillTicks;
        s.AddStack(1, st.Tile, 60);
        Run(s, 30);
        Assert.Equal(still, st.StillTicks);
        Assert.Equal(100, st.Entrench);
    }

    private static (long attackerLoss, long defenderLoss) OneTickClash(int defenderEntrench)
    {
        var s = NewMatch(3);
        s.Stacks.Clear();
        int a = s.Players[0].Capital;
        int b = s.Map.Neighbors[a][0];
        var atk = s.AddStack(0, a, 10);
        var def = s.AddStack(1, b, 10);
        def.Entrench = defenderEntrench;
        long ah = atk.Hp, dh = def.Hp;
        Simulation.Step(s, new Command[] { new MoveCommand(0, atk.Id, b) });
        return (ah - atk.Hp, dh - def.Hp);
    }

    [Fact]
    public void AttackersLoseSlightlyMoreThanDefenders()
    {
        var (atk, def) = OneTickClash(0);
        Assert.True(atk > def);
        Assert.True(atk * 100 < def * 125);     // "slightly": under 25% more
    }

    [Fact]
    public void EntrenchmentCutsDamageTaken()
    {
        var (_, plain) = OneTickClash(0);
        var (_, dug) = OneTickClash(500);
        Assert.True(dug * 3 <= plain * 2 + 3);  // +50% defence -> two thirds
    }

    [Fact]
    public void BeingAttackedFromTwoFrontsHurtsMoreThanTwiceAsMuch()
    {
        long Loss(int fronts)
        {
            var s = NewMatch(3);
            s.Stacks.Clear();
            int target = Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.Neighbors[t].Length >= 3);
            var victim = s.AddStack(1, target, 20);
            long before = victim.Hp;
            foreach (int n in s.Map.Neighbors[target].Take(fronts))
            {
                var a = s.AddStack(0, n, 10);
                a.Target = target;
            }
            Simulation.Step(s, None);
            return before - victim.Hp;
        }
        long one = Loss(1), two = Loss(2), three = Loss(3);
        Assert.True(two > one * 2);
        Assert.True(three > two);
    }

    private static int MineFarFrom(GameState s) =>
        Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.Deposit[t] && s.Players.All(p => p.Capital != t));

    [Fact]
    public void RecruitsAppearOnAnOwnedMineWhenTheCapitalIsBusier()
    {
        var s = NewMatch(3);
        int mine = MineFarFrom(s);
        s.TileOwner[mine] = 0;
        s.Players[0].Supply = Rules.StackCostMilli;
        var spawn = Simulation.Step(s, None).OfType<StackSpawned>().Single();
        Assert.Equal(mine, spawn.Tile);          // capital already holds a stack, the mine holds none
    }

    [Fact]
    public void RecruitsNeverAppearOnOrdinaryTiles()
    {
        var s = NewMatch(3);
        s.TileOwner[s.Players[0].Capital] = 1;   // capital lost
        s.Stacks.RemoveAll(x => x.Owner == 0);
        int plain = Enumerable.Range(0, s.Map.TileCount).First(t => !s.Map.Deposit[t] && s.TileOwner[t] == -1);
        s.TileOwner[plain] = 0;
        s.Players[0].Supply = Rules.StackCostMilli * 3;
        var events = Simulation.Step(s, None);
        Assert.DoesNotContain(events, e => e is StackSpawned { Owner: 0 });
        Assert.True(s.Players[0].Supply >= Rules.StackCostMilli * 3);   // nothing was spent
    }

    [Fact]
    public void NoRecruitsOnATileWithAnEnemyStack()
    {
        var s = NewMatch(3);
        s.AddStack(1, s.Players[0].Capital, 3);
        s.Players[0].Supply = Rules.StackCostMilli;
        Assert.DoesNotContain(Simulation.Step(s, None), e => e is StackSpawned { Owner: 0 });
    }
}
