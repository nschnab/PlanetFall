using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Presentation;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall;

/// <summary>
/// The playable slice. Draws the sim state and turns clicks into Commands; it never mutates the state itself.
/// Player 0 is the human, player 1 is the RivalAi. Split over several files: input and flow here,
/// <c>MatchDraw</c> for the map and HUD, <c>MatchMenus</c> for menus, settings and saves, <c>MatchDiplomacy</c> for the panel.
/// </summary>
public partial class Match : Node2D
{
    private const float W = 1280f, H = 720f, TopBar = 40f;
    private const int AutosaveEveryTicks = 3000;      // five minutes
    private const int AutosaveSlots = 3;

    private enum Screen { Menu, Playing, Loading, Sheet }

    private Screen _screen = Screen.Menu;
    private SoloMatch _m = null!;
    private GameState _s => _m.State;
    private RivalAi _ai => _m.Ai;

    private bool _debug;
    private string _endReason = "";
    private bool _dipOpen;
    private int _dipSel = -1;
    private readonly List<string> _log = new();
    private string _toast = "";
    private int _toastUntil;

    private readonly List<Command> _pending = new();
    private double _acc;
    private bool _paused;
    private int _speed = 1;
    private int _selected = -1;
    private int _hover = -1;
    private ulong _seed;
    private Font _font = null!;
    private readonly List<(Rect2 rect, Action act)> _buttons = new();

