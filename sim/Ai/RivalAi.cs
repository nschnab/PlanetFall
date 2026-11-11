using Planetfall.Sim.Core;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Ai;

public enum AiGoal { Expand, Develop, Defend, Attack }

/// <summary>What the operational layer told a stack to do; the tactical layer carries it out.</summary>
public enum AiRole { Muster, Garrison, Hunt, Intercept, Regroup, Build }

public sealed record AiOrder(AiRole Role, int Tile = -1, string? Structure = null);

/// <summary>Read-only view of the AI's thinking for the debug overlay and the balance runner.</summary>
public sealed class AiDebug
{
    public AiGoal Goal { get; internal set; }
    public int[] Scores { get; internal set; } = new int[4];
    public int Hopelessness { get; internal set; }
    public string HopelessReason { get; internal set; } = "";
    public int SurrenderInTicks { get; internal set; } = -1;
    public int Enemy { get; internal set; } = -1;
}

/// <summary>
/// The rival commander, in three layers that only read the state and return Commands:
/// <list type="bullet">
/// <item><b>Strategic</b> (every 5 s, one rival per tick): utility scores for Expand / Develop / Defend / Attack,
/// scaled by the personality, with +0.1 hysteresis on the current goal.</item>
/// <item><b>Operational</b> (every 2 s): recruits a mixed army, plans builds for its engineers, and gives every
/// stack an <see cref="AiOrder"/> (muster, garrison, hunt, intercept, regroup, build).</item>
/// <item><b>Tactical</b> (every 1 s per stack): a selector of retreat-if-losing, join-nearby-battle, execute-order.</item>
/// </list>
/// It also scores <see cref="Hopelessness"/> and offers to surrender once that stays high for 60 s.
/// No hidden bonuses: it issues the same Commands a player does.
/// </summary>
public sealed class RivalAi
{
    public const int StrategicTicks = 50;
    public const int OperationalTicks = 20;
    public const int TacticalTicks = 10;
    public const int Hysteresis = 100;
    public const int MinSurrenderTick = 6000;       // never before minute 10
    public const int SurrenderPersistTicks = 600;
    private const int BuildGiveUpTicks = 2400;           // 4 min to reach a site and afford the structure   // 60 s above the personality's threshold

    private readonly int _me;
    private readonly Dictionary<int, AiOrder> _orders = new();
    private readonly Dictionary<int, int> _buildSince = new();   // stack id -> tick its current build order was given
    private readonly Dictionary<int, int> _avoid = new();        // tile -> tick until which builds there are not attempted
    private int _tickNow;
    private int _enemy = -1;
    private int _hopelessSince = -1;
    private bool _surrendered;

    public Personality Personality { get; }
    public AiDebug Debug { get; } = new();
    public AiGoal Goal => Debug.Goal;

    public RivalAi(int player, Personality? personality = null)
    {
        _me = player;
        Personality = personality ?? Personality.All[0];
        Debug.Goal = AiGoal.Expand;
    }

    public AiOrder? OrderOf(int stackId) => _orders.GetValueOrDefault(stackId);

    // ---- entry point ----------------------------------------------------------------------

    public IReadOnlyList<Command> Think(GameState s)
    {
        var cmds = new List<Command>();
        if (s.Over || _surrendered) return cmds;
        _tickNow = s.Tick;
        _enemy = PickEnemy(s);
        Debug.Enemy = _enemy;
        if (_enemy == -1) return cmds;

        if ((s.Tick + _me * 17) % StrategicTicks == 0) Strategize(s);
        if ((s.Tick + _me * 7) % OperationalTicks == 0) Operate(s, cmds);
        Tactics(s, cmds);
        if ((s.Tick + _me) % TacticalTicks == 0) CheckSurrender(s, cmds);
        return cmds;
    }

    // ---- helpers --------------------------------------------------------------------------

