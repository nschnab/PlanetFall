using Planetfall.Sim.Core;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Systems;

/// <summary>
/// One 10 Hz tick: (state, commands) -> (state', events). Stacks are always processed in id
/// order and each phase reads a snapshot taken before it mutates, so results never depend on
/// iteration order.
/// </summary>
public static class Simulation
{
    public static List<SimEvent> Step(GameState s, IReadOnlyList<Command> commands)
    {
        var events = new List<SimEvent>();
        if (s.Over) return events;
        int surrendered = ApplyCommands(s, commands);
        Movement(s, events);
        Combat(s, events);
        Merge(s, events);
        Capture(s, events);
        Economy(s, events);
        Victory(s, events, surrendered);
        s.Tick++;
        return events;
    }

    // ---- commands -------------------------------------------------------------------------

    /// <summary>Returns the id of a player who surrendered this tick, or -1.</summary>
    private static int ApplyCommands(GameState s, IReadOnlyList<Command> commands)
    {
        int surrendered = -1;
        foreach (var cmd in commands)
        {
            switch (cmd)
            {
                case MoveCommand m:
                {
                    var st = s.FindStack(m.StackId);
                    if (st == null || st.Owner != m.Player || m.TargetTile < 0 || m.TargetTile >= s.Map.TileCount) break;
                    if (m.Queue)
                    {
                        int origin = st.Path.Count > 0 ? st.Path[^1] : st.Target != -1 ? st.Target : st.Tile;
                        st.Path.AddRange(Pathfinder.Find(s.Map.Neighbors, origin, m.TargetTile));
                    }
                    else
                    {
                        var route = Pathfinder.Find(s.Map.Neighbors, st.Tile, m.TargetTile);
                        // A hop already under way stays only if the new route still begins with it.
                        if (st.Target != -1 && (route.Count == 0 || route[0] != st.Target))
                        {
                            st.Target = -1;
                            st.Timer = 0;
                        }
                        else if (st.Target != -1)
                        {
                            route.RemoveAt(0);
                        }
                        st.Path.Clear();
                        st.Path.AddRange(route);
                    }
                    break;
                }
                case SurrenderCommand sc:
                    if (surrendered == -1 && sc.Player >= 0 && sc.Player < s.Players.Length && !s.Players[sc.Player].Minor)
                        surrendered = sc.Player;
                    break;
                case StopCommand c:
                {
                    var st = s.FindStack(c.StackId);
                    if (st == null || st.Owner != c.Player) break;
                    st.Path.Clear();
                    st.Target = -1;
                    st.Timer = 0;
                    break;
                }
            }
        }
        return surrendered;
    }

    // ---- fights ---------------------------------------------------------------------------

    /// <summary>
    /// Hostile stacks fight when they share a tile, or when either has begun a hop into the
    /// other's tile. Fights therefore start when the order begins, not on arrival.
    /// </summary>
    public static bool Fighting(Stack a, Stack b) =>
        a.Owner != b.Owner && (a.Tile == b.Tile || a.Target == b.Tile || b.Target == a.Tile);

    public static bool Engaged(GameState s, Stack a)
    {
        foreach (var o in s.Stacks)
            if (o != a && Fighting(a, o)) return true;
        return false;
    }

    // ---- movement -------------------------------------------------------------------------

    private static void Movement(GameState s, List<SimEvent> events)
    {
        // Phase A: idle stacks with a route begin their next hop.
        foreach (var st in s.Stacks)
        {
            if (st.Target == -1 && st.Path.Count > 0)
            {
                st.Target = st.Path[0];
                st.Path.RemoveAt(0);
                st.Timer = 0;
                events.Add(new HopStarted(st.Id, st.Tile, st.Target));
            }
        }

        // Phase B: who is in a fight right now (snapshot, before anyone hops).
        var engaged = new bool[s.Stacks.Count];
        for (int i = 0; i < s.Stacks.Count; i++) engaged[i] = Engaged(s, s.Stacks[i]);

        // Phase C: advance timers and complete hops.
        for (int i = 0; i < s.Stacks.Count; i++)
        {
            var st = s.Stacks[i];
            if (st.Target == -1) continue;
            long rate = s.Stat(st.Owner, Rules.StatMoveSpeed, Rules.SpeedPermille);
            if (engaged[i]) rate = rate * Rules.BattleSlowPermille / 1000;
            st.Timer += Rules.TickMs * rate;
            long needed = s.Stat(st.Owner, Rules.StatMoveDelay, Rules.HopDelayMs) * 1000;
            if (st.Timer >= needed)
            {
                st.Tile = st.Target;
                st.Target = -1;
                st.Timer = 0;
                st.Retreating = false;
                events.Add(new HopCompleted(st.Id, st.Tile));
            }
        }
    }

    // ---- combat ---------------------------------------------------------------------------

