using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Ai;

public sealed record AiMatchResult(
    ulong Seed, int Ticks, int Winner, string EndReason, int Surrenderer, int StuckStacks,
    string[] Personalities, int[] TilesAtEnd, string StuckDetail = "");

/// <summary>
/// Plays one AI-versus-AI match to the end (or the tick limit). Shared by the tests and the
/// <c>balance-runner</c> tool so both measure the same thing.
/// </summary>
public static class AiMatch
{
    public const int StandardCols = 35, StandardRows = 20;   // ~700 tiles
    public const int AiMaxStacks = int.MaxValue;
    public const int StuckAfterTicks = 3000;    // 5 minutes without moving, fighting or building, and no reason to

    private static readonly Leader Commander = Leader.Parse("""{ "id": "commander", "name": "Field Commander" }""");

    public static GameState Setup(ulong seed, out RivalAi[] ais, int cols = StandardCols, int rows = StandardRows)
    {
        var map = MapGenerator.Generate(seed, cols, rows);
        var s = GameState.NewWithMinors(map, Rules.MinorStrength, Commander, Commander);
        ais = new RivalAi[2];
        for (int i = 0; i < 2; i++)
        {
            s.Players[i].AutoRecruit = false;
            s.Players[i].MaxStacks = AiMaxStacks;
            ais[i] = new RivalAi(i, Personality.ForSeed(seed, i));
            s.Players[i].PersonalityId = ais[i].Personality.Id;
        }
        return s;
    }

    public static AiMatchResult Play(ulong seed, int maxTicks, int cols = StandardCols, int rows = StandardRows)
    {
        var s = Setup(seed, out var ais, cols, rows);
        var lastTile = new Dictionary<int, (int Tile, int Since)>();
        var stuck = new HashSet<int>();
        string detail = "";
        int surrenderer = -1;
        string reason = "unfinished";

        while (!s.Over && s.Tick < maxTicks)
        {
            var cmds = ais.SelectMany(a => a.Think(s)).ToList();
            foreach (var c in cmds)
                if (c is SurrenderCommand sc) surrenderer = sc.Player;
            foreach (var e in Simulation.Step(s, cmds))
                if (e is PlayerDefeated d) reason = d.Reason;

            if (s.Tick % 50 != 0) continue;
            foreach (var st in s.Stacks)
            {
                if (st.Owner >= 2) continue;     // minors never move
                if (!lastTile.TryGetValue(st.Id, out var seen) || seen.Tile != st.Tile || !st.Idle || Simulation.Engaged(s, st)
                    || s.Constructions.Any(c => c.StackId == st.Id))
                {
                    lastTile[st.Id] = (st.Tile, s.Tick);
                    continue;
                }
                var role = ais[st.Owner].OrderOf(st.Id)?.Role;
                if (role is AiRole.Muster or AiRole.Garrison) { lastTile[st.Id] = (st.Tile, s.Tick); continue; }
                if (s.Tick - seen.Since >= StuckAfterTicks && stuck.Add(st.Id) && detail == "")
                    detail = $"stack {st.Id} p{st.Owner} t={s.Tick} tile {st.Tile} hp {st.Hp * 100 / st.MaxHp}% {string.Join("+", st.Units.Select(u => u.Value + u.Key))} order {role}->{ais[st.Owner].OrderOf(st.Id)?.Tile} goal {ais[st.Owner].Goal} starved {s.Players[st.Owner].Starved}";
            }
        }
        return new AiMatchResult(seed, s.Tick, s.Winner, reason, surrenderer, stuck.Count,
            ais.Select(a => a.Personality.Id).ToArray(), new[] { s.OwnedTiles(0), s.OwnedTiles(1) }, detail);
    }
}
