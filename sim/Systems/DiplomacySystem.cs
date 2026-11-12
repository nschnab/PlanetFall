using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Systems;

/// <summary>One line of an opinion or acceptance breakdown, so the UI can always say why.</summary>
public sealed record OpinionLine(string Label, int Points);

/// <summary>An answer to a proposal or demand: yes or no, in words, with the numbers behind it.</summary>
public sealed record Verdict(bool Accepted, string Reason, int Score = 0, int Need = 0, IReadOnlyList<OpinionLine>? Lines = null);

/// <summary>
/// Treaties, opinion, minor-power behaviour and integration. Everything is a pure function of the
/// <see cref="GameState"/> and the Commands, so it replays exactly. Rival and minor answers are utility
/// scores that carry a reason string.
/// </summary>
public static class DiplomacySystem
{
    // Opinion needed before a nation will agree to move up to each treaty (index = Treaty).
    public static readonly int[] TreatyNeed = { 0, 0, 15, 30, 50 };
    // Influence (thousandths) the proposer pays when the treaty is signed.
    public static readonly long[] TreatyInfluence = { 0, 0, 5_000, 10_000, 20_000 };

    public const int IntegrateOpinion = 75;
    public const int IntegrateKeepOpinion = 60;          // below this the integration falls through
    public const long IntegrateInfluence = 40_000;
    public const int IntegrateTicks = 600;               // 60 s
    public const int GiftMaxPoints = 40;
    public const int GiftDecayTicks = 600;               // a gift's warmth fades one point a minute
    public const int TradeAlloyPermille = 80;            // each trade or alliance partner adds this to alloy income
    public const int MaxTradePartners = 3;
    public const int BreakCooldownTicks = 3000;          // five minutes
    public const int RefusalTicks = 600;                 // a refused proposal cannot be repeated for a minute
    public const int DemandRefusalTicks = 1200;          // two minutes
    public const int TreacherousPoints = 40;
    public const int TreacherousDecayTicks = 150;        // fades over about ten minutes
    public const int FirstTaskTick = 1200;
    public const int TaskGiftAlloy = 15;
    public const int TaskReward = 12;
    public const int TaskLifeTicks = 3000;
    public const int RaidOpinion = -50;
    public const int RaidEveryTicks = 3000;
    public const int RaidReach = 6;
    public const int RaiderStrength = 3;
    public const int RaiderLifeTicks = 4800;

    // ---- opinion ---------------------------------------------------------------------------

    /// <summary>How <paramref name="from"/> feels about <paramref name="toward"/>, −100..+100, with every cause listed.</summary>
    public static int Opinion(GameState s, int from, int toward, List<OpinionLine>? lines = null)
    {
        int total = 0;
        void Add(string label, int pts)
        {
            if (pts == 0) return;
            total += pts;
            lines?.Add(new OpinionLine(label, pts));
        }

        var dip = s.Diplomacy;
        switch (dip.Get(from, toward))
        {
            case Treaty.War: Add(s.Players[from].Minor ? "Natives distrust invaders" : "At war", s.Players[from].Minor ? -15 : -20); break;
            case Treaty.NonAggression: Add("Non-aggression pact", 5); break;
            case Treaty.TradePact: Add("Trade pact", 10); break;
            case Treaty.Alliance: Add("Alliance", 20); break;
        }
        foreach (var m in dip.Mods)
            if (m.From == from && m.Toward == toward) Add(m.Kind, m.Live(s.Tick));
        return Math.Clamp(total, -100, 100);
    }

    public static void AddMod(GameState s, int from, int toward, string kind, int value, int decayEvery, int cap)
    {
        var dip = s.Diplomacy;
        var m = dip.Mods.FirstOrDefault(x => x.From == from && x.Toward == toward && x.Kind == kind);
        if (m == null)
        {
            dip.Mods.Add(new OpinionMod { From = from, Toward = toward, Kind = kind, Value = Math.Clamp(value, -cap, cap), Tick = s.Tick, DecayEvery = decayEvery });
            return;
        }
        m.Value = Math.Clamp(m.Live(s.Tick) + value, -cap, cap);
        m.Tick = s.Tick;
    }

