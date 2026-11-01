using Planetfall.Sim.Core;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Ai;

/// <summary>
/// The rival commander. Reads the state like a player would and returns Commands; it never
/// touches the state. Healthy stacks hunt the cheapest foreign tile to reach (deposits and the
/// enemy capital are worth a detour), wounded stacks fall back to the capital to merge with
/// fresh recruits, and nothing walks into a tile held by a much bigger force.
/// </summary>
public sealed class RivalAi
{
    public const int ThinkEveryTicks = 10;

    private readonly int _me;

    public RivalAi(int player) => _me = player;

    public IReadOnlyList<Command> Think(GameState s)
    {
        var cmds = new List<Command>();
        if (s.Over || (s.Tick + _me) % ThinkEveryTicks != 0) return cmds;

        var mine = s.Stacks.Where(x => x.Owner == _me).OrderBy(x => x.Id).ToList();
        var claimed = new HashSet<int>();
        foreach (var st in mine)
        {
            int dest = st.Path.Count > 0 ? st.Path[^1] : st.Target;
            if (dest != -1) claimed.Add(dest);
        }

        int capital = s.Players[_me].Capital;
        foreach (var st in mine)
        {
            if (!st.Idle) continue;

            if (st.Hp * 2 < st.MaxHp)
            {
                if (st.Tile != capital) cmds.Add(new MoveCommand(_me, st.Id, capital));
                continue;
            }

            int target = ChooseTarget(s, st, claimed);
            if (target == -1) continue;
            claimed.Add(target);
            cmds.Add(new MoveCommand(_me, st.Id, target));
        }
        return cmds;
    }

    private int ChooseTarget(GameState s, Stack st, HashSet<int> claimed)
    {
        var dist = new int[s.Map.TileCount];
        Array.Fill(dist, -1);
        var q = new Queue<int>();
        dist[st.Tile] = 0;
        q.Enqueue(st.Tile);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (int n in s.Map.Neighbors[c])
            {
                if (dist[n] != -1) continue;
                dist[n] = dist[c] + 1;
                q.Enqueue(n);
            }
        }

        int best = -1;
        int bestCost = int.MaxValue;
        for (int t = 0; t < s.Map.TileCount; t++)
        {
            if (dist[t] <= 0 || s.TileOwner[t] == _me || claimed.Contains(t)) continue;
            if (Defenders(s, t) * 2 > st.EffectiveStrength * 3) continue;   // more than 1.5x our strength

            int owner = s.TileOwner[t];
            int value = 2;
            if (owner != -1) value += 1;
            if (s.Map.Deposit[t]) value += 2;
            if (owner != -1 && owner != _me && s.Players[owner].Capital == t && !s.Players[owner].Minor) value += 5;

            int cost = dist[t] * 100 - value * 40;
            if (cost < bestCost) { bestCost = cost; best = t; }
        }
        return best;
    }

    private int Defenders(GameState s, int tile)
    {
        int total = 0;
        foreach (var o in s.Stacks)
            if (o.Owner != _me && (o.Tile == tile || o.Target == tile)) total += o.EffectiveStrength;
        return total;
    }
}
