namespace Planetfall.Sim.Presentation;

/// <summary>
/// Placeholder audio made from maths: three 16-second music layers that loop without a seam, and short effects.
/// Mono 16-bit at 22.05 kHz. Everything is deterministic, so a test can check it and no audio files ship yet.
/// </summary>
public static class Synth
{
    public const int Rate = 22050;
    public const int LoopSeconds = 16;
    public const int LoopSamples = Rate * LoopSeconds;

    public static readonly string[] MusicLayers = { "calm", "tense", "battle" };
    public static readonly string[] SfxNames = { "click", "recruit", "capture", "lost", "alert", "hit", "victory", "defeat", "gift" };

    // Frequencies are snapped to multiples of 1/16 Hz so every sine finishes whole cycles in 16 s: the loop has no click.
    private static double Snap(double f) => Math.Round(f * LoopSeconds) / LoopSeconds;

    private sealed class Noise
    {
        private uint _s = 2463534242;
        public double Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s / (double)uint.MaxValue * 2 - 1; }
    }

    private static short[] Finish(double[] mix, double peak)
    {
        double max = 1e-9;
        foreach (double v in mix) max = Math.Max(max, Math.Abs(v));
        var pcm = new short[mix.Length];
        for (int i = 0; i < mix.Length; i++) pcm[i] = (short)Math.Round(mix[i] / max * peak * short.MaxValue);
        return pcm;
    }

    public static short[] Music(string layer)
    {
        var mix = new double[LoopSamples];
        var noise = new Noise();
        switch (layer)
        {
            case "calm":
                foreach (double f in new[] { 110.0, 130.8125, 164.8125, 220.0 })
                {
                    double hz = Snap(f);
                    for (int i = 0; i < mix.Length; i++)
                    {
                        double t = (double)i / Rate;
                        double swell = 0.65 + 0.35 * Math.Sin(2 * Math.PI * Snap(0.125) * t + f);
                        mix[i] += swell * (Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(4 * Math.PI * hz * t));
                    }
                }
                break;
            case "tense":
                for (int i = 0; i < mix.Length; i++)
                {
                    double t = (double)i / Rate;
                    double beat = t % 0.5;
                    mix[i] += 0.5 * Math.Sin(2 * Math.PI * Snap(55) * t) + 0.45 * Math.Sin(2 * Math.PI * Snap(58.25) * t);
                    mix[i] += 0.35 * Math.Sin(2 * Math.PI * Snap(440) * t) * Math.Exp(-beat * 18) * (t % 4 < 3.5 ? 1 : 0.2);
                }
                break;
            case "battle":
                double[] bass = { 55, 55, 65.4, 55, 49, 55, 65.4, 82.4 };
                for (int i = 0; i < mix.Length; i++)
                {
                    double t = (double)i / Rate;
                    double beat = t % 0.5, eighth = t % 0.25;
                    double kick = Math.Sin(2 * Math.PI * (45 * beat + 75 * (1 - Math.Exp(-beat * 30)) / 30)) * Math.Exp(-beat * 14);
                    double hat = noise.Next() * Math.Exp(-eighth * 60) * 0.35;
                    double f = Snap(bass[(int)(t / 0.25) % bass.Length]);
                    double saw = 2 * ((f * t) % 1) - 1;
                    mix[i] = kick * 1.0 + hat + saw * 0.22 * Math.Exp(-eighth * 5);
                }
                break;
            default:
                throw new ArgumentException("Unknown music layer " + layer);
        }
        return Finish(mix, 0.8);
    }

    private static double[] Tone(double seconds, Func<double, double> f)
    {
        var d = new double[(int)(seconds * Rate)];
        for (int i = 0; i < d.Length; i++) d[i] = f((double)i / Rate);
        return d;
    }

    private static double Env(double t, double length, double attack = 0.005) => Math.Min(t / attack, 1) * Math.Max(0, 1 - t / length);

    private static double[] Seq(params (double Hz, double Len)[] notes)
    {
        var all = new List<double>();
        foreach (var (hz, len) in notes) all.AddRange(Tone(len, t => Math.Sin(2 * Math.PI * hz * t) * Env(t, len)));
        return all.ToArray();
    }

    public static short[] Sfx(string name)
    {
        var noise = new Noise();
        double[] mix = name switch
        {
            "click" => Tone(0.04, t => Math.Sin(2 * Math.PI * 1400 * t) * Env(t, 0.04, 0.001)),
            "recruit" => Seq((520, 0.08), (780, 0.12)),
            "capture" => Seq((440, 0.07), (554, 0.07), (659, 0.14)),
            "lost" => Seq((330, 0.10), (262, 0.10), (196, 0.20)),
            "alert" => Seq((880, 0.09), (0, 0.04), (880, 0.09)),
            "hit" => Tone(0.09, t => noise.Next() * Math.Exp(-t * 45) + 0.6 * Math.Sin(2 * Math.PI * 110 * t) * Math.Exp(-t * 30)),
            "gift" => Seq((660, 0.06), (990, 0.10)),
            "victory" => Seq((523, 0.14), (659, 0.14), (784, 0.14), (1047, 0.40)),
            "defeat" => Seq((392, 0.22), (349, 0.22), (294, 0.22), (196, 0.55)),
            _ => throw new ArgumentException("Unknown sound " + name),
        };
        return Finish(mix, 0.6);
    }
}
