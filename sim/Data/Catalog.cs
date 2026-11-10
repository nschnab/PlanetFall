using System.Reflection;
using System.Text.Json;
using Planetfall.Sim.Core.Modifiers;
using Planetfall.Sim.MapGen;

namespace Planetfall.Sim.Data;

public sealed class Cost
{
    public long Alloy { get; init; }
    public long Energy { get; init; }
}

public sealed class Attack
{
    public string Type { get; init; } = "";
    public int Permille { get; init; }
}

/// <summary>One unit type. Everything about it is data; adding a unit means adding a JSON file.</summary>
public sealed class UnitDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Domain { get; init; } = "ground";
    public List<string> Roles { get; init; } = new();

    /// <summary>Weight in stack strength; hp is Strength x HpUnit.</summary>
    public int Strength { get; init; } = 1;

    public Attack Attack { get; init; } = new();
    public string Armor { get; init; } = "";
    public int SpeedPermille { get; init; } = 1000;
    public int Vision { get; init; } = 2;

    /// <summary>Energy per tick, in thousandths, while the unit is alive.</summary>
    public long UpkeepMilli { get; init; }

    public Cost Cost { get; init; } = new();

    /// <summary>0 means the unit cannot take ground; a stack captures only if any unit in it can.</summary>
    public int CaptureMultiplier { get; init; } = 1000;

    public List<string> Tags { get; init; } = new();

    /// <summary>Attack scaling while fighting hand to hand (artillery is weak when engaged).</summary>
    public int EngagedAttackPermille { get; init; } = 1000;

    public Dictionary<string, int> TerrainAttackPermille { get; init; } = new();
    public bool Unlocked { get; init; } = true;

    public bool Supports => Tags.Contains("support");
    public bool Builds => Tags.Contains("builder");
}

public sealed class StructureDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Cost Cost { get; init; } = new();
    public int BuildTicks { get; init; } = 300;
    public bool RequiresDeposit { get; init; }

    /// <summary>Scales what the deposit underneath yields (extractor doubles it).</summary>
    public int DepositYieldPermille { get; init; } = 1000;

    /// <summary>Owned tiles within this many hops of the structure count as supplied (depot).</summary>
    public int SupplyRadius { get; init; }

    /// <summary>New recruits may appear here.</summary>
    public bool SpawnSite { get; init; }

    /// <summary>Extra defence in permille for the owner's stacks standing on the tile.</summary>
    public int DefencePermille { get; init; }

    /// <summary>If set, stacks on this tile count as having this armor type (fortified).</summary>
    public string? ArmorOverride { get; init; }

    /// <summary>"keep" or "destroy" when the tile changes hands.</summary>
    public string OnCapture { get; init; } = "keep";

    public bool Unlocked { get; init; } = true;
}

public sealed class TerrainDef
{
    public int AlloyPermille { get; init; } = 1000;
    public long EnergyMilli { get; init; }
    public int DefencePermille { get; init; }
}

/// <summary>Damage type x armor type multipliers, in permille.</summary>
public sealed class CombatTable
{
    public List<string> DamageTypes { get; init; } = new();
    public List<string> ArmorTypes { get; init; } = new();
    public Dictionary<string, Dictionary<string, int>> MatrixPermille { get; init; } = new();

    public int Multiplier(string damage, string armor) =>
        MatrixPermille.TryGetValue(damage, out var row) && row.TryGetValue(armor, out var m) ? m : 1000;
}

/// <summary>Everything data-driven: units, structures, terrain and the combat table.</summary>
public sealed class Catalog
{
    public static readonly string[] KnownRoles = { "infantry", "armor", "artillery", "recon", "engineer" };

