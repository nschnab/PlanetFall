using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Presentation;

public enum MusicMood { Calm, Tense, Battle }

/// <summary>
/// Decides how intense the music should be from what the player can see, and fades three looping layers
/// (calm, tense, battle) towards that mood. Battle begins the tick a visible fight starts; the layers then
/// cross-fade in under a second.
/// </summary>
public sealed class MusicDirector
{
    public const int BattleHoldTicks = 150;     // stays at Battle 15 s after the last visible fight
    public const int TenseHoldTicks = 450;      // then Tense for a while longer
    public const double FadePerSecond = 1.25;   // 0 -> 1 in 0.8 s

    private int _lastFight = int.MinValue / 2, _lastThreat = int.MinValue / 2;

    public MusicMood Mood { get; private set; } = MusicMood.Calm;
    public double[] Layers { get; } = { 1, 0, 0 };

    public void Update(int tick, bool fighting, bool threat)
    {
        if (fighting) _lastFight = tick;
        if (threat) _lastThreat = tick;
        Mood = tick - _lastFight <= BattleHoldTicks ? MusicMood.Battle
             : tick - _lastFight <= TenseHoldTicks || tick - _lastThreat <= TenseHoldTicks ? MusicMood.Tense
             : MusicMood.Calm;
    }

    public static double[] Target(MusicMood mood) => mood switch
    {
        MusicMood.Battle => new[] { 0.25, 0.5, 1.0 },
        MusicMood.Tense => new[] { 0.6, 1.0, 0.0 },
        _ => new[] { 1.0, 0.0, 0.0 },
    };

    /// <summary>Moves each layer towards the mood's mix; call every frame with the elapsed real seconds.</summary>
    public void Fade(double seconds)
    {
        var t = Target(Mood);
        double step = FadePerSecond * seconds;
        for (int i = 0; i < Layers.Length; i++)
            Layers[i] = Layers[i] < t[i] ? Math.Min(t[i], Layers[i] + step) : Math.Max(t[i], Layers[i] - step);
    }

    /// <summary>What the player can see right now: is there a fight, and is an enemy in view.</summary>
    public static (bool Fighting, bool Threat) Sense(GameState s, Fog fog)
    {
        bool fighting = false, threat = false;
        foreach (var st in s.Stacks)
        {
            if (!fog.CanSee(s, st)) continue;
            if (st.Owner != fog.Player && !s.Players[st.Owner].Minor && s.AtWar(fog.Player, st.Owner)) threat = true;
            if (!fighting && Simulation.Engaged(s, st) && (st.Owner == fog.Player || fog.Visible(st.Tile))) fighting = true;
        }
        return (fighting, threat);
    }
}
