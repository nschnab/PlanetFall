namespace Planetfall.Sim.World;

public sealed class Stack
{
    public int Id { get; init; }
    public int Owner { get; set; }
    public int Tile { get; set; }

    /// <summary>Tile being hopped into, or -1 when idle. Set when the hop order begins.</summary>
    public int Target { get; set; } = -1;

    /// <summary>Progress of the current hop in ms x permille (see Simulation movement phase).</summary>
    public long Timer { get; set; }

    public List<int> Path { get; } = new();
    public long Hp { get; set; }
    public long MaxHp { get; set; }
    public bool Retreating { get; set; }

    /// <summary>Ticks stood still and not under attack; drives entrenchment.</summary>
    public int StillTicks { get; set; }

    /// <summary>Defence bonus in permille from digging in (0..500). Lost the moment the stack moves.</summary>
    public int Entrench { get; set; }

    /// <summary>Effective strength: whole hp, rounded up, so a wounded stack hits weaker.</summary>
    public int EffectiveStrength => (int)((Hp + 999) / 1000);

    public bool Idle => Target == -1 && Path.Count == 0;
}
