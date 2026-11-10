using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;
using Xunit;

namespace Planetfall.Sim.Tests;

public class RosterTests
{
    private static readonly Leader Commander = Leader.Parse("""{ "id": "commander", "name": "Field Commander" }""");
    private static readonly IReadOnlyList<Command> None = Array.Empty<Command>();

    private static GameState NewGame(ulong seed = 3, Leader? p0 = null, Catalog? catalog = null)
    {
        var s = GameState.New(MapGenerator.Generate(seed), p0 ?? Commander, Commander);
        if (catalog != null) s.Catalog = catalog;
        s.Stacks.Clear();
        s.Players[0].AutoRecruit = false;
        s.Players[1].AutoRecruit = false;
        return s;
    }

    private static void Run(GameState s, int ticks) { for (int i = 0; i < ticks; i++) Simulation.Step(s, None); }

    /// <summary>Two adjacent plains tiles away from both capitals, so terrain defence never muddies a result.</summary>
    private static (int a, int b) PlainsPair(GameState s)
    {
        for (int a = 0; a < s.Map.TileCount; a++)
        {
            if (s.Map.Terrain[a] != Terrain.Plains || s.Players.Any(p => p.Capital == a)) continue;
            foreach (int b in s.Map.Neighbors[a])
                if (s.Map.Terrain[b] == Terrain.Plains && s.Players.All(p => p.Capital != b)) return (a, b);
        }
        throw new InvalidOperationException("no plains pair");
    }

    private static Stack Put(GameState s, int owner, int tile, string unit, int count)
    {
        s.TileOwner[tile] = owner;
        return s.AddStack(owner, tile, new Dictionary<string, int> { [unit] = count });
    }

    /// <summary>Hp the victim loses in one tick when attacker hops into it.</summary>
    private static long OneTickLoss(GameState s, Stack attacker, Stack victim)
    {
        attacker.Target = victim.Tile;
        long before = victim.Hp;
        Simulation.Step(s, None);
        return before - victim.Hp;
    }

    // ---- data & schema --------------------------------------------------------------------

    [Fact]
    public void ShippedDataLoadsAndCoversAllFiveRoles()
    {
        var c = Catalog.Default;
        foreach (var role in Catalog.KnownRoles) Assert.Contains(c.Units.Values, u => u.Roles.Contains(role));
        Assert.Equal(3, c.Structures.Count);
        Assert.Equal(5, c.Terrain.Count);
        foreach (var d in c.Combat.DamageTypes)
        foreach (var a in c.Combat.ArmorTypes)
            Assert.True(c.Combat.MatrixPermille[d].ContainsKey(a));
    }

    [Theory]
    [InlineData("""{ "id": "x", "name": "X", "roles": ["wizard"], "attack": { "type": "kinetic", "permille": 1 }, "armor": "light" }""")]
    [InlineData("""{ "id": "x", "name": "X", "roles": ["infantry"], "attack": { "type": "laser", "permille": 1 }, "armor": "light" }""")]
    [InlineData("""{ "id": "x", "name": "X", "roles": ["infantry"], "attack": { "type": "kinetic", "permille": 1 }, "armor": "plate" }""")]
    [InlineData("""{ "id": "x", "name": "X", "roles": [], "attack": { "type": "kinetic", "permille": 1 }, "armor": "light" }""")]
    [InlineData("""{ "id": "infantry", "name": "Dupe", "roles": ["infantry"], "attack": { "type": "kinetic", "permille": 1 }, "armor": "light" }""")]
    [InlineData("""{ "id": "x", "name": "X", "roles": ["infantry"], "strength": 0, "attack": { "type": "kinetic", "permille": 1 }, "armor": "light" }""")]
    [InlineData("""{ "id": "x", "name": "X", "roles": ["infantry"], "speedPermille": 0, "attack": { "type": "kinetic", "permille": 1 }, "armor": "light" }""")]
    [InlineData("""{ "id": "", "name": "X" }""")]
    public void BadUnitJsonIsRejected(string json) =>
        Assert.Throws<FormatException>(() => Catalog.Default.With(extraUnits: new[] { json }));

