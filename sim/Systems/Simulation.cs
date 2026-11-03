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
        Entrench(s);
        Merge(s, events);
        Capture(s, events);
        Encircle(s, events);
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

    /// <summary>Timer units a stack's current hop needs; river crossings take longer.</summary>
    public static long HopNeeded(GameState s, Stack st)
    {
        long needed = s.Stat(st.Owner, Rules.StatMoveDelay, Rules.HopDelayMs) * 1000;
        if (st.Target != -1 && s.Map.IsRiver(st.Tile, st.Target)) needed = needed * Rules.RiverCrossPermille / 1000;
        return needed;
    }

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
            long needed = HopNeeded(s, st);
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
        for (int v = 0; v < s.Stacks.Count; v++)
        {
            var victim = s.Stacks[v];
            long raw = 0;
            var fronts = new HashSet<int>();
            foreach (var foe in s.Stacks)
            {
                if (!Fighting(victim, foe)) continue;
                raw += (long)foe.EffectiveStrength * Rules.DamagePerStrPerTickMilli;
                fronts.Add(foe.Tile);
            }
            if (raw == 0) continue;

            long flank = 1000 + (long)Rules.FlankBonusPermille * (Math.Min(fronts.Count, Rules.MaxFronts) - 1);
            long attacker = IsAttackerAgainstAny(s, victim) ? Rules.AttackerPenaltyPermille : 1000;
            long dug = 1000 + victim.Entrench;
            long denom = 1000L * 1000 * dug;
            damage[v] = (raw * flank * attacker * 1000 + denom / 2) / denom;
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

    /// <summary>
    /// Attackers are the side that began a hop into the other's tile; where both share a tile the
    /// side that does not own it attacks. Two stacks hopping at each other are both attackers
    /// (so neither is penalised relative to the other).
    /// </summary>
    private static bool IsAttackerAgainstAny(GameState s, Stack v)
    {
        foreach (var o in s.Stacks)
        {
            if (!Fighting(v, o)) continue;
            if (v.Target == o.Tile && o.Target != v.Tile) return true;
            if (v.Tile == o.Tile && s.TileOwner[v.Tile] != v.Owner && s.TileOwner[v.Tile] == o.Owner) return true;
        }
        return false;
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

    // ---- entrenchment ---------------------------------------------------------------------

    /// <summary>
    /// Standing still for 10 s starts digging in: +10% defence per further 10 s, up to +50%. Moving
    /// removes it. Being attacked freezes the timer but keeps what has been dug.
    /// </summary>
    private static void Entrench(GameState s)
    {
        foreach (var st in s.Stacks)
        {
            if (st.Target != -1 || st.Path.Count > 0)
            {
                st.StillTicks = 0;
                st.Entrench = 0;
                continue;
            }
            if (Engaged(s, st)) continue;
            st.StillTicks++;
            if (st.StillTicks % Rules.EntrenchStartTicks == 0 && st.Entrench < Rules.EntrenchMaxPermille)
                st.Entrench = Math.Min(Rules.EntrenchMaxPermille, st.Entrench + Rules.EntrenchStepPermille);
        }
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
                a.Entrench = Math.Min(a.Entrench, b.Entrench);
                a.StillTicks = Math.Min(a.StillTicks, b.StillTicks);
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

    // ---- encirclement ---------------------------------------------------------------------

    /// <summary>
    /// A pocket of tiles (neutral or foreign) whose every border tile is yours, and whose border
    /// is connected to your capital, falls to you automatically. Not if an enemy stack is inside
    /// or the pocket holds another major's capital.
    /// </summary>
    private static void Encircle(GameState s, List<SimEvent> events)
    {
        foreach (var p in s.Majors)
        {
            if (s.TileOwner[p.Capital] != p.Id) continue;
            var linked = ConnectedToCapital(s, p);
            var seen = new bool[s.Map.TileCount];

            for (int start = 0; start < s.Map.TileCount; start++)
            {
                if (seen[start] || s.TileOwner[start] == p.Id || !s.Map.Passable(start)) continue;

                var pocket = new List<int>();
                var q = new Queue<int>();
                q.Enqueue(start);
                seen[start] = true;
                bool enclosed = true;
                while (q.Count > 0)
                {
                    int c = q.Dequeue();
                    pocket.Add(c);
                    foreach (int n in s.Map.Neighbors[c])
                    {
                        if (s.TileOwner[n] == p.Id)
                        {
                            if (!linked[n]) enclosed = false;
                        }
                        else if (!seen[n])
                        {
                            seen[n] = true;
                            q.Enqueue(n);
                        }
                    }
                }
                if (!enclosed) continue;
                if (pocket.Any(t => s.Players.Any(o => o.Id != p.Id && o.Capital == t && !o.Minor))) continue;
                if (s.Stacks.Any(x => x.Owner != p.Id && pocket.Contains(x.Tile))) continue;

                pocket.Sort();
                foreach (int t in pocket)
                {
                    int prev = s.TileOwner[t];
                    s.TileOwner[t] = p.Id;
                    events.Add(new TileCaptured(t, p.Id, prev));
                }
            }
        }
    }

    private static bool[] ConnectedToCapital(GameState s, Player p)
    {
        var linked = new bool[s.Map.TileCount];
        var q = new Queue<int>();
        linked[p.Capital] = true;
        q.Enqueue(p.Capital);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (int n in s.Map.Neighbors[c])
            {
                if (linked[n] || s.TileOwner[n] != p.Id) continue;
                linked[n] = true;
                q.Enqueue(n);
            }
        }
        return linked;
    }

    // ---- economy --------------------------------------------------------------------------

    /// <summary>
    /// New stacks appear only at your capital or on a deposit ("mine") you own, and never on a tile
    /// an enemy stack is standing on. Of the open sites the one holding the fewest of your stacks
    /// wins (capital first on ties), so recruits spread out. -1 when no site is open.
    /// </summary>
    private static int SpawnTile(GameState s, Player p)
    {
        int best = -1;
        int bestCount = int.MaxValue;
        for (int t = -1; t < s.Map.TileCount; t++)
        {
            int tile = t == -1 ? p.Capital : t;
            if (t != -1 && (!s.Map.Deposit[t] || tile == p.Capital)) continue;
            if (s.TileOwner[tile] != p.Id) continue;
            if (s.Stacks.Any(x => x.Owner != p.Id && x.Tile == tile)) continue;
            int count = s.Stacks.Count(x => x.Owner == p.Id && x.Tile == tile);
            if (count < bestCount) { bestCount = count; best = tile; }
        }
        return best;
    }

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
            int at = SpawnTile(s, p);
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
