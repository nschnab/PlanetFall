using Planetfall.Sim.MapGen;

namespace Planetfall.Sim.World;

/// <summary>What one player knows about a tile.</summary>
public enum Sight : byte { Unknown = 0, Explored = 1, Visible = 2 }

/// <summary>
/// One player's view of the map. A tile is Visible while it lies within vision hops of one of that player's
/// (or an ally's) tiles or stacks, Explored once it has been seen (with its last-known owner), and Unknown before.
/// Terrain itself is public knowledge; fog hides ownership and stacks.
/// </summary>
public sealed class Fog
{
    public const int OwnedTileRange = 1;
    public const int HighlandsBonus = 1;

    public int Player { get; }
    public Sight[] Tiles { get; }
    public int[] LastOwner { get; }

    public Fog(int player, int tileCount)
    {
        Player = player;
        Tiles = new Sight[tileCount];
        LastOwner = new int[tileCount];
        Array.Fill(LastOwner, -1);
    }

    public bool Visible(int tile) => Tiles[tile] == Sight.Visible;

    /// <summary>The owner this player believes a tile has: the live owner when visible, the last one seen otherwise.</summary>
    public int KnownOwner(GameState s, int tile) => Tiles[tile] == Sight.Visible ? s.TileOwner[tile] : LastOwner[tile];

    /// <summary>Who shares sight with this player: itself, plus every nation it is allied to.</summary>
    public static IEnumerable<int> Sharers(GameState s, int player)
    {
        for (int p = 0; p < s.Players.Length; p++)
            if (p == player || s.Diplomacy.Get(player, p) == Treaty.Alliance) yield return p;
    }

    /// <summary>Hops a stack sees: its best unit's vision, resolved through the leader's modifiers, plus one on highlands.</summary>
    public static int StackRange(GameState s, Stack st)
    {
        int best = 0;
        foreach (var kv in st.Units)
            if (kv.Value > 0) best = Math.Max(best, s.Catalog.Units[kv.Key].Vision);
        best = (int)s.Stat(st.Owner, "vision.range", best);
        if (s.Map.Terrain[st.Tile] == Terrain.Highlands) best += HighlandsBonus;
        return best;
    }

    /// <summary>Recomputes what is visible now; anything seen becomes Explored for good.</summary>
    public void Update(GameState s)
    {
        int n = Tiles.Length;
        for (int t = 0; t < n; t++)
            if (Tiles[t] == Sight.Visible) Tiles[t] = Sight.Explored;

        var best = new int[n];                     // furthest range remaining when a tile was reached; -1 = unseen
        Array.Fill(best, -1);
        var queue = new Queue<int>();
        var sharers = new HashSet<int>(Sharers(s, Player));

        void Seed(int tile, int range)
        {
            if (range > best[tile]) { best[tile] = range; queue.Enqueue(tile); }
        }

        for (int t = 0; t < n; t++)
            if (sharers.Contains(s.TileOwner[t]))
                Seed(t, OwnedTileRange + (s.Map.Terrain[t] == Terrain.Highlands ? HighlandsBonus : 0));
        foreach (var st in s.Stacks)
            if (sharers.Contains(st.Owner)) Seed(st.Tile, StackRange(s, st));

        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            int r = best[t];
            if (r <= 0) continue;
            foreach (int nb in s.Map.Neighbors[t])
                if (r - 1 > best[nb]) { best[nb] = r - 1; queue.Enqueue(nb); }
        }

        for (int t = 0; t < n; t++)
        {
            if (best[t] < 0) continue;
            Tiles[t] = Sight.Visible;
            LastOwner[t] = s.TileOwner[t];
        }
    }

    /// <summary>True if the player may see this stack: it is theirs or an ally's, or it stands on (or is hopping across) a visible tile.</summary>
    public bool CanSee(GameState s, Stack st)
    {
        if (st.Owner == Player || s.Diplomacy.Get(Player, st.Owner) == Treaty.Alliance) return true;
        return Visible(st.Tile) || (st.Target != -1 && Visible(st.Target));
    }
}