    [Fact]
    public void BadStructureAndCombatJsonAreRejected()
    {
        Assert.Throws<FormatException>(() => Catalog.Default.With(extraStructures: new[] { """{ "id": "w", "name": "W", "onCapture": "explode" }""" }));
        Assert.Throws<FormatException>(() => Catalog.Default.With(extraStructures: new[] { """{ "id": "w", "name": "W", "armorOverride": "plate" }""" }));
        Assert.Throws<FormatException>(() => Catalog.Parse(
            new[] { """{ "id": "u", "name": "U", "roles": ["infantry"], "attack": { "type": "kinetic", "permille": 1 }, "armor": "light" }""" },
            Array.Empty<string>(),
            """{ "plains": {}, "forest": {}, "highlands": {}, "mountains": {}, "water": {} }""",
            """{ "damageTypes": ["kinetic"], "armorTypes": ["light", "heavy"], "matrixPermille": { "kinetic": { "light": 1000 } } }"""));
    }

    [Fact]
    public void ModifierKeysAreValidatedAgainstTheCatalog()
    {
        var c = Catalog.Default;
        foreach (var k in new[] { "yield.energy", "unit.attack.armor", "unit.hp.heavy", "unit.speed.all", "build.cost.recon", "build.time.depot", "unlock.unit.armor", "upkeep.energy", "fort.max", "vision.range" })
            Assert.True(c.IsKnownKey(k), k);
        foreach (var k in new[] { "yield.gold", "unit.attack.dragon", "build.time.warpgate", "nonsense" })
            Assert.False(c.IsKnownKey(k), k);
        var bad = Leader.Parse("""{ "id": "b", "name": "B", "modifiers": [ { "key": "unit.attack.dragon", "op": "pct", "value": 100 } ] }""");
        Assert.Single(c.Validate(bad));
        Assert.Empty(c.Validate(Commander));
    }

    [Fact]
    public void ANewUnitIsDataOnlyAndPlaysWithoutCodeChanges()
    {
        var cat = Catalog.Default.With(extraUnits: new[]
        {
            """
            { "id": "heavy_tank", "name": "Heavy Tank", "roles": ["armor"], "strength": 5,
              "attack": { "type": "explosive", "permille": 1200 }, "armor": "armored",
              "upkeepMilli": 10, "cost": { "alloy": 20000 }, "tags": ["heavy"] }
            """,
        });
        Assert.True(cat.IsKnownKey("unit.attack.heavy_tank"));
        var s = NewGame(catalog: cat);
        s.Players[0].Supply = 50_000;
        Simulation.Step(s, new Command[] { new RecruitCommand(0, "heavy_tank") });
        var st = Assert.Single(s.Stacks);
        Assert.Equal(1, st.Units["heavy_tank"]);
        Assert.Equal(5 * Rules.HpUnit, st.MaxHp);
        Assert.True(s.Players[0].Supply < 50_000 - 19_000);
    }

    // ---- combat ---------------------------------------------------------------------------

    private static long Matchup(string attackerUnit, string defenderUnit, int atkCount, int defCount)
    {
        var s = NewGame();
        var (a, b) = PlainsPair(s);
        return OneTickLoss(s, Put(s, 0, a, attackerUnit, atkCount), Put(s, 1, b, defenderUnit, defCount));
    }

    [Fact]
    public void TheMatrixGivesHardCounters()
    {
        // Kinetic fire is poor against armor; explosive is strong against it.
        Assert.True(Matchup("infantry", "armor", 3, 2) * 3 < Matchup("infantry", "infantry", 3, 6) * 2);
        Assert.True(Matchup("armor", "armor", 2, 2) > Matchup("armor", "infantry", 2, 6));
    }

    [Fact]
    public void ArmorIsPoorInForestAndHighlands()
    {
        var s = NewGame();
        int forest = Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.Terrain[t] == Terrain.Forest && s.Map.Neighbors[t].Any(n => s.Map.Terrain[n] == Terrain.Plains));
        int plain = s.Map.Neighbors[forest].First(n => s.Map.Terrain[n] == Terrain.Plains);

