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
        return s;
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

        Add(Tick); Add(NextStackId);
        foreach (int o in TileOwner) Add(o);
        foreach (var p in Players) { Add(p.Id); Add(p.Supply); }
        foreach (var s in Stacks)
        {
            Add(s.Id); Add(s.Owner); Add(s.Tile); Add(s.Target); Add(s.Timer);
            Add(s.Hp); Add(s.MaxHp); Add(s.Retreating ? 1 : 0);
            Add(s.Path.Count);
            foreach (int t in s.Path) Add(t);
        }
        return h;
    }
}