    private static int[] Bfs(GameState s, IEnumerable<int> sources, int maxDepth = int.MaxValue)
    {
        var dist = new int[s.Map.TileCount];
        Array.Fill(dist, -1);
        var q = new Queue<int>();
        foreach (int t in sources) { dist[t] = 0; q.Enqueue(t); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            if (dist[c] >= maxDepth) continue;
            foreach (int n in s.Map.Neighbors[c])
            {
                if (dist[n] != -1) continue;
                dist[n] = dist[c] + 1;
                q.Enqueue(n);
            }
        }
        return dist;
    }

    private static bool IsBuilderOnly(GameState s, Stack st) => st.Units.Keys.All(k => s.Catalog.Units[k].Builds);
    private static bool HasBuilder(GameState s, Stack st) => st.Units.Keys.Any(k => s.Catalog.Units[k].Builds);

    private List<Stack> Mine(GameState s) => s.Stacks.Where(x => x.Owner == _me).OrderBy(x => x.Id).ToList();

    private static int PowerOf(IEnumerable<Stack> stacks) => stacks.Sum(x => x.EffectiveStrength);

    private int PickEnemy(GameState s)
    {
        var others = s.Majors.Where(p => p.Id != _me).ToList();
        if (others.Count == 0) return -1;
        if (Personality.Id == "zealot" && _enemy != -1 && others.Any(p => p.Id == _enemy)) return _enemy;   // a permanent enemy
        if (Personality.Id == "opportunist")
            return others.OrderBy(p => PowerOf(s.Stacks.Where(x => x.Owner == p.Id))).ThenBy(p => p.Id).First().Id;
        var dist = Bfs(s, new[] { s.Players[_me].Capital });
        return others.OrderBy(p => dist[p.Capital] < 0 ? int.MaxValue : dist[p.Capital]).ThenBy(p => p.Id).First().Id;
    }

    private bool IsSpawn(GameState s, int tile) =>
        s.TileOwner[tile] == _me && (tile == s.Players[_me].Capital || s.Map.Deposit[tile] || s.Structure[tile] != null && s.Catalog.Structures[s.Structure[tile]!].SpawnSite);

    // ---- strategic ------------------------------------------------------------------------

    private void Strategize(GameState s)
    {
        var me = s.Players[_me];
        var mine = Mine(s);
        var foes = s.Stacks.Where(x => x.Owner == _enemy).ToList();
        int myPower = PowerOf(mine.Where(x => !IsBuilderOnly(s, x)));
        int foePower = PowerOf(foes);

        var owned = Enumerable.Range(0, s.Map.TileCount).Where(t => s.TileOwner[t] == _me).ToList();
        var near = Bfs(s, owned, 2);
        int frontier = Enumerable.Range(0, s.Map.TileCount).Count(t => near[t] > 0 && s.TileOwner[t] == -1);

        int bareMines = owned.Count(t => s.Map.Deposit[t] && s.Structure[t] == null);
        int depots = owned.Count(t => s.Structure[t] == "depot");
        int wantDepots = owned.Count >= 8 ? 1 + owned.Count / 14 : 0;
        int develop = Math.Min(1000, bareMines * 300 + Math.Max(0, wantDepots - depots) * 250);
        if (me.Supply < 15000) develop /= 2;

        var capDist = Bfs(s, new[] { me.Capital }, 4);
        int threat = PowerOf(foes.Where(x => capDist[x.Tile] >= 0 || s.TileOwner[x.Tile] == _me));
        int defend = threat == 0 ? 0 : Math.Min(1000, 400 + threat * 600 / (myPower + threat + 1));

        int ratio = (int)Math.Min(4000, myPower * 1000L / (foePower + 1));
        int attack = Math.Clamp((ratio - 500) * 2 / 3, 0, 1000);
        if (frontier == 0) attack = Math.Min(1000, attack + 300);

        var p = Personality;
        var scores = new[]
        {
            Math.Min(1000, frontier * 70) * p.Expand / 1000,
            develop * p.Develop / 1000,
            defend * p.Defend / 1000,
            attack * p.Attack / 1000,
        };
        scores[(int)Debug.Goal] += Hysteresis;
        int best = 0;
        for (int i = 1; i < scores.Length; i++) if (scores[i] > scores[best]) best = i;
        Debug.Goal = (AiGoal)best;
        Debug.Scores = scores;
    }

