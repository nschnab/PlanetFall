using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;
using Xunit;
using Xunit.Abstractions;

namespace Planetfall.Sim.Tests;

public class AiTests
{
    private static readonly IReadOnlyList<Command> None = Array.Empty<Command>();
    private readonly ITestOutputHelper _out;

    public AiTests(ITestOutputHelper output) => _out = output;

    // ---- personalities (data schema) ------------------------------------------------------

    [Fact]
    public void ShippedPersonalitiesAreTheFiveFromThePlan()
    {
        Assert.Equal(new[] { "builder", "conqueror", "diplomat", "opportunist", "zealot" }, Personality.All.Select(p => p.Id).OrderBy(x => x).ToArray());
        Assert.Equal(Personality.All.Max(p => p.PainThreshold), Personality.ById("zealot").PainThreshold);   // zealot: very high
        Assert.Equal(Personality.All.Min(p => p.PainThreshold), Personality.ById("builder").PainThreshold); // builder: low
    }

    private const string Good = """{ "id": "x", "name": "X", "expand": 1000, "develop": 1000, "defend": 1000, "attack": 1000, "muster": 5, "riskPermille": 1000, "painThreshold": 700, "engineers": 1 }""";

    [Fact]
    public void AValidPersonalityParses() =>
        Assert.Equal("x", Personality.Parse("{ \"personalities\": [" + Good + "] }").Single().Id);

    [Theory]
    [InlineData("""{ "personalities": [] }""")]
    [InlineData("""{ "personalities": [ { "id": "", "name": "X", "expand": 1, "develop": 1, "defend": 1, "attack": 1, "muster": 1, "riskPermille": 100, "painThreshold": 1, "engineers": 0 } ] }""")]
    [InlineData("""{ "personalities": [ { "id": "x", "name": "X", "expand": 0, "develop": 1, "defend": 1, "attack": 1, "muster": 1, "riskPermille": 100, "painThreshold": 1, "engineers": 0 } ] }""")]
    [InlineData("""{ "personalities": [ { "id": "x", "name": "X", "expand": 1, "develop": 1, "defend": 1, "attack": 1, "muster": 1, "riskPermille": 100, "painThreshold": 0, "engineers": 0 } ] }""")]
    [InlineData("""{ "personalities": [ { "id": "x", "name": "X", "expand": 1, "develop": 1, "defend": 1, "attack": 1, "muster": 0, "riskPermille": 100, "painThreshold": 5, "engineers": 0 } ] }""")]
    public void BadPersonalitiesAreRejected(string json) => Assert.Throws<FormatException>(() => Personality.Parse(json));

    [Fact]
    public void DuplicatePersonalityIdsAreRejected() =>
        Assert.Throws<FormatException>(() => Personality.Parse("{ \"personalities\": [" + Good + "," + Good + "] }"));

    [Fact]
    public void PersonalityComesFromTheSeedAndSpreadsAcrossAllFive()
    {
        Assert.Equal(Personality.ForSeed(9, 0).Id, Personality.ForSeed(9, 0).Id);
        var seen = Enumerable.Range(1, 60).SelectMany(sd => new[] { Personality.ForSeed((ulong)sd, 0).Id, Personality.ForSeed((ulong)sd, 1).Id }).Distinct();
        Assert.Equal(5, seen.Count());
    }

    // ---- sim support ----------------------------------------------------------------------

    [Fact]
    public void AStackThatIsBuildingDoesNotMerge()
    {
        var s = AiMatch.Setup(3, out _);
        int cap = s.Players[0].Capital;
        s.Players[0].Supply = 100_000;
        var eng = s.AddStack(0, cap, new Dictionary<string, int> { ["engineer"] = 1 });
        s.Constructions.Add(new Construction { Tile = cap, Owner = 0, StackId = eng.Id, Structure = "fortress", TicksLeft = 100 });
        Simulation.Step(s, None);
        Assert.Contains(s.Stacks, x => x.Id == eng.Id);       // the home army did not swallow it
    }

