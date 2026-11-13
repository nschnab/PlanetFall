using System.Text.Json;
using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.Presentation;
using Xunit;
using Xunit.Abstractions;

namespace Planetfall.Sim.Tests;

public class PresentationTests
{
    private static readonly IReadOnlyList<Command> None = Array.Empty<Command>();
    private readonly ITestOutputHelper _out;
    public PresentationTests(ITestOutputHelper output) => _out = output;

    // What the map actually shows: stack colours, the darker tile fills, and the neutral ground.
    private static IEnumerable<(string Name, Rgb Colour)> Swatches() => new (string, Rgb)[]
    {
        ("you", Palette.OwnerColor(0)), ("rival", Palette.OwnerColor(1)), ("minor", Palette.OwnerColor(2)),
        ("you tile", Palette.TileFill(0)), ("rival tile", Palette.TileFill(1)), ("minor tile", Palette.TileFill(2)), ("neutral tile", Palette.TileFill(-1)),
    };

    [Theory]
    [InlineData(Cvd.Normal)]
    [InlineData(Cvd.Protanopia)]
    [InlineData(Cvd.Deuteranopia)]
    [InlineData(Cvd.Tritanopia)]
    public void OwnersStayApartUnderEveryColourVisionType(Cvd kind)
    {
        // Tiles of different owners touch, so every owner pair must differ, whether drawn as stack colour or as tile fill.
        var groups = new[] { new[] { 0, 1, 2 } };
        double worst = double.MaxValue;
        string pair = "";
        foreach (var (kindName, fill) in new (string, Func<int, Rgb>)[] { ("stack", Palette.OwnerColor), ("tile", Palette.TileFill) })
            foreach (int a in new[] { -1, 0, 1, 2 })
                foreach (int b in new[] { -1, 0, 1, 2 })
                {
                    if (a >= b || (kindName == "stack" && a == -1)) continue;
                    double d = Palette.DeltaE(Palette.Simulate(fill(a), kind), Palette.Simulate(fill(b), kind));
                    if (d < worst) { worst = d; pair = $"{kindName} {a} v {b}"; }
                }
        _out.WriteLine($"{kind}: closest owner pair {pair} at deltaE {worst:F1}");
        Assert.True(worst >= 18, $"{kind}: {pair} only {worst:F1} apart");
    }

    [Fact]
    public void SimulationIsIdentityForNormalVisionAndChangesColourOtherwise()
    {
        Assert.Equal(Palette.Vermillion, Palette.Simulate(Palette.Vermillion, Cvd.Normal));
        Assert.True(Palette.DeltaE(Palette.Vermillion, Palette.Simulate(Palette.Vermillion, Cvd.Protanopia)) > 10);
        Assert.Equal(0, Palette.DeltaE(Palette.Blue, Palette.Blue), 6);
    }

    [Fact]
    public void GlyphsAreDistinctShapesNotJustColours()
    {
        double worst = 1;
        string pair = "";
        for (int i = 0; i < Glyphs.All.Length; i++)
        {
            Assert.Contains(true, Enumerable.Range(0, 32 * 32).Select(k => Glyphs.Rasterize(Glyphs.All[i])[k % 32, k / 32]));
            for (int j = i + 1; j < Glyphs.All.Length; j++)
            {
                double d = Glyphs.Distance(Glyphs.All[i], Glyphs.All[j]);
                if (d < worst) { worst = d; pair = $"{Glyphs.All[i].Role}/{Glyphs.All[j].Role}"; }
            }
        }
        _out.WriteLine($"closest glyph pair {pair}: {worst:P0} of inked pixels differ");
        Assert.True(worst >= 0.5, $"{pair} too alike ({worst:P0})");
    }

    [Fact]
    public void EveryRosterRoleHasAGlyph()
    {
        foreach (var role in new[] { "infantry", "armor", "artillery", "recon", "engineer" })
            Assert.Equal(role, Glyphs.Of(role).Role);
        Assert.Equal("armor", Glyphs.DominantRole(new Dictionary<string, int> { ["infantry"] = 2, ["armor"] = 5 }));
    }

