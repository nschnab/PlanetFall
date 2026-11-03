using Planetfall.Sim.Core;

namespace Planetfall.Sim.MapGen;

/// <summary>
/// M2 map generator: flat rectangle, jittered points, one Lloyd pass, Voronoi tiles,
/// adjacency, scarce deposits, two spawns, fairness check with bounded retries.
/// The cylindrical wrap, terrain and minors arrive with the full pipeline in M3.
/// </summary>
public static class MapGenerator
{
    public const int Width = 1280;
    public const int Height = 720;
    public const int MaxAttempts = 50;
    public const double MaxSpawnValueRatio = 1.15;
    public const int FairnessHops = 4;
    public const int MinSpawnHops = 8;
    public const int DepositValue = 5;
    public const int ChokeHops = 3;
    public const int MaxChokeDifference = 1;
    public const int MaxMinorHops = 6;
    public const int MinMinors = 6;
    public const int MaxMinors = 12;
    public const int RelicMinHops = 5;   // relics sit away from spawns so they are contested
    public const int MinClusterSize = 3;
    public const int MaxClusterSize = 8;
    public const int MinorSpawnClearance = 3;   // cluster tiles stay more than this many hops from a spawn
    public const double WaterShare = 0.08;
    public const double MountainShare = 0.09;
    public const double HighlandShare = 0.15;
    public const double MinPassable = 0.72;
    public const double MaxPassable = 0.92;