    [Fact]
    public void IncomeMatchesWhatTheEconomyPays()
    {
        var s = AiMatch.Setup(4, out _);
        var p = s.Players[0];
        s.Stacks.RemoveAll(x => x.Owner == 0);                  // no upkeep, so the whole income lands in the treasury
        var income = Simulation.Income(s, p);
        long before = p.Supply;
        Simulation.Step(s, None);
        Assert.Equal(income.Alloy, p.Supply - before);
    }

    // ---- hopelessness and surrender -------------------------------------------------------

    private static GameState Crushed(out RivalAi loser, string personality = "builder")
    {
        var s = AiMatch.Setup(3, out _);
        loser = new RivalAi(0, Personality.ById(personality));
        // Player 0 is reduced to its capital, with an enemy army parked next door and no defenders.
        int cap = s.Players[0].Capital;
        for (int t = 0; t < s.Map.TileCount; t++) if (t != cap) s.TileOwner[t] = 1;
        s.Stacks.RemoveAll(x => x.Owner == 0);
        s.AddStack(1, s.Map.Neighbors[cap][0], 40);
        return s;
    }

    [Fact]
    public void AHealthyNationIsNotHopeless()
    {
        var s = AiMatch.Setup(3, out var ais);
        foreach (var a in ais) Assert.True(a.Hopelessness(s, out _) < 300);
    }

    [Fact]
    public void ACrushedNationScoresHigh()
    {
        var s = Crushed(out var ai);
        int h = ai.Hopelessness(s, out string why);
        _out.WriteLine($"{h}: {why}");
        Assert.True(h >= 800);
    }

    [Fact]
    public void ALiveCapitalAndArmyStaysBelowEveryThreshold()
    {
        var s = AiMatch.Setup(3, out var ais);
        int cap = s.Players[0].Capital;
        for (int t = 0; t < s.Map.TileCount; t++) if (t != cap) s.TileOwner[t] = 1;   // only the capital left, but garrisoned and unthreatened
        Assert.True(ais[0].Hopelessness(s, out _) < Personality.All.Min(p => p.PainThreshold));
    }

    [Fact]
    public void NeverSurrendersBeforeMinuteTenEvenWhenHopeless()
    {
        var s = Crushed(out var ai);
        var sur = new List<int>();
        for (int i = 0; i < RivalAi.MinSurrenderTick - 1; i++)
        {
            s.Tick = i;
            foreach (var c in ai.Think(s)) if (c is SurrenderCommand) sur.Add(i);
        }
        Assert.Empty(sur);
    }

    [Fact]
    public void SurrendersOnlyAfterSixtySecondsAboveTheThreshold()
    {
        var s = Crushed(out var ai);
        s.Tick = RivalAi.MinSurrenderTick;      // minute 10: the floor no longer blocks
        int first = -1;
        for (int i = 0; i < 2000 && first == -1; i++)
        {
            s.Tick = RivalAi.MinSurrenderTick + i;
            if (ai.Think(s).Any(c => c is SurrenderCommand)) first = i;
        }
        Assert.InRange(first, RivalAi.SurrenderPersistTicks - 10, RivalAi.SurrenderPersistTicks + 20);
        Assert.True(ai.Debug.Hopelessness >= ai.Personality.PainThreshold);
    }

    [Theory]
    [InlineData("builder", true)]
    [InlineData("zealot", false)]
    public void ALosingNationWithAnArmyGivesUpOnlyIfItsPersonalityAllowsIt(string personality, bool surrenders)
    {
        // Land and income gone, capital held but unthreatened, army outnumbered five to one.
        var s = AiMatch.Setup(3, out _);
        var ai = new RivalAi(0, Personality.ById(personality));
        int cap = s.Players[0].Capital;
        for (int t = 0; t < s.Map.TileCount; t++) if (t != cap) s.TileOwner[t] = 1;
        var far = Enumerable.Range(0, s.Map.TileCount).First(t => s.TileOwner[t] == 1 && t != s.Players[1].Capital && !s.Map.Neighbors[cap].Contains(t) && t != cap);
        s.AddStack(1, far, 50);

        bool gaveUp = false;
        for (int i = 0; i < 3000 && !gaveUp; i++)
        {
            s.Tick = RivalAi.MinSurrenderTick + i;
            gaveUp = ai.Think(s).Any(c => c is SurrenderCommand);
        }
        _out.WriteLine($"{personality}: hopelessness {ai.Debug.Hopelessness} vs threshold {ai.Personality.PainThreshold}");
        Assert.Equal(surrenders, gaveUp);
    }

