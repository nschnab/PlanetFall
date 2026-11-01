namespace Planetfall.Sim.Core.Modifiers;

public enum ModifierOp
{
    /// <summary>Percent in permille: 500 = +50%, -250 = -25%. All percent mods multiply.</summary>
    Pct,

    /// <summary>Flat amount added after the percent mods.</summary>
    Flat,
}

public readonly record struct Modifier(string Key, ModifierOp Op, int Value);

/// <summary>Anything that can contribute modifiers: a leader today; terrain, treaties or tech later.</summary>
public interface IModifierSource
{
    IReadOnlyList<Modifier> Modifiers { get; }
}

/// <summary>
/// Single place every tunable number is read through:
/// <c>Stat = base x PRODUCT(1 + pct) + SUM(flat)</c>, in integers (permille steps, truncating
/// toward zero) so results never differ between machines. Sources are applied in the order given,
/// so callers must pass them in a stable order.
/// </summary>
public static class StatResolver
{
    public static long Resolve(string key, long baseValue, IEnumerable<IModifierSource> sources)
    {
        long value = baseValue;
        long flat = 0;
        foreach (var source in sources)
        {
            foreach (var m in source.Modifiers)
            {
                if (m.Key != key) continue;
                if (m.Op == ModifierOp.Pct) value = value * (1000 + m.Value) / 1000;
                else flat += m.Value;
            }
        }
        return value + flat;
    }
}