        long Hit(int attackerTile, int victimTile, string victimUnit)
        {
            var g = NewGame();
            return OneTickLoss(g, Put(g, 0, attackerTile, "armor", 2), Put(g, 1, victimTile, victimUnit, 6));
        }
        // Attacking into forest uses the forest attack penalty (and forest defence); onto plains has neither.
        Assert.True(Hit(plain, forest, "infantry") < Hit(forest, plain, "infantry"));
    }

    [Fact]
    public void ArtilleryNextToABattleSupportsItButIsWeakWhenEngaged()
    {
        var s = NewGame();
        var (a, b) = PlainsPair(s);
        int behind = s.Map.Neighbors[b].First(n => n != a);
        var atk = Put(s, 0, a, "infantry", 5);
        var def = Put(s, 1, b, "infantry", 5);
        Put(s, 0, behind, "artillery", 3);
        long supported = OneTickLoss(s, atk, def);

        var s2 = NewGame();
        var alone = OneTickLoss(s2, Put(s2, 0, a, "infantry", 5), Put(s2, 1, b, "infantry", 5));
        Assert.True(supported > alone, $"supported {supported} vs alone {alone}");

        // The same guns in a hand-to-hand fight hit at the engaged rate (400 vs 1000 permille).
        var s3 = NewGame();
        long engaged = OneTickLoss(s3, Put(s3, 0, a, "artillery", 3), Put(s3, 1, b, "infantry", 6));
        var cat = Catalog.Default.With();
        Assert.Equal(400, cat.Units["artillery"].EngagedAttackPermille);
        Assert.True(engaged > 0);
        // Same strength of infantry hits harder than engaged artillery of equal strength after matrix (kinetic 1000 vs
        // explosive 800 vs light, x1300 attack x400 engaged).
        var s4 = NewGame();
        long inf = OneTickLoss(s4, Put(s4, 0, a, "infantry", 6), Put(s4, 1, b, "infantry", 6));
        Assert.True(engaged * 2 < inf * 3 / 1 + 1 && engaged < inf);
    }

    [Fact]
    public void ReconIsFastAndArtilleryAndEngineersCannotCapture()
    {
        var s = NewGame();
        var (a, b) = PlainsPair(s);
        Assert.Equal(1500, Simulation.StackSpeed(s, Put(s, 0, a, "recon", 1)));
        s.Stacks.Clear();
        Assert.Equal(800, Simulation.StackSpeed(s, Put(s, 0, a, "artillery", 1)));
        var mixed = s.AddStack(0, a, new Dictionary<string, int> { ["recon"] = 2, ["artillery"] = 1 });
        Assert.Equal(800, Simulation.StackSpeed(s, mixed));

        s.Stacks.Clear();
        s.TileOwner[b] = -1;
        s.AddStack(0, b, new Dictionary<string, int> { ["artillery"] = 2 });
        Simulation.Step(s, None);
        Assert.Equal(-1, s.TileOwner[b]);
        s.Stacks.Clear();
        s.AddStack(0, b, new Dictionary<string, int> { ["engineer"] = 1 });
        Simulation.Step(s, None);
        Assert.Equal(-1, s.TileOwner[b]);
        s.Stacks.Clear();
        s.AddStack(0, b, new Dictionary<string, int> { ["recon"] = 1 });
        Simulation.Step(s, None);
        Assert.Equal(0, s.TileOwner[b]);
    }

    // ---- economy --------------------------------------------------------------------------

    private static void OwnPath(GameState s, int player, int to)
    {
        foreach (int t in Pathfinder.Find(s.Map.Neighbors, s.Players[player].Capital, to)) s.TileOwner[t] = player;
    }

    [Fact]
    public void ThreeResourcesComeFromTheirOwnSources()
    {
        var s = NewGame();
        int energyDep = Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.DepositKinds[t] == DepositKind.Energy);
        int relic = Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.DepositKinds[t] == DepositKind.Relic);
        OwnPath(s, 0, energyDep);
        OwnPath(s, 0, relic);

        var p = s.Players[0];
        long e0 = p.Energy, i0 = p.Influence;
        Simulation.Step(s, None);
        Assert.True(p.Energy - e0 >= Rules.CapitalEnergyMilli + Rules.EnergyDepositMilli);
        Assert.Equal(Rules.RelicInfluenceMilli, p.Influence - i0);
        Assert.Equal(0, s.Players[1].Influence);
    }

    [Fact]
    public void UpkeepStarvesAnOverbuiltArmyAndHaltsRecruitingAndHalvesAlloy()
    {
        var s = NewGame();
        var p = s.Players[0];
        Put(s, 0, p.Capital, "armor", 20);          // 20 x 8 = 160 energy per tick against a 30 income
        Simulation.Step(s, None);
        Assert.True(p.Starved);
        Assert.Equal(0, p.Energy);

        p.Supply = 100_000;
        Simulation.Step(s, new Command[] { new RecruitCommand(0, "infantry") });
        Assert.Single(s.Stacks);                    // recruiting refused while starved

        s.Stacks.Clear();
        p.Supply = 0;
        Simulation.Step(s, None);
        Assert.False(p.Starved);
        long free = p.Supply;

        Put(s, 0, p.Capital, "armor", 20);
        p.Supply = 0;
        Simulation.Step(s, None);
        Assert.Equal(free / 2, p.Supply);           // alloy halved while starved
    }

    [Fact]
    public void WoundedStacksCostLessUpkeep()
    {
        var s = NewGame();
        var st = Put(s, 0, s.Players[0].Capital, "armor", 4);
        long full = Simulation.Upkeep(s, s.Players[0]);
        st.Hp = st.MaxHp / 2;
        Assert.True(Simulation.Upkeep(s, s.Players[0]) < full);
    }

    [Fact]
    public void TilesCutOffFromTheCapitalYieldHalf()
    {
        var s = NewGame();
        int far = Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.Passable(t) && !s.Map.Deposit[t] && s.Map.Terrain[t] != Terrain.Forest
            && Pathfinder.Find(s.Map.Neighbors, s.Players[0].Capital, t).Count > 3);
        long before = s.Players[0].Supply;
        Simulation.Step(s, None);
        long baseline = s.Players[0].Supply - before;
        s.TileOwner[far] = 0;
        before = s.Players[0].Supply;
        Simulation.Step(s, None);
        Assert.Equal(baseline + Rules.TileYieldMilli * Rules.UnlinkedPermille / 1000, s.Players[0].Supply - before);
    }

    // ---- recruiting & structures ----------------------------------------------------------

    [Fact]
    public void RecruitingAnyRoleCostsItsAlloyAndAppearsOnASpawnSite()
    {
        foreach (var unit in Catalog.Default.Units.Values)
        {
            var s = NewGame();
            s.Players[0].Supply = 100_000;
            var ev = Simulation.Step(s, new Command[] { new RecruitCommand(0, unit.Id) });
            var spawn = Assert.Single(ev.OfType<StackSpawned>());
            Assert.Equal(s.Players[0].Capital, spawn.Tile);
            Assert.Equal(1, s.FindStack(spawn.StackId)!.Units[unit.Id]);
            Assert.True(s.Players[0].Supply <= 100_000 - unit.Cost.Alloy + 40);   // plus one tick of income
        }
    }

    [Fact]
    public void RecruitingFailsWithoutAlloyOrOnForeignGround()
    {
        var s = NewGame();
        Simulation.Step(s, new Command[] { new RecruitCommand(0, "armor") });
        Assert.Empty(s.Stacks);
        s.Players[0].Supply = 100_000;
        Simulation.Step(s, new Command[] { new RecruitCommand(0, "armor", s.Players[1].Capital) });
        Assert.Empty(s.Stacks);
    }

    [Fact]
    public void AnEngineerBuildsADepotWhichIsASpawnSiteAndExtendsSupply()
    {
        var s = NewGame();
        var p = s.Players[0];
        int site = s.Map.Neighbors[p.Capital].First(n => s.Map.Passable(n));
        var eng = Put(s, 0, site, "engineer", 1);
        p.Supply = 100_000;
        var ev = Simulation.Step(s, new Command[] { new BuildCommand(0, eng.Id, "depot") });
        Assert.Contains(ev, e => e is BuildStarted);
        var def = Catalog.Default.Structures["depot"];
        Run(s, def.BuildTicks - 2);
        Assert.Null(s.Structure[site]);
        Run(s, 3);
        Assert.Equal("depot", s.Structure[site]);

        // It is a spawn site even when the capital is blocked by an enemy stack.
        p.Supply = 100_000;
        s.Stacks.Clear();
        s.Stacks.Add(new Stack { Id = 999, Owner = 1, Tile = p.Capital });
        Simulation.Step(s, new Command[] { new RecruitCommand(0, "infantry", site) });
        Assert.Contains(s.Stacks, x => x.Owner == 0 && x.Tile == site);

        // Supply radius: with the capital lost, ground beside the depot still counts as supplied.
        s.Stacks.Clear();
        s.TileOwner[p.Capital] = 1;
        int adj = s.Map.Neighbors[site].First(n => n != p.Capital);
        s.TileOwner[adj] = 0;
        var linked = Simulation.SupplyLinked(s, p);
        Assert.True(linked[site]);
        Assert.True(linked[adj]);
    }

    [Fact]
    public void BuildingNeedsAnEngineerOwnedGroundAndTheRightSite()
    {
        var s = NewGame();
        var p = s.Players[0];
        p.Supply = 200_000;
        var inf = Put(s, 0, p.Capital, "infantry", 3);
        Simulation.Step(s, new Command[] { new BuildCommand(0, inf.Id, "depot") });
        Assert.Empty(s.Constructions);

        s.Stacks.Clear();
        var eng = Put(s, 0, p.Capital, "engineer", 1);
        Simulation.Step(s, new Command[] { new BuildCommand(0, eng.Id, "extractor") });   // the capital is not a deposit
        Assert.Empty(s.Constructions);
        Simulation.Step(s, new Command[] { new BuildCommand(0, eng.Id, "fortress") });
        Assert.Single(s.Constructions);
        Simulation.Step(s, new Command[] { new BuildCommand(0, eng.Id, "depot") });       // slot already claimed
        Assert.Single(s.Constructions);
    }

    [Fact]
    public void ABuildPausesWhileTheEngineerMovesAndIsAbandonedIfTheTileIsLost()
    {
        var s = NewGame();
        var p = s.Players[0];
        p.Supply = 200_000;
        var eng = Put(s, 0, p.Capital, "engineer", 1);
        Simulation.Step(s, new Command[] { new BuildCommand(0, eng.Id, "depot") });
        int left = s.Constructions[0].TicksLeft;
        eng.Target = s.Map.Neighbors[p.Capital][0];       // walking away
        Run(s, 3);
        Assert.Equal(left, s.Constructions[0].TicksLeft);
        s.TileOwner[p.Capital] = 1;
        Simulation.Step(s, None);
        Assert.Empty(s.Constructions);
    }

    [Fact]
    public void AnExtractorDoublesADepositAndFortressesHardenTheTile()
    {
        var s = NewGame();
        var p = s.Players[0];
        int dep = Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.DepositKinds[t] == DepositKind.Alloy);
        OwnPath(s, 0, dep);
        Simulation.Step(s, None);
        long b = p.Supply; Simulation.Step(s, None); long plain = p.Supply - b;
        s.Structure[dep] = "extractor";
        b = p.Supply; Simulation.Step(s, None); long boosted = p.Supply - b;
        Assert.Equal(plain + Rules.TileYieldMilli * Rules.DepositMultiplier, boosted);

        // A fortress makes defenders "fortified" and adds defence, so kinetic fire does far less.
        var s1 = NewGame();
        var (a1, b1) = PlainsPair(s1);
        long open = OneTickLoss(s1, Put(s1, 0, a1, "infantry", 6), Put(s1, 1, b1, "infantry", 6));

        var s2 = NewGame();
        var (a2, b2) = PlainsPair(s2);
        var atk2 = Put(s2, 0, a2, "infantry", 6);
        var def2 = Put(s2, 1, b2, "infantry", 6);
        s2.Structure[b2] = "fortress";
        long fort = OneTickLoss(s2, atk2, def2);
        Assert.True(fort * 10 < open * 7, $"fortress {fort} vs open {open}");
    }

    [Fact]
    public void CapturedStructuresAreKeptOrDestroyedByType()
    {
        var s = NewGame();
        var (a, b) = PlainsPair(s);
        int c = s.Map.Neighbors[b].First(n => n != a && s.Map.Terrain[n] == Terrain.Plains && s.Players.All(p => p.Capital != n));
        s.TileOwner[b] = 1; s.Structure[b] = "depot";
        s.TileOwner[c] = 1; s.Structure[c] = "fortress";
        s.AddStack(0, b, new Dictionary<string, int> { ["infantry"] = 2 });
        s.AddStack(0, c, new Dictionary<string, int> { ["infantry"] = 2 });
        var ev = Simulation.Step(s, None);
        Assert.Equal(0, s.TileOwner[b]);
        Assert.Equal("depot", s.Structure[b]);
        Assert.Equal(0, s.TileOwner[c]);
        Assert.Null(s.Structure[c]);
        Assert.Contains(ev, e => e is StructureLost { Structure: "fortress" });
    }

    // ---- modifiers ------------------------------------------------------------------------

    [Fact]
    public void LeadersChangeRosterNumbersThroughDataOnly()
    {
        var leader = Leader.Parse("""
            { "id": "t", "name": "T", "modifiers": [
              { "key": "unit.attack.armor", "op": "pct", "value": 500 },
              { "key": "unit.hp.infantry", "op": "pct", "value": 1000 },
              { "key": "build.cost.recon", "op": "pct", "value": -500 },
              { "key": "upkeep.energy", "op": "flat", "value": -1 },
              { "key": "yield.influence", "op": "pct", "value": 1000 } ] }
            """);
        Assert.Empty(Catalog.Default.Validate(leader));

        var s = NewGame(p0: leader);
        var inf = Put(s, 0, s.Players[0].Capital, "infantry", 3);
        Assert.Equal(3 * Rules.HpUnit * 2, inf.MaxHp);

        long Hit(Leader l)
        {
            var g = NewGame(p0: l);
            var (a, b) = PlainsPair(g);
            return OneTickLoss(g, Put(g, 0, a, "armor", 2), Put(g, 1, b, "infantry", 6));
        }
        Assert.True(Hit(leader) > Hit(Commander) * 14 / 10);

        var g2 = NewGame(p0: leader);
        g2.Players[0].Supply = 10_000;
        Simulation.Step(g2, new Command[] { new RecruitCommand(0, "recon") });
        Assert.Single(g2.Stacks);
        Assert.True(g2.Players[0].Supply > 10_000 - 4000 * 6 / 10);
    }

    [Fact]
    public void LockedUnitsNeedAnUnlockModifier()
    {
        var cat = Catalog.Default.With(extraUnits: new[]
        {
            """{ "id": "elite", "name": "Elite", "roles": ["infantry"], "unlocked": false, "attack": { "type": "kinetic", "permille": 1500 }, "armor": "light", "cost": { "alloy": 1000 } }""",
        });
        var unlocker = Leader.Parse("""{ "id": "u", "name": "U", "modifiers": [ { "key": "unlock.unit.elite", "op": "flat", "value": 1 } ] }""");
        Assert.Empty(cat.Validate(unlocker));
        var locked = NewGame(catalog: cat);
        locked.Players[0].Supply = 50_000;
        Simulation.Step(locked, new Command[] { new RecruitCommand(0, "elite") });
        Assert.Empty(locked.Stacks);
        var open = NewGame(p0: unlocker, catalog: cat);
        open.Players[0].Supply = 50_000;
        Simulation.Step(open, new Command[] { new RecruitCommand(0, "elite") });
        Assert.Single(open.Stacks);
    }

    [Fact]
    public void TheStateHashCoversUnitsStructuresAndResources()
    {
        var a = NewGame();
        var b = NewGame();
        Put(a, 0, a.Players[0].Capital, "infantry", 2);
        Put(b, 0, b.Players[0].Capital, "recon", 2);
        Assert.NotEqual(a.Hash(), b.Hash());
        var c = NewGame(); var d = NewGame();
        d.Structure[d.Players[0].Capital] = "depot";
        Assert.NotEqual(c.Hash(), d.Hash());
        var e = NewGame(); var f = NewGame();
        f.Players[0].Influence = 5;
        Assert.NotEqual(e.Hash(), f.Hash());
    }
}