    public static MapData Generate(ulong seed, int cols = 20, int rows = 10)
    {
        MapData? last = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ulong sub = SplitMix64.Derive(seed, "map:" + attempt);
            MapData map = Build(seed, attempt, sub, cols, rows, usedFallback: false);
            last = map;
            if (Valid(map)) return map;
        }
        return last! with { UsedFallback = true };
    }

    private static bool Valid(MapData m) =>
        m.SpawnValueRatio <= MaxSpawnValueRatio
        && SpawnsFarEnough(m)
        && m.PassableFraction is >= MinPassable and <= MaxPassable
        && Math.Abs(ChokesNear(m, m.Spawns[0]) - ChokesNear(m, m.Spawns[1])) <= MaxChokeDifference
        && MinorNearEverySpawn(m);

    private static int ChokesNear(MapData m, int spawn)
    {
        int count = 0;
        for (int t = 0; t < m.TileCount; t++)
            if (m.Chokepoint[t] && HopDistance(m.Neighbors, spawn, t) is >= 0 and <= ChokeHops) count++;
        return count;
    }

    private static bool MinorNearEverySpawn(MapData m) =>
        m.MinorClusters.Length >= MinMinors
        && m.Spawns.All(sp => m.MinorClusters.Any(c => HopDistance(m.Neighbors, sp, c[0]) is >= 0 and <= MaxMinorHops));

    /// <summary>
    /// 6-12 clusters of 3-8 connected passable tiles, kept clear of the spawns and of each other
    /// (a one-tile gap between clusters). Placed greedily; the count may fall short on a cramped map,
    /// which the validity check then rejects.
    /// </summary>
    private static int[][] Minors(ulong sub, int[][] nbrs, int[] spawns)
    {
        var rng = new Pcg32(SplitMix64.Derive(sub, "minors"));
        int n = nbrs.Length;
        var clear = new bool[n];
        foreach (int sp in spawns)
        {
            var dist = new int[n];
            Array.Fill(dist, -1);
            var q = new Queue<int>();
            dist[sp] = 0;
            q.Enqueue(sp);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                foreach (int nb in nbrs[c])
                    if (dist[nb] == -1) { dist[nb] = dist[c] + 1; q.Enqueue(nb); }
            }
            for (int t = 0; t < n; t++) if (dist[t] != -1 && dist[t] <= MinorSpawnClearance) clear[t] = true;
        }

        var taken = new bool[n];     // in a cluster, or adjacent to one
        var clusters = new List<int[]>();
        int wanted = rng.NextInt(MinMinors, MaxMinors);
        for (int tries = 0; clusters.Count < wanted && tries < 400; tries++)
        {
            int seed = rng.NextInt(0, n - 1);
            if (nbrs[seed].Length == 0 || clear[seed] || taken[seed]) continue;
            int size = rng.NextInt(MinClusterSize, MaxClusterSize);
            var cluster = new List<int> { seed };
            var inCluster = new HashSet<int> { seed };
            while (cluster.Count < size)
            {
                var options = cluster.SelectMany(c => nbrs[c]).Where(t => !inCluster.Contains(t) && !clear[t] && !taken[t]).Distinct().ToList();
                if (options.Count == 0) break;
                int pick = options[rng.NextInt(0, options.Count - 1)];
                cluster.Add(pick);
                inCluster.Add(pick);
            }
            if (cluster.Count < MinClusterSize) continue;
            foreach (int t in cluster) { taken[t] = true; foreach (int nb in nbrs[t]) taken[nb] = true; }
            clusters.Add(cluster.ToArray());
        }
        return clusters.ToArray();
    }

    /// <summary>
    /// Rivers start in highlands and walk downhill tile to tile. The river itself runs along the
    /// boundary between each path tile and its neighbours on the left, so it separates the path
    /// from that bank without closing the way along the path.
    /// </summary>
    private static long[] Rivers(ulong sub, (double X, double Y)[] pts, int[][] nbrs, Terrain[] terrain, double[] elev, int[] spawns)
    {
        var rng = new Pcg32(SplitMix64.Derive(sub, "rivers"));
        var edges = new SortedSet<long>();
        var highlands = Enumerable.Range(0, pts.Length).Where(t => terrain[t] == Terrain.Highlands && nbrs[t].Length > 0).ToList();
        int wanted = Math.Min(4, highlands.Count);
        var used = new HashSet<int>();
        for (int r = 0; r < wanted; r++)
        {
            int src = highlands[rng.NextInt(0, highlands.Count - 1)];
            var path = new List<int> { src };
            var seen = new HashSet<int> { src };
            while (path.Count < 14)
            {
                int cur = path[^1];
                int best = -1;
                foreach (int nb in nbrs[cur])
                    if (!seen.Contains(nb) && elev[nb] < elev[cur] && (best == -1 || elev[nb] < elev[best])) best = nb;
                if (best == -1) break;
                path.Add(best);
                seen.Add(best);
            }
            if (path.Count < 4) continue;
            for (int i = 0; i < path.Count; i++)
            {
                var prev = path[Math.Max(i - 1, 0)];
                var next = path[Math.Min(i + 1, path.Count - 1)];
                double dx = pts[next].X - pts[prev].X, dy = pts[next].Y - pts[prev].Y;
                foreach (int nb in nbrs[path[i]])
                {
                    if (seen.Contains(nb) || spawns.Contains(nb) || spawns.Contains(path[i])) continue;
                    double vx = pts[nb].X - pts[path[i]].X, vy = pts[nb].Y - pts[path[i]].Y;
                    if (dx * vy - dy * vx > 0) edges.Add(MapData.EdgeKey(path[i], nb));
                }
            }
        }
        return edges.ToArray();
    }

    /// <summary>Articulation points of the passable graph (iterative Tarjan).</summary>
    private static bool[] Chokepoints(int[][] nbrs, int root)
    {
        int n = nbrs.Length;
        var disc = new int[n];
        var low = new int[n];
        var parent = new int[n];
        var next = new int[n];
        var choke = new bool[n];
        Array.Fill(parent, -1);
        int time = 0, rootChildren = 0;
        var stack = new Stack<int>();
        disc[root] = low[root] = ++time;
        stack.Push(root);
        while (stack.Count > 0)
        {
            int u = stack.Peek();
            if (next[u] < nbrs[u].Length)
            {
                int v = nbrs[u][next[u]++];
                if (disc[v] == 0)
                {
                    parent[v] = u;
                    if (u == root) rootChildren++;
                    disc[v] = low[v] = ++time;
                    stack.Push(v);
                }
                else if (v != parent[u]) low[u] = Math.Min(low[u], disc[v]);
            }
            else
            {
                stack.Pop();
                int p = parent[u];
                if (p == -1) continue;
                low[p] = Math.Min(low[p], low[u]);
                if (p != root && low[u] >= disc[p]) choke[p] = true;
            }
        }
        if (rootChildren > 1) choke[root] = true;
        return choke;
    }

    private static bool SpawnsFarEnough(MapData m) => HopDistance(m.Neighbors, m.Spawns[0], m.Spawns[1]) >= MinSpawnHops;

    private static MapData Build(ulong seed, int attempt, ulong sub, int cols, int rows, bool usedFallback)
    {
        var rng = new Pcg32(sub);
        int n = cols * rows;
        double cw = (double)Width / cols;
        double ch = (double)Height / rows;

        var pts = new (double X, double Y)[n];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            double jx = (rng.NextDouble() * 2 - 1) * 0.32 * cw;
            double jy = (rng.NextDouble() * 2 - 1) * 0.32 * ch;
            pts[r * cols + c] = ((c + 0.5) * cw + jx, (r + 0.5) * ch + jy);
        }

        var cells = Voronoi(pts, cw);
        for (int i = 0; i < n; i++) pts[i] = Centroid(cells[i]);   // one Lloyd relaxation
        cells = Voronoi(pts, cw);

        var nbrs = Adjacency(pts, cells);

        int[] spawns =
        {
            Nearest(pts, Width * 0.25, Height * 0.25),
            Nearest(pts, Width * 0.75, Height * 0.75),
        };

        var terrain = Terrains(sub, pts, out var elev);
        // Spawns and their immediate surroundings are always open ground.
        foreach (int sp in spawns)
        {
            terrain[sp] = Terrain.Plains;
            foreach (int nb in nbrs[sp]) if (terrain[nb] is Terrain.Mountains or Terrain.Water) terrain[nb] = Terrain.Highlands;
        }
        // Land cut off from the spawns becomes lake: every passable tile must be reachable.
        var open = new bool[n];
        var q = new Queue<int>();
        open[spawns[0]] = true;
        q.Enqueue(spawns[0]);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (int nb in nbrs[c])
            {
                if (open[nb] || terrain[nb] is Terrain.Mountains or Terrain.Water) continue;
                open[nb] = true;
                q.Enqueue(nb);
            }
        }
        for (int t = 0; t < n; t++) if (!open[t] && terrain[t] is not (Terrain.Mountains or Terrain.Water)) terrain[t] = Terrain.Water;
        // Impassable tiles drop out of the graph so movement, AI and encirclement never see them.
        for (int t = 0; t < n; t++)
            nbrs[t] = terrain[t] is Terrain.Mountains or Terrain.Water ? Array.Empty<int>() : nbrs[t].Where(x => terrain[x] is not (Terrain.Mountains or Terrain.Water)).ToArray();

        int depositCount = Math.Max(2, n * 5 / 88);
        var deposit = new bool[n];
        var banned = new HashSet<int>(spawns);
        int placed = 0;
        for (int tries = 0; placed < depositCount && tries < 2000; tries++)
        {
            int t = rng.NextInt(0, n - 1);
            if (deposit[t] || banned.Contains(t) || terrain[t] is Terrain.Mountains or Terrain.Water) continue;
            bool crowded = false;
            for (int d = 0; d < n && !crowded; d++)
                if (deposit[d] && HopDistance(nbrs, t, d) < 3) crowded = true;
            if (crowded) continue;
            deposit[t] = true;
            placed++;
        }

        var kinds = DepositKinds(sub, nbrs, deposit, spawns);

        double v0 = RegionValue(nbrs, deposit, spawns[0]);
        double v1 = RegionValue(nbrs, deposit, spawns[1]);
        double ratio = Math.Max(v0, v1) / Math.Min(v0, v1);

        return new MapData
        {
            Seed = seed,
            Attempt = attempt,
            UsedFallback = usedFallback,
            Width = Width,
            Height = Height,
            CentroidX = pts.Select(p => (int)Math.Round(p.X * MapData.Scale)).ToArray(),
            CentroidY = pts.Select(p => (int)Math.Round(p.Y * MapData.Scale)).ToArray(),
            Polygon = cells.Select(Flatten).ToArray(),
            Neighbors = nbrs,
            Deposit = deposit,
            Terrain = terrain,
            Chokepoint = Chokepoints(nbrs, spawns[0]),
            DepositKinds = kinds,
            MinorClusters = Minors(sub, nbrs, spawns),
            Rivers = Rivers(sub, pts, nbrs, terrain, elev, spawns),
            Spawns = spawns,
            SpawnValueRatio = ratio,
        };
    }

    /// <summary>
    /// Quotas: every spawn gets an Alloy deposit and an Energy deposit within the fairness radius when
    /// it has two or more nearby; roughly a sixth of the rest become Relics, only beyond RelicMinHops
    /// of every spawn; the remainder are Alloy.
    /// </summary>
    private static DepositKind[] DepositKinds(ulong sub, int[][] nbrs, bool[] deposit, int[] spawns)
    {
        var rng = new Pcg32(SplitMix64.Derive(sub, "depositKinds"));
        var kinds = new DepositKind[deposit.Length];
        var all = Enumerable.Range(0, deposit.Length).Where(t => deposit[t]).ToList();
        foreach (int t in all) kinds[t] = DepositKind.Alloy;
        foreach (int sp in spawns)
        {
            var near = all.Where(t => HopDistance(nbrs, sp, t) is >= 0 and <= FairnessHops).ToList();
            if (near.Count >= 2) kinds[near[rng.NextInt(0, near.Count - 1)]] = DepositKind.Energy;
        }
        var far = all.Where(t => kinds[t] == DepositKind.Alloy && spawns.All(sp => HopDistance(nbrs, sp, t) is < 0 or >= RelicMinHops)).ToList();
        int relics = Math.Max(1, all.Count / 6);
        for (int i = 0; i < relics && far.Count > 0; i++)
        {
            int k = rng.NextInt(0, far.Count - 1);
            kinds[far[k]] = DepositKind.Relic;
            far.RemoveAt(k);
        }
        return kinds;
    }

    /// <summary>Elevation and moisture value-noise (separate sub-seeds) classified into biomes.</summary>
    private static Terrain[] Terrains(ulong sub, (double X, double Y)[] pts, out double[] elev)
    {
        ulong elevSeed = SplitMix64.Derive(sub, "elevation");
        ulong moistSeed = SplitMix64.Derive(sub, "moisture");
        var result = new Terrain[pts.Length];
        elev = new double[pts.Length];
        var moist = new double[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            double x = pts[i].X / Width * NoisePeriodX, y = pts[i].Y / Height * 3;
            elev[i] = Fbm(elevSeed, x, y);
            moist[i] = Fbm(moistSeed, x, y);
        }
        // Value-noise fBm clusters tightly around 0.5, so classify by rank to get stable proportions.
        var sorted = (double[])elev.Clone();
        Array.Sort(sorted);
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        double sea = At(WaterShare), highland = At(1 - MountainShare - HighlandShare), peak = At(1 - MountainShare);
        // Moisture splits forest from plains at its own median.
        var msorted = (double[])moist.Clone();
        Array.Sort(msorted);
        double wet = msorted[msorted.Length / 2];
        for (int i = 0; i < pts.Length; i++)
        {
            double e = elev[i];
            result[i] = e >= peak ? Terrain.Mountains
                      : e < sea ? Terrain.Water
                      : e >= highland ? Terrain.Highlands
                      : moist[i] > wet ? Terrain.Forest
                      : Terrain.Plains;
        }
        return result;
    }

    private static double Fbm(ulong seed, double x, double y)
    {
        double sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < 4; o++)
        {
            sum += amp * ValueNoise(seed + (ulong)o, x, y, NoisePeriodX << o);
            norm += amp;
            amp *= 0.5; x *= 2; y *= 2;
        }
        return sum / norm;
    }

    private const int NoisePeriodX = 5;   // lattice cells across the map at the first octave; noise wraps east-west

    private static double ValueNoise(ulong seed, double x, double y, int periodX)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        int xa = ((x0 % periodX) + periodX) % periodX, xb = (xa + 1) % periodX;
        double a = Lattice(seed, xa, y0), b = Lattice(seed, xb, y0);
        double c = Lattice(seed, xa, y0 + 1), d = Lattice(seed, xb, y0 + 1);
        return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
    }

    private static double Lattice(ulong seed, int x, int y)
    {
        ulong s = seed ^ ((ulong)(uint)x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL);
        return (SplitMix64.Next(ref s) >> 11) / (double)(1UL << 53);
    }

    private static int[] Flatten(List<(double X, double Y)> poly)
    {
        var o = new int[poly.Count * 2];
        for (int i = 0; i < poly.Count; i++)
        {
            o[i * 2] = (int)Math.Round(poly[i].X * MapData.Scale);
            o[i * 2 + 1] = (int)Math.Round(poly[i].Y * MapData.Scale);
        }
        return o;
    }

    private static readonly int[] WrapShifts = { 0, -1, 1 };

    private static List<(double X, double Y)>[] Voronoi((double X, double Y)[] pts, double reach)
    {
        var cells = new List<(double X, double Y)>[pts.Length];
        double maxD2 = reach * 3.4 * reach * 3.4;
        for (int i = 0; i < pts.Length; i++)
        {
            var poly = new List<(double X, double Y)> { (0, 0), (Width, 0), (Width, Height), (0, Height) };
            // Ghost copies one map-width east and west make the east and west edges meet (cylinder).
            for (int j = 0; j < pts.Length; j++)
            foreach (int shift in WrapShifts)
            {
                if (i == j && shift == 0) continue;
                var other = (pts[j].X + shift * Width, pts[j].Y);
                double dx = other.Item1 - pts[i].X, dy = other.Item2 - pts[i].Y;
                if (dx * dx + dy * dy > maxD2) continue;
                poly = Clip(poly, pts[i], other);
            }
            cells[i] = poly;
        }
        return cells;
    }

    private static List<(double X, double Y)> Clip(List<(double X, double Y)> poly, (double X, double Y) a, (double X, double Y) b)
    {
        double mx = (a.X + b.X) * 0.5, my = (a.Y + b.Y) * 0.5;
        double nx = b.X - a.X, ny = b.Y - a.Y;
        var o = new List<(double X, double Y)>(poly.Count + 2);
        for (int k = 0; k < poly.Count; k++)
        {
            var p = poly[k];
            var q = poly[(k + 1) % poly.Count];
            double dp = (p.X - mx) * nx + (p.Y - my) * ny;
            double dq = (q.X - mx) * nx + (q.Y - my) * ny;
            if (dp <= 0) o.Add(p);
            if ((dp <= 0) != (dq <= 0))
            {
                double f = dp / (dp - dq);
                o.Add((p.X + (q.X - p.X) * f, p.Y + (q.Y - p.Y) * f));
            }
        }
        return o;
    }

    private static (double X, double Y) Centroid(List<(double X, double Y)> p)
    {
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var u = p[i];
            var v = p[(i + 1) % p.Count];
            double cross = u.X * v.Y - v.X * u.Y;
            a += cross;
            cx += (u.X + v.X) * cross;
            cy += (u.Y + v.Y) * cross;
        }
        a *= 0.5;
        if (Math.Abs(a) < 1e-9) return (p.Average(q => q.X), p.Average(q => q.Y));
        return (cx / (6 * a), cy / (6 * a));
    }

    private static int[][] Adjacency((double X, double Y)[] pts, List<(double X, double Y)>[] cells)
    {
        int n = pts.Length;
        var result = new int[n][];
        for (int i = 0; i < n; i++)
        {
            var list = new List<int>();
            for (int j = 0; j < n; j++)
            {
                foreach (int shift in WrapShifts)
                {
                    if (i == j) continue;
                    if (SharesEdge(pts[i], (pts[j].X + shift * Width, pts[j].Y), cells[i])) { list.Add(j); break; }
                }
            }
            result[i] = list.ToArray();
        }
        // Symmetrise so numerical noise can never produce a one-way edge.
        var sets = result.Select(r => new SortedSet<int>(r)).ToArray();
        for (int i = 0; i < n; i++)
            foreach (int j in result[i]) sets[j].Add(i);
        return sets.Select(s => s.ToArray()).ToArray();
    }

    private static bool SharesEdge((double X, double Y) a, (double X, double Y) b, List<(double X, double Y)> cell)
    {
        double mx = (a.X + b.X) * 0.5, my = (a.Y + b.Y) * 0.5;
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        double nx = (b.X - a.X) / len, ny = (b.Y - a.Y) / len;
        var on = new List<(double X, double Y)>();
        foreach (var v in cell)
            if (Math.Abs((v.X - mx) * nx + (v.Y - my) * ny) < 0.05) on.Add(v);
        if (on.Count < 2) return false;
        for (int i = 0; i < on.Count; i++)
        for (int j = i + 1; j < on.Count; j++)
            if (Math.Abs(on[i].X - on[j].X) + Math.Abs(on[i].Y - on[j].Y) > 1.0) return true;
        return false;
    }

    private static int Nearest((double X, double Y)[] pts, double x, double y)
    {
        int best = 0;
        double bd = double.MaxValue;
        for (int i = 0; i < pts.Length; i++)
        {
            double d = (pts[i].X - x) * (pts[i].X - x) + (pts[i].Y - y) * (pts[i].Y - y);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    public static int HopDistance(int[][] nbrs, int from, int to)
    {
        if (from == to) return 0;
        var dist = new int[nbrs.Length];
        Array.Fill(dist, -1);
        var q = new Queue<int>();
        dist[from] = 0;
        q.Enqueue(from);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (int nb in nbrs[c])
            {
                if (dist[nb] != -1) continue;
                dist[nb] = dist[c] + 1;
                if (nb == to) return dist[nb];
                q.Enqueue(nb);
            }
        }
        return -1;
    }

    /// <summary>Tiles within FairnessHops of a spawn, each worth 1, deposits worth DepositValue.</summary>
    private static double RegionValue(int[][] nbrs, bool[] deposit, int spawn)
    {
        var dist = new int[nbrs.Length];
        Array.Fill(dist, -1);
        var q = new Queue<int>();
        dist[spawn] = 0;
        q.Enqueue(spawn);
        double value = 0;
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            value += deposit[c] ? DepositValue : 1;
            if (dist[c] == FairnessHops) continue;
            foreach (int nb in nbrs[c])
            {
                if (dist[nb] != -1) continue;
                dist[nb] = dist[c] + 1;
                q.Enqueue(nb);
            }
        }
        return value;
    }
}