    public static Mood MoodOf(GameState s, int minor, int toward)
    {
        var dip = s.Diplomacy;
        if (dip.IntegratedInto[minor] != -1) return Mood.Integrated;
        var treaty = dip.Get(minor, toward);
        if (treaty == Treaty.Alliance) return Mood.Allied;
        if (dip.Tasks.Any(t => t.Kind == "clear" && t.Minor == minor && t.Player == toward)) return Mood.Raiding;
        int op = Opinion(s, minor, toward);
        if (op >= 30 || treaty >= Treaty.TradePact) return Mood.Friendly;
        if (op <= -35) return Mood.Hostile;
        if (op <= -5) return Mood.Wary;
        return Mood.Peaceful;
    }

    // ---- who is a valid counterpart -------------------------------------------------------

    private static bool Valid(GameState s, int a, int b) =>
        a >= 0 && b >= 0 && a < s.Players.Length && b < s.Players.Length && a != b
        && s.Diplomacy.IntegratedInto[a] == -1 && s.Diplomacy.IntegratedInto[b] == -1;

    // ---- acceptance -----------------------------------------------------------------------

    /// <summary>Would <paramref name="to"/> agree to move its relationship with <paramref name="from"/> up to <paramref name="want"/>?</summary>
    public static Verdict Evaluate(GameState s, int from, int to, Treaty want)
    {
        var dip = s.Diplomacy;
        if (!Valid(s, from, to) || s.Players[from].Minor) return new Verdict(false, "That is not possible.");
        var have = dip.Get(from, to);
        if (want != have + 1) return new Verdict(false, "Treaties are signed one step at a time.");
        if (dip.TreatyCooldownUntil[from] > s.Tick)
            return new Verdict(false, $"Nobody will deal with you for {(dip.TreatyCooldownUntil[from] - s.Tick + 9) / 10} s after a broken treaty.");
        if (dip.RefusedUntil[dip.Pair(from, to)] > s.Tick)
            return new Verdict(false, $"They refused recently; ask again in {(dip.RefusedUntil[dip.Pair(from, to)] - s.Tick + 9) / 10} s.");
        if (s.Players[from].Influence < TreatyInfluence[(int)want])
            return new Verdict(false, $"This treaty costs {TreatyInfluence[(int)want] / 1000} Influence.");
        if (!s.Players[to].Minor && want == Treaty.Alliance && s.Majors.Count() <= 2)
            return new Verdict(false, "Only one nation can win; the last two nations cannot ally.");

        var lines = new List<OpinionLine>();
        int opinion = Opinion(s, to, from, lines);
        int need = TreatyNeed[(int)want];

        if (s.Players[to].Minor)
        {
            bool ok = opinion >= need;
            string why = ok ? $"They trust you enough (opinion {opinion}, needs {need})."
                            : $"They do not trust you enough yet (opinion {opinion}, needs {need}).";
            return new Verdict(ok, why, opinion, need, lines);
        }

        if (s.Players[to].PersonalityId == null)
            return new Verdict(false, "Only AI nations answer instantly.", opinion, need, lines);

        var pers = Personality.ById(s.Players[to].PersonalityId!);
        if (pers.Id == "zealot" && s.Majors.Count(p => p.Id != to) == 1)
            return new Verdict(false, "They have sworn to destroy you.", opinion, need, lines);

        int score = opinion;
        string bias = pers.PeaceBias >= 0 ? $"They prefer peace ({pers.Name})" : $"They prefer war ({pers.Name})";
        int bonus = pers.PeaceBias + (want >= Treaty.TradePact ? pers.Courting / 100 : 0);
        lines.Add(new OpinionLine(bias, bonus));
        score += bonus;

        int own = s.OwnedTiles(to), theirs = s.OwnedTiles(from);
        if (own * 1000L / Math.Max(1, own + theirs) >= 600)
        {
            lines.Add(new OpinionLine("They think they are winning", -25));
            score -= 25;
        }
        if (Hopelessness.Score(s, to, out _) >= 400)
        {
            lines.Add(new OpinionLine("They are losing the war", 30));
            score += 30;
        }

        bool accepted = score >= need;
        var pick = accepted ? lines.Where(l => l.Points > 0).OrderByDescending(l => l.Points).FirstOrDefault()
                            : lines.Where(l => l.Points < 0).OrderBy(l => l.Points).FirstOrDefault();
        string reason = accepted
            ? $"They agree: {pick?.Label ?? "it suits them"} (score {score}, needs {need})."
            : $"They refuse: {pick?.Label ?? "they are not convinced"} (score {score}, needs {need}).";
        return new Verdict(accepted, reason, score, need, lines);
    }

