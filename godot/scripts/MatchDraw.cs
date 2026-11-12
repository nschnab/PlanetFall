using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall;

public partial class Match
{
    private static readonly Color[] OwnerColor =
    {
        new(0.20f, 0.45f, 0.85f), new(0.80f, 0.25f, 0.25f), new(0.85f, 0.65f, 0.15f),
    };
    private static Color ColorOf(int owner) => OwnerColor[Math.Min(owner, OwnerColor.Length - 1)];
    private static readonly Color Neutral = new(0.24f, 0.26f, 0.30f);
    private static readonly Color Unseen = new(0.03f, 0.035f, 0.05f);

    private Fog Fog => _m.Fog;

    public override void _Draw()
    {
        _buttons.Clear();
        DrawRect(new Rect2(0, 0, W, H), new Color(0.07f, 0.08f, 0.10f));
        if (_screen == Screen.Menu) { DrawMainMenu(); return; }
        if (_screen == Screen.Loading) { DrawLoading(); return; }

        DrawWorld();
        DrawHud();
        DrawAlerts();
        DrawSelection();
        DrawMinimap();
        if (_debug) DrawDebug();
        if (_dipOpen) DrawDiplomacy();
        else if (_s.Tick < _toastUntil) DrawString(_font, new Vector2(14, TopBar + 24), _toast, HorizontalAlignment.Left, 1000, 15, Info);
        if (_s.Over) DrawGameOver();
        if (_menuOpen) DrawPauseMenu();
        if (_settingsOpen) DrawSettings();
    }

    /// <summary>The map is drawn once, or three times side by side when zoomed in so the seam is seamless.</summary>
    private void DrawWorld()
    {
        float period = _s.Map.Width * PxPerUnit;
        bool wraps = Wraps;
        float originX = ToScreen(0, 0).X;
        for (int k = wraps ? -1 : 0; k <= (wraps ? 1 : 0); k++)
        {
            float left = originX + k * period;
            if (left + period < 0 || left > W) continue;
            DrawSetTransform(new Vector2(k * period, 0));
            DrawTiles();
            DrawRivers();
            DrawRoutes();
            DrawStacks();
        }
        DrawSetTransform(Vector2.Zero);
    }

    private Vector2[] Poly(int tile)
    {
        var raw = _s.Map.Polygon[tile];
        var pts = new Vector2[raw.Length / 2];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = ToScreen(raw[i * 2] / (float)MapData.Scale, raw[i * 2 + 1] / (float)MapData.Scale);
        return pts;
    }

