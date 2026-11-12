using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
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
        int surrendered = ApplyCommands(s, commands, events);
        Movement(s, events);
        Combat(s, events);
        Entrench(s);
        Construct(s, events);
        Merge(s, events);
        Capture(s, events);
        Encircle(s, events);
        Economy(s, events);
        DiplomacySystem.Tick(s, events);
        Victory(s, events, surrendered);
        s.Tick++;
        if (s.Tick % GameState.FogEveryTicks == 0) s.UpdateFogs();
        return events;
    }

    // ---- commands -------------------------------------------------------------------------

    /// <summary>Returns the id of a player who surrendered this tick, or -1.</summary>
    private static int ApplyCommands(GameState s, IReadOnlyList<Command> commands, List<SimEvent> events)
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
                case RecruitCommand rc:
                    Recruit(s, rc, events);
                    break;
                case BuildCommand bc:
                    StartBuild(s, bc, events);
                    break;
                case GiftCommand or ProposeTreatyCommand or BreakTreatyCommand or DeclareWarCommand or IntegrateCommand or DemandSurrenderCommand:
                {
                    int gave = DiplomacySystem.Apply(s, cmd, events);
                    if (gave != -1 && surrendered == -1) surrendered = gave;
                    break;
                }
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
    public static bool Fighting(GameState s, Stack a, Stack b) =>
        s.AtWar(a.Owner, b.Owner) && (a.Tile == b.Tile || a.Target == b.Tile || b.Target == a.Tile);

    public static bool Engaged(GameState s, Stack a)
    {
        foreach (var o in s.Stacks)
            if (o != a && Fighting(s, a, o)) return true;
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

    /// <summary>A stack moves at the pace of its slowest unit.</summary>
    public static long StackSpeed(GameState s, Stack st)
    {
        long slowest = long.MaxValue;
        foreach (var kv in st.Units)
            slowest = Math.Min(slowest, s.UnitStat(st.Owner, "unit.speed", s.Catalog.Units[kv.Key], s.Catalog.Units[kv.Key].SpeedPermille));
        return slowest == long.MaxValue ? 1000 : slowest;
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
            long rate = s.Stat(st.Owner, Rules.StatMoveSpeed, Rules.SpeedPermille) * StackSpeed(s, st) / 1000;
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
            var battleTiles = new HashSet<int>();
            var foeOwners = new HashSet<int>();
            foreach (var foe in s.Stacks)
            {
                if (!Fighting(s, victim, foe)) continue;
                int bt = BattleTile(victim, foe);
                raw += AttackOutput(s, foe, victim, bt, engaged: true, supportOnly: false);
                fronts.Add(foe.Tile);
                battleTiles.Add(bt);
                foeOwners.Add(foe.Owner);
            }
            if (raw == 0) continue;

            // Artillery next door that is not itself in a fight shells the battle.
            foreach (var a in s.Stacks)
            {
                if (a.Owner == victim.Owner || !foeOwners.Contains(a.Owner) || !a.Idle) continue;
                if (!a.Units.Keys.Any(k => s.Catalog.Units[k].Supports)) continue;
                int near = battleTiles.FirstOrDefault(bt => s.Map.Neighbors[bt].Contains(a.Tile), -1);
                if (near == -1 || Engaged(s, a)) continue;
                raw += AttackOutput(s, a, victim, near, engaged: false, supportOnly: true);
            }

            long flank = 1000 + (long)Rules.FlankBonusPermille * (Math.Min(fronts.Count, Rules.MaxFronts) - 1);
            long attacker = IsAttackerAgainstAny(s, victim) ? Rules.AttackerPenaltyPermille : 1000;
            long dug = 1000 + victim.Entrench + Defence(s, victim);
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

    /// <summary>The tile a fight between these two is fought on: where the victim stands unless it is the one attacking.</summary>
    private static int BattleTile(Stack victim, Stack foe) =>
        victim.Target == foe.Tile && foe.Target != victim.Tile ? foe.Tile : victim.Tile;

    private static StructureDef? BuiltStructure(GameState s, int tile) =>
        s.Structure[tile] is { } id && s.Catalog.Structures.TryGetValue(id, out var d) ? d : null;

    /// <summary>Defence in permille added by the ground the victim stands on and any fortress it owns there.</summary>
    private static long Defence(GameState s, Stack victim)
    {
        long d = s.Catalog.Terrain[s.Map.Terrain[victim.Tile]].DefencePermille;
        var sd = BuiltStructure(s, victim.Tile);
        if (sd != null && sd.DefencePermille > 0 && s.TileOwner[victim.Tile] == victim.Owner)
            d += s.Stat(victim.Owner, "fort.max", sd.DefencePermille);
        return d;
    }

    private static string ArmorOf(GameState s, Stack victim, UnitDef u)
    {
        var sd = BuiltStructure(s, victim.Tile);
        return sd?.ArmorOverride != null && s.TileOwner[victim.Tile] == victim.Owner ? sd.ArmorOverride : u.Armor;
    }

    /// <summary>Damage type vs the victim's armor mix (weighted by strength), in permille.</summary>
    private static long ArmorMultiplier(GameState s, Stack victim, string damageType)
    {
        long num = 0, den = 0;
        foreach (var kv in victim.Units)
        {
            var def = s.Catalog.Units[kv.Key];
            long w = (long)kv.Value * def.Strength;
            num += w * s.Catalog.Combat.Multiplier(damageType, ArmorOf(s, victim, def));
            den += w;
        }
        return den == 0 ? 1000 : num / den;
    }

    /// <summary>
    /// Raw damage per tick (thousandths of hp) a stack deals to a victim: each unit type contributes
    /// its strength x attack x terrain x engaged-penalty x type matrix, scaled by how healthy the stack is.
    /// </summary>
    private static long AttackOutput(GameState s, Stack foe, Stack victim, int battleTile, bool engaged, bool supportOnly)
    {
        int nominal = s.NominalStrength(foe);
        if (nominal == 0) return 0;
        string terrain = s.Map.Terrain[battleTile].ToString().ToLowerInvariant();
        long total = 0;
        foreach (var kv in foe.Units)
        {
            var def = s.Catalog.Units[kv.Key];
            if (supportOnly && !def.Supports) continue;
            long t = (long)kv.Value * def.Strength * 1000;
            t = t * s.UnitStat(foe.Owner, "unit.attack", def, def.Attack.Permille) / 1000;
            if (def.TerrainAttackPermille.TryGetValue(terrain, out int tm)) t = t * tm / 1000;
            if (engaged) t = t * def.EngagedAttackPermille / 1000;
            t = t * ArmorMultiplier(s, victim, def.Attack.Type) / 1000;
            total += t;
        }
        return Rules.DamagePerStrPerTickMilli * foe.EffectiveStrength * total / ((long)nominal * 1000);
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
            if (!Fighting(s, v, o)) continue;
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
            if (s.Stacks.Any(o => s.AtWar(o.Owner, st.Owner) && (o.Tile == n || o.Target == n))) continue;
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

    /// <summary>A stack with a build under way stays put and keeps its identity, so it never merges.</summary>
    private static bool Building(GameState s, Stack st) => s.Constructions.Any(c => c.StackId == st.Id);

    private static void Merge(GameState s, List<SimEvent> events)
    {
        for (int i = 0; i < s.Stacks.Count; i++)
        {
            var a = s.Stacks[i];
            if (!a.Idle || Building(s, a)) continue;
            for (int j = i + 1; j < s.Stacks.Count; j++)
            {
                var b = s.Stacks[j];
                if (!b.Idle || b.Owner != a.Owner || b.Tile != a.Tile || Building(s, b)) continue;
                a.Hp += b.Hp;
                a.MaxHp += b.MaxHp;
                foreach (var u in b.Units) a.Units[u.Key] = a.Units.GetValueOrDefault(u.Key) + u.Value;
                a.Entrench = Math.Min(a.Entrench, b.Entrench);
                a.StillTicks = Math.Min(a.StillTicks, b.StillTicks);
                s.Stacks.RemoveAt(j);
                events.Add(new StacksMerged(a.Id, b.Id));
                j--;
            }
        }
    }

    /// <summary>Change a tile's owner. Structures are kept or destroyed by type; unfinished builds there are abandoned.</summary>
    private static void Flip(GameState s, int tile, int newOwner, List<SimEvent> events)
    {
        int prev = s.TileOwner[tile];
        s.TileOwner[tile] = newOwner;
        DiplomacySystem.OnCapture(s, newOwner, prev);
        events.Add(new TileCaptured(tile, newOwner, prev));
        s.Constructions.RemoveAll(c => c.Tile == tile && c.Owner != newOwner);
        var sd = BuiltStructure(s, tile);
        if (sd != null && sd.OnCapture == "destroy")
        {
            s.Structure[tile] = null;
            events.Add(new StructureLost(tile, sd.Id));
        }
    }

    // ---- capture --------------------------------------------------------------------------

    private static void Capture(GameState s, List<SimEvent> events)
    {
        foreach (var st in s.Stacks)
        {
            int holder = s.TileOwner[st.Tile];
            if (holder == st.Owner || st.Raider) continue;
            if (holder != -1 && !s.AtWar(st.Owner, holder)) continue;                          // treaty partners keep their land
            if (!st.Units.Keys.Any(k => s.Catalog.Units[k].CaptureMultiplier > 0)) continue;   // artillery and engineers cannot take ground
            if (s.Stacks.Any(o => s.AtWar(o.Owner, st.Owner) && o.Tile == st.Tile)) continue;
            Flip(s, st.Tile, st.Owner, events);
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
                if (pocket.Any(t => s.TileOwner[t] != -1 && !s.AtWar(p.Id, s.TileOwner[t]))) continue;   // a partner's land is not ours to swallow
                if (pocket.Any(t => s.Players.Any(o => o.Id != p.Id && o.Capital == t && !o.Minor))) continue;
                if (s.Stacks.Any(x => s.AtWar(x.Owner, p.Id) && pocket.Contains(x.Tile))) continue;

                pocket.Sort();
                foreach (int t in pocket) Flip(s, t, p.Id, events);
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

    private static bool IsSpawnSite(GameState s, Player p, int tile)
    {
        if (s.TileOwner[tile] != p.Id) return false;
        if (tile == p.Capital || s.Map.Deposit[tile]) return true;
        return BuiltStructure(s, tile)?.SpawnSite == true;
    }

    /// <summary>
    /// New stacks appear only at your capital, on a deposit ("mine") you own, or on a depot, and never on a
    /// tile an enemy stack is standing on. Of the open sites the one holding the fewest of your stacks wins
    /// (capital first on ties), so recruits spread out. -1 when no site is open.
    /// </summary>
    private static int SpawnTile(GameState s, Player p)
    {
        int best = -1;
        int bestCount = int.MaxValue;
        for (int t = -1; t < s.Map.TileCount; t++)
        {
            int tile = t == -1 ? p.Capital : t;
            if (t != -1 && tile == p.Capital) continue;
            if (!IsSpawnSite(s, p, tile)) continue;
            if (s.Stacks.Any(x => s.AtWar(x.Owner, p.Id) && x.Tile == tile)) continue;
            int count = s.Stacks.Count(x => x.Owner == p.Id && x.Tile == tile);
            if (count < bestCount) { bestCount = count; best = tile; }
        }
        return best;
    }

    /// <summary>
    /// Tiles connected to the capital through owned ground, plus the area around each owned depot
    /// (within its supply radius over owned ground). Unconnected tiles yield half.
    /// </summary>
    public static bool[] SupplyLinked(GameState s, Player p)
    {
        var linked = ConnectedToCapital(s, p);
        for (int d = 0; d < s.Map.TileCount; d++)
        {
            if (s.TileOwner[d] != p.Id) continue;
            var sd = BuiltStructure(s, d);
            if (sd == null || sd.SupplyRadius <= 0) continue;
            var dist = new Dictionary<int, int> { [d] = 0 };
            var q = new Queue<int>();
            q.Enqueue(d);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                linked[c] = true;
                if (dist[c] >= sd.SupplyRadius) continue;
                foreach (int n in s.Map.Neighbors[c])
                    if (s.TileOwner[n] == p.Id && dist.TryAdd(n, dist[c] + 1)) q.Enqueue(n);
            }
        }
        return linked;
    }

    /// <summary>Energy per tick the player's living units cost; wounded stacks cost proportionally less.</summary>
    public static long Upkeep(GameState s, Player p)
    {
        long total = 0;
        foreach (var st in s.Stacks)
        {
            if (st.Owner != p.Id || st.MaxHp == 0) continue;
            foreach (var kv in st.Units)
            {
                long alive = ((long)kv.Value * st.Hp + st.MaxHp - 1) / st.MaxHp;
                total += alive * s.Stat(p.Id, "upkeep.energy", s.Catalog.Units[kv.Key].UpkeepMilli);
            }
        }
        return total;
    }

    /// <summary>Per-tick alloy, energy and influence a player earns right now (before starvation); also what the AI plans with.</summary>
    public static (long Alloy, long Energy, long Influence) Income(GameState s, Player p)
    {
        var linked = SupplyLinked(s, p);
        long alloy = 0, energy = 0, influence = 0;
        for (int t = 0; t < s.TileOwner.Length; t++)
        {
            if (s.TileOwner[t] != p.Id) continue;
            var td = s.Catalog.Terrain[s.Map.Terrain[t]];
            long a = s.Stat(p.Id, Rules.StatYield, Rules.TileYieldMilli * td.AlloyPermille / 1000);
            long e = td.EnergyMilli > 0 ? s.Stat(p.Id, "yield.energy", td.EnergyMilli) : 0;
            long i = 0;
            int boost = BuiltStructure(s, t)?.DepositYieldPermille ?? 1000;
            switch (s.Map.DepositKinds[t])
            {
                case DepositKind.Alloy: a = a * Rules.DepositMultiplier * boost / 1000; break;
                case DepositKind.Energy: e += s.Stat(p.Id, "yield.energy", Rules.EnergyDepositMilli) * boost / 1000; break;
                case DepositKind.Relic: i += s.Stat(p.Id, "yield.influence", Rules.RelicInfluenceMilli) * boost / 1000; break;
            }
            if (!linked[t])
            {
                a = a * Rules.UnlinkedPermille / 1000;
                e = e * Rules.UnlinkedPermille / 1000;
                i = i * Rules.UnlinkedPermille / 1000;
            }
            alloy += a; energy += e; influence += i;
        }
        if (s.TileOwner[p.Capital] == p.Id) energy += s.Stat(p.Id, "yield.energy", Rules.CapitalEnergyMilli);
        alloy += alloy * DiplomacySystem.TradeBonusPermille(s, p.Id) / 1000;
        return (alloy, energy, influence);
    }

    private static void Economy(GameState s, List<SimEvent> events)
    {
        foreach (var p in s.Majors)
        {
            var (alloy, energy, influence) = Income(s, p);

            p.Energy += energy - Upkeep(s, p);
            p.Starved = p.Energy < 0;
            if (p.Starved) p.Energy = 0;
            if (p.Starved) alloy = alloy * Rules.StarvedAlloyPermille / 1000;
            p.Supply += alloy;
            p.Influence += influence;

            if (!p.AutoRecruit || p.Starved) continue;
            long cost = s.Stat(p.Id, Rules.StatStackCost, Rules.StackCostMilli);
            if (p.Supply < cost || s.Stacks.Count(x => x.Owner == p.Id) >= p.MaxStacks) continue;
            int at = SpawnTile(s, p);
            if (at < 0) continue;
            p.Supply -= cost;
            var ns = s.AddStack(p.Id, at, Rules.NewStackStrength);
            events.Add(new StackSpawned(ns.Id, p.Id, at));
        }
    }

    // ---- recruiting and building ----------------------------------------------------------

    public static long UnitCost(GameState s, int player, UnitDef def, long baseCost)
    {
        long v = s.Stat(player, "build.cost.all", baseCost);
        var seen = new HashSet<string> { def.Id };
        v = s.Stat(player, "build.cost." + def.Id, v);
        foreach (var r in def.Roles) if (seen.Add(r)) v = s.Stat(player, "build.cost." + r, v);
        return v;
    }

    private static bool UnitUnlocked(GameState s, int player, UnitDef def) =>
        def.Unlocked || s.Stat(player, "unlock.unit." + def.Id, 0) > 0;

    private static bool StructureUnlocked(GameState s, int player, StructureDef def) =>
        def.Unlocked || s.Stat(player, "unlock.structure." + def.Id, 0) > 0;

    private static void Recruit(GameState s, RecruitCommand rc, List<SimEvent> events)
    {
        if (rc.Player < 0 || rc.Player >= s.Players.Length) return;
        var p = s.Players[rc.Player];
        if (p.Minor || p.Starved) return;
        if (!s.Catalog.Units.TryGetValue(rc.UnitId, out var def) || !UnitUnlocked(s, p.Id, def)) return;
        if (s.Stacks.Count(x => x.Owner == p.Id) >= p.MaxStacks) return;
        int at = rc.Tile == -1 ? SpawnTile(s, p) : rc.Tile;
        if (at < 0 || at >= s.Map.TileCount || !IsSpawnSite(s, p, at)) return;
        if (s.Stacks.Any(x => s.AtWar(x.Owner, p.Id) && x.Tile == at)) return;
        long alloy = UnitCost(s, p.Id, def, def.Cost.Alloy);
        long energy = def.Cost.Energy;
        if (p.Supply < alloy || p.Energy < energy) return;
        p.Supply -= alloy;
        p.Energy -= energy;
        var ns = s.AddStack(p.Id, at, new Dictionary<string, int> { [def.Id] = 1 });
        events.Add(new StackSpawned(ns.Id, p.Id, at));
    }

    private static void StartBuild(GameState s, BuildCommand bc, List<SimEvent> events)
    {
        var st = s.FindStack(bc.StackId);
        if (st == null || st.Owner != bc.Player || !st.Idle) return;
        var p = s.Players[bc.Player];
        if (p.Minor || !st.Units.Keys.Any(k => s.Catalog.Units[k].Builds)) return;
        if (!s.Catalog.Structures.TryGetValue(bc.StructureId, out var def) || !StructureUnlocked(s, p.Id, def)) return;
        int tile = st.Tile;
        if (s.TileOwner[tile] != p.Id || s.Structure[tile] != null || s.Constructions.Any(c => c.Tile == tile)) return;
        if (def.RequiresDeposit && !s.Map.Deposit[tile]) return;
        long alloy = s.Stat(p.Id, "build.cost." + def.Id, s.Stat(p.Id, "build.cost.all", def.Cost.Alloy));
        if (p.Supply < alloy || p.Energy < def.Cost.Energy) return;
        p.Supply -= alloy;
        p.Energy -= def.Cost.Energy;
        long ticks = s.Stat(p.Id, "build.time." + def.Id, s.Stat(p.Id, "build.time.all", def.BuildTicks));
        s.Constructions.Add(new Construction { Tile = tile, Owner = p.Id, StackId = st.Id, Structure = def.Id, TicksLeft = (int)Math.Max(1, ticks) });
        events.Add(new BuildStarted(tile, p.Id, def.Id));
    }

    /// <summary>Builds advance only while the engineer's stack stands still on the tile and is not in a fight.</summary>
    private static void Construct(GameState s, List<SimEvent> events)
    {
        for (int i = 0; i < s.Constructions.Count; i++)
        {
            var c = s.Constructions[i];
            var st = s.FindStack(c.StackId);
            if (st == null || s.TileOwner[c.Tile] != c.Owner)
            {
                s.Constructions.RemoveAt(i--);
                continue;
            }
            if (st.Tile != c.Tile || !st.Idle || Engaged(s, st)) continue;
            if (--c.TicksLeft > 0) continue;
            s.Structure[c.Tile] = c.Structure;
            s.Constructions.RemoveAt(i--);
            events.Add(new StructureBuilt(c.Tile, c.Owner, c.Structure));
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