    /// <summary>Would <paramref name="to"/>, an AI at war with <paramref name="from"/>, give up now? Same Hopelessness it uses to offer surrender.</summary>
    public static Verdict EvaluateDemand(GameState s, int from, int to)
    {
        var dip = s.Diplomacy;
        if (!Valid(s, from, to) || s.Players[from].Minor || s.Players[to].Minor) return new Verdict(false, "That is not possible.");
        if (s.Players[to].PersonalityId == null) return new Verdict(false, "Only AI nations can be asked to surrender.");
        if (!s.AtWar(from, to)) return new Verdict(false, "You are not at war with them.");
        if (dip.DemandRefusedUntil[dip.Pair(from, to)] > s.Tick)
            return new Verdict(false, $"They refused recently; ask again in {(dip.DemandRefusedUntil[dip.Pair(from, to)] - s.Tick + 9) / 10} s.");

        int h = Hopelessness.Score(s, to, out string why);
        int threshold = Personality.ById(s.Players[to].PersonalityId!).PainThreshold;
        if (h >= threshold)
            return new Verdict(true, $"They surrender: \"{why}.\" (hopelessness {h}, limit {threshold})", h, threshold);

        int cap = s.Players[to].Capital;
        string refusal =
            s.TileOwner[cap] == to && s.Stacks.Any(x => x.Owner == to && x.Tile == cap) ? "They still hold a fortified capital"
            : s.Stacks.Any(x => x.Owner == to && !s.Catalog.Units[x.Units.Keys.First()].Builds) ? "They still have an army in the field"
            : "They have a way to keep fighting";
        return new Verdict(false, $"They refuse: {refusal}. (hopelessness {h}, limit {threshold})", h, threshold);
    }

    // ---- commands -------------------------------------------------------------------------

    /// <summary>Applies a diplomacy command. Returns the id of a nation that surrendered because of it, or -1.</summary>
    public static int Apply(GameState s, Command cmd, List<SimEvent> events)
    {
        switch (cmd)
        {
            case GiftCommand g: Gift(s, g, events); break;
            case ProposeTreatyCommand p: Propose(s, p, events); break;
            case BreakTreatyCommand b: Break(s, b, events); break;
            case DeclareWarCommand w: DeclareWar(s, w, events); break;
            case IntegrateCommand i: StartIntegration(s, i, events); break;
            case DemandSurrenderCommand d: return Demand(s, d, events);
        }
        return -1;
    }

    private static void Gift(GameState s, GiftCommand g, List<SimEvent> events)
    {
        if (!Valid(s, g.Player, g.To) || s.Players[g.Player].Minor || g.Alloy < 1) return;
        var from = s.Players[g.Player];
        long cost = g.Alloy * 1000L;
        if (from.Supply < cost) return;
        from.Supply -= cost;
        if (!s.Players[g.To].Minor) s.Players[g.To].Supply += cost;
        AddMod(s, g.To, g.Player, "Gifts", Math.Min(GiftMaxPoints, Math.Max(1, g.Alloy / 2)), GiftDecayTicks, 60);
        events.Add(new GiftGiven(g.Player, g.To, g.Alloy));

        // A big enough gift settles an open gift task from that minor.
        var dip = s.Diplomacy;
        for (int i = 0; i < dip.Tasks.Count; i++)
        {
            var t = dip.Tasks[i];
            if (t.Kind != "gift" || t.Minor != g.To || g.Alloy * 1000L < t.Amount) continue;
            Complete(s, t, g.Player, events);
            dip.Tasks.RemoveAt(i--);
        }
    }

    private static void Complete(GameState s, MinorTask t, int player, List<SimEvent> events)
    {
        AddMod(s, t.Minor, player, "Did us a favour", t.Reward, 1200, 60);
        events.Add(new TaskCompleted(t.Id, t.Minor, player));
    }

