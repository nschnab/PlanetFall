using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;
using Xunit;
using Xunit.Abstractions;

namespace Planetfall.Sim.Tests;

public class DiplomacyTests
{
    private readonly ITestOutputHelper _out;
    public DiplomacyTests(ITestOutputHelper output) => _out = output;

    private static List<SimEvent> Do(GameState s, params Command[] cmds) => Simulation.Step(s, cmds);

    private static void Run(GameState s, int ticks)
    {
        for (int i = 0; i < ticks; i++) Simulation.Step(s, Array.Empty<Command>());
    }

    private static GameState NewGame(ulong seed = 3)
    {
        var s = AiMatch.Setup(seed, out _);
        s.Players[0].Supply = 10_000_000;
        s.Players[0].Influence = 500_000;
        return s;
    }

    private static Player FirstMinor(GameState s, int skip = 0) => s.Players.Where(p => p.Minor).Skip(skip).First();

    /// <summary>Gifts and proposals, one step at a time, until the pair reaches <paramref name="goal"/>.</summary>
    private static void Befriend(GameState s, int player, int minor, Treaty goal)
    {
        for (int i = 0; i < 80 && s.Diplomacy.Get(player, minor) < goal; i++)
        {
            var next = s.Diplomacy.Get(player, minor) + 1;
            if (DiplomacySystem.Opinion(s, minor, player) >= DiplomacySystem.TreatyNeed[(int)next])
                Do(s, new ProposeTreatyCommand(player, minor, next));
            else
                Do(s, new GiftCommand(player, minor, 40));
        }
        Assert.Equal(goal, s.Diplomacy.Get(player, minor));
    }

    // ---- opinion --------------------------------------------------------------------------

