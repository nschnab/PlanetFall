namespace Planetfall.Sim.Presentation;

/// <summary>A unit glyph: line segments in a -1..1 square, NATO-symbol style. Shape alone tells the roles apart, never colour.</summary>
public sealed record Glyph(string Role, (double X1, double Y1, double X2, double Y2)[] Lines);

public static class Glyphs
{
    private static (double, double, double, double)[] Ellipse(double rx, double ry, int n = 16) =>
        Enumerable.Range(0, n).Select(i =>
        {
            double a = i * 2 * Math.PI / n, b = (i + 1) * 2 * Math.PI / n;
            return (rx * Math.Cos(a), ry * Math.Sin(a), rx * Math.Cos(b), ry * Math.Sin(b));
        }).ToArray();

    public static readonly Glyph[] All =
    {
        new("infantry", new[] { (-0.8, -0.55, 0.8, 0.55), (-0.8, 0.55, 0.8, -0.55) }),                                  // X
        new("armor", Ellipse(0.8, 0.5)),                                                                                // tracked-vehicle oval
        new("artillery", Ellipse(0.22, 0.22, 10)),                                                                      // a single dot
        new("recon", new[] { (-0.7, 0.55, 0.7, 0.55), (0.7, 0.55, 0.0, -0.65), (0.0, -0.65, -0.7, 0.55) }),                  // triangle
        new("engineer", new[] { (-0.8, 0.5, -0.8, -0.1), (-0.8, -0.1, 0.8, -0.1), (0.8, -0.1, 0.8, 0.5), (-0.3, -0.1, -0.3, 0.5), (0.3, -0.1, 0.3, 0.5) }),   // bridge
    };

    public static Glyph Of(string role) => All.FirstOrDefault(g => g.Role == role) ?? All[0];

    /// <summary>Draws the glyph into a size x size bitmap with thick lines, for the distinctness check.</summary>
    public static bool[,] Rasterize(Glyph g, int size = 32, double thickness = 0.14)
    {
        var bmp = new bool[size, size];
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                double x = (px + 0.5) / size * 2 - 1, y = (py + 0.5) / size * 2 - 1;
                foreach (var (x1, y1, x2, y2) in g.Lines)
                {
                    double dx = x2 - x1, dy = y2 - y1, len2 = dx * dx + dy * dy;
                    double t = len2 == 0 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / len2, 0, 1);
                    double ex = x1 + t * dx - x, ey = y1 + t * dy - y;
                    if (ex * ex + ey * ey <= thickness * thickness) { bmp[px, py] = true; break; }
                }
            }
        return bmp;
    }

    /// <summary>Fraction of inked pixels (in either glyph) where the two disagree: 0 identical, 1 disjoint.</summary>
    public static double Distance(Glyph a, Glyph b)
    {
        var ba = Rasterize(a);
        var bb = Rasterize(b);
        int either = 0, differ = 0;
        for (int y = 0; y < ba.GetLength(1); y++)
            for (int x = 0; x < ba.GetLength(0); x++)
            {
                if (!ba[x, y] && !bb[x, y]) continue;
                either++;
                if (ba[x, y] != bb[x, y]) differ++;
            }
        return either == 0 ? 0 : (double)differ / either;
    }

    /// <summary>The unit type with the most members decides a stack's glyph.</summary>
    public static string DominantRole(IEnumerable<KeyValuePair<string, int>> units) =>
        units.Where(u => u.Value > 0).OrderByDescending(u => u.Value).ThenBy(u => u.Key, StringComparer.Ordinal).Select(u => u.Key).FirstOrDefault() ?? "infantry";
}