    private static void Propose(GameState s, ProposeTreatyCommand p, List<SimEvent> events)
    {
        if (!Valid(s, p.Player, p.To) || s.Players[p.Player].Minor) return;
        var dip = s.Diplomacy;
        var verdict = Evaluate(s, p.Player, p.To, p.Treaty);
        events.Add(new ProposalAnswered(p.Player, p.To, p.Treaty, verdict.Accepted, verdict.Reason));
        if (!verdict.Accepted)
        {
            // Only a real refusal blocks repeats, not "that is not possible" or a missing bill.
            if (verdict.Lines != null) dip.RefusedUntil[dip.Pair(p.Player, p.To)] = s.Tick + RefusalTicks;
            return;
        }
        s.Players[p.Player].Influence -= TreatyInfluence[(int)p.Treaty];
        SetTreaty(s, p.Player, p.To, p.Treaty, events);
    }

    private static void SetTreaty(GameState s, int a, int b, Treaty t, List<SimEvent> events)
    {
        var dip = s.Diplomacy;
        var old = dip.Get(a, b);
        if (old == t) return;
        dip.Set(a, b, t);
        events.Add(new TreatyChanged(a, b, old, t));
        if (t == Treaty.War || t < Treaty.Alliance) CancelIntegrations(s, a, b, "the alliance ended", events);
    }

    private static void MakeTreacherous(GameState s, int player, List<SimEvent> events)
    {
        var dip = s.Diplomacy;
        dip.TreatyCooldownUntil[player] = s.Tick + BreakCooldownTicks;
        foreach (var o in s.Players)
            if (o.Id != player && dip.IntegratedInto[o.Id] == -1)
                AddMod(s, o.Id, player, "Treacherous", -TreacherousPoints, TreacherousDecayTicks, 80);
        events.Add(new Treacherous(player));
    }

    private static void Break(GameState s, BreakTreatyCommand b, List<SimEvent> events)
    {
        if (!Valid(s, b.Player, b.With) || s.Players[b.Player].Minor) return;
        if (s.Diplomacy.Get(b.Player, b.With) < Treaty.NonAggression) return;
        SetTreaty(s, b.Player, b.With, Treaty.Neutral, events);
        MakeTreacherous(s, b.Player, events);
    }

    private static void DeclareWar(GameState s, DeclareWarCommand w, List<SimEvent> events)
    {
        if (!Valid(s, w.Player, w.On) || s.Players[w.Player].Minor) return;
        var have = s.Diplomacy.Get(w.Player, w.On);
        if (have == Treaty.War) return;
        SetTreaty(s, w.Player, w.On, Treaty.War, events);
        AddMod(s, w.On, w.Player, "Declared war on us", -20, 900, 60);
        if (have >= Treaty.NonAggression) MakeTreacherous(s, w.Player, events);
    }

    private static int Demand(GameState s, DemandSurrenderCommand d, List<SimEvent> events)
    {
        if (!Valid(s, d.Player, d.From)) return -1;
        var v = EvaluateDemand(s, d.Player, d.From);
        events.Add(new SurrenderAnswered(d.From, d.Player, v.Accepted, v.Reason));
        if (v.Accepted) return d.From;
        if (v.Score > 0 || v.Need > 0) s.Diplomacy.DemandRefusedUntil[s.Diplomacy.Pair(d.Player, d.From)] = s.Tick + DemandRefusalTicks;
        return -1;
    }

    // ---- integration ----------------------------------------------------------------------

    private static void StartIntegration(GameState s, IntegrateCommand c, List<SimEvent> events)
    {
        var dip = s.Diplomacy;
        if (!Valid(s, c.Player, c.Minor) || s.Players[c.Player].Minor || !s.Players[c.Minor].Minor) return;
        if (dip.Get(c.Player, c.Minor) != Treaty.Alliance || dip.Integrations.Any(i => i.Minor == c.Minor)) return;
        if (Opinion(s, c.Minor, c.Player) < IntegrateOpinion) return;
        if (s.Players[c.Player].Influence < IntegrateInfluence) return;
        s.Players[c.Player].Influence -= IntegrateInfluence;
        dip.Integrations.Add(new Integration { Player = c.Player, Minor = c.Minor, TicksLeft = IntegrateTicks });
        events.Add(new IntegrationStarted(c.Player, c.Minor));
    }

