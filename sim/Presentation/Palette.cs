namespace Planetfall.Sim.Presentation;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public Rgb Darkened(double amount) => new((byte)(R * (1 - amount)), (byte)(G * (1 - amount)), (byte)(B * (1 - amount)));
}

public enum Cvd { Normal, Protanopia, Deuteranopia, Tritanopia }

/// <summary>
/// Player colours from the Okabe-Ito set, chosen to stay apart for the common kinds of colour blindness,
/// plus the colour-vision simulation and the colour distance the tests use to prove it.
/// </summary>
public static class Palette
{
    public static readonly Rgb Blue = new(0, 114, 178);
    public static readonly Rgb Vermillion = new(213, 94, 0);
    public static readonly Rgb Yellow = new(240, 228, 66);
    public static readonly Rgb Green = new(0, 158, 115);
    public static readonly Rgb Sky = new(86, 180, 233);
    public static readonly Rgb Purple = new(204, 121, 167);
    public static readonly Rgb Orange = new(230, 159, 0);

    /// <summary>Majors by player id; every minor power shares the third entry.</summary>
    public static readonly Rgb[] Players = { Blue, Vermillion, Yellow, Green, Sky, Purple, Orange };
    public static readonly Rgb Neutral = new(40, 43, 50);

    public static Rgb OwnerColor(int owner) => Players[Math.Clamp(owner, 0, 2)];      // 0 human, 1 rival, 2+ every minor

    /// <summary>How a tile of this owner is filled on the map: a darker shade of the owner colour, so glyphs on top stand out.</summary>
    public static Rgb TileFill(int owner) => owner < 0 ? Neutral : OwnerColor(owner).Darkened(0.35);

    // Linear-RGB simulation matrices (Vienot, Brettel and Mollon 1999 for protan and deutan; Machado 2009 for tritan, severity 1).
    private static readonly double[][] Protan = { new[] { 0.152286, 1.052583, -0.204868 }, new[] { 0.114503, 0.786281, 0.099216 }, new[] { -0.003882, -0.048116, 1.051998 } };
    private static readonly double[][] Deutan = { new[] { 0.367322, 0.860646, -0.227968 }, new[] { 0.280085, 0.672501, 0.047413 }, new[] { -0.011820, 0.042940, 0.968881 } };
    private static readonly double[][] Tritan = { new[] { 1.255528, -0.076749, -0.178779 }, new[] { -0.078411, 0.930809, 0.147602 }, new[] { 0.004733, 0.691367, 0.303900 } };

    private static double ToLinear(byte v) { double c = v / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    private static byte ToByte(double l) { l = Math.Clamp(l, 0, 1); double c = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055; return (byte)Math.Round(c * 255); }

    public static Rgb Simulate(Rgb c, Cvd kind)
    {
        if (kind == Cvd.Normal) return c;
        var m = kind switch { Cvd.Protanopia => Protan, Cvd.Deuteranopia => Deutan, _ => Tritan };
        double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
        return new Rgb(ToByte(m[0][0] * r + m[0][1] * g + m[0][2] * b), ToByte(m[1][0] * r + m[1][1] * g + m[1][2] * b), ToByte(m[2][0] * r + m[2][1] * g + m[2][2] * b));
    }

    public static (double L, double A, double B) Lab(Rgb c)
    {
        double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * b, z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIE76 colour difference; about 2.3 is just noticeable, 20 or more is clearly different.</summary>
    public static double DeltaE(Rgb a, Rgb b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }
}