    private Bindings _keys = new();
    private Vector2 _lastMouse;
    private bool _dragPan;
    private readonly List<int>[] _groups = Enumerable.Range(0, 10).Select(_ => new List<int>()).ToArray();
    private int _lastGroup = -1;
    private ulong _lastGroupTapMs;
    private int _nextAutosave = AutosaveEveryTicks;
    private int _autosaveRing;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        LoadBindings();
        LoadSettings();
        InitAudio();
        _seedText = "";
        string auto = System.Environment.GetEnvironmentVariable("PLANETFALL_AUTOSTART") ?? "";
        if (DisplayServer.GetName() == "headless" || auto != "")      // CI smoke test and scripted runs skip the menu
        {
            NewMatch(ulong.TryParse(auto, out var fixedSeed) && fixedSeed > 1 ? fixedSeed : (ulong)Random.Shared.NextInt64(1, 1_000_000));
            if (System.Environment.GetEnvironmentVariable("PLANETFALL_SELFTEST") == "1") _selfTest = 1;
            if (int.TryParse(System.Environment.GetEnvironmentVariable("PLANETFALL_SPEED"), out int sp)) _speed = Math.Clamp(sp, 1, 40);
            GD.Print($"Planetfall sim match started: seed {_seed}, {_s.Map.TileCount} tiles");   // CI greps "Planetfall sim "
        }
        else if (System.Environment.GetEnvironmentVariable("PLANETFALL_SHEET") == "1") _screen = Screen.Sheet;
        else GD.Print("Planetfall sim menu ready");
    }

    private void NewMatch(ulong seed) => BeginMatch(new SoloMatch(seed));

    private void BeginMatch(SoloMatch m)
    {
        _m = m;
        _seed = m.Seed;
        _screen = Screen.Playing;
        _endReason = "";
        _dipSel = -1;
        _dipOpen = false;
        _menuOpen = false;
        _settingsOpen = false;
        _log.Clear();
        _toast = "";
        _pending.Clear();
        _selected = -1;
        foreach (var g in _groups) g.Clear();
        _acc = 0;
        _paused = false;
        _speed = 1;
        _alerts.Clear();
        _engaged.Clear();
        _seenEnemy.Clear();
        _nextAutosave = (m.State.Tick / AutosaveEveryTicks + 1) * AutosaveEveryTicks;
        _zoom = 1f;
        _flash.Clear();
        _music = new MusicDirector();
        BuildEdges();
        CentreOn(m.State.Players[0].Capital);
    }

    // ---- loop -----------------------------------------------------------------------------

    // Scripted check of the real save/load path inside Godot (PLANETFALL_SELFTEST=1): save at 1 minute, reload, compare hashes.
    private int _selfTest;          // 0 off/idle, 1 waiting to save, 2 waiting for load
    private ulong _selfTestHash;

    private void SelfTestStep()
    {
        if (_selfTest == 1 && _screen == Screen.Playing && _s.Tick >= 600)
        {
            _paused = true;
            QuickSave();
            _selfTestHash = _s.Hash();
            _selfTest = 2;
            QuickLoad();
        }
        else if (_selfTest == 2 && _screen == Screen.Playing && _loadTask == null && _m.State.Tick >= 600 && _paused == false)
        {
            GD.Print(_s.Hash() == _selfTestHash ? "Planetfall selftest save/load OK" : "Planetfall selftest save/load FAIL");
            GetTree().Quit();
        }
        else if (_selfTest == 2 && _screen == Screen.Menu)
        {
            GD.Print("Planetfall selftest save/load FAIL: " + _menuNote);
            GetTree().Quit();
        }
    }

    public override void _Process(double delta)
    {
        PollLoad();
        if (_selfTest != 0) SelfTestStep();
        UpdateAudio(delta);
        if (_screen == Screen.Playing)
        {
            bool frozen = _paused || _menuOpen || _settingsOpen;
            if (!frozen && !_s.Over)
            {
                _acc += delta * _speed;
                int steps = 0;
                while (_acc >= Rules.TickMs / 1000.0 && steps++ < 30 && !_s.Over)
                {
                    _acc -= Rules.TickMs / 1000.0;
                    var cmds = _pending.ToList();
                    _pending.Clear();
                    foreach (var ev in _m.Step(cmds)) OnEvent(ev);
                    ScanAlerts();
                    SenseMusic();
                    if (_s.Tick >= _nextAutosave) { Autosave(); _nextAutosave += AutosaveEveryTicks; }
                }
            }
            PanWithKeys(delta);
            if (_selected != -1 && _s.FindStack(_selected) == null) _selected = -1;
        }
        QueueRedraw();
    }

    private void PanWithKeys(double delta)
    {
        if (_settingsOpen || _menuOpen) return;
        Vector2 d = Vector2.Zero;
        if (Held("pan_left")) d.X -= 1;
        if (Held("pan_right")) d.X += 1;
        if (Held("pan_up")) d.Y -= 1;
        if (Held("pan_down")) d.Y += 1;
        if (d == Vector2.Zero) return;
        float speed = 700f / PxPerUnit;       // screen pixels per second, in map units
        _camX += d.X * speed * (float)delta;
        _camY += d.Y * speed * (float)delta;
        ClampCam();
    }

    private bool Held(string action)
    {
        var key = OS.FindKeycodeFromString(_keys.KeyOf(action));
        return key != Key.None && Input.IsKeyPressed(key);
    }

    // ---- camera ---------------------------------------------------------------------------

    private float _zoom = 1f, _camX, _camY;
    private const float MaxZoom = 5f;

    /// <summary>Screen pixels per map unit.</summary>
    private float PxPerUnit => Math.Min(W / _s.Map.Width, (H - TopBar - 6) / _s.Map.Height) * _zoom;
    private float ViewCentreY => TopBar + (H - TopBar) / 2f;
    private bool Wraps => W / PxPerUnit < _s.Map.Width - 0.5f;

    private Vector2 ToScreen(float x, float y) => new((x - _camX) * PxPerUnit + W / 2f, (y - _camY) * PxPerUnit + ViewCentreY);

    private Vector2 ToMap(Vector2 screen)
    {
        float x = (screen.X - W / 2f) / PxPerUnit + _camX;
        float wd = _s.Map.Width;
        x = ((x % wd) + wd) % wd;
        return new Vector2(x, (screen.Y - ViewCentreY) / PxPerUnit + _camY);
    }

    private void ClampCam()
    {
        float halfH = (H - TopBar) / 2f / PxPerUnit;
        _camY = halfH * 2 >= _s.Map.Height ? _s.Map.Height / 2f : Mathf.Clamp(_camY, halfH, _s.Map.Height - halfH);
        float wd = _s.Map.Width;
        _camX = Wraps ? ((_camX % wd) + wd) % wd : wd / 2f;
    }

    private void CentreOn(int tile)
    {
        _camX = _s.Map.CentroidX[tile] / (float)Planetfall.Sim.MapGen.MapData.Scale;
        _camY = _s.Map.CentroidY[tile] / (float)Planetfall.Sim.MapGen.MapData.Scale;
        ClampCam();
    }

    private void ZoomAt(Vector2 screen, float factor)
    {
        var before = ToMap(screen);
        _zoom = Mathf.Clamp(_zoom * factor, 1f, MaxZoom);
        // keep the map point under the cursor fixed (taking the nearest wrapped copy)
        var after = ToMap(screen);
        float dx = before.X - after.X;
        float wd = _s.Map.Width;
        if (dx > wd / 2) dx -= wd; else if (dx < -wd / 2) dx += wd;
        _camX += dx;
        _camY += before.Y - after.Y;
        ClampCam();
    }

    // ---- picking --------------------------------------------------------------------------

    private Vector2 Centre(int tile) =>
        ToScreen(_s.Map.CentroidX[tile] / (float)Planetfall.Sim.MapGen.MapData.Scale, _s.Map.CentroidY[tile] / (float)Planetfall.Sim.MapGen.MapData.Scale);

    private int TileAt(Vector2 screen)
    {
        if (screen.Y < TopBar) return -1;
        var p = ToMap(screen);
        if (p.Y < 0 || p.Y > _s.Map.Height) return -1;
        int best = -1;
        float bestD = float.MaxValue;
        float wd = _s.Map.Width;
        for (int t = 0; t < _s.Map.TileCount; t++)
        {
            float dx = Math.Abs(_s.Map.CentroidX[t] / (float)Planetfall.Sim.MapGen.MapData.Scale - p.X);
            dx = Math.Min(dx, wd - dx);
            float dy = _s.Map.CentroidY[t] / (float)Planetfall.Sim.MapGen.MapData.Scale - p.Y;
            float d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    private List<Stack> MyStacksOn(int tile) =>
        _s.Stacks.Where(x => x.Owner == 0 && x.Tile == tile).OrderBy(x => x.Id).ToList();

    // ---- input ----------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (_screen == Screen.Loading) return;
        if (e is InputEventMouseMotion mmv) _lastMouse = mmv.Position;
        if (e is InputEventMouseButton mbp) _lastMouse = mbp.Position;

        if (e is InputEventKey kk && kk.Pressed && !kk.Echo && _rebinding != "") { CaptureRebind(kk); return; }

        if (_screen == Screen.Menu)
        {
            if (e is InputEventMouseButton m0 && m0.Pressed && m0.ButtonIndex == MouseButton.Left) ClickButtons(m0.Position);
            else if (e is InputEventKey mk && mk.Pressed && !mk.Echo) MenuKey(mk);
            return;
        }

        if (e is InputEventMouseMotion mm)
        {
            if (_dragPan && !_menuOpen && !_settingsOpen)
            {
                _camX -= mm.Relative.X / PxPerUnit;
                _camY -= mm.Relative.Y / PxPerUnit;
                ClampCam();
            }
            _hover = TileAt(mm.Position);
            return;
        }
        if (e is InputEventMagnifyGesture mg && !_menuOpen && !_settingsOpen) { ZoomAt(mg.Position, mg.Factor); return; }
        if (e is InputEventPanGesture pg && !_menuOpen && !_settingsOpen)
        {
            _camX += pg.Delta.X * 6f / PxPerUnit;
            _camY += pg.Delta.Y * 6f / PxPerUnit;
            ClampCam();
            return;
        }

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Middle) { _dragPan = mb.Pressed; return; }
            if (!mb.Pressed) return;
            if (_menuOpen || _settingsOpen) { ClickButtons(mb.Position); return; }
            if (mb.ButtonIndex == MouseButton.WheelUp) { ZoomAt(mb.Position, 1.15f); return; }
            if (mb.ButtonIndex == MouseButton.WheelDown) { ZoomAt(mb.Position, 1f / 1.15f); return; }
            if (ClickButtons(mb.Position)) return;
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
            OnKey(k);
        }
    }

    /// <summary>Topmost button under the point wins (the last one drawn).</summary>
    private bool ClickButtons(Vector2 pos)
    {
        for (int i = _buttons.Count - 1; i >= 0; i--)
            if (_buttons[i].rect.HasPoint(pos)) { Sfx("click"); _buttons[i].act(); return true; }
        return false;
    }

    private void OnKey(InputEventKey k)
    {
        // Control groups: Ctrl/Cmd+digit stores the selection, digit recalls it, a second tap centres on it.
        if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9 && !_menuOpen && !_settingsOpen)
        {
            int g = (int)(k.Keycode - Key.Key1) + 1;
            if (k.IsCommandOrControlPressed())
            {
                _groups[g].Clear();
                if (_selected != -1) _groups[g].Add(_selected);
                Note($"Group {g}: {_groups[g].Count} stack(s)");
            }
            else RecallGroup(g);
            return;
        }

        if (k.Keycode == Key.R && _s.Over && !_menuOpen && !_settingsOpen) { NewMatch((ulong)Random.Shared.NextInt64(1, 1_000_000)); return; }

        string name = OS.GetKeycodeString(k.Keycode);
        var action = _keys.ActionOf(name);
        if (action == null) return;

        if (action == "menu") { ToggleMenu(); return; }
        if (action == "clear")
        {
            if (_settingsOpen) _settingsOpen = false;
            else if (_menuOpen) _menuOpen = false;
            else if (_dipOpen) _dipOpen = false;
            else if (_selected != -1) _selected = -1;
            else ToggleMenu();
            return;
        }
        if (_menuOpen || _settingsOpen) return;

        switch (action)
        {
            case "pause": _paused = !_paused; break;
            case "slower": _speed = _speed >= 4 ? 2 : 1; break;
            case "faster": _speed = _speed <= 1 ? 2 : 4; break;
            case "stop": if (_selected != -1) _pending.Add(new StopCommand(0, _selected)); break;
            case "idle": CycleIdle(); break;
            case "recruit_infantry": _pending.Add(new RecruitCommand(0, "infantry")); break;
            case "recruit_armor": _pending.Add(new RecruitCommand(0, "armor")); break;
            case "recruit_artillery": _pending.Add(new RecruitCommand(0, "artillery")); break;
            case "recruit_recon": _pending.Add(new RecruitCommand(0, "recon")); break;
            case "recruit_engineer": _pending.Add(new RecruitCommand(0, "engineer")); break;
            case "build_depot": if (_selected != -1) _pending.Add(new BuildCommand(0, _selected, "depot")); break;
            case "build_extractor": if (_selected != -1) _pending.Add(new BuildCommand(0, _selected, "extractor")); break;
            case "build_fortress": if (_selected != -1) _pending.Add(new BuildCommand(0, _selected, "fortress")); break;
            case "diplomacy": _dipOpen = !_dipOpen; break;
            case "debug": _debug = !_debug; break;
            case "home": CentreOn(_s.Players[0].Capital); break;
            case "save": QuickSave(); break;
            case "load": QuickLoad(); break;
        }
    }

    private void RecallGroup(int g)
    {
        var alive = _groups[g].Where(id => _s.FindStack(id) != null).ToList();
        _groups[g] = alive;
        if (alive.Count == 0) return;
        ulong now = Time.GetTicksMsec();
        _selected = alive[0];
        if (_lastGroup == g && now - _lastGroupTapMs < 400) CentreOn(_s.FindStack(_selected)!.Tile);
        _lastGroup = g;
        _lastGroupTapMs = now;
    }

    private void CycleIdle()
    {
        var idle = _s.Stacks.Where(x => x.Owner == 0 && x.Idle).OrderBy(x => x.Id).ToList();
        if (idle.Count == 0) { Note("No idle stacks."); return; }
        int idx = idle.FindIndex(x => x.Id == _selected);
        var pick = idle[(idx + 1) % idle.Count];
        _selected = pick.Id;
        CentreOn(pick.Tile);
    }

    private void IssueMove(int tile, bool queue) => _pending.Add(new MoveCommand(0, _selected, tile, queue));
}
