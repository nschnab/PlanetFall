namespace Planetfall.Sim.Systems;

public static class Pathfinder
{
    /// <summary>
    /// Fewest-hops route, excluding the start tile. Neighbour lists are sorted, so ties break the
    /// same way every run. Hop cost is uniform in M2; terrain costs arrive with M3/M4.
    /// </summary>
    public static List<int> Find(int[][] neighbors, int from, int to)
    {
        var path = new List<int>();
        if (from == to) return path;
        var prev = new int[neighbors.Length];
        Array.Fill(prev, -2);
        prev[from] = -1;
        var q = new Queue<int>();
        q.Enqueue(from);
        while (q.Count > 0 && prev[to] == -2)
        {
            int c = q.Dequeue();
            foreach (int n in neighbors[c])
            {
                if (prev[n] != -2) continue;
                prev[n] = c;
                q.Enqueue(n);
            }
        }
        if (prev[to] == -2) return path;
        for (int c = to; c != from; c = prev[c]) path.Add(c);
        path.Reverse();
        return path;
    }
}
