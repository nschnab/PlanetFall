using System.Text.Json;

namespace Planetfall.Sim.Core;

/// <summary>
/// Action-to-key map for the client, kept engine-free so rebinding rules are testable. Keys are stored as plain
/// names ("Space", "Z", "F3"); the client converts them. One key may drive only one action.
/// </summary>
public sealed class Bindings
{
    public static readonly (string Action, string Label, string Default)[] Actions =
    {
        ("pause", "Pause", "Space"),
        ("slower", "Slower", "Minus"),
        ("faster", "Faster", "Equal"),
        ("stop", "Stop stack", "S"),
        ("idle", "Next idle stack", "Tab"),
        ("clear", "Deselect", "Escape"),
        ("recruit_infantry", "Buy infantry", "I"),
        ("recruit_armor", "Buy armor", "A"),
        ("recruit_artillery", "Buy artillery", "G"),
        ("recruit_recon", "Buy recon", "V"),
        ("recruit_engineer", "Buy engineer", "N"),
        ("build_depot", "Build depot", "Z"),
        ("build_extractor", "Build extractor", "X"),
        ("build_fortress", "Build fortress", "C"),
        ("diplomacy", "Diplomacy panel", "D"),
        ("home", "Centre on capital", "Home"),
        ("pan_up", "Pan up", "Up"),
        ("pan_down", "Pan down", "Down"),
        ("pan_left", "Pan left", "Left"),
        ("pan_right", "Pan right", "Right"),
        ("debug", "AI debug", "F3"),
        ("save", "Quick save", "F5"),
        ("load", "Quick load", "F9"),
        ("menu", "Menu", "F10"),
    };

    private readonly Dictionary<string, string> _map = new();

    public Bindings() => Reset();

    public void Reset()
    {
        _map.Clear();
        foreach (var (a, _, d) in Actions) _map[a] = d;
    }

    public string KeyOf(string action) => _map[action];

    public string? ActionOf(string key) => _map.FirstOrDefault(kv => kv.Value == key).Key;

    public static string LabelOf(string action) => Actions.First(a => a.Action == action).Label;

    /// <summary>Binds a key to an action. Fails, naming the action in the way, if another action already uses that key.</summary>
    public bool TryRebind(string action, string key, out string? conflict)
    {
        conflict = null;
        if (!_map.ContainsKey(action)) throw new ArgumentException("Unknown action " + action);
        var other = ActionOf(key);
        if (other != null && other != action) { conflict = LabelOf(other); return false; }
        _map[action] = key;
        return true;
    }

    public string ToJson() => JsonSerializer.Serialize(_map.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));

    /// <summary>Reads saved bindings, ignoring unknown actions and any entry that would clash; the defaults fill the rest.</summary>
    public static Bindings FromJson(string? json)
    {
        var b = new Bindings();
        if (string.IsNullOrWhiteSpace(json)) return b;
        Dictionary<string, string>? saved;
        try { saved = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (JsonException) { return b; }
        if (saved == null) return b;
        // Apply in two passes so a swap of two defaults (A<->B) is not rejected halfway.
        var wanted = Actions.Where(a => saved.TryGetValue(a.Action, out var k) && !string.IsNullOrEmpty(k)).ToDictionary(a => a.Action, a => saved[a.Action]);
        if (wanted.Values.Distinct().Count() != wanted.Count) return b;
        var taken = new HashSet<string>(wanted.Values);
        foreach (var (a, _, d) in Actions)
            if (!wanted.ContainsKey(a) && taken.Contains(d)) return b;     // a default would now clash: drop the file
        foreach (var kv in wanted) b._map[kv.Key] = kv.Value;
        return b;
    }
}