    // ---- hopelessness and surrender -------------------------------------------------------

    private static int PainFromShare(long own, long foe)
    {
        if (own + foe == 0) return 0;
        long share = own * 1000 / (own + foe);
        return (int)Math.Clamp((450 - share) * 1000 / 450, 0, 1000);
    }

    /// <summary>
    /// 0..1000: how little this nation can still hope for. Weighs territory, income, army strength and
    /// above all the state of its capital, so a nation with a live capital and an army stays well below
    /// every personality's threshold.
    /// </summary>
    public int Hopelessness(GameState s, out string reason)
    {
        reason = "";
        int foe = PickStrongest(s);
        if (foe == -1) return 0;
        var me = s.Players[_me];
        int tiles = PainFromShare(s.OwnedTiles(_me), s.OwnedTiles(foe));
        int income = PainFromShare(Simulation.Income(s, me).Alloy, Simulation.Income(s, s.Players[foe]).Alloy);
        int army = PainFromShare(PowerOf(s.Stacks.Where(x => x.Owner == _me)), PowerOf(s.Stacks.Where(x => x.Owner == foe)));

        int cap = me.Capital;
        bool defended = s.Stacks.Any(x => x.Owner == _me && x.Tile == cap);
        bool besieged = s.Stacks.Any(x => x.Owner != _me && !s.Players[x.Owner].Minor
            && (x.Tile == cap || x.Target == cap || s.Map.Neighbors[cap].Contains(x.Tile)));
        int capital = s.TileOwner[cap] != _me ? 1000 : besieged ? (defended ? 600 : 1000) : defended ? 0 : 150;

        int score = (tiles * 3 + income * 2 + army * 2 + capital * 3) / 10;
        reason = capital >= 600 ? "Our capital is falling"
            : army >= 700 ? "We have no army left to fight with"
            : tiles >= 700 ? "We have lost most of our land"
            : "Our capital still stands";
        return score;
    }

    private int PickStrongest(GameState s)
    {
        var others = s.Majors.Where(p => p.Id != _me).ToList();
        if (others.Count == 0) return -1;
        return others.OrderByDescending(p => s.OwnedTiles(p.Id) + PowerOf(s.Stacks.Where(x => x.Owner == p.Id))).ThenBy(p => p.Id).First().Id;
    }

    private void CheckSurrender(GameState s, List<Command> cmds)
    {
        int h = Hopelessness(s, out string why);
        Debug.Hopelessness = h;
        Debug.HopelessReason = why;
        if (h < Personality.PainThreshold)
        {
            _hopelessSince = -1;
            Debug.SurrenderInTicks = -1;
            return;
        }
        if (_hopelessSince == -1) _hopelessSince = s.Tick;
        int left = Math.Max(_hopelessSince + SurrenderPersistTicks - s.Tick, MinSurrenderTick - s.Tick);
        Debug.SurrenderInTicks = Math.Max(0, left);
        if (left > 0) return;
        _surrendered = true;
        cmds.Add(new SurrenderCommand(_me));
    }

    // ---- operational ----------------------------------------------------------------------

    private void Operate(GameState s, List<Command> cmds)
    {
        var mine = Mine(s);
        foreach (int id in _orders.Keys.Where(id => mine.All(x => x.Id != id)).ToList()) _orders.Remove(id);

        Recruit(s, cmds, mine);
        PlanBuilds(s, mine);
        AssignArmies(s, mine);
    }

    private static readonly (string Role, int Share)[] BaseMix = { ("infantry", 400), ("armor", 300), ("artillery", 200), ("recon", 100) };

