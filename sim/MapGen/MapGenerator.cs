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

    public static MapData Generate(ulong seed, int cols = 20, int rows = 10)
    {
        MapData? last = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ulong sub = SplitMix64.Derive(seed, "map:" + attempt);
            MapData map = Build(seed, attempt, sub, cols, rows, usedFallback: false);
            last = map;
            if (map.SpawnValueRatio <= MaxSpawnValueRatio && SpawnsFarEnough(map)) return map;
        }
        return last! with { UsedFallback = true };
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
            Nearest(pts, Width * 0.12, Height * 0.25),
            Nearest(pts, Width * 0.88, Height * 0.75),
        };

        int depositCount = Math.Max(2, n * 5 / 88);
        var deposit = new bool[n];
        var banned = new HashSet<int>(spawns);
        int placed = 0;
        for (int tries = 0; placed < depositCount && tries < 2000; tries++)
        {
            int t = rng.NextInt(0, n - 1);
            if (deposit[t] || banned.Contains(t)) continue;
            bool crowded = false;
            for (int d = 0; d < n && !crowded; d++)
                if (deposit[d] && HopDistance(nbrs, t, d) < 3) crowded = true;
            if (crowded) continue;
            deposit[t] = true;
            placed++;
        }

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
            Spawns = spawns,
            SpawnValueRatio = ratio,
        };
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

    private static List<(double X, double Y)>[] Voronoi((double X, double Y)[] pts, double reach)
    {
        var cells = new List<(double X, double Y)>[pts.Length];
        double maxD2 = reach * 3.4 * reach * 3.4;
        for (int i = 0; i < pts.Length; i++)
        {
            var poly = new List<(double X, double Y)> { (0, 0), (Width, 0), (Width, Height), (0, Height) };
            for (int j = 0; j < pts.Length; j++)
            {
                if (i == j) continue;
                double dx = pts[j].X - pts[i].X, dy = pts[j].Y - pts[i].Y;
                if (dx * dx + dy * dy > maxD2) continue;
                poly = Clip(poly, pts[i], pts[j]);
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
                if (i == j) continue;
                if (SharesEdge(pts[i], pts[j], cells[i])) list.Add(j);
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