    [Fact]
    public void NativesStartWaryAtWarAndGiftsWarmThem()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Assert.True(s.AtWar(0, m.Id));
        var before = DiplomacySystem.Opinion(s, m.Id, 0);
        Assert.Equal(Mood.Wary, DiplomacySystem.MoodOf(s, m.Id, 0));
        Do(s, new GiftCommand(0, m.Id, 40));
        Assert.True(DiplomacySystem.Opinion(s, m.Id, 0) > before);
    }

    [Fact]
    public void OpinionAlwaysListsItsCauses()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Do(s, new GiftCommand(0, m.Id, 20));
        var lines = new List<OpinionLine>();
        int total = DiplomacySystem.Opinion(s, m.Id, 0, lines);
        Assert.Equal(total, lines.Sum(l => l.Points));
        Assert.Contains(lines, l => l.Label == "Gifts");
    }

    [Fact]
    public void GiftsFadeOverTime()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Do(s, new GiftCommand(0, m.Id, 40));
        int warm = DiplomacySystem.Opinion(s, m.Id, 0);
        Run(s, 3 * DiplomacySystem.GiftDecayTicks);
        Assert.True(DiplomacySystem.Opinion(s, m.Id, 0) < warm);
    }

    [Fact]
    public void ConqueringAMinorAngersItsKinButNotOtherCultures()
    {
        var s = NewGame();
        var victim = FirstMinor(s);
        var kin = s.Players.First(p => p.Minor && p.Id != victim.Id && s.CultureOf(p.Id) == s.CultureOf(victim.Id));
        var stranger = s.Players.First(p => p.Minor && s.CultureOf(p.Id) != s.CultureOf(victim.Id));
        int kinBefore = DiplomacySystem.Opinion(s, kin.Id, 0), strangerBefore = DiplomacySystem.Opinion(s, stranger.Id, 0);
        DiplomacySystem.OnCapture(s, 0, victim.Id);
        Assert.True(DiplomacySystem.Opinion(s, kin.Id, 0) < kinBefore);
        Assert.Equal(strangerBefore, DiplomacySystem.Opinion(s, stranger.Id, 0));
        Assert.True(DiplomacySystem.Opinion(s, victim.Id, 0) < kinBefore);
    }

    // ---- treaties -------------------------------------------------------------------------

    [Fact]
    public void ATreatyCannotBeSignedWithoutTrustAndIsRefusedWithAReason()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        var ev = Do(s, new ProposeTreatyCommand(0, m.Id, Treaty.Neutral));
        var answer = ev.OfType<ProposalAnswered>().Single();
        Assert.False(answer.Accepted);
        Assert.Contains("trust", answer.Reason);
        Assert.Equal(Treaty.War, s.Diplomacy.Get(0, m.Id));
    }

    [Fact]
    public void TreatiesAreSignedOneStepAtATime()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        var ev = Do(s, new ProposeTreatyCommand(0, m.Id, Treaty.Alliance));
        Assert.False(ev.OfType<ProposalAnswered>().Single().Accepted);
        Assert.Equal(Treaty.War, s.Diplomacy.Get(0, m.Id));
    }

    [Fact]
    public void TreatiesCostInfluence()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Befriend(s, 0, m.Id, Treaty.Neutral);
        s.Players[0].Influence = 0;
        for (int i = 0; i < 6; i++) Do(s, new GiftCommand(0, m.Id, 40));
        var ev = Do(s, new ProposeTreatyCommand(0, m.Id, Treaty.NonAggression));
        var answer = ev.OfType<ProposalAnswered>().Single();
        Assert.False(answer.Accepted);
        Assert.Contains("Influence", answer.Reason);
    }

    [Fact]
    public void AlliesDoNotFightAndCannotTakeEachOthersLand()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Befriend(s, 0, m.Id, Treaty.NonAggression);
        s.Stacks.RemoveAll(x => x.Owner == 0);
        var intruder = s.AddStack(0, m.Capital, 20);
        long hp = intruder.Hp;
        Run(s, 200);
        Assert.Equal(m.Id, s.TileOwner[m.Capital]);
        Assert.Equal(hp, intruder.Hp);
        Assert.Contains(s.Stacks, x => x.Owner == m.Id && x.Tile == m.Capital && x.Hp == x.MaxHp);
    }

    [Fact]
    public void AtWarTheSameStackWouldHaveFought()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        s.Stacks.RemoveAll(x => x.Owner == 0);
        var intruder = s.AddStack(0, m.Capital, 20);
        long hp = intruder.Hp;
        Run(s, 50);
        Assert.True(intruder.Hp < hp);
    }

    [Fact]
    public void TradePactsRaiseAlloyIncome()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        long before = Simulation.Income(s, s.Players[0]).Alloy;
        Befriend(s, 0, m.Id, Treaty.TradePact);
        Assert.True(Simulation.Income(s, s.Players[0]).Alloy > before);
    }

    // ---- scripted: befriend -> integrate ----------------------------------------------------

    [Fact]
    public void BefriendThenIntegrateAMinorPeacefully()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        int[] tiles = Enumerable.Range(0, s.Map.TileCount).Where(t => s.TileOwner[t] == m.Id).ToArray();
        Assert.NotEmpty(tiles);

        Befriend(s, 0, m.Id, Treaty.Alliance);
        for (int i = 0; i < 20 && DiplomacySystem.Opinion(s, m.Id, 0) < DiplomacySystem.IntegrateOpinion; i++)
            Do(s, new GiftCommand(0, m.Id, 40));
        Assert.Equal(Mood.Allied, DiplomacySystem.MoodOf(s, m.Id, 0));

        long influence = s.Players[0].Influence;
        var ev = Do(s, new IntegrateCommand(0, m.Id));
        Assert.Contains(ev, e => e is IntegrationStarted);
        Assert.Equal(influence - DiplomacySystem.IntegrateInfluence, s.Players[0].Influence);

        Run(s, DiplomacySystem.IntegrateTicks - 20);
        Assert.All(tiles, t => Assert.Equal(m.Id, s.TileOwner[t]));      // not yet

        Run(s, 40);
        Assert.All(tiles, t => Assert.Equal(0, s.TileOwner[t]));
        Assert.Equal(0, s.Diplomacy.IntegratedInto[m.Id]);
        Assert.Equal(Mood.Integrated, DiplomacySystem.MoodOf(s, m.Id, 0));
        Assert.Contains(s.Stacks, x => x.Owner == 0 && x.Tile == m.Capital);   // the garrison joins
        Assert.DoesNotContain(s.Stacks, x => x.Owner == m.Id);
    }

    [Fact]
    public void IntegrationNeedsAnAllianceHighOpinionAndInfluence()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Assert.DoesNotContain(Do(s, new IntegrateCommand(0, m.Id)), e => e is IntegrationStarted);   // at war
        Befriend(s, 0, m.Id, Treaty.Alliance);
        s.Players[0].Influence = 0;
        for (int i = 0; i < 20; i++) Do(s, new GiftCommand(0, m.Id, 40));
        Assert.DoesNotContain(Do(s, new IntegrateCommand(0, m.Id)), e => e is IntegrationStarted);   // no Influence
    }

    [Fact]
    public void BreakingTheAllianceCancelsIntegrationAndRefundsInfluence()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Befriend(s, 0, m.Id, Treaty.Alliance);
        for (int i = 0; i < 20; i++) Do(s, new GiftCommand(0, m.Id, 40));
        Do(s, new IntegrateCommand(0, m.Id));
        long during = s.Players[0].Influence;
        var ev = Do(s, new BreakTreatyCommand(0, m.Id));
        Assert.Contains(ev, e => e is IntegrationCancelled);
        Assert.Equal(during + DiplomacySystem.IntegrateInfluence, s.Players[0].Influence);
        Assert.Empty(s.Diplomacy.Integrations);
    }

    // ---- scripted: ally -> betray -> Treacherous everywhere ---------------------------------

    [Fact]
    public void BetrayingAnAllyMakesYouTreacherousToEveryone()
    {
        var s = NewGame();
        var ally = FirstMinor(s);
        var others = s.Players.Where(p => p.Id != 0 && p.Id != ally.Id).ToList();
        Befriend(s, 0, ally.Id, Treaty.Alliance);
        var before = others.ToDictionary(p => p.Id, p => DiplomacySystem.Opinion(s, p.Id, 0));

        var ev = Do(s, new DeclareWarCommand(0, ally.Id));
        Assert.Contains(ev, e => e is Treacherous { Player: 0 });
        Assert.True(s.AtWar(0, ally.Id));
        foreach (var p in others)
        {
            var lines = new List<OpinionLine>();
            int now = DiplomacySystem.Opinion(s, p.Id, 0, lines);
            Assert.Contains(lines, l => l.Label == "Treacherous" && l.Points < 0);
            Assert.True(now < before[p.Id], $"player {p.Id} should think less of the traitor");
        }
    }

    [Fact]
    public void ABrokenTreatyBlocksNewTreatiesForFiveMinutes()
    {
        var s = NewGame();
        var a = FirstMinor(s);
        var b = FirstMinor(s, 1);
        Befriend(s, 0, a.Id, Treaty.NonAggression);
        for (int i = 0; i < 6; i++) Do(s, new GiftCommand(0, b.Id, 40));
        Do(s, new BreakTreatyCommand(0, a.Id));
        var blocked = Do(s, new ProposeTreatyCommand(0, b.Id, Treaty.Neutral)).OfType<ProposalAnswered>().Single();
        Assert.False(blocked.Accepted);
        Assert.Contains("broken treaty", blocked.Reason);

        Run(s, DiplomacySystem.BreakCooldownTicks + 10);
        for (int i = 0; i < 12; i++) Do(s, new GiftCommand(0, b.Id, 40));   // the Treacherous mark still weighs
        var later = Do(s, new ProposeTreatyCommand(0, b.Id, Treaty.Neutral)).OfType<ProposalAnswered>().Single();
        Assert.DoesNotContain("broken treaty", later.Reason);
    }

    [Fact]
    public void DecliningWarOnAStrangerIsNotTreacherousButDroppingAPactIs()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        Befriend(s, 0, m.Id, Treaty.Neutral);
        Assert.DoesNotContain(Do(s, new DeclareWarCommand(0, m.Id)), e => e is Treacherous);   // a neutral is not a pact
        Befriend(s, 0, m.Id, Treaty.NonAggression);
        Assert.Contains(Do(s, new DeclareWarCommand(0, m.Id)), e => e is Treacherous);
    }

    // ---- scripted: AI accept / reject shows a reason -----------------------------------------

    [Theory]
    [InlineData("builder", true)]
    [InlineData("conqueror", false)]
    public void RivalsAnswerPeaceInCharacterAndSayWhy(string personality, bool accepts)
    {
        var s = NewGame();
        s.Players[1].PersonalityId = personality;
        var answer = Do(s, new ProposeTreatyCommand(0, 1, Treaty.Neutral)).OfType<ProposalAnswered>().Single();
        _out.WriteLine(answer.Reason);
        Assert.Equal(accepts, answer.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(answer.Reason));
        Assert.Equal(accepts ? Treaty.Neutral : Treaty.War, s.Diplomacy.Get(0, 1));
    }

    [Fact]
    public void AZealotAlwaysRefusesItsOnlyRival()
    {
        var s = NewGame();
        s.Players[1].PersonalityId = "zealot";
        var answer = Do(s, new ProposeTreatyCommand(0, 1, Treaty.Neutral)).OfType<ProposalAnswered>().Single();
        Assert.False(answer.Accepted);
        Assert.Contains("sworn", answer.Reason);
    }

    [Fact]
    public void TheLastTwoNationsCannotAlly()
    {
        var s = NewGame();
        s.Players[1].PersonalityId = "diplomat";
        s.Diplomacy.Set(0, 1, Treaty.TradePact);
        var answer = Do(s, new ProposeTreatyCommand(0, 1, Treaty.Alliance)).OfType<ProposalAnswered>().Single();
        Assert.False(answer.Accepted);
        Assert.Contains("one nation can win", answer.Reason);
    }

    [Fact]
    public void ARefusedProposalCannotBeRepeatedAtOnce()
    {
        var s = NewGame();
        s.Players[1].PersonalityId = "conqueror";
        Do(s, new ProposeTreatyCommand(0, 1, Treaty.Neutral));
        var again = Do(s, new ProposeTreatyCommand(0, 1, Treaty.Neutral)).OfType<ProposalAnswered>().Single();
        Assert.Contains("recently", again.Reason);
    }

    // ---- scripted: demand surrender ----------------------------------------------------------

    private static GameState CrushedLoser()
    {
        var s = NewGame();
        s.Players[0].PersonalityId = "builder";
        s.Players[1].PersonalityId = "conqueror";
        int cap = s.Players[0].Capital;
        for (int t = 0; t < s.Map.TileCount; t++) if (t != cap && !s.Players[s.TileOwner[t] < 0 ? 0 : s.TileOwner[t]].Minor) s.TileOwner[t] = 1;
        s.Stacks.RemoveAll(x => x.Owner == 0);
        s.AddStack(1, s.Map.Neighbors[cap][0], 40);
        return s;
    }

    [Fact]
    public void AHopelessAiAcceptsADemandToSurrenderAndSaysWhy()
    {
        var s = CrushedLoser();
        var ev = Do(s, new DemandSurrenderCommand(1, 0));
        var answer = ev.OfType<SurrenderAnswered>().Single();
        _out.WriteLine(answer.Reason);
        Assert.True(answer.Accepted);
        Assert.Contains("surrender", answer.Reason);
        Assert.True(s.Over);
        Assert.Equal(1, s.Winner);
    }

    [Fact]
    public void AHealthyAiRefusesADemandAndSaysWhy()
    {
        var s = NewGame();
        s.Players[1].PersonalityId = "builder";
        var ev = Do(s, new DemandSurrenderCommand(0, 1));
        var answer = ev.OfType<SurrenderAnswered>().Single();
        _out.WriteLine(answer.Reason);
        Assert.False(answer.Accepted);
        Assert.Contains("fortified capital", answer.Reason);
        Assert.False(s.Over);
    }

    [Fact]
    public void ARefusedDemandCannotBeRepeatedForTwoMinutes()
    {
        var s = NewGame();
        s.Players[1].PersonalityId = "builder";
        Do(s, new DemandSurrenderCommand(0, 1));
        var again = Do(s, new DemandSurrenderCommand(0, 1)).OfType<SurrenderAnswered>().Single();
        Assert.False(again.Accepted);
        Assert.Contains("recently", again.Reason);
    }

    [Fact]
    public void ADemandNeedsAnAiAtWar()
    {
        var s = NewGame();
        var human = Do(s, new DemandSurrenderCommand(0, 1)).OfType<SurrenderAnswered>().Single();   // no personality: a human
        Assert.False(human.Accepted);
        s.Players[1].PersonalityId = "builder";
        s.Diplomacy.Set(0, 1, Treaty.Neutral);
        var peace = Do(s, new DemandSurrenderCommand(0, 1)).OfType<SurrenderAnswered>().Single();
        Assert.Contains("not at war", peace.Reason);
    }

    [Fact]
    public void AZealotRefusesWhereABuilderGivesIn()
    {
        // Land and income gone, capital held and unthreatened, army outnumbered five to one: a builder gives in, a zealot never.
        foreach (var (who, gives) in new[] { ("builder", true), ("zealot", false) })
        {
            var s = NewGame();
            s.Players[0].PersonalityId = who;
            s.Players[1].PersonalityId = "conqueror";
            int cap = s.Players[0].Capital;
            for (int t = 0; t < s.Map.TileCount; t++) if (t != cap && !(s.TileOwner[t] >= 0 && s.Players[s.TileOwner[t]].Minor)) s.TileOwner[t] = 1;
            int far = Enumerable.Range(0, s.Map.TileCount).First(t => s.TileOwner[t] == 1 && t != s.Players[1].Capital && t != cap && !s.Map.Neighbors[cap].Contains(t));
            s.AddStack(1, far, 50);
            var answer = Do(s, new DemandSurrenderCommand(1, 0)).OfType<SurrenderAnswered>().Single();
            _out.WriteLine($"{who}: {answer.Reason}");
            Assert.Equal(gives, answer.Accepted);
        }
    }

    // ---- minor tasks and raids ---------------------------------------------------------------

    [Fact]
    public void MinorsPostGiftTasksAndGivingSettlesThem()
    {
        var s = NewGame();
        s.Players[0].Supply = 100_000_000;
        TaskPosted? posted = null;
        for (int i = 0; i < 2500 && posted == null; i++)
            posted = Simulation.Step(s, Array.Empty<Command>()).OfType<TaskPosted>().FirstOrDefault(t => t.Kind == "gift");
        Assert.NotNull(posted);
        int before = DiplomacySystem.Opinion(s, posted!.Minor, 0);
        var ev = Do(s, new GiftCommand(0, posted.Minor, DiplomacySystem.TaskGiftAlloy));
        Assert.Contains(ev, e => e is TaskCompleted c && c.Minor == posted.Minor && c.Player == 0);
        var lines = new List<OpinionLine>();
        int after = DiplomacySystem.Opinion(s, posted.Minor, 0, lines);
        Assert.Contains(lines, l => l.Label == "Did us a favour");
        Assert.True(after > before + DiplomacySystem.TaskReward);
    }

    [Fact]
    public void AWrongedMinorRaidsAndNobodyFarFromItIsTouched()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        // The minor hates player 0 and player 0 owns ground within reach of it.
        var near = Enumerable.Range(0, s.Map.TileCount)
            .Where(t => s.TileOwner[t] == -1 && s.Map.Passable(t)).OrderBy(t => HopsBetween(s, m.Capital, t)).First();
        Assert.InRange(HopsBetween(s, m.Capital, near), 1, DiplomacySystem.RaidReach);
        s.TileOwner[near] = 0;
        s.Tick = 100;                                                   // raids are checked every ten ticks; 0 means "not scheduled yet"
        DiplomacySystem.AddMod(s, m.Id, 0, "Took our land", -75, 600, 75);
        s.Diplomacy.NextRaid[m.Id] = s.Tick;
        var ev = Simulation.Step(s, Array.Empty<Command>());
        for (int i = 0; i < 20 && !ev.Any(e => e is RaidLaunched); i++) ev = Simulation.Step(s, Array.Empty<Command>());
        var raid = Assert.Single(ev.OfType<RaidLaunched>());
        Assert.Equal(0, raid.Target);
        var raider = s.FindStack(raid.StackId)!;
        Assert.True(raider.Raider);
        Assert.Equal(Mood.Raiding, DiplomacySystem.MoodOf(s, m.Id, 0));

        raider.Hp = 0;                                                    // player 0 beats them off
        var after = Simulation.Step(s, Array.Empty<Command>());
        Assert.Contains(after, e => e is TaskCompleted c && c.Player == 0);
        Assert.NotEqual(Mood.Raiding, DiplomacySystem.MoodOf(s, m.Id, 0));
    }

    private static int HopsBetween(GameState s, int a, int b)
    {
        var dist = new Dictionary<int, int> { [a] = 0 };
        var q = new Queue<int>();
        q.Enqueue(a);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            if (c == b) return dist[c];
            foreach (int n in s.Map.Neighbors[c]) if (dist.TryAdd(n, dist[c] + 1)) q.Enqueue(n);
        }
        return int.MaxValue;
    }

    [Fact]
    public void RaidersNeverTakeGround()
    {
        var s = NewGame();
        var m = FirstMinor(s);
        var target = Enumerable.Range(0, s.Map.TileCount).First(t => s.TileOwner[t] == -1 && s.Map.Passable(t));
        s.TileOwner[target] = 0;
        s.Stacks.RemoveAll(x => x.Owner == 0);
        var raider = s.AddStack(m.Id, target, 3);
        raider.Raider = true;
        Run(s, 100);
        Assert.Equal(0, s.TileOwner[target]);
    }

    // ---- determinism --------------------------------------------------------------------------

    [Fact]
    public void DiplomacyReplaysExactly()
    {
        ulong Run()
        {
            var s = NewGame(8);
            var m = FirstMinor(s);
            Befriend(s, 0, m.Id, Treaty.Alliance);
            Do(s, new DeclareWarCommand(0, m.Id));
            Do(s, new DemandSurrenderCommand(0, 1));
            for (int i = 0; i < 300; i++) Simulation.Step(s, Array.Empty<Command>());
            return s.Hash();
        }
        Assert.Equal(Run(), Run());
    }

    // ---- data -----------------------------------------------------------------------------------

    [Fact]
    public void PersonalitiesCarryDiplomaticTemperament()
    {
        Assert.True(Personality.ById("builder").PeaceBias > 0);
        Assert.True(Personality.ById("conqueror").PeaceBias < 0);
        Assert.True(Personality.ById("diplomat").Courting > Personality.ById("conqueror").Courting);
        Assert.Throws<FormatException>(() => Personality.Parse("""{ "personalities": [ { "id": "x", "name": "X", "expand": 1, "develop": 1, "defend": 1, "attack": 1, "muster": 1, "riskPermille": 100, "painThreshold": 5, "engineers": 0, "peaceBias": 500 } ] }"""));
    }

    [Fact]
    public void AiThatCourtsMinorsEventuallyGainsAFriend()
    {
        var s = AiMatch.Setup(21, out var ais);
        s.Players[0].PersonalityId = "diplomat";
        var diplomat = new RivalAi(0, Personality.ById("diplomat"));
        ais[0] = diplomat;
        s.Players[0].Influence = 200_000;
        for (int i = 0; i < 12_000 && !s.Over; i++)
            Simulation.Step(s, ais.SelectMany(a => a.Think(s)).ToList());
        int pacts = s.Players.Count(p => p.Minor && s.Diplomacy.Get(0, p.Id) >= Treaty.Neutral);
        _out.WriteLine($"minors at peace with the diplomat: {pacts}; courting {diplomat.Debug.Courting}");
        Assert.True(pacts >= 1);
    }
}