    private string? RoleToBuy(GameState s, List<Stack> mine, long alloy)
    {
        var have = new Dictionary<string, int>();
        foreach (var st in mine)
            foreach (var kv in st.Units)
            {
                var def = s.Catalog.Units[kv.Key];
                foreach (var r in def.Roles) have[r] = have.GetValueOrDefault(r) + kv.Value * def.Strength;
            }
        int total = have.Where(kv => kv.Key != "engineer").Sum(kv => kv.Value);

        // Counter what the enemy fields: guns and armour against armour, armour against infantry.
        var foeHave = new Dictionary<string, int>();
        foreach (var st in s.Stacks.Where(x => x.Owner == _enemy))
            foreach (var kv in st.Units)
            {
                var def = s.Catalog.Units[kv.Key];
                foreach (var r in def.Roles) foeHave[r] = foeHave.GetValueOrDefault(r) + kv.Value * def.Strength;
            }
        int foeTotal = Math.Max(1, foeHave.Where(kv => kv.Key != "engineer").Sum(kv => kv.Value));
        int foeArmor = foeHave.GetValueOrDefault("armor") * 1000 / foeTotal;
        int foeInf = foeHave.GetValueOrDefault("infantry") * 1000 / foeTotal;

        string? best = null;
        int bestGap = int.MinValue;
        foreach (var (role, baseShare) in BaseMix)
        {
            int share = baseShare;
            if (role == "artillery") share += foeArmor / 5;
            if (role == "armor") share += foeInf / 6;
            if (role == "infantry") share -= foeArmor / 5;
            var def = s.Catalog.Units.Values.Where(u => u.Roles.Contains(role) && (u.Unlocked)).OrderBy(u => u.Id, StringComparer.Ordinal).FirstOrDefault();
            if (def == null || Simulation.UnitCost(s, _me, def, def.Cost.Alloy) > alloy) continue;
            int gap = share - (total == 0 ? 0 : have.GetValueOrDefault(role) * 1000 / total);
            if (gap > bestGap) { bestGap = gap; best = def.Id; }
        }
        return best;
    }

    private void Recruit(GameState s, List<Command> cmds, List<Stack> mine)
    {
        var me = s.Players[_me];
        if (me.Starved) return;
        int count = mine.Count;
        long alloy = me.Supply;
        var income = Simulation.Income(s, me);
        long net = income.Energy - Simulation.Upkeep(s, me);
        int cap = me.Capital;
        bool capOpen = !s.Stacks.Any(x => x.Owner != _me && x.Tile == cap);

        // An engineer joins a bare mine it can upgrade (never where an army idles, or they would merge).
        int engineers = mine.Count(x => HasBuilder(s, x));
        if (engineers < Personality.Engineers && count < me.MaxStacks && s.Catalog.Units.TryGetValue("engineer", out var eng) && eng.Unlocked)
        {
            long cost = Simulation.UnitCost(s, _me, eng, eng.Cost.Alloy);
            int site = Enumerable.Range(0, s.Map.TileCount).FirstOrDefault(t =>
                t != cap && s.TileOwner[t] == _me && s.Map.Deposit[t] && s.Structure[t] == null
                && !s.Constructions.Any(c => c.Tile == t) && !s.Stacks.Any(x => x.Tile == t || x.Target == t), -1);
            if (site >= 0 && alloy >= cost + 15000 && net - eng.UpkeepMilli >= -me.Energy / 3000)
            {
                cmds.Add(new RecruitCommand(_me, eng.Id, site));
                alloy -= cost;
                net -= eng.UpkeepMilli;
                count++;
            }
        }

        for (int n = 0; n < 3 && count < me.MaxStacks && capOpen; n++)
        {
            string? unit = RoleToBuy(s, mine, alloy);
            if (unit == null) break;
            var def = s.Catalog.Units[unit];
            long cost = Simulation.UnitCost(s, _me, def, def.Cost.Alloy);
            if (net - def.UpkeepMilli < -me.Energy / 3000) break;        // would starve
            if (alloy - cost < ReserveFor(s)) break;
            cmds.Add(new RecruitCommand(_me, unit, cap));
            alloy -= cost;
            net -= def.UpkeepMilli;
            count++;
        }
    }

    /// <summary>Alloy held back for structures an engineer is waiting to start, so armies cannot starve the builders.</summary>
    private long ReserveFor(GameState s)
    {
        long total = 0;
        foreach (var (id, o) in _orders)
            if (o.Role == AiRole.Build && o.Structure != null && !s.Constructions.Any(c => c.StackId == id))
                total += s.Catalog.Structures[o.Structure].Cost.Alloy;
        return total;
    }

