using System.Text.Json;
using Planetfall.Sim.Core.Modifiers;

namespace Planetfall.Sim.Data;

/// <summary>A leader is pure data: identity plus modifiers. It never changes core rules.</summary>
public sealed class Leader : IModifierSource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Portrait { get; init; } = "placeholder";
    public IReadOnlyList<Modifier> Modifiers { get; init; } = Array.Empty<Modifier>();

    private sealed record ModifierJson(string Key, string Op, int Value);
    private sealed record LeaderJson(string Id, string Name, string? Portrait, List<ModifierJson>? Modifiers);

    public static Leader Parse(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var dto = JsonSerializer.Deserialize<LeaderJson>(json, opts)
                  ?? throw new FormatException("Leader JSON is empty");
        if (string.IsNullOrWhiteSpace(dto.Id)) throw new FormatException("Leader needs an id");
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new FormatException("Leader needs a name");

        var mods = new List<Modifier>();
        foreach (var m in dto.Modifiers ?? new List<ModifierJson>())
        {
            if (string.IsNullOrWhiteSpace(m.Key)) throw new FormatException($"Leader {dto.Id}: modifier without a key");
            var op = m.Op?.ToLowerInvariant() switch
            {
                "pct" => ModifierOp.Pct,
                "flat" => ModifierOp.Flat,
                _ => throw new FormatException($"Leader {dto.Id}: unknown modifier op '{m.Op}'"),
            };
            mods.Add(new Modifier(m.Key, op, m.Value));
        }
        return new Leader { Id = dto.Id, Name = dto.Name, Portrait = dto.Portrait ?? "placeholder", Modifiers = mods };
    }
}