    private bool OnScreen(Vector2[] pts)
    {
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in pts) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        return maxY >= TopBar && minY <= H;
    }

    private void DrawRivers()
    {
        var blue = new Color(0.30f, 0.60f, 0.95f, 0.9f);
        foreach (long key in _s.Map.Rivers)
        {
            int a = (int)(key / 100_000), b = (int)(key % 100_000);
            if (Fog.Tiles[a] == Sight.Unknown && Fog.Tiles[b] == Sight.Unknown) continue;
            var pa = Poly(a);
            var pb = Poly(b);
            var shared = pa.Where(v => pb.Any(w => v.DistanceTo(w) < 2f)).ToList();
            if (shared.Count >= 2) DrawLine(shared[0], shared[1], blue, 3f * Math.Min(_zoom, 2f));
        }
    }

    private void DrawTiles()
    {
        for (int t = 0; t < _s.Map.TileCount; t++)
        {
            var pts = Poly(t);
            if (!OnScreen(pts)) continue;
            var terrain = _s.Map.Terrain[t];
            bool rock = terrain is Terrain.Water or Terrain.Mountains;       // terrain is public knowledge; ownership is not
            var sight = Fog.Tiles[t];
            int o = Fog.KnownOwner(_s, t);

            var fill = o == -1 ? Neutral : ColorOf(o).Darkened(0.35f);
            switch (terrain)
            {
                case Terrain.Water: fill = new Color(0.07f, 0.16f, 0.30f); break;
                case Terrain.Mountains: fill = new Color(0.30f, 0.30f, 0.32f); break;
                case Terrain.Forest when o == -1: fill = fill.Lerp(new Color(0.10f, 0.28f, 0.14f), 0.5f); break;
                case Terrain.Highlands when o == -1: fill = fill.Lerp(new Color(0.40f, 0.34f, 0.22f), 0.5f); break;
            }
            if (!rock)
            {
                if (sight == Sight.Unknown) fill = Unseen.Lerp(fill, 0.12f);
                else if (sight == Sight.Explored) fill = fill.Lerp(Unseen, 0.45f);
            }
            if (t == _hover && sight != Sight.Unknown) fill = fill.Lightened(0.12f);
            DrawColoredPolygon(pts, fill);
            DrawPolyline(pts.Append(pts[0]).ToArray(), new Color(0, 0, 0, 0.55f), 1.5f);

            if (sight == Sight.Unknown && !rock) continue;
            var c = Centre(t);
            if (_s.Structure[t] is { } built && (sight == Sight.Visible || o == 0))
            {
                var box = new Rect2(c + new Vector2(-17, 7), new Vector2(10, 10));
                DrawRect(box, new Color(0.9f, 0.9f, 0.9f, 0.9f));
                DrawString(_font, box.Position + new Vector2(1, 9), built[..1].ToUpperInvariant(), HorizontalAlignment.Left, -1, 10, Colors.Black);
            }
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
            if (p.Id != 0 && Fog.Tiles[p.Capital] == Sight.Unknown) continue;
            DrawArc(Centre(p.Capital), 20, 0, Mathf.Tau, 24, ColorOf(p.Id).Lightened(0.4f), 2.5f);
        }
    }

    private bool Shown(Stack st) => _debug || Fog.CanSee(_s, st);

    private void DrawRoutes()
    {
        foreach (var st in _s.Stacks)
        {
            if (!Shown(st)) continue;
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
        float span = _s.Map.Width * PxPerUnit;
        if (Math.Abs(a.X - b.X) > span / 2)
        {
            float shift = a.X < b.X ? -span : span;
            DrawLine(a, b + new Vector2(shift, 0), col, w);
            DrawArrowHead(a - new Vector2(shift, 0), b, col, w);
            return;
        }
        DrawArrowHead(a, b, col, w);
    }

    private void DrawArrowHead(Vector2 a, Vector2 b, Color col, float w)
    {
        DrawLine(a, b, col, w);
        var dir = (b - a).Normalized();
        var side = new Vector2(-dir.Y, dir.X);
        DrawColoredPolygon(new[] { b, b - dir * 12 + side * 6, b - dir * 12 - side * 6 }, col);
    }

    private void DrawStacks()
    {
        float zs = Math.Min(Math.Max(PxPerUnit / Math.Min(W / _s.Map.Width, (H - TopBar - 6) / _s.Map.Height), 1f), 2f);
        foreach (var g in _s.Stacks.Where(Shown).GroupBy(x => x.Tile))
        {
            var list = g.OrderBy(x => x.Id).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var st = list[i];
                var pos = Centre(st.Tile) + new Vector2((i - (list.Count - 1) / 2f) * 26f * zs, 0);
                bool sel = st.Id == _selected;
                float r = (11 + Math.Min(st.EffectiveStrength, 20) * 0.35f) * zs;
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

    // ---- HUD ------------------------------------------------------------------------------

    private void DrawHud()
    {
        _buttons.Add((new Rect2(0, 0, W, TopBar), () => { }));
        DrawRect(new Rect2(0, 0, W, TopBar), new Color(0, 0, 0, 0.6f));
        var me = _s.Players[0];
        int mine = _s.OwnedTiles(0), theirs = _s.OwnedTiles(1);
        long upkeep = Simulation.Upkeep(_s, me);
        var inc = Simulation.Income(_s, me);
        string rival = Fog.Tiles[_s.Players[1].Capital] == Sight.Unknown ? "?" : theirs.ToString();
        string text = $"Alloy {me.Supply / 1000} (+{inc.Alloy / 100f:F1}/s)   Energy {me.Energy / 1000} ({(inc.Energy - upkeep) / 100f:+0.0;-0.0}/s){(me.Starved ? " STARVED" : "")}   Influence {me.Influence / 1000}   Tiles {mine} v {rival}   " +
                      $"{_s.Tick / 600}:{_s.Tick / 10 % 60:00}   {(_paused ? "PAUSED" : $"x{_speed}")}";
        DrawString(_font, new Vector2(14, 26), text, HorizontalAlignment.Left, 820, 16, Colors.White);

        for (int p = 0; p < 2; p++)
        {
            int held = _s.CapitalHeld[p];
            if (held <= 0) continue;
            float left = (Rules.CapitulationTicks - held) / 10f;
            string who = p == 0 ? "YOUR capital is held" : "Enemy capital held";
            DrawString(_font, new Vector2(700, 26), $"{who}: {left:F0}s", HorizontalAlignment.Left, 300, 16,
                p == 0 ? new Color(1f, 0.5f, 0.4f) : new Color(0.5f, 1f, 0.5f));
            break;
        }

        int idle = _s.Stacks.Count(x => x.Owner == 0 && x.Idle);
        Button(new Rect2(1196, 6, 76, 28), "Menu", ToggleMenu, new Color(0.12f, 0.2f, 0.3f), 14);
        Button(new Rect2(1080, 6, 110, 28), _dipOpen ? "Close (D)" : "Diplomacy", () => _dipOpen = !_dipOpen, new Color(0.12f, 0.2f, 0.3f), 14);
        Button(new Rect2(1000, 6, 74, 28), idle > 0 ? $"Idle {idle}" : "Idle 0", CycleIdle, new Color(0.15f, 0.25f, 0.18f), 14);
        DrawString(_font, new Vector2(14, 713),
            "Esc menu   Space pause   -/= speed   Tab idle stack   Ctrl+1-9 groups   wheel zoom   arrows / middle-drag pan   Home capital   F5/F9 quick save/load",
            HorizontalAlignment.Left, 1000, 12, new Color(1, 1, 1, 0.5f));
    }

    private void DrawAlerts()
    {
        _alerts.RemoveAll(a => a.Until <= _s.Tick);
        float y = TopBar + 8;
        foreach (var a in _alerts)
        {
            var r = new Rect2(W - 262, y, 254, 24);
            float fade = Mathf.Clamp((a.Until - _s.Tick) / 20f, 0.3f, 1f);
            DrawRect(r, new Color(0, 0, 0, 0.65f * fade));
            DrawRect(new Rect2(r.Position, new Vector2(4, r.Size.Y)), a.Colour with { A = fade });
            DrawString(_font, r.Position + new Vector2(12, 17), a.Text, HorizontalAlignment.Left, 238, 13, a.Colour with { A = fade });
            int tile = a.Tile;
            _buttons.Add((r, () => CentreOn(tile)));
            y += 28;
        }
    }

    private void DrawSelection()
    {
        var st = _selected == -1 ? null : _s.FindStack(_selected);
        if (st == null) return;
        var r = new Rect2(8, H - 112, 330, 92);
        _buttons.Add((r, () => { }));
        DrawRect(r, new Color(0, 0, 0, 0.7f));
        DrawRect(r, new Color(1, 1, 1, 0.3f), false, 1f);
        string units = string.Join(", ", st.Units.Where(u => u.Value > 0).Select(u => $"{u.Value} {u.Key}"));
        DrawString(_font, r.Position + new Vector2(10, 20), $"Stack {st.Id}: {units}", HorizontalAlignment.Left, 310, 14, Colors.White);
        float frac = st.MaxHp <= 0 ? 0 : Mathf.Clamp(st.Hp / (float)st.MaxHp, 0f, 1f);
        DrawRect(new Rect2(r.Position + new Vector2(10, 30), new Vector2(310, 10)), new Color(0.25f, 0.05f, 0.05f));
        DrawRect(new Rect2(r.Position + new Vector2(10, 30), new Vector2(310 * frac, 10)), new Color(0.35f, 0.85f, 0.35f));
        string state = st.Retreating ? "retreating" : st.Target != -1 ? $"moving ({st.Path.Count + 1} hops)" : Simulation.Engaged(_s, st) ? "in combat" : "idle";
        DrawString(_font, r.Position + new Vector2(10, 58), $"Strength {st.EffectiveStrength}   {state}   {(st.Entrench > 0 ? $"dug in +{st.Entrench / 10}%" : "")}",
            HorizontalAlignment.Left, 310, 13, new Color(1, 1, 1, 0.8f));
        DrawString(_font, r.Position + new Vector2(10, 80), "Z depot  X extractor  C fortress (engineer)  S stop",
            HorizontalAlignment.Left, 310, 12, new Color(1, 1, 1, 0.5f));
    }

    // ---- minimap --------------------------------------------------------------------------

    private void DrawMinimap()
    {
        const float mw = 240f;
        float mh = mw * _s.Map.Height / _s.Map.Width;
        var r = new Rect2(W - mw - 10, H - mh - 22, mw, mh);
        DrawRect(r.Grow(3), new Color(0, 0, 0, 0.75f));
        DrawRect(r, Unseen);
        float sx = mw / _s.Map.Width, sy = mh / _s.Map.Height;
        Vector2 P(int t) => r.Position + new Vector2(_s.Map.CentroidX[t] / (float)MapData.Scale * sx, _s.Map.CentroidY[t] / (float)MapData.Scale * sy);

        for (int t = 0; t < _s.Map.TileCount; t++)
        {
            var terrain = _s.Map.Terrain[t];
            var sight = Fog.Tiles[t];
            Color c;
            if (terrain == Terrain.Water) c = new Color(0.07f, 0.16f, 0.30f);
            else if (terrain == Terrain.Mountains) c = new Color(0.30f, 0.30f, 0.32f);
            else if (sight == Sight.Unknown) continue;
            else
            {
                int o = Fog.KnownOwner(_s, t);
                c = o == -1 ? Neutral : ColorOf(o).Darkened(0.2f);
                if (sight == Sight.Explored) c = c.Lerp(Unseen, 0.45f);
            }
            DrawRect(new Rect2(P(t) - new Vector2(2, 2), new Vector2(4, 4)), c);
        }
        foreach (var st in _s.Stacks.Where(Shown))
            DrawRect(new Rect2(P(st.Tile) - new Vector2(2, 2), new Vector2(5, 5)), st.Owner == 0 ? Colors.White : ColorOf(st.Owner).Lightened(0.3f));

        // where the main view is looking
        float vw = Math.Min(W / PxPerUnit, _s.Map.Width), vh = Math.Min((H - TopBar) / PxPerUnit, _s.Map.Height);
        var view = new Rect2(r.Position + new Vector2((_camX - vw / 2) * sx, (_camY - vh / 2) * sy), new Vector2(vw * sx, vh * sy));
        DrawRect(view, Colors.White with { A = 0.8f }, false, 1.5f);

        _buttons.Add((r.Grow(3), () =>
        {
            var local = (_lastMouse - r.Position);
            _camX = local.X / sx;
            _camY = local.Y / sy;
            ClampCam();
        }));
    }

    // ---- debug and end --------------------------------------------------------------------

    /// <summary>F3: what the rival is thinking (goal scores, hopelessness, what each of its stacks was told to do).</summary>
    private void DrawDebug()
    {
        var d = _ai.Debug;
        var lines = new List<string>
        {
            $"RIVAL  {_ai.Personality.Name}   goal {d.Goal}",
            $"expand {d.Scores[0]}  develop {d.Scores[1]}  defend {d.Scores[2]}  attack {d.Scores[3]}",
            $"hopelessness {d.Hopelessness}/{_ai.Personality.PainThreshold}  ({d.HopelessReason})" +
                (d.SurrenderInTicks >= 0 ? $"  surrender in {d.SurrenderInTicks / 10}s" : ""),
            "fog off (all stacks shown)   seed " + _seed,
        };
        DrawRect(new Rect2(8, TopBar + 6, 520, 20 + lines.Count * 18), new Color(0, 0, 0, 0.65f));
        for (int i = 0; i < lines.Count; i++)
            DrawString(_font, new Vector2(16, TopBar + 24 + i * 18), lines[i], HorizontalAlignment.Left, -1, 14, Colors.White);

        foreach (var st in _s.Stacks.Where(x => x.Owner == 1))
        {
            var o = _ai.OrderOf(st.Id);
            if (o == null) continue;
            var c = Centre(st.Tile);
            DrawString(_font, c + new Vector2(-14, -16), o.Role.ToString(), HorizontalAlignment.Left, -1, 11, new Color(1f, 0.9f, 0.4f));
            if (o.Tile >= 0 && o.Tile != st.Tile) DrawLine(c, Centre(o.Tile), new Color(1f, 0.9f, 0.4f, 0.45f), 1.5f);
        }
    }

    private void DrawGameOver()
    {
        _buttons.Add((new Rect2(0, 0, W, H), () => { }));
        DrawRect(new Rect2(0, 0, W, H), new Color(0, 0, 0, 0.6f));
        string msg = _s.Winner == 0 ? "VICTORY" : "DEFEAT";
        string how = _endReason switch
        {
            "surrender" => _s.Winner == 0 ? "The rival surrendered." : "You surrendered.",
            "capital held" => _s.Winner == 0 ? "Their capital fell." : "Your capital fell.",
            _ => "",
        };
        if (how != "") DrawString(_font, new Vector2(0, 440), how, HorizontalAlignment.Center, W, 22, Colors.White);
        DrawString(_font, new Vector2(0, 340), msg, HorizontalAlignment.Center, W, 64,
            _s.Winner == 0 ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.45f, 0.4f));
        DrawString(_font, new Vector2(0, 390), $"{_s.Tick / 600}:{_s.Tick / 10 % 60:00} played.  Press R for a new map.", HorizontalAlignment.Center, W, 22,
            Colors.White);
        Button(new Rect2(W / 2 - 150, 470, 140, 36), "New map", () => NewMatch((ulong)Random.Shared.NextInt64(1, 1_000_000)), new Color(0.15f, 0.3f, 0.2f), 16);
        Button(new Rect2(W / 2 + 10, 470, 140, 36), "Main menu", () => _screen = Screen.Menu, new Color(0.12f, 0.2f, 0.3f), 16);
    }

    private void Button(Rect2 r, string label, Action act, Color? fill = null, int size = 15)
    {
        DrawRect(r, fill ?? new Color(0.25f, 0.12f, 0.12f));
        DrawRect(r, new Color(1, 1, 1, 0.4f), false, 1f);
        DrawString(_font, r.Position + new Vector2(8, r.Size.Y * 0.5f + size * 0.35f), label, HorizontalAlignment.Left, (int)r.Size.X - 8, size, Colors.White);
        _buttons.Add((r, act));
    }
}