    private void PlanBuilds(GameState s, List<Stack> mine)
    {
        var me = s.Players[_me];
        var taken = new HashSet<int>(_orders.Values.Where(o => o.Role == AiRole.Build).Select(o => o.Tile));
        var fromCapital = Bfs(s, new[] { me.Capital });
        int owned = s.OwnedTiles(_me);

        foreach (var st in mine.Where(x => HasBuilder(s, x)))
        {
            if (s.Constructions.Any(c => c.StackId == st.Id)) { _buildSince[st.Id] = s.Tick; continue; }
            if (_orders.TryGetValue(st.Id, out var cur) && cur.Role == AiRole.Build && StillBuildable(s, cur))
            {
                if (s.Tick - _buildSince.GetValueOrDefault(st.Id, s.Tick) < BuildGiveUpTicks) continue;
                _avoid[cur.Tile] = s.Tick + 3000;       // it never got going (cost, route or enemy): try elsewhere
            }
            _orders.Remove(st.Id);
            _buildSince.Remove(st.Id);
            if (!st.Idle) continue;
            var dist = Bfs(s, new[] { st.Tile });

            // 1. extractor on the nearest bare mine
            int mine1 = Pick(s, t => s.Map.Deposit[t] && s.Structure[t] == null, dist, taken);
            if (mine1 >= 0) { Give(st, mine1, "extractor", taken); continue; }

            // 2. depots spread away from the capital
            int depots = Enumerable.Range(0, s.Map.TileCount).Count(t => s.Structure[t] == "depot") + _orders.Values.Count(o => o.Structure == "depot");
            int want = owned >= 8 ? 1 + owned / 14 + (Debug.Goal == AiGoal.Develop ? 1 : 0) : 0;
            if (depots < want)
            {
                var depotTiles = Enumerable.Range(0, s.Map.TileCount).Where(t => s.Structure[t] == "depot").ToList();
                var nearDepot = depotTiles.Count == 0 ? null : Bfs(s, depotTiles, 3);
                int d = Pick(s, t => fromCapital[t] >= 3 && (nearDepot == null || nearDepot[t] < 0), dist, taken);
                if (d >= 0) { Give(st, d, "depot", taken); continue; }
            }

            // 3. fortress on the front when under pressure
            if (Debug.Goal == AiGoal.Defend && !_orders.Values.Any(o => o.Structure == "fortress"))
            {
                int f = Pick(s, t => s.Map.Neighbors[t].Any(n => s.TileOwner[n] == _enemy) && s.Structure[t] == null, dist, taken);
                if (f >= 0) { Give(st, f, "fortress", taken); continue; }
            }

            _orders[st.Id] = new AiOrder(AiRole.Garrison, st.Tile);   // no job right now: wait here
        }
    }

    private int Pick(GameState s, Func<int, bool> ok, int[] dist, HashSet<int> taken)
    {
        int best = -1;
        for (int t = 0; t < s.Map.TileCount; t++)
        {
            if (dist[t] < 0 || s.TileOwner[t] != _me || taken.Contains(t) || s.Structure[t] != null) continue;
            if (_avoid.TryGetValue(t, out int until) && s.Tick < until) continue;
            if (s.Constructions.Any(c => c.Tile == t) || !ok(t)) continue;
            if (best == -1 || dist[t] < dist[best]) best = t;
        }
        return best;
    }

    private void Give(Stack st, int tile, string structure, HashSet<int> taken)
    {
        _orders[st.Id] = new AiOrder(AiRole.Build, tile, structure);
        _buildSince[st.Id] = _tickNow;
        taken.Add(tile);
    }

    private bool StillBuildable(GameState s, AiOrder o) =>
        s.TileOwner[o.Tile] == _me && s.Structure[o.Tile] == null && !s.Constructions.Any(c => c.Tile == o.Tile)
        && !s.Stacks.Any(x => x.Owner != _me && x.Tile == o.Tile);

