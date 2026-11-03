using System.Diagnostics;
using System.Globalization;
using Planetfall.Sim.MapGen;

// Usage: mapgen-sweep [count=1000] [firstSeed=1] [cols=20] [rows=10] [csvPath]
// Generates maps for a range of seeds, prints histograms and the M3 done-test verdict,
// and optionally writes one CSV row per seed.
int count = Arg(0, 1000), first = Arg(1, 1), cols = Arg(2, 20), rows = Arg(3, 10);
string? csv = args.Length > 4 ? args[4] : null;

var rowsOut = new List<string> { "seed,attempt,fallback,ratio,passable,minors,deposits,relics,rivers,chokepoints,ms" };
var times = new List<double>();
var attempts = new int[MapGenerator.MaxAttempts + 1];
int fallbacks = 0, withinSpread = 0;
var ratioBins = new int[8];
var passBins = new int[10];

for (int i = 0; i < count; i++)
{
    ulong seed = (ulong)(first + i);
    var sw = Stopwatch.StartNew();
    var m = MapGenerator.Generate(seed, cols, rows);
    sw.Stop();
    double ms = sw.Elapsed.TotalMilliseconds;
    times.Add(ms);
    attempts[Math.Min(m.Attempt, MapGenerator.MaxAttempts)]++;
    if (m.UsedFallback) fallbacks++;
    if (m.SpawnValueRatio <= MapGenerator.MaxSpawnValueRatio) withinSpread++;
    ratioBins[Math.Min(ratioBins.Length - 1, (int)((m.SpawnValueRatio - 1.0) / 0.05))]++;
    passBins[Math.Min(passBins.Length - 1, (int)(m.PassableFraction * 10))]++;
    rowsOut.Add(string.Join(',', new[]
    {
        seed.ToString(), m.Attempt.ToString(), m.UsedFallback ? "1" : "0",
        m.SpawnValueRatio.ToString("F3", CultureInfo.InvariantCulture),
        m.PassableFraction.ToString("F3", CultureInfo.InvariantCulture),
        m.MinorClusters.Length.ToString(),
        m.Deposit.Count(d => d).ToString(),
        m.DepositKinds.Count(k => k == DepositKind.Relic).ToString(),
        m.Rivers.Length.ToString(),
        m.Chokepoint.Count(c => c).ToString(),
        ms.ToString("F1", CultureInfo.InvariantCulture),
    }));
}

if (csv != null) File.WriteAllLines(csv, rowsOut);

times.Sort();
double P(double q) => times[Math.Min(times.Count - 1, (int)(q * times.Count))];
Console.WriteLine($"Maps {count} (seeds {first}..{first + count - 1}), grid {cols}x{rows} = {cols * rows} tiles");
Console.WriteLine($"Fallbacks: {fallbacks}  ({100.0 * (count - fallbacks) / count:F1}% validated within {MapGenerator.MaxAttempts} tries)");
Console.WriteLine($"Spread <= {MapGenerator.MaxSpawnValueRatio}: {withinSpread}  ({100.0 * withinSpread / count:F1}%)");
Console.WriteLine($"Gen time ms: median {P(0.5):F1}, p95 {P(0.95):F1}, max {times[^1]:F1}");
Console.WriteLine("Attempts used:");
for (int a = 0; a < attempts.Length; a++) if (attempts[a] > 0) Console.WriteLine($"  {a,2}: {Bar(attempts[a], count)} {attempts[a]}");
Console.WriteLine("Spawn value ratio (bins of 0.05 from 1.00):");
for (int b = 0; b < ratioBins.Length; b++) Console.WriteLine($"  {1 + b * 0.05:F2}+: {Bar(ratioBins[b], count)} {ratioBins[b]}");
Console.WriteLine("Passable fraction (bins of 10%):");
for (int b = 0; b < passBins.Length; b++) if (passBins[b] > 0) Console.WriteLine($"  {b * 10,2}%+: {Bar(passBins[b], count)} {passBins[b]}");

bool pass = fallbacks == 0 && 100.0 * withinSpread / count >= 95 && P(0.95) < 1000;
Console.WriteLine(pass ? "M3 done-test: PASS" : "M3 done-test: FAIL");
return pass ? 0 : 1;

int Arg(int i, int def) => args.Length > i && int.TryParse(args[i], out var v) ? v : def;
static string Bar(int n, int total) => new string('#', (int)Math.Round(40.0 * n / total));