    public required IReadOnlyDictionary<string, UnitDef> Units { get; init; }
    public required IReadOnlyDictionary<string, StructureDef> Structures { get; init; }
    public required IReadOnlyDictionary<Terrain, TerrainDef> Terrain { get; init; }
    public required CombatTable Combat { get; init; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static Catalog? _default;
    public static Catalog Default => _default ??= LoadEmbedded();

    private static Catalog LoadEmbedded()
    {
        var asm = typeof(Catalog).Assembly;
        string Read(string name)
        {
            using var s = asm.GetManifestResourceStream(name) ?? throw new FormatException($"Missing embedded data {name}");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        var names = asm.GetManifestResourceNames().Select(n => n.Replace('\\', '/')).ToList();
        var raw = asm.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'));
        string Get(string logical) => Read(raw[logical]);
        return Parse(
            names.Where(n => n.StartsWith("data/units/")).OrderBy(n => n, StringComparer.Ordinal).Select(Get),
            names.Where(n => n.StartsWith("data/structures/")).OrderBy(n => n, StringComparer.Ordinal).Select(Get),
            Get("data/terrain.json"),
            Get("data/combat.json"));
    }

    /// <summary>Build a catalog from JSON text, validating the whole schema. Throws FormatException on any problem.</summary>
    public static Catalog Parse(IEnumerable<string> unitJson, IEnumerable<string> structureJson, string terrainJson, string combatJson)
    {
        T Load<T>(string json, string what)
        {
            try { return JsonSerializer.Deserialize<T>(json, Opts) ?? throw new FormatException($"{what}: empty"); }
            catch (JsonException e) { throw new FormatException($"{what}: {e.Message}"); }
        }

        var combat = Load<CombatTable>(combatJson, "combat.json");
        if (combat.DamageTypes.Count == 0 || combat.ArmorTypes.Count == 0) throw new FormatException("combat.json: needs damage and armor types");
        foreach (var d in combat.DamageTypes)
        foreach (var a in combat.ArmorTypes)
        {
            if (!combat.MatrixPermille.TryGetValue(d, out var row) || !row.TryGetValue(a, out int m))
                throw new FormatException($"combat.json: matrix missing {d} -> {a}");
            if (m < 0) throw new FormatException($"combat.json: negative multiplier {d} -> {a}");
        }

        var units = new SortedDictionary<string, UnitDef>(StringComparer.Ordinal);
        foreach (var json in unitJson)
        {
            var u = Load<UnitDef>(json, "unit");
            if (string.IsNullOrWhiteSpace(u.Id)) throw new FormatException("unit needs an id");
            if (units.ContainsKey(u.Id)) throw new FormatException($"duplicate unit id '{u.Id}'");
            if (string.IsNullOrWhiteSpace(u.Name)) throw new FormatException($"unit {u.Id}: needs a name");
            if (u.Domain != "ground") throw new FormatException($"unit {u.Id}: only the ground domain exists in v1");
            if (u.Roles.Count == 0) throw new FormatException($"unit {u.Id}: needs at least one role");
            foreach (var r in u.Roles)
                if (!KnownRoles.Contains(r)) throw new FormatException($"unit {u.Id}: unknown role '{r}'");
            if (u.Strength < 1) throw new FormatException($"unit {u.Id}: strength must be at least 1");
            if (!combat.DamageTypes.Contains(u.Attack.Type)) throw new FormatException($"unit {u.Id}: unknown damage type '{u.Attack.Type}'");
            if (u.Attack.Permille < 0) throw new FormatException($"unit {u.Id}: negative attack");
            if (!combat.ArmorTypes.Contains(u.Armor)) throw new FormatException($"unit {u.Id}: unknown armor type '{u.Armor}'");
            if (u.SpeedPermille <= 0) throw new FormatException($"unit {u.Id}: speed must be positive");
            if (u.UpkeepMilli < 0 || u.Cost.Alloy < 0 || u.Cost.Energy < 0) throw new FormatException($"unit {u.Id}: negative cost or upkeep");
            if (u.CaptureMultiplier < 0 || u.EngagedAttackPermille < 0) throw new FormatException($"unit {u.Id}: negative multiplier");
            foreach (var kv in u.TerrainAttackPermille)
                if (!Enum.TryParse<Terrain>(kv.Key, true, out _)) throw new FormatException($"unit {u.Id}: unknown terrain '{kv.Key}'");
            units[u.Id] = u;
        }
        if (units.Count == 0) throw new FormatException("no units defined");

        var structures = new SortedDictionary<string, StructureDef>(StringComparer.Ordinal);
        foreach (var json in structureJson)
        {
            var d = Load<StructureDef>(json, "structure");
            if (string.IsNullOrWhiteSpace(d.Id)) throw new FormatException("structure needs an id");
            if (structures.ContainsKey(d.Id)) throw new FormatException($"duplicate structure id '{d.Id}'");
            if (string.IsNullOrWhiteSpace(d.Name)) throw new FormatException($"structure {d.Id}: needs a name");
            if (d.BuildTicks < 1) throw new FormatException($"structure {d.Id}: buildTicks must be positive");
            if (d.Cost.Alloy < 0 || d.Cost.Energy < 0) throw new FormatException($"structure {d.Id}: negative cost");
            if (d.ArmorOverride != null && !combat.ArmorTypes.Contains(d.ArmorOverride)) throw new FormatException($"structure {d.Id}: unknown armor type '{d.ArmorOverride}'");
            if (d.OnCapture is not ("keep" or "destroy")) throw new FormatException($"structure {d.Id}: onCapture must be keep or destroy");
            structures[d.Id] = d;
        }

        var terrainRaw = Load<Dictionary<string, TerrainDef>>(terrainJson, "terrain.json");
        var terrain = new Dictionary<Terrain, TerrainDef>();
        foreach (var kv in terrainRaw)
        {
            if (!Enum.TryParse<Terrain>(kv.Key, true, out var t)) throw new FormatException($"terrain.json: unknown terrain '{kv.Key}'");
            terrain[t] = kv.Value;
        }
        foreach (Terrain t in Enum.GetValues<Terrain>())
            if (!terrain.ContainsKey(t)) throw new FormatException($"terrain.json: missing {t}");

        return new Catalog { Units = units, Structures = structures, Terrain = terrain, Combat = combat };
    }

    /// <summary>A copy of this catalog with extra unit or structure JSON added (used to prove units are data only).</summary>
    public Catalog With(IEnumerable<string>? extraUnits = null, IEnumerable<string>? extraStructures = null)
    {
        var opts = Opts;
        string Ser<T>(T v) => JsonSerializer.Serialize(v, opts);
        return Parse(
            Units.Values.Select(Ser).Concat(extraUnits ?? Array.Empty<string>()),
            Structures.Values.Select(Ser).Concat(extraStructures ?? Array.Empty<string>()),
            JsonSerializer.Serialize(Terrain.ToDictionary(k => k.Key.ToString().ToLowerInvariant(), v => v.Value), opts),
            Ser(Combat));
    }

    // ---- modifier keys --------------------------------------------------------------------

    public static readonly string[] ExactKeys =
    {
        "capture.rate", "move.delay", "move.speed", "yield.alloy", "yield.energy", "yield.influence",
        "upkeep.energy", "vision.range", "fort.max", "opinion.minor", "treaty.acceptance", "build.cost.stack",
    };

    /// <summary>
    /// Keys a modifier may target. Selector suffixes name a unit id, role, tag or "all" for
    /// unit.* keys, a unit id or "all" for build.cost.*, a structure id or "all" for build.time.*,
    /// and an id for unlock.*.
    /// </summary>
    public bool IsKnownKey(string key)
    {
        if (ExactKeys.Contains(key)) return true;
        int cut = key.LastIndexOf('.');
        if (cut <= 0) return false;
        string prefix = key[..cut], sel = key[(cut + 1)..];
        switch (prefix)
        {
            case "unit.attack":
            case "unit.hp":
            case "unit.speed":
                return sel == "all" || Units.ContainsKey(sel) || KnownRoles.Contains(sel) || Units.Values.Any(u => u.Tags.Contains(sel));
            case "build.cost": return sel == "all" || Units.ContainsKey(sel) || Structures.ContainsKey(sel);
            case "build.time": return sel == "all" || Structures.ContainsKey(sel);
            case "unlock.unit": return Units.ContainsKey(sel);
            case "unlock.structure": return Structures.ContainsKey(sel);
            default: return false;
        }
    }

    /// <summary>Problems with a leader's modifiers against this catalog; empty when it is valid.</summary>
    public List<string> Validate(Leader leader) =>
        leader.Modifiers.Where(m => !IsKnownKey(m.Key)).Select(m => $"leader {leader.Id}: unknown modifier key '{m.Key}'").ToList();
}