    [Fact]
    public void MusicTurnsToBattleTheTickAFightIsSeenAndLayersFollowWithinTwoSeconds()
    {
        var m = new SoloMatch(11);
        var dir = new MusicDirector();
        int firstFight = -1;
        for (int i = 0; i < 12000 && firstFight < 0; i++)
        {
            m.Step(None);
            var (fighting, threat) = MusicDirector.Sense(m.State, m.Fog);
            dir.Update(m.State.Tick, fighting, threat);
            if (fighting) firstFight = m.State.Tick;
            else Assert.NotEqual(MusicMood.Battle, dir.Mood);
        }
        Assert.True(firstFight > 0, "the rival should reach the idle player within 20 minutes");
        Assert.Equal(MusicMood.Battle, dir.Mood);

        // Real time: 20 frames of 50 ms = 1 s after the fight starts, the battle layer is nearly at full volume.
        for (int f = 0; f < 20; f++) dir.Fade(0.05);
        _out.WriteLine($"battle layer after 1 s: {dir.Layers[2]:F2}, calm {dir.Layers[0]:F2}");
        Assert.True(dir.Layers[2] >= 0.9);
        Assert.True(dir.Layers[0] <= 0.4);
    }

    [Fact]
    public void MusicRelaxesAfterTheFightingStops()
    {
        var d = new MusicDirector();
        d.Update(100, true, true);
        Assert.Equal(MusicMood.Battle, d.Mood);
        d.Update(100 + MusicDirector.BattleHoldTicks + 1, false, false);
        Assert.Equal(MusicMood.Tense, d.Mood);
        d.Update(100 + MusicDirector.TenseHoldTicks + 1, false, false);
        Assert.Equal(MusicMood.Calm, d.Mood);
    }

    [Fact]
    public void MusicLayersAreAudibleBoundedAndLoopWithoutAJump()
    {
        foreach (var layer in Synth.MusicLayers)
        {
            var pcm = Synth.Music(layer);
            Assert.Equal(Synth.LoopSamples, pcm.Length);
            double rms = Math.Sqrt(pcm.Average(s => (double)s * s)) / short.MaxValue;
            Assert.InRange(rms, 0.03, 0.8);
            Assert.True(pcm.Max(s => Math.Abs((int)s)) <= short.MaxValue);
            int jump = Math.Abs(pcm[0] - pcm[^1]);
            int typical = (int)Enumerable.Range(1, 2000).Select(i => Math.Abs(pcm[i] - pcm[i - 1])).Max();
            _out.WriteLine($"{layer}: rms {rms:F3}, seam jump {jump}, largest normal step {typical}");
            Assert.True(jump <= Math.Max(typical * 2, 2000), $"{layer} loop seam jumps by {jump}");
        }
    }

    [Fact]
    public void EverySoundEffectIsShortAudibleAndDeterministic()
    {
        foreach (var name in Synth.SfxNames)
        {
            var a = Synth.Sfx(name);
            Assert.InRange(a.Length, Synth.Rate / 50, Synth.Rate * 2);
            Assert.True(a.Max(s => Math.Abs((int)s)) > 4000, name);
            Assert.Equal(a, Synth.Sfx(name));
        }
    }

    [Fact]
    public void ManifestListsExactlyWhatTheCodeProvides()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "data", "presentation", "manifest.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "data", "presentation", "manifest.json")));
        var root = doc.RootElement;
        static string[] Names(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(Glyphs.All.Select(g => g.Role).OrderBy(x => x, StringComparer.Ordinal), Names(root.GetProperty("glyphs")));
        Assert.Equal(Synth.SfxNames.OrderBy(x => x, StringComparer.Ordinal), Names(root.GetProperty("sfx")));
        Assert.Equal(Synth.MusicLayers.OrderBy(x => x, StringComparer.Ordinal), Names(root.GetProperty("music")));
        Assert.Equal(Palette.OwnerColor(0).R, root.GetProperty("palette").GetProperty("you")[0].GetInt32());
        Assert.Equal(Palette.OwnerColor(1).G, root.GetProperty("palette").GetProperty("rival")[1].GetInt32());
        Assert.Equal(Palette.OwnerColor(2).B, root.GetProperty("palette").GetProperty("minor")[2].GetInt32());
    }
}
