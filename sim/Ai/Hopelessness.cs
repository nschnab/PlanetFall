using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Ai;

/// <summary>
/// 0..1000: how little a nation can still hope for. Weighs territory, income, army strength and above
/// all the state of its capital, so a nation with a live capital and an army stays well below every
/// personality's threshold. The rival uses it to offer surrender; "demand surrender" asks the same question.
/// </summary>
public static class Hopelessness
{
    public static int Score(GameState s, int me, out string reason)
    {
        reason = "";
        int foe = Strongest(s, me);
        if (foe == -1) return 0;
        var self = s.Players[me];
        int tiles = PainFromShare(s.OwnedTiles(me), s.OwnedTiles(foe));
        int income = PainFromShare(Simulation.Income(s, self).Alloy, Simulation.Income(s, s.Players[foe]).Alloy);
        int army = PainFromShare(Power(s.Stacks.Where(x => x.Owner == me)), Power(s.Stacks.Where(x => x.Owner == foe)));

        int cap = self.Capital;
        bool defended = s.Stacks.Any(x => x.Owner == me && x.Tile == cap);
        bool besieged = s.Stacks.Any(x => x.Owner != me && !s.Players[x.Owner].Minor
            && (x.Tile == cap || x.Target == cap || s.Map.Neighbors[cap].Contains(x.Tile)));
        int capital = s.TileOwner[cap] != me ? 1000 : besieged ? (defended ? 600 : 1000) : defended ? 0 : 150;

        int score = (tiles * 3 + income * 2 + army * 2 + capital * 3) / 10;
        reason = capital >= 600 ? "Our capital is falling"
            : army >= 700 ? "We have no army left to fight with"
            : tiles >= 700 ? "We have lost most of our land"
            : "Our capital still stands";
        return score;
    }

    private static int PainFromShare(long own, long foe)
    {
        if (own + foe == 0) return 0;
        long share = own * 1000 / (own + foe);
        return (int)Math.Clamp((450 - share) * 1000 / 450, 0, 1000);
    }

    private static int Power(IEnumerable<Stack> stacks) => stacks.Sum(x => x.EffectiveStrength);

    public static int Strongest(GameState s, int me)
    {
        var others = s.Majors.Where(p => p.Id != me).ToList();
        if (others.Count == 0) return -1;
        return others.OrderByDescending(p => s.OwnedTiles(p.Id) + Power(s.Stacks.Where(x => x.Owner == p.Id))).ThenBy(p => p.Id).First().Id;
    }
}