    private void AssignArmies(GameState s, List<Stack> mine)
    {
        var me = s.Players[_me];
        int cap = me.Capital;
        var foes = s.Stacks.Where(x => x.Owner == _enemy).ToList();
        var capDist = Bfs(s, new[] { cap }, 5);
        var threats = foes.Where(x => capDist[x.Tile] >= 0 || s.TileOwner[x.Tile] == _me).OrderBy(x => capDist[x.Tile] < 0 ? 99 : capDist[x.Tile]).ThenBy(x => x.Id).ToList();
        bool pressed = threats.Count > 0 || Debug.Goal == AiGoal.Defend;
        int garrisonNeed = pressed ? Math.Max(3, Personality.Muster / 2) : 0;
        int garrison = 0;
        var claimed = new HashSet<int>();
        foreach (var st in mine)
        {
            int dest = st.Path.Count > 0 ? st.Path[^1] : st.Target;
            if (dest != -1 && !HasBuilder(s, st)) claimed.Add(dest);
        }

        foreach (var st in mine.Where(x => !IsBuilderOnly(s, x)))
        {
            bool fresh = st.Idle || !_orders.TryGetValue(st.Id, out var existing) || existing.Role is AiRole.Muster or AiRole.Garrison or AiRole.Regroup;
            if (!fresh) continue;
            int power = st.EffectiveStrength;

            if (st.Hp * 2 < st.MaxHp)
            {
                _orders[st.Id] = new AiOrder(st.Tile == cap ? AiRole.Garrison : AiRole.Regroup, cap);   // wounded stacks guard home until recruits arrive
                continue;
            }
            if (st.Tile == cap && garrison < garrisonNeed)
            {
                garrison += power;
                _orders[st.Id] = new AiOrder(AiRole.Garrison, cap);
                continue;
            }
            if (Debug.Goal == AiGoal.Defend && threats.Count > 0)
            {
                var th = threats[0];
                int local = PowerOf(threats.Where(x => capDist[x.Tile] >= 0 && capDist[th.Tile] >= 0 && Math.Abs(capDist[x.Tile] - capDist[th.Tile]) <= 1));
                if (power * 1000L >= local * 600L) { _orders[st.Id] = new AiOrder(AiRole.Intercept, th.Tile); continue; }
                _orders[st.Id] = new AiOrder(AiRole.Muster, cap);
                continue;
            }
            int muster = Debug.Goal == AiGoal.Expand ? Math.Max(2, Personality.Muster / 2) : Personality.Muster;
            if (IsSpawn(s, st.Tile) && s.NominalStrength(st) < muster)
            {
                _orders[st.Id] = new AiOrder(AiRole.Muster, st.Tile);
                continue;
            }
            int target = ChooseTarget(s, st, claimed);
            if (target == -1)
            {
                // Nothing this stack can take alone: gather at the staging tile so the army fights as one.
                int rally = StagingTile(s);
                if (rally == -1 || IsBuilderOnly(s, st)) _orders.Remove(st.Id);
                else _orders[st.Id] = st.Tile == rally ? new AiOrder(AiRole.Muster, rally) : new AiOrder(AiRole.Regroup, rally);
                continue;
            }
            claimed.Add(target);
            _orders[st.Id] = new AiOrder(AiRole.Hunt, target);
        }
    }

    /// <summary>An owned tile as close to the enemy capital as possible: where idle armies pool before an assault.</summary>
    private int StagingTile(GameState s)
    {
        if (_enemy == -1) return -1;
        var d = Bfs(s, new[] { s.Players[_enemy].Capital });
        var mine = Bfs(s, new[] { s.Players[_me].Capital });
        int best = -1;
        for (int t = 0; t < s.Map.TileCount; t++)
        {
            if (s.TileOwner[t] != _me || d[t] < 0) continue;
            if (s.Stacks.Any(x => x.Owner != _me && x.Tile == t)) continue;
            if (best == -1 || d[t] < d[best] || d[t] == d[best] && mine[t] < mine[best]) best = t;
        }
        return best;
    }