    private static void CancelIntegrations(GameState s, int a, int b, string why, List<SimEvent> events)
    {
        var dip = s.Diplomacy;
        for (int i = 0; i < dip.Integrations.Count; i++)
        {
            var it = dip.Integrations[i];
            if (!(it.Player == a && it.Minor == b) && !(it.Player == b && it.Minor == a)) continue;
            s.Players[it.Player].Influence += IntegrateInfluence;     // the bill comes back
            dip.Integrations.RemoveAt(i--);
            events.Add(new IntegrationCancelled(it.Player, it.Minor, why));
        }
    }

    /// <summary>The minor's land and garrison become the player's, with no fighting.</summary>
    private static void FinishIntegration(GameState s, Integration it, List<SimEvent> events)
    {
        for (int t = 0; t < s.TileOwner.Length; t++)
            if (s.TileOwner[t] == it.Minor)
            {
                s.TileOwner[t] = it.Player;
                events.Add(new TileCaptured(t, it.Player, it.Minor));
            }
        s.Stacks.RemoveAll(x => x.Owner == it.Minor && x.Raider);
        foreach (var st in s.Stacks) if (st.Owner == it.Minor) st.Owner = it.Player;
        s.Diplomacy.IntegratedInto[it.Minor] = it.Player;
        s.Diplomacy.Tasks.RemoveAll(t => t.Minor == it.Minor);
        s.Diplomacy.Set(it.Player, it.Minor, Treaty.Neutral);
        events.Add(new MinorIntegrated(it.Player, it.Minor));
    }

    // ---- hooks from the rest of the sim ---------------------------------------------------

    /// <summary>A major took ground from a minor power: it, and its kin, remember.</summary>
    public static void OnCapture(GameState s, int newOwner, int previousOwner)
    {
        if (previousOwner < 0 || newOwner < 0 || newOwner == previousOwner) return;
        if (!s.Players[previousOwner].Minor || s.Players[newOwner].Minor) return;
        AddMod(s, previousOwner, newOwner, "Took our land", -25, 600, 75);
        foreach (var o in s.Players)
            if (o.Minor && o.Id != previousOwner && s.CultureOf(o.Id) == s.CultureOf(previousOwner) && s.Diplomacy.IntegratedInto[o.Id] == -1)
                AddMod(s, o.Id, newOwner, "Kin of the conquered", -15, 900, 45);
    }

    /// <summary>Alloy income bonus (permille) from trade pacts and alliances.</summary>
    public static int TradeBonusPermille(GameState s, int player)
    {
        int partners = 0;
        for (int o = 0; o < s.Players.Length; o++)
            if (o != player && s.Diplomacy.IntegratedInto[o] == -1 && s.Diplomacy.Get(player, o) >= Treaty.TradePact) partners++;
        return Math.Min(partners, MaxTradePartners) * TradeAlloyPermille;
    }

    // ---- per-tick upkeep ------------------------------------------------------------------

    public static void Tick(GameState s, List<SimEvent> events)
    {
        var dip = s.Diplomacy;

        for (int i = 0; i < dip.Integrations.Count; i++)
        {
            var it = dip.Integrations[i];
            if (s.Tick % 10 == 0 && (dip.Get(it.Player, it.Minor) != Treaty.Alliance || Opinion(s, it.Minor, it.Player) < IntegrateKeepOpinion))
            {
                s.Players[it.Player].Influence += IntegrateInfluence;
                dip.Integrations.RemoveAt(i--);
                events.Add(new IntegrationCancelled(it.Player, it.Minor, "their trust slipped"));
                continue;
            }
            if (--it.TicksLeft > 0) continue;
            dip.Integrations.RemoveAt(i--);
            FinishIntegration(s, it, events);
        }

        // Raiders that died settle their clear-raiders task for the nation they harassed.
        for (int i = 0; i < dip.Tasks.Count; i++)
        {
            var t = dip.Tasks[i];
            if (t.Kind != "clear") continue;
            bool died = events.Any(e => e is StackDied d && d.StackId == t.Target);
            bool merged = events.Any(e => e is StacksMerged m && m.AbsorbedStackId == t.Target);
            if (died) Complete(s, t, t.Player, events);
            if (died || merged) dip.Tasks.RemoveAt(i--);
        }

        if (s.Tick % 10 != 0) return;
        dip.Tasks.RemoveAll(t => t.Expires <= s.Tick);
        dip.Mods.RemoveAll(m => m.Live(s.Tick) == 0 && s.Tick - m.Tick > 0);

        foreach (var minor in s.Players.Where(p => p.Minor && dip.IntegratedInto[p.Id] == -1))
        {
            PostGiftTask(s, minor, events);
            Raid(s, minor, events);
            RecallRaiders(s, minor);
        }
    }