    [Fact]
    public void RecoveringResetsTheSurrenderClock()
    {
        var s = Crushed(out var ai);
        for (int i = 0; i < 400; i++) { s.Tick = RivalAi.MinSurrenderTick + i; ai.Think(s); }
        Assert.True(ai.Debug.SurrenderInTicks > 0);
        s.Stacks.Add(new Stack { Id = 9999, Owner = 0, Tile = s.Players[0].Capital, Hp = 60_000, MaxHp = 60_000, Units = { ["infantry"] = 60 } });
        s.Stacks.RemoveAll(x => x.Owner == 1);
        s.Tick = RivalAi.MinSurrenderTick + 500;
        ai.Think(s);
        Assert.Equal(-1, ai.Debug.SurrenderInTicks);
    }

    // ---- behaviour ------------------------------------------------------------------------

    [Fact]
    public void RivalsRecruitMixedUnitsAndBuildStructures()
    {
        var s = AiMatch.Setup(11, out var ais);
        var recruited = new HashSet<string>();
        int built = 0;
        for (int i = 0; i < 12_000 && !s.Over; i++)
        {
            var cmds = ais.SelectMany(a => a.Think(s)).ToList();
            foreach (var c in cmds.OfType<RecruitCommand>()) recruited.Add(c.UnitId);
            built += Simulation.Step(s, cmds).Count(e => e is StructureBuilt);
        }
        _out.WriteLine($"recruited: {string.Join(",", recruited.OrderBy(x => x))}; structures built: {built}");
        Assert.True(recruited.Count >= 4, "should field at least four unit types");
        Assert.Contains("engineer", recruited);
        Assert.True(built > 0, "engineers should finish at least one structure");
    }

    [Fact]
    public void AiPlayIsDeterministic()
    {
        ulong Run()
        {
            var s = AiMatch.Setup(7, out var ais);
            for (int i = 0; i < 4000 && !s.Over; i++) Simulation.Step(s, ais.SelectMany(a => a.Think(s)).ToList());
            return s.Hash();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void RivalsNeverCheatTheirEconomy()
    {
        var s = AiMatch.Setup(5, out var ais);
        for (int i = 0; i < 3000 && !s.Over; i++)
        {
            Simulation.Step(s, ais.SelectMany(a => a.Think(s)).ToList());
            foreach (var p in s.Majors) { Assert.True(p.Supply >= 0); Assert.True(p.Energy >= 0); }
        }
    }

    [Fact]
    public void StandardMapMatchesAllFinishWithNoStuckStacksAndPlausibleSurrenders()
    {
        const int Matches = 10;
        var results = Enumerable.Range(1, Matches).Select(i => AiMatch.Play((ulong)i, 120 * 600)).ToList();
        foreach (var r in results)
            _out.WriteLine($"seed {r.Seed}: {string.Join(" v ", r.Personalities)} winner {r.Winner} ({r.EndReason}) {r.Ticks / 600.0:F1} min stuck {r.StuckStacks} {r.StuckDetail}");
        Assert.All(results, r => Assert.NotEqual(-1, r.Winner));
        Assert.All(results, r => Assert.Equal(0, r.StuckStacks));
        Assert.All(results.Where(r => r.EndReason == "surrender"), r => Assert.True(r.Ticks >= RivalAi.MinSurrenderTick));
    }
}
