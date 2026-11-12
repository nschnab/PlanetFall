using System.Text.Json;
using Planetfall.Sim.Core;

namespace Planetfall.Sim.Ai;

/// <summary>
/// How an AI decides, never what it has: goal weights and risk tolerance. Pure data from
/// data/ai/personalities.json; stats stay identical for everyone (pillar 2).
/// </summary>
public sealed class Personality
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Goal weights in permille (1000 = neutral).</summary>
    public int Expand { get; init; }
    public int Develop { get; init; }
    public int Defend { get; init; }
    public int Attack { get; init; }

    /// <summary>Strength a stack gathers at a spawn site before it marches out.</summary>
    public int Muster { get; init; }

    /// <summary>A target is skipped if its defenders exceed our strength times this, in permille.</summary>
    public int RiskPermille { get; init; }

    /// <summary>Hopelessness (0..1000) above which the AI starts counting towards surrender. 990 is nearly never.</summary>
    public int PainThreshold { get; init; }

    /// <summary>Engineer stacks it keeps alive.</summary>
    public int Engineers { get; init; }

    /// <summary>Points added to every treaty it is asked to sign: positive prefers peace, negative prefers war (-100..100).</summary>
    public int PeaceBias { get; init; }

    /// <summary>How hard it works to befriend minor powers, in permille (0 never, 1000 steadily, 3000 obsessively).</summary>
    public int Courting { get; init; }

    private sealed record Dto(string Id, string Name, int Expand, int Develop, int Defend, int Attack,
        int Muster, int RiskPermille, int PainThreshold, int Engineers, int PeaceBias = 0, int Courting = 0);
    private sealed record FileDto(List<Dto>? Personalities);

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
    private static IReadOnlyList<Personality>? _all;
    public static IReadOnlyList<Personality> All => _all ??= LoadEmbedded();

    private static IReadOnlyList<Personality> LoadEmbedded()
    {
        var asm = typeof(Personality).Assembly;
        string name = asm.GetManifestResourceNames().Single(n => n.Replace('\\', '/') == "data/ai/personalities.json");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static IReadOnlyList<Personality> Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json, Opts) ?? throw new FormatException("Personalities JSON is empty");
        if (dto.Personalities == null || dto.Personalities.Count == 0) throw new FormatException("No personalities defined");
        var seen = new HashSet<string>();
        var list = new List<Personality>();
        foreach (var d in dto.Personalities)
        {
            if (string.IsNullOrWhiteSpace(d.Id) || !seen.Add(d.Id)) throw new FormatException($"Personality id missing or duplicated: '{d.Id}'");
            if (string.IsNullOrWhiteSpace(d.Name)) throw new FormatException($"Personality {d.Id}: needs a name");
            foreach (var (label, w) in new[] { ("expand", d.Expand), ("develop", d.Develop), ("defend", d.Defend), ("attack", d.Attack) })
                if (w <= 0 || w > 3000) throw new FormatException($"Personality {d.Id}: {label} weight must be 1..3000");
            if (d.Muster < 1) throw new FormatException($"Personality {d.Id}: muster must be at least 1");
            if (d.RiskPermille < 100) throw new FormatException($"Personality {d.Id}: riskPermille must be at least 100");
            if (d.PainThreshold < 1 || d.PainThreshold > 1000) throw new FormatException($"Personality {d.Id}: painThreshold must be 1..1000");
            if (d.Engineers < 0 || d.Engineers > 5) throw new FormatException($"Personality {d.Id}: engineers must be 0..5");
            if (d.PeaceBias is < -100 or > 100) throw new FormatException($"Personality {d.Id}: peaceBias must be -100..100");
            if (d.Courting is < 0 or > 3000) throw new FormatException($"Personality {d.Id}: courting must be 0..3000");
            list.Add(new Personality
            {
                Id = d.Id, Name = d.Name, Expand = d.Expand, Develop = d.Develop, Defend = d.Defend, Attack = d.Attack,
                Muster = d.Muster, RiskPermille = d.RiskPermille, PainThreshold = d.PainThreshold, Engineers = d.Engineers, PeaceBias = d.PeaceBias, Courting = d.Courting,
            });
        }
        return list;
    }

    public static Personality ById(string id) => All.First(p => p.Id == id);

    /// <summary>Each player's personality comes from the match seed, so a seed always replays the same minds.</summary>
    public static Personality ForSeed(ulong seed, int player) =>
        All[(int)(SplitMix64.Derive(seed, "ai-personality-" + player) % (ulong)All.Count)];
}