    private int ChooseTarget(GameState s, Stack st, HashSet<int> claimed)
    {
        var dist = Bfs(s, new[] { st.Tile });
        bool attack = Debug.Goal == AiGoal.Attack;
        var me = s.Players[_me];
        bool energyShort = me.Starved || Simulation.Income(s, me).Energy < Simulation.Upkeep(s, me);
        var towardFoe = Bfs(s, new[] { s.Players[_enemy].Capital });   // equal-cost ties go to the tile nearer the enemy, not the lower tile index
        int best = -1;
        int bestCost = int.MaxValue;
        for (int t = 0; t < s.Map.TileCount; t++)
        {
            if (dist[t] <= 0 || s.TileOwner[t] == _me || claimed.Contains(t)) continue;
            if (Defenders(s, t) * 1000L > st.EffectiveStrength * (long)Personality.RiskPermille) continue;

            int owner = s.TileOwner[t];
            int value = owner == -1 ? (attack ? 2 : 5) : (attack ? 5 : 2);
            value += s.Map.DepositKinds[t] switch
            {
                MapGen.DepositKind.Energy => energyShort ? 6 : 2,
                MapGen.DepositKind.Alloy => 3,
                MapGen.DepositKind.Relic => 1,
                _ => 0,
            };
            if (owner != -1 && !s.Players[owner].Minor && s.Players[owner].Capital == t) value += attack ? 10 : 4;
            if (owner != -1 && s.Players[owner].Minor) value -= 1;

            int cost = dist[t] * 100 - value * 40 + Math.Max(0, towardFoe[t]);
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

    // ---- tactical -------------------------------------------------------------------------

    private void Tactics(GameState s, List<Command> cmds)
    {
        int cap = s.Players[_me].Capital;
        foreach (var st in Mine(s))
        {
            if ((s.Tick + st.Id + _me) % TacticalTicks != 0) continue;
            _orders.TryGetValue(st.Id, out var order);
            bool engaged = Simulation.Engaged(s, st);

            // 1. Retreat-if-losing: outnumbered two to one away from home.
            if (engaged && st.Tile != cap && !st.Retreating)
            {
                int foe = s.Stacks.Where(o => Simulation.Fighting(st, o)).Sum(o => o.EffectiveStrength);
                if (foe * 1000L > st.EffectiveStrength * 2000L)
                {
                    cmds.Add(new MoveCommand(_me, st.Id, cap));
                    continue;
                }
            }

            // 2. Join-nearby-battle: an idle field army helps a friend that is under attack two hops away.
            if (st.Idle && !engaged && order is { Role: AiRole.Hunt or AiRole.Intercept } or null && !HasBuilder(s, st) && st.Hp * 100 >= st.MaxHp * 60)
            {
                int ally = FriendInBattle(s, st);
                if (ally != -1)
                {
                    cmds.Add(new MoveCommand(_me, st.Id, ally));
                    continue;
                }
            }

            // 3. Execute the order from the operational layer.
            if (order == null) continue;
            switch (order.Role)
            {
                case AiRole.Build:
                    if (st.Tile == order.Tile)
                    {
                        if (st.Idle && !engaged && !s.Constructions.Any(c => c.StackId == st.Id))
                            cmds.Add(new BuildCommand(_me, st.Id, order.Structure!));
                    }
                    else if (st.Idle && !engaged) cmds.Add(new MoveCommand(_me, st.Id, order.Tile));
                    break;
                case AiRole.Hunt:
                case AiRole.Intercept:
                case AiRole.Regroup:
                    if (st.Idle && st.Tile != order.Tile) cmds.Add(new MoveCommand(_me, st.Id, order.Tile));
                    else if (st.Idle) _orders.Remove(st.Id);       // arrived; the operational layer picks the next job
                    break;
                // 4. Muster / Garrison: Idle-fortify. The stack stands still and digs in.
            }
        }
    }

    private int FriendInBattle(GameState s, Stack st)
    {
        var near = Bfs(s, new[] { st.Tile }, 2);
        foreach (var f in s.Stacks.Where(x => x.Owner == _me && x.Id != st.Id).OrderBy(x => x.Id))
        {
            if (near[f.Tile] <= 0 || HasBuilder(s, f) || !Simulation.Engaged(s, f)) continue;
            if (_orders.TryGetValue(f.Id, out var o) && o.Role is AiRole.Garrison) continue;
            return f.Tile;
        }
        return -1;
    }
}
