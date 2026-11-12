namespace Planetfall.Sim.World;

/// <summary>Where a pair of nations stands. Ordered: each step up is a closer bond.</summary>
public enum Treaty { War = 0, Neutral = 1, NonAggression = 2, TradePact = 3, Alliance = 4 }

/// <summary>How a minor power feels about one player. Derived from opinion and treaty, never stored.</summary>
public enum Mood { Hostile, Wary, Peaceful, Friendly, Allied, Raiding, Integrated }

/// <summary>
/// One reason a nation feels the way it does about another. It starts at <see cref="Value"/> points and
/// fades towards zero by one point every <see cref="DecayEvery"/> ticks, so the diplomacy panel can list
/// every cause and its remaining weight.
/// </summary>
public sealed class OpinionMod
{
    public int From { get; init; }
    public int Toward { get; init; }
    public required string Kind { get; init; }
    public int Value { get; set; }
    public int Tick { get; set; }
    public int DecayEvery { get; init; }

    public int Live(int now)
    {
        int faded = DecayEvery <= 0 ? 0 : (now - Tick) / DecayEvery;
        return Value >= 0 ? Math.Max(0, Value - faded) : Math.Min(0, Value + faded);
    }
}

/// <summary>A job a minor power wants done. Finishing it earns the doer opinion with that minor.</summary>
public sealed class MinorTask
{
    public int Id { get; init; }
    public int Minor { get; init; }
    public required string Kind { get; init; }   // "gift" or "clear"
    public long Amount { get; init; }             // gift: alloy in thousandths
    public int Target { get; init; } = -1;        // clear: id of the raider stack; -1 for gifts
    public int Player { get; init; } = -1;        // clear: the nation being raided (it earns the reward); -1 means open to anyone
    public int Reward { get; init; }
    public int Expires { get; init; }
}

/// <summary>A minor power being brought peacefully into a nation; its land changes hands when the timer runs out.</summary>
public sealed class Integration
{
    public int Player { get; init; }
    public int Minor { get; init; }
    public int TicksLeft { get; set; }
}

/// <summary>Every treaty, opinion, task and cooldown in the match. Deterministic and hashed.</summary>
public sealed class Diplomacy
{
    private readonly int _n;
    private readonly Treaty[] _treaty;

    public int[] TreatyCooldownUntil { get; }                 // per player: cannot propose until this tick (after breaking a treaty)
    public int[] RefusedUntil { get; }                        // n*n [from*n+to]: a refused proposal blocks repeats
    public int[] DemandRefusedUntil { get; }                  // n*n [from*n+to]: a refused surrender demand blocks repeats
    public int[] IntegratedInto { get; }                      // per minor: the player that absorbed it, or -1
    public int[] NextRaid { get; }                            // per minor: tick of its next raid
    public List<OpinionMod> Mods { get; } = new();
    public List<MinorTask> Tasks { get; } = new();
    public List<Integration> Integrations { get; } = new();
    public int NextTaskId { get; set; } = 1;

    public Diplomacy(Player[] players)
    {
        _n = players.Length;
        _treaty = new Treaty[_n * _n];
        for (int a = 0; a < _n; a++)
            for (int b = 0; b < _n; b++)
                _treaty[a * _n + b] = a != b && players[a].Minor && players[b].Minor ? Treaty.Neutral : Treaty.War;
        TreatyCooldownUntil = new int[_n];
        RefusedUntil = new int[_n * _n];
        DemandRefusedUntil = new int[_n * _n];
        IntegratedInto = new int[_n];
        Array.Fill(IntegratedInto, -1);
        NextRaid = new int[_n];
    }

    public int Count => _n;
    public int Pair(int a, int b) => a * _n + b;
    public Treaty Get(int a, int b) => a == b ? Treaty.Alliance : _treaty[a * _n + b];

    public void Set(int a, int b, Treaty t)
    {
        _treaty[a * _n + b] = t;
        _treaty[b * _n + a] = t;
    }

    public IEnumerable<Treaty> Raw => _treaty;
}
