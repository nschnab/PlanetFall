using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;
using Xunit;

namespace Planetfall.Sim.Tests;

public class FogSaveTests
{
    private static readonly IReadOnlyList<Command> None = Array.Empty<Command>();

    [Fact]
    public void StartingVisionCoversOwnLandAndNotTheEnemy()
    {
        var m = new SoloMatch(5);
        var fog = m.Fog;
        Assert.True(fog.Visible(m.State.Players[0].Capital));
        Assert.Equal(Sight.Unknown, fog.Tiles[m.State.Players[1].Capital]);
        Assert.Equal(-1, fog.KnownOwner(m.State, m.State.Players[1].Capital));
    }

    [Fact]
    public void ScoutingRevealsThenLeavesTheTileExploredWithItsLastOwner()
    {
        var s = SoloMatch5State();
        var fog = s.Fogs[0];
        int far = s.Players[1].Capital;
        Assert.Equal(Sight.Unknown, fog.Tiles[far]);

        var scout = s.AddStack(0, far, new Dictionary<string, int> { ["recon"] = 1 });
        s.UpdateFogs();
        Assert.True(fog.Visible(far));
        Assert.Equal(1, fog.KnownOwner(s, far));

        s.Stacks.Remove(scout);
        s.TileOwner[far] = -1;                // the tile changes hands while we are not looking
        s.UpdateFogs();
        Assert.Equal(Sight.Explored, fog.Tiles[far]);
        Assert.Equal(1, fog.KnownOwner(s, far));      // we still believe the old owner
    }

    [Fact]
    public void ReconSeesFurtherThanInfantryAndHighlandsAddOne()
    {
        var s = SoloMatch5State();
        var inf = s.AddStack(0, s.Players[0].Capital, new Dictionary<string, int> { ["infantry"] = 1 });
        var rec = s.AddStack(0, s.Players[0].Capital, new Dictionary<string, int> { ["recon"] = 1 });
        int baseInf = Fog.StackRange(s, inf);
        Assert.True(Fog.StackRange(s, rec) > baseInf);
        int hl = Enumerable.Range(0, s.Map.TileCount).First(t => s.Map.Terrain[t] == Terrain.Highlands);
        inf.Tile = hl;
        Assert.Equal(baseInf + Fog.HighlandsBonus, Fog.StackRange(s, inf));
    }

    [Fact]
    public void AllianceSharesSight()
    {
        var s = SoloMatch5State();
        int minor = 2;
        int theirs = s.Players[minor].Capital;
        s.TrackFog(0);
        s.UpdateFogs();
        bool before = s.Fogs[0].Visible(theirs);
        s.Diplomacy.Set(0, minor, Treaty.Alliance);
        s.UpdateFogs();
        Assert.True(s.Fogs[0].Visible(theirs));
        Assert.False(before);
    }

    [Fact]
    public void NoEnemyStackIsShownOutsideVisionAcrossARealMatch()
    {
        var m = new SoloMatch(11);
        int checkedStacks = 0, hidden = 0;
        for (int i = 0; i < 6000; i++)
        {
            m.Step(None);
            if (i % 25 != 0) continue;
            foreach (var st in m.State.Stacks)
            {
                if (!m.Fog.CanSee(m.State, st)) { hidden++; continue; }
                checkedStacks++;
                if (st.Owner != 0) Assert.True(m.Fog.Visible(st.Tile) || (st.Target != -1 && m.Fog.Visible(st.Target)), $"stack {st.Id} shown in fog at tick {m.State.Tick}");
            }
        }
        Assert.True(hidden > 0, "fog should hide something over ten minutes");
        Assert.True(checkedStacks > 0);
    }

    [Fact]
    public void TwoSoloMatchesOfTheSameSeedStayIdentical()
    {
        var a = new SoloMatch(21);
        var b = new SoloMatch(21);
        for (int i = 0; i < 1500; i++) { a.Step(None); b.Step(None); }
        Assert.Equal(a.State.Hash(), b.State.Hash());
    }

    [Fact]
    public void SaveMidBattleReloadsToTheSameStateHash()
    {
        var m = new SoloMatch(7);
        var cap = m.State.Players[0].Capital;
        for (int i = 0; i < 4000; i++)
        {
            var cmds = new List<Command>();
            if (i % 100 == 10) cmds.Add(new RecruitCommand(0, "infantry"));
            if (i % 100 == 20)
            {
                var mine = m.State.Stacks.Where(x => x.Owner == 0).OrderBy(x => x.Id).LastOrDefault();
                if (mine != null) cmds.Add(new MoveCommand(0, mine.Id, m.State.Players[1].Capital));
            }
            m.Step(cmds);
        }
        Assert.Contains(m.Log, l => l.Command is MoveCommand);

        var json = SoloMatch.ToJson(m.ToSave());
        var loaded = SoloMatch.Load(SoloMatch.FromJson(json));
        Assert.Equal(m.State.Hash(), loaded.State.Hash());
        Assert.Equal(m.State.Tick, loaded.State.Tick);

        // And both carry on identically.
        for (int i = 0; i < 600; i++) { m.Step(None); loaded.Step(None); }
        Assert.Equal(m.State.Hash(), loaded.State.Hash());
    }

    [Fact]
    public void EveryCommandKindSurvivesTheSaveFormat()
    {
        var all = new Command[]
        {
            new MoveCommand(0, 3, 9, true), new StopCommand(0, 3), new SurrenderCommand(0), new RecruitCommand(0, "armor", 4),
            new BuildCommand(0, 3, "depot"), new GiftCommand(0, 2, 15), new ProposeTreatyCommand(0, 2, Treaty.TradePact),
            new BreakTreatyCommand(0, 2), new DeclareWarCommand(0, 2), new IntegrateCommand(0, 2), new DemandSurrenderCommand(0, 1),
        };
        var save = new SaveGame(1, MapData.GeneratorVersion, 1, 35, 20, 0, 0, all.Select((c, i) => new LoggedCommand(i, c)).ToList());
        var back = SoloMatch.FromJson(SoloMatch.ToJson(save));
        Assert.Equal(all, back.Commands.Select(c => c.Command).ToArray());
    }

    [Fact]
    public void TamperedOrOutdatedSavesAreRefusedClearly()
    {
        var m = new SoloMatch(8);
        for (int i = 0; i < 300; i++) m.Step(None);
        var good = m.ToSave();
        Assert.Throws<SaveException>(() => SoloMatch.Load(good with { Hash = good.Hash + 1 }));
        Assert.Throws<SaveException>(() => SoloMatch.Load(good with { Version = 99 }));
        Assert.Throws<SaveException>(() => SoloMatch.Load(good with { GeneratorVersion = 0 }));
        Assert.Throws<SaveException>(() => SoloMatch.FromJson("{ not json"));
    }

    [Fact]
    public void FileWriteIsAtomicAndRoundTrips()
    {
        var m = new SoloMatch(9);
        for (int i = 0; i < 200; i++) m.Step(None);
        var path = Path.Combine(Path.GetTempPath(), $"pf-save-{Guid.NewGuid():N}.json");
        try
        {
            SoloMatch.WriteFile(path, m.ToSave());
            SoloMatch.WriteFile(path, m.ToSave());       // overwrite path
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(m.State.Hash(), SoloMatch.Load(SoloMatch.ReadFile(path)).State.Hash());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static GameState SoloMatch5State() => new SoloMatch(5).State;
}
