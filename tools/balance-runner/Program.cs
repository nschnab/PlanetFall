using System.Collections.Concurrent;
using System.Diagnostics;
using Planetfall.Sim.Ai;

// Usage: balance-runner [matches=500] [firstSeed=1] [maxMinutes=120] [cols=35] [rows=20]   (35x20 = a Standard 700-tile map)
// Plays AI-vs-AI matches in parallel and checks the M5 done-test:
//   no stuck stacks; >= 90% end by a win condition; surrenders in >= 30% of matches and never before minute 10;
//   win rate per spawn slot within 40..60%.
int count = Arg(0, 500), first = Arg(1, 1), maxMin = Arg(2, 120);
int cols = Arg(3, AiMatch.StandardCols), rows = Arg(4, AiMatch.StandardRows);
int limit = maxMin * 600;
if (count == 1) { Trace((ulong)first, limit, cols, rows); return 0; }
var results = new ConcurrentBag<AiMatchResult>();
var sw = Stopwatch.StartNew();
Parallel.For(0, count, i => results.Add(AiMatch.Play((ulong)(first + i), limit, cols, rows)));
var all = results.OrderBy(r => r.Seed).ToList();

int finished = all.Count(r => r.Winner != -1);
int surrenders = all.Count(r => r.EndReason == "surrender");
int earlySurrenders = all.Count(r => r.EndReason == "surrender" && r.Ticks < 6000);
int stuck = all.Sum(r => r.StuckStacks);
int stuckMatches = all.Count(r => r.StuckStacks > 0);
int[] wins = { all.Count(r => r.Winner == 0), all.Count(r => r.Winner == 1) };
double slot0 = finished == 0 ? 0 : 100.0 * wins[0] / finished;
var mins = all.Where(r => r.Winner != -1).Select(r => r.Ticks / 600.0).OrderBy(x => x).ToList();

Console.WriteLine($"{count} matches in {sw.Elapsed.TotalSeconds:F1}s (cap {maxMin} min)");
Console.WriteLine($"finished by a win condition: {finished}/{count} ({100.0 * finished / count:F1}%)");
Console.WriteLine($"end reasons: {string.Join(", ", all.GroupBy(r => r.EndReason).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))}");
Console.WriteLine($"surrenders: {surrenders} ({100.0 * surrenders / count:F1}%), before minute 10: {earlySurrenders}");
Console.WriteLine($"stuck stacks: {stuck} in {stuckMatches} matches");
if (stuck > 0) Console.WriteLine($"  stuck seeds: {string.Join(", ", all.Where(r => r.StuckStacks > 0).Take(8).Select(r => r.Seed))}");
foreach (var r in all.Where(r => r.StuckStacks > 0).Take(8)) Console.WriteLine($"    seed {r.Seed}: {r.StuckDetail}");
var unfinished = all.Where(r => r.Winner == -1).Take(8).Select(r => r.Seed).ToList();
if (unfinished.Count > 0) Console.WriteLine($"  unfinished seeds: {string.Join(", ", unfinished)}");
Console.WriteLine($"wins by spawn slot: slot0={wins[0]} slot1={wins[1]} (slot0 {slot0:F1}%)");
if (mins.Count > 0)
    Console.WriteLine($"minutes to win: min {mins[0]:F1}, p25 {mins[mins.Count / 4]:F1}, median {mins[mins.Count / 2]:F1}, p75 {mins[mins.Count * 3 / 4]:F1}, max {mins[^1]:F1}");
Console.WriteLine("by personality (wins/appearances):");
foreach (var g in all.SelectMany(r => new[] { (r.Personalities[0], r.Winner == 0), (r.Personalities[1], r.Winner == 1) }).GroupBy(x => x.Item1).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key,-12} {g.Count(x => x.Item2),4}/{g.Count(),4}");

bool pass = stuck == 0
    && 100.0 * finished / count >= 90
    && 100.0 * surrenders / count >= 30 && earlySurrenders == 0
    && slot0 is >= 40 and <= 60;
Console.WriteLine($"M5 done-test: {(pass ? "PASS" : "FAIL")}");
return pass ? 0 : 1;

int Arg(int i, int d) => args.Length > i && int.TryParse(args[i], out int v) ? v : d;

static void Trace(ulong seed, int limit, int cols, int rows)
{
    var s = AiMatch.Setup(seed, out var ais, cols, rows);
    Console.WriteLine($"seed {seed}: {ais[0].Personality.Id} vs {ais[1].Personality.Id}");
    while (!s.Over && s.Tick < limit)
    {
        var cmds = ais.SelectMany(a => a.Think(s)).ToList();
        foreach (var c in cmds.OfType<Planetfall.Sim.Core.SurrenderCommand>()) Console.WriteLine($"  t={s.Tick}: player {c.Player} surrenders");
        Planetfall.Sim.Systems.Simulation.Step(s, cmds);
        if (s.Tick % 600 != 0) continue;
        string P(int i)
        {
            var mine = s.Stacks.Where(x => x.Owner == i).ToList();
            var a = ais[i];
            return $"tiles {s.OwnedTiles(i),3} stacks {mine.Count} pow {mine.Sum(x => x.EffectiveStrength),3} alloy {s.Players[i].Supply / 1000,3} en {s.Players[i].Energy / 1000,3}{(s.Players[i].Starved ? "!" : " ")} {a.Goal,-7} hope {a.Debug.Hopelessness,4}";
        }
        Console.WriteLine($"  {s.Tick / 600,3}m | {P(0)} | {P(1)}");
    }
    foreach (var st in s.Stacks.Where(x => x.Owner < 2))
    {
        var o = ais[st.Owner].OrderOf(st.Id);
        Console.WriteLine($"    stack {st.Id} p{st.Owner} tile {st.Tile} idle={st.Idle} hp {st.Hp * 100 / st.MaxHp}% units {string.Join("+", st.Units.Select(u => u.Value + u.Key))} order {o?.Role}->{o?.Tile}");
    }
    Console.WriteLine($"  end: winner {s.Winner} at {s.Tick / 600.0:F1} min");
}