    private static void PostGiftTask(GameState s, Player minor, List<SimEvent> events)
    {
        var dip = s.Diplomacy;
        if (s.Tick < FirstTaskTick + minor.Id * 37 || dip.Tasks.Any(t => t.Minor == minor.Id && t.Kind == "gift")) return;
        if ((s.Tick + minor.Id * 10) % 600 != 0) return;
        var task = new MinorTask { Id = dip.NextTaskId++, Minor = minor.Id, Kind = "gift", Amount = TaskGiftAlloy * 1000L, Reward = TaskReward, Expires = s.Tick + TaskLifeTicks };
        dip.Tasks.Add(task);
        events.Add(new TaskPosted(task.Id, minor.Id, task.Kind));
    }

    /// <summary>A minor that has been wronged sends a small raiding party at the nearest land of the nation it hates most.</summary>
    private static void Raid(GameState s, Player minor, List<SimEvent> events)
    {
        var dip = s.Diplomacy;
        if (dip.NextRaid[minor.Id] == 0) dip.NextRaid[minor.Id] = s.Tick + RaidEveryTicks + minor.Id * 97;
        if (s.Tick < dip.NextRaid[minor.Id]) return;
        if (s.Stacks.Any(x => x.Owner == minor.Id && x.Raider)) return;

        int victim = -1, worst = RaidOpinion + 1;
        foreach (var p in s.Majors)
        {
            if (!s.AtWar(minor.Id, p.Id)) continue;
            int op = Opinion(s, minor.Id, p.Id);
            if (op < worst) { worst = op; victim = p.Id; }
        }
        dip.NextRaid[minor.Id] = s.Tick + RaidEveryTicks;
        if (victim == -1) return;

        // Nearest tile of the victim within reach of the minor's capital.
        var dist = new int[s.Map.TileCount];
        Array.Fill(dist, -1);
        var q = new Queue<int>();
        dist[minor.Capital] = 0;
        q.Enqueue(minor.Capital);
        int target = -1;
        while (q.Count > 0 && target == -1)
        {
            int c = q.Dequeue();
            if (dist[c] >= RaidReach) continue;
            foreach (int n in s.Map.Neighbors[c])
            {
                if (dist[n] != -1) continue;
                dist[n] = dist[c] + 1;
                if (s.TileOwner[n] == victim) { target = n; break; }
                q.Enqueue(n);
            }
        }
        if (target == -1) return;

        var raider = s.AddStack(minor.Id, minor.Capital, RaiderStrength);
        raider.Raider = true;
        raider.Path.AddRange(Pathfinder.Find(s.Map.Neighbors, minor.Capital, target));
        var task = new MinorTask { Id = dip.NextTaskId++, Minor = minor.Id, Kind = "clear", Target = raider.Id, Player = victim, Reward = TaskReward + 3, Expires = s.Tick + RaiderLifeTicks };
        dip.Tasks.Add(task);
        events.Add(new RaidLaunched(minor.Id, victim, raider.Id));
        events.Add(new TaskPosted(task.Id, minor.Id, task.Kind));
    }

    /// <summary>A raider that has stood idle a while goes home and rejoins the garrison.</summary>
    private static void RecallRaiders(GameState s, Player minor)
    {
        foreach (var r in s.Stacks.Where(x => x.Owner == minor.Id && x.Raider && x.Idle && x.Tile != minor.Capital && !Simulation.Engaged(s, x)))
        {
            if (r.StillTicks < 300) continue;             // Entrench counts the ticks it has stood still
            r.Path.AddRange(Pathfinder.Find(s.Map.Neighbors, r.Tile, minor.Capital));
            r.StillTicks = 0;
        }
    }
}
