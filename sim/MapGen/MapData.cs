namespace Planetfall.Sim.MapGen;

/// <summary>
/// Plain generated map. Coordinates are map units x16 (integers) so a saved map and a
/// regenerated one compare byte-for-byte. Generation may use doubles; nothing downstream does.
/// </summary>
public sealed record MapData
{
    public const int GeneratorVersion = 1;
    public const int Scale = 16;

    public required ulong Seed { get; init; }
    public required int Attempt { get; init; }
    public required bool UsedFallback { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int[] CentroidX { get; init; }
    public required int[] CentroidY { get; init; }

    /// <summary>Per tile: flattened x,y pairs (render only).</summary>
    public required int[][] Polygon { get; init; }

    /// <summary>Per tile: sorted neighbour ids.</summary>
    public required int[][] Neighbors { get; init; }

    public required bool[] Deposit { get; init; }

    /// <summary>One capital tile per major player slot.</summary>
    public required int[] Spawns { get; init; }

    /// <summary>max/min of the spawn values within the fairness radius; &lt;= 1.15 to pass.</summary>
    public required double SpawnValueRatio { get; init; }

    public int TileCount => CentroidX.Length;

    /// <summary>FNV-1a over every generated field, for determinism tests.</summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        void Add(long v)
        {
            for (int i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
        }

        Add((long)Seed); Add(Attempt); Add(UsedFallback ? 1 : 0); Add(Width); Add(Height);
        for (int t = 0; t < TileCount; t++)
        {
            Add(CentroidX[t]); Add(CentroidY[t]); Add(Deposit[t] ? 1 : 0);
            Add(Polygon[t].Length);
            foreach (int v in Polygon[t]) Add(v);
            Add(Neighbors[t].Length);
            foreach (int n in Neighbors[t]) Add(n);
        }
        foreach (int s in Spawns) Add(s);
        return h;
    }
}