    private static void Combat(GameState s, List<SimEvent> events)
    {
        var damage = new long[s.Stacks.Count];
        for (int i = 0; i < s.Stacks.Count; i++)
        for (int j = i + 1; j < s.Stacks.Count; j++)
        {
            var a = s.Stacks[i];
            var b = s.Stacks[j];
            if (!Fighting(a, b)) continue;
            damage[i] += (long)b.EffectiveStrength * Rules.DamagePerStrPerTickMilli;
            damage[j] += (long)a.EffectiveStrength * Rules.DamagePerStrPerTickMilli;
        }
        for (int i = 0; i < s.Stacks.Count; i++) s.Stacks[i].Hp -= damage[i];

        foreach (var st in s.Stacks.Where(x => x.Hp <= 0).ToList())
        {
            s.Stacks.Remove(st);
            events.Add(new StackDied(st.Id, st.Owner, st.Tile));
        }

        // Below a quarter of its health a stack breaks off to a friendly neighbouring tile.
        foreach (var st in s.Stacks)
        {
            if (st.Retreating || st.Hp * 4 >= st.MaxHp || !Engaged(s, st)) continue;
            int dest = RetreatTile(s, st);
            if (dest == -1) continue;
            st.Retreating = true;
            st.Path.Clear();
            st.Target = dest;
            st.Timer = 0;
            events.Add(new StackRetreating(st.Id, dest));
        }
    }

    private static int RetreatTile(GameState s, Stack st)
    {
        foreach (int n in s.Map.Neighbors[st.Tile])
        {
            if (s.TileOwner[n] != st.Owner) continue;
            if (s.Stacks.Any(o => o.Owner != st.Owner && (o.Tile == n || o.Target == n))) continue;
            return n;
        }
        return -1;
    }

    // ---- merging --------------------------------------------------------------------------

    private static void Merge(GameState s, List<SimEvent> events)
    {
        for (int i = 0; i < s.Stacks.Count; i++)
        {
            var a = s.Stacks[i];
            if (!a.Idle) continue;
            for (int j = i + 1; j < s.Stacks.Count; j++)
            {
                var b = s.Stacks[j];
                if (!b.Idle || b.Owner != a.Owner || b.Tile != a.Tile) continue;
                a.Hp += b.Hp;
                a.MaxHp += b.MaxHp;
                s.Stacks.RemoveAt(j);
                events.Add(new StacksMerged(a.Id, b.Id));
                j--;
            }
        }
    }

    // ---- capture --------------------------------------------------------------------------

    private static void Capture(GameState s, List<SimEvent> events)
    {
        foreach (var st in s.Stacks)
        {
            if (s.TileOwner[st.Tile] == st.Owner) continue;
            if (s.Stacks.Any(o => o.Owner != st.Owner && o.Tile == st.Tile)) continue;
            int prev = s.TileOwner[st.Tile];
            s.TileOwner[st.Tile] = st.Owner;
            events.Add(new TileCaptured(st.Tile, st.Owner, prev));
        }
    }

    // ---- economy --------------------------------------------------------------------------

    private static void Economy(GameState s, List<SimEvent> events)
    {
        foreach (var p in s.Majors)
        {
            long income = 0;
            long tileYield = s.Stat(p.Id, Rules.StatYield, Rules.TileYieldMilli);
            for (int t = 0; t < s.TileOwner.Length; t++)
                if (s.TileOwner[t] == p.Id) income += s.Map.Deposit[t] ? tileYield * Rules.DepositMultiplier : tileYield;
            p.Supply += income;

            long cost = s.Stat(p.Id, Rules.StatStackCost, Rules.StackCostMilli);
            if (p.Supply < cost || s.Stacks.Count(x => x.Owner == p.Id) >= p.MaxStacks) continue;
            int at = s.TileOwner[p.Capital] == p.Id ? p.Capital : Array.IndexOf(s.TileOwner, p.Id);
            if (at < 0) continue;
            p.Supply -= cost;
            var ns = s.AddStack(p.Id, at, Rules.NewStackStrength);
            events.Add(new StackSpawned(ns.Id, p.Id, at));
        }
    }

    // ---- victory --------------------------------------------------------------------------

    /// <summary>
    /// A major loses when an enemy holds its capital for CapitulationTicks, when it has no tiles
    /// and no stacks, or when it surrenders. The other major wins and receives its tiles.
    /// </summary>
    private static void Victory(GameState s, List<SimEvent> events, int surrendered)
    {
        int loser = surrendered;
        string reason = "surrender";

        foreach (var p in s.Majors)
        {
            s.CapitalHeld[p.Id] = s.TileOwner[p.Capital] != p.Id ? s.CapitalHeld[p.Id] + 1 : 0;
            if (loser != -1) continue;
            if (s.CapitalHeld[p.Id] >= Rules.CapitulationTicks) { loser = p.Id; reason = "capital held"; }
            else if (s.OwnedTiles(p.Id) == 0 && !s.Stacks.Any(x => x.Owner == p.Id)) { loser = p.Id; reason = "annihilated"; }
        }
        if (loser == -1) return;

        int winner = s.Majors.First(p => p.Id != loser).Id;
        for (int t = 0; t < s.TileOwner.Length; t++)
            if (s.TileOwner[t] == loser) s.TileOwner[t] = winner;
        s.Stacks.RemoveAll(x => x.Owner == loser);
        s.Winner = winner;
        events.Add(new PlayerDefeated(loser, reason));
        events.Add(new MatchOver(winner));
    }
}
