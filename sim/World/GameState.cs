using Planetfall.Sim.Core;
using Planetfall.Sim.Core.Modifiers;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;

namespace Planetfall.Sim.World;

public sealed class Player
{
    public int Id { get; init; }
    public required Leader Leader { get; init; }
    public int Capital { get; init; }

    /// <summary>Alloy in thousandths.</summary>
    public long Supply { get; set; }

    /// <summary>Minor powers hold ground and never expand, buy stacks, or count for victory.</summary>
    public bool Minor { get; init; }

    /// <summary>Most stacks this player may field at once; recruiting pauses at the cap.</summary>
    public int MaxStacks { get; set; } = int.MaxValue;
}

public sealed class GameState
{
    public required MapData Map { get; init; }
    public required Player[] Players { get; init; }

    /// <summary>Owner player id per tile, -1 for neutral.</summary>
    public required int[] TileOwner { get; init; }

    public List<Stack> Stacks { get; } = new();
    public int Tick { get; set; }
    public int NextStackId { get; set; } = 1;

    /// <summary>Winning player id, or -1 while the match is on.</summary>
    public int Winner { get; set; } = -1;
    public bool Over => Winner != -1;

    /// <summary>Consecutive ticks an enemy has held each player's capital.</summary>
    public int[] CapitalHeld { get; set; } = Array.Empty<int>();

    public static GameState New(MapData map, params Leader[] leaders)
    {
        if (leaders.Length > map.Spawns.Length) throw new ArgumentException("More players than spawns");
        var players = leaders.Select((l, i) => new Player { Id = i, Leader = l, Capital = map.Spawns[i] }).ToArray();
        var owner = new int[map.TileCount];
        Array.Fill(owner, -1);
        var s = new GameState { Map = map, Players = players, TileOwner = owner };
        foreach (var p in players)
        {
            owner[p.Capital] = p.Id;
            s.AddStack(p.Id, p.Capital, Rules.StartStrength);
        }
        s.CapitalHeld = new int[players.Length];
        return s;
    }

    /// <summary>The majors plus a static hostile minor power garrisoning one tile.</summary>
    public static GameState NewWithMinor(MapData map, int minorTile, int minorStrength, params Leader[] leaders)
    {
        var s = New(map, leaders);
        var minor = new Player
        {
            Id = leaders.Length,
            Leader = Leader.Parse("""{ "id": "minor", "name": "Minor Power" }"""),
            Capital = minorTile,
            Minor = true,
        };
        var players = s.Players.Append(minor).ToArray();
        var g = new GameState { Map = map, Players = players, TileOwner = s.TileOwner };
        g.Stacks.AddRange(s.Stacks);
        g.NextStackId = s.NextStackId;
        g.CapitalHeld = new int[players.Length];
        g.TileOwner[minorTile] = minor.Id;
        g.AddStack(minor.Id, minorTile, minorStrength);
        return g;
    }

    /// <summary>The majors plus one static minor power per map cluster, each owning its whole cluster and garrisoning its first tile.</summary>
    public static GameState NewWithMinors(MapData map, int minorStrength, params Leader[] leaders)
    {
        var s = New(map, leaders);
        var minors = map.MinorClusters.Select((c, i) => new Player
        {
            Id = leaders.Length + i,
            Leader = Leader.Parse($$"""{ "id": "minor{{i}}", "name": "Minor Power {{i + 1}}" }"""),
            Capital = c[0],
            Minor = true,
        }).ToArray();
        var players = s.Players.Concat(minors).ToArray();
        var g = new GameState { Map = map, Players = players, TileOwner = s.TileOwner };
        g.Stacks.AddRange(s.Stacks);
        g.NextStackId = s.NextStackId;
        g.CapitalHeld = new int[players.Length];
        foreach (var m in minors)
        {
            foreach (int t in map.MinorClusters[m.Id - leaders.Length]) g.TileOwner[t] = m.Id;
            g.AddStack(m.Id, m.Capital, minorStrength);
        }
        return g;
    }

    /// <summary>Deposit nearest the map centre that is more than three hops from every spawn.</summary>
    public static int PickMinorTile(MapData map)
    {
        var near = new HashSet<int>();
        foreach (int sp in map.Spawns)
        {
            var frontier = new List<int> { sp };
            near.Add(sp);
            for (int d = 0; d < 3; d++)
            {
                var next = new List<int>();
                foreach (int t in frontier)
                    foreach (int n in map.Neighbors[t])
                        if (near.Add(n)) next.Add(n);
                frontier = next;
            }
        }
        long cx = map.CentroidX.Sum(x => (long)x) / map.TileCount;
        long cy = map.CentroidY.Sum(y => (long)y) / map.TileCount;
        int best = -1;
        long bestScore = long.MaxValue;
        for (int t = 0; t < map.TileCount; t++)
        {
            if (near.Contains(t) || !map.Passable(t)) continue;
            long dx = map.CentroidX[t] - cx, dy = map.CentroidY[t] - cy;
            long score = dx * dx + dy * dy - (map.Deposit[t] ? 1L << 40 : 0);
            if (score < bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    public Stack AddStack(int owner, int tile, int strength)
    {
        var st = new Stack
        {
            Id = NextStackId++,
            Owner = owner,
            Tile = tile,
            Hp = strength * Rules.HpUnit,
            MaxHp = strength * Rules.HpUnit,
        };
        Stacks.Add(st);
        return st;
    }

    public Stack? FindStack(int id) => Stacks.FirstOrDefault(s => s.Id == id);

    public long Stat(int player, string key, long baseValue) =>
        StatResolver.Resolve(key, baseValue, new IModifierSource[] { Players[player].Leader });

    public IEnumerable<Player> Majors => Players.Where(p => !p.Minor);

    public int OwnedTiles(int player) => TileOwner.Count(o => o == player);

    /// <summary>FNV-1a over everything that can differ between two runs.</summary>
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

        Add(Tick); Add(NextStackId); Add(Winner);
        foreach (int c in CapitalHeld) Add(c);
        foreach (int o in TileOwner) Add(o);
        foreach (var p in Players) { Add(p.Id); Add(p.Supply); }
        foreach (var s in Stacks)
        {
            Add(s.Id); Add(s.Owner); Add(s.Tile); Add(s.Target); Add(s.Timer);
            Add(s.Hp); Add(s.MaxHp); Add(s.Retreating ? 1 : 0); Add(s.StillTicks); Add(s.Entrench);
            Add(s.Path.Count);
            foreach (int t in s.Path) Add(t);
        }
        return h;
    }
}
