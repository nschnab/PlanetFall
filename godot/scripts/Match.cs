using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall;

/// <summary>
/// The playable slice. Draws the sim state and turns clicks into Commands; it never mutates the
/// state itself. Player 0 is the human, player 1 is the RivalAi.
/// </summary>
public partial class Match : Node2D
{
    private const float MapScale = 0.94f;
    private const float TopBar = 40f;
    private static readonly Vector2 MapOffset = new((1280f - 1280f * MapScale) / 2f, TopBar);

    private static readonly Color[] OwnerColor =
    {
        new(0.20f, 0.45f, 0.85f), new(0.80f, 0.25f, 0.25f), new(0.85f, 0.65f, 0.15f),
    };
    private static Color ColorOf(int owner) => OwnerColor[Math.Min(owner, OwnerColor.Length - 1)];
    private static readonly Color Neutral = new(0.24f, 0.26f, 0.30f);

    private static readonly Leader Commander =
        Leader.Parse("""{ "id": "commander", "name": "Field Commander" }""");

    /// <summary>Difficulty knob: most stacks the rival may field at once.</summary>
    private const int RivalMaxStacks = 4;

    private GameState _s = null!;
    private RivalAi _ai = null!;
    private readonly List<Command> _pending = new();
    private double _acc;
    private bool _paused;
    private int _speed = 1;
    private int _selected = -1;
    private int _hover = -1;
    private ulong _seed;
    private Font _font = null!;
    private readonly List<(Rect2 rect, Action act)> _buttons = new();

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        Start((ulong)Random.Shared.NextInt64(1, 1_000_000));
        GD.Print($"Planetfall sim match started: seed {_seed}, {_s.Map.TileCount} tiles");   // CI smoke test greps "Planetfall sim "
    }

    private void Start(ulong seed)
    {
        _seed = seed;
        var map = MapGenerator.Generate(seed);
        _s = GameState.NewWithMinors(map, Rules.MinorStrength, Commander, Commander);
        _s.Players[1].MaxStacks = RivalMaxStacks;
        _ai = new RivalAi(1);
        _pending.Clear();
        _selected = -1;
        _acc = 0;
        _paused = false;
        _speed = 1;
    }

    // ---- loop -----------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!_paused && !_s.Over)
        {
            _acc += delta * _speed;
            int steps = 0;
            while (_acc >= Rules.TickMs / 1000.0 && steps++ < 30 && !_s.Over)
            {
                _acc -= Rules.TickMs / 1000.0;
                var cmds = new List<Command>(_pending);
                _pending.Clear();
                cmds.AddRange(_ai.Think(_s));
                Simulation.Step(_s, cmds);
            }
        }
        if (_selected != -1 && _s.FindStack(_selected) == null) _selected = -1;
        QueueRedraw();
    }

    // ---- input ----------------------------------------------------------------------------

    private Vector2 ToMap(Vector2 screen) => (screen - MapOffset) / MapScale;
    private Vector2 ToScreen(float x, float y) => new Vector2(x, y) * MapScale + MapOffset;

    private Vector2 Centre(int tile) =>
        ToScreen(_s.Map.CentroidX[tile] / (float)MapData.Scale, _s.Map.CentroidY[tile] / (float)MapData.Scale);

    private int TileAt(Vector2 screen)
    {
        var p = ToMap(screen);
        if (p.X < 0 || p.Y < 0 || p.X > _s.Map.Width || p.Y > _s.Map.Height) return -1;
        int best = -1;
        float bestD = float.MaxValue;
        for (int t = 0; t < _s.Map.TileCount; t++)
        {
            var c = new Vector2(_s.Map.CentroidX[t], _s.Map.CentroidY[t]) / MapData.Scale;
            float d = c.DistanceSquaredTo(p);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    private List<Stack> MyStacksOn(int tile) =>
        _s.Stacks.Where(x => x.Owner == 0 && x.Tile == tile).OrderBy(x => x.Id).ToList();

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm) { _hover = TileAt(mm.Position); return; }

        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            foreach (var (rect, act) in _buttons)
                if (rect.HasPoint(mb.Position)) { act(); return; }
            if (_s.Over) return;

            int tile = TileAt(mb.Position);
            if (tile == -1) return;

            if (mb.ButtonIndex == MouseButton.Left)
            {
                var here = MyStacksOn(tile);
                if (here.Count > 0)
                {
                    int idx = here.FindIndex(x => x.Id == _selected);
                    _selected = here[(idx + 1) % here.Count].Id;      // click again to cycle
                }
                else if (_selected != -1) IssueMove(tile, mb.ShiftPressed);
                else _selected = -1;
            }
            else if (mb.ButtonIndex == MouseButton.Right && _selected != -1)
            {
                IssueMove(tile, mb.ShiftPressed);
            }
        }
        else if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch (k.Keycode)
            {
                case Key.Space: _paused = !_paused; break;
                case Key.Key1: _speed = 1; break;
                case Key.Key2: _speed = 2; break;
                case Key.Key3: _speed = 4; break;
                case Key.Escape: _selected = -1; break;
                case Key.S: if (_selected != -1) _pending.Add(new StopCommand(0, _selected)); break;
                case Key.R: if (_s.Over) Start((ulong)Random.Shared.NextInt64(1, 1_000_000)); break;
            }
        }
    }

    private void IssueMove(int tile, bool queue) => _pending.Add(new MoveCommand(0, _selected, tile, queue));

    // ---- drawing --------------------------------------------------------------------------

    public override void _Draw()
    {
        _buttons.Clear();
        DrawRect(new Rect2(0, 0, 1280, 720), new Color(0.07f, 0.08f, 0.10f));
        DrawTiles();
        DrawRivers();
        DrawRoutes();
        DrawStacks();
        DrawHud();
        if (_s.Over) DrawGameOver();
    }

    private Vector2[] Poly(int tile)
    {
        var raw = _s.Map.Polygon[tile];
        var pts = new Vector2[raw.Length / 2];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = ToScreen(raw[i * 2] / (float)MapData.Scale, raw[i * 2 + 1] / (float)MapData.Scale);
        return pts;
    }

    private void DrawRivers()
    {
        var blue = new Color(0.30f, 0.60f, 0.95f, 0.9f);
        foreach (long key in _s.Map.Rivers)
        {
            int a = (int)(key / 100_000), b = (int)(key % 100_000);
            var pa = Poly(a);
            var pb = Poly(b);
            var shared = pa.Where(v => pb.Any(w => v.DistanceTo(w) < 2f)).ToList();
            if (shared.Count >= 2) DrawLine(shared[0], shared[1], blue, 3f);
        }
    }

    private void DrawTiles()
    {
        for (int t = 0; t < _s.Map.TileCount; t++)
        {
            int o = _s.TileOwner[t];
            var fill = o == -1 ? Neutral : ColorOf(o).Darkened(0.35f);
            switch (_s.Map.Terrain[t])
            {
                case Terrain.Water: fill = new Color(0.07f, 0.16f, 0.30f); break;
                case Terrain.Mountains: fill = new Color(0.30f, 0.30f, 0.32f); break;
                case Terrain.Forest when o == -1: fill = fill.Lerp(new Color(0.10f, 0.28f, 0.14f), 0.5f); break;
                case Terrain.Highlands when o == -1: fill = fill.Lerp(new Color(0.40f, 0.34f, 0.22f), 0.5f); break;
            }
            if (t == _hover) fill = fill.Lightened(0.12f);
            var pts = Poly(t);
            DrawColoredPolygon(pts, fill);
            DrawPolyline(pts.Append(pts[0]).ToArray(), new Color(0, 0, 0, 0.55f), 1.5f);

            var c = Centre(t);
            if (_s.Map.Chokepoint[t])
                DrawArc(c, 5f, 0f, Mathf.Tau, 12, new Color(1f, 1f, 1f, 0.35f), 1.5f);
            if (_s.Map.Deposit[t])
            {
                DrawColoredPolygon(new[] { c + new Vector2(0, -9), c + new Vector2(8, 0), c + new Vector2(0, 9), c + new Vector2(-8, 0) },
                    _s.Map.DepositKinds[t] switch
                    {
                        DepositKind.Energy => new Color(0.35f, 0.90f, 1f, 0.9f),
                        DepositKind.Relic => new Color(0.85f, 0.45f, 1f, 0.9f),
                        _ => new Color(1f, 0.85f, 0.25f, 0.9f),
                    });
            }
        }
        foreach (var p in _s.Players)
        {
            var c = Centre(p.Capital);
            DrawArc(c, 20, 0, Mathf.Tau, 24, ColorOf(p.Id).Lightened(0.4f), 2.5f);
        }
    }

    private void DrawRoutes()
    {
        foreach (var st in _s.Stacks)
        {
            if (st.Owner != 0 && st.Target == -1) continue;
            var col = ColorOf(st.Owner).Lightened(0.5f);
            var from = Centre(st.Tile);
            if (st.Target != -1)
            {
                DrawArrow(from, Centre(st.Target), col, 3f);
                from = Centre(st.Target);
            }
            if (st.Owner != 0) continue;
            foreach (int t in st.Path)
            {
                var to = Centre(t);
                DrawDashedLine(from, to, col with { A = 0.6f }, 2f, 6f);
                from = to;
            }
        }
    }

    private void DrawArrow(Vector2 a, Vector2 b, Color col, float w)
    {
        // The map wraps east-west: across the seam, draw the arrow leaving one edge and entering the other.
        float span = 1280f * MapScale;
        if (Math.Abs(a.X - b.X) > span / 2)
        {
            float shift = a.X < b.X ? -span : span;
            DrawLine(a, b + new Vector2(shift, 0), col, w);
            DrawArrow(a - new Vector2(shift, 0), b, col, w);
            return;
        }
        DrawLine(a, b, col, w);
        var dir = (b - a).Normalized();
        var side = new Vector2(-dir.Y, dir.X);
        DrawColoredPolygon(new[] { b, b - dir * 12 + side * 6, b - dir * 12 - side * 6 }, col);
    }

    private void DrawStacks()
    {
        var byTile = _s.Stacks.GroupBy(x => x.Tile);
        foreach (var g in byTile)
        {
            var list = g.OrderBy(x => x.Id).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var st = list[i];
                var pos = Centre(st.Tile) + new Vector2((i - (list.Count - 1) / 2f) * 26f, 0);
                bool sel = st.Id == _selected;
                float r = 11 + Math.Min(st.EffectiveStrength, 20) * 0.35f;
                DrawCircle(pos, r, ColorOf(st.Owner));
                DrawArc(pos, r, 0, Mathf.Tau, 24, sel ? Colors.White : Colors.Black, sel ? 3f : 1.5f);
                if (st.Target != -1)
                {
                    long need = Simulation.HopNeeded(_s, st);
                    float frac = Mathf.Clamp(st.Timer / (float)need, 0f, 1f);
                    DrawArc(pos, r + 5, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * frac, 24, Colors.White, 2.5f);
                }
                if (st.Entrench > 0)
                    DrawString(_font, pos + new Vector2(-14, r + 14), $"+{st.Entrench / 10}% def", HorizontalAlignment.Left, -1, 11, new Color(0.7f, 1f, 0.7f));
                var txt = st.EffectiveStrength.ToString();
                var sz = _font.GetStringSize(txt, HorizontalAlignment.Left, -1, 14);
                DrawString(_font, pos + new Vector2(-sz.X / 2, 5), txt, HorizontalAlignment.Left, -1, 14, Colors.White);
            }
        }
    }

    private void DrawHud()
    {
        DrawRect(new Rect2(0, 0, 1280, TopBar), new Color(0, 0, 0, 0.6f));
        var me = _s.Players[0];
        int mine = _s.OwnedTiles(0), theirs = _s.OwnedTiles(1);
        string text = $"Alloy {me.Supply / 1000}   Tiles {mine} v {theirs}   Time {_s.Tick / 600}:{_s.Tick / 10 % 60:00}   " +
                      $"{(_paused ? "PAUSED" : $"x{_speed}")}   Seed {_seed}";
        DrawString(_font, new Vector2(14, 26), text, HorizontalAlignment.Left, -1, 17, Colors.White);

        // Capital pressure
        for (int p = 0; p < 2; p++)
        {
            int held = _s.CapitalHeld[p];
            if (held <= 0) continue;
            float left = (Rules.CapitulationTicks - held) / 10f;
            string who = p == 0 ? "YOUR capital is held" : "Enemy capital held";
            DrawString(_font, new Vector2(640, 26 + p * 0), $"{who}: {left:F0}s to capitulation", HorizontalAlignment.Left, -1, 17,
                p == 0 ? new Color(1f, 0.5f, 0.4f) : new Color(0.5f, 1f, 0.5f));
            break;
        }

        Button(new Rect2(1180, 6, 90, 28), "Surrender", () => _pending.Add(new SurrenderCommand(0)));
        DrawString(_font, new Vector2(14, 712),
            "L-click: select/cycle, then L/R-click tile to move (Shift = queue)   S stop   Space pause   1/2/3 speed   Esc deselect",
            HorizontalAlignment.Left, -1, 13, new Color(1, 1, 1, 0.55f));
    }

    private void Button(Rect2 r, string label, Action act)
    {
        DrawRect(r, new Color(0.25f, 0.12f, 0.12f));
        DrawRect(r, new Color(1, 1, 1, 0.4f), false, 1f);
        DrawString(_font, r.Position + new Vector2(10, 19), label, HorizontalAlignment.Left, -1, 15, Colors.White);
        _buttons.Add((r, act));
    }

    private void DrawGameOver()
    {
        DrawRect(new Rect2(0, 0, 1280, 720), new Color(0, 0, 0, 0.6f));
        string msg = _s.Winner == 0 ? "VICTORY" : "DEFEAT";
        DrawString(_font, new Vector2(0, 340), msg, HorizontalAlignment.Center, 1280, 64,
            _s.Winner == 0 ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.45f, 0.4f));
        DrawString(_font, new Vector2(0, 390), $"{_s.Tick / 600}:{_s.Tick / 10 % 60:00} played.  Press R for a new map.", HorizontalAlignment.Center, 1280, 22,
            Colors.White);
    }
}
