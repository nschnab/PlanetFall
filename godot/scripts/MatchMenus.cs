using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Planetfall.Sim.Ai;
using Planetfall.Sim.Core;

namespace Planetfall;

public partial class Match
{
    private bool _menuOpen, _settingsOpen;
    private string _rebinding = "";
    private string _menuNote = "";
    private string _seedText = "";

    private Task<SoloMatch>? _loadTask;
    private volatile int _loadDone, _loadTotal;
    private string _loadError = "";
    private Screen _loadReturn = Screen.Menu;

    // ---- files ----------------------------------------------------------------------------

    private static string SaveDir
    {
        get
        {
            var dir = ProjectSettings.GlobalizePath("user://saves");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SlotPath(string slot) => Path.Combine(SaveDir, slot + ".json");
    private static string BindingsPath => ProjectSettings.GlobalizePath("user://bindings.json");

    private void LoadBindings()
    {
        try { _keys = Bindings.FromJson(File.Exists(BindingsPath) ? File.ReadAllText(BindingsPath) : null); }
        catch (Exception) { _keys = new Bindings(); }
    }

    private void SaveBindings()
    {
        try { File.WriteAllText(BindingsPath, _keys.ToJson()); }
        catch (Exception e) { GD.PushWarning("Could not save key bindings: " + e.Message); }
    }

    private void WriteSlot(string slot, string label)
    {
        try
        {
            SoloMatch.WriteFile(SlotPath(slot), _m.ToSave());
            Note($"{label} saved at {_s.Tick / 600}:{_s.Tick / 10 % 60:00}");
        }
        catch (Exception e)
        {
            Note("Save failed: " + e.Message);
        }
    }

    private void QuickSave() { if (_screen == Screen.Playing) WriteSlot("quick", "Game"); }

    private void Autosave()
    {
        WriteSlot($"auto{_autosaveRing}", "Autosave");
        _autosaveRing = (_autosaveRing + 1) % AutosaveSlots;
    }

    private void QuickLoad() => StartLoad(SlotPath("quick"));

    private IEnumerable<string> SavedFiles() =>
        new[] { "quick" }.Concat(Enumerable.Range(0, AutosaveSlots).Select(i => $"auto{i}"))
            .Select(SlotPath).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc);

    private void StartLoad(string path)
    {
        if (_loadTask != null) return;
        if (!File.Exists(path))
        {
            _menuNote = "No saved game there.";
            if (_screen == Screen.Playing) Note(_menuNote);
            return;
        }
        _loadReturn = _screen;
        _loadError = "";
        _loadDone = 0;
        _loadTotal = 1;
        _screen = Screen.Loading;
        _loadTask = Task.Run(() => SoloMatch.Load(SoloMatch.ReadFile(path), (done, total) => { _loadDone = done; _loadTotal = total; }));
    }

    private void PollLoad()
    {
        if (_loadTask == null || !_loadTask.IsCompleted) return;
        var task = _loadTask;
        _loadTask = null;
        if (task.IsFaulted)
        {
            var ex = task.Exception!.GetBaseException();
            _loadError = ex is SaveException ? ex.Message : "Could not read the save: " + ex.Message;
            _menuNote = _loadError;
            _screen = _loadReturn;
            if (_screen == Screen.Playing) Note(_loadError);
            return;
        }
        BeginMatch(task.Result);
        Note("Game loaded.");
    }

    // ---- menus ----------------------------------------------------------------------------

    private void ToggleMenu()
    {
        if (_screen != Screen.Playing) return;
        _settingsOpen = false;
        _menuOpen = !_menuOpen;
    }

    private void MenuKey(InputEventKey k)
    {
        if (_settingsOpen)
        {
            if (k.Keycode == Key.Escape) _settingsOpen = false;
            return;
        }
        if (k.Keycode >= Key.Key0 && k.Keycode <= Key.Key9 && _seedText.Length < 12) _seedText += (char)('0' + (k.Keycode - Key.Key0));
        else if (k.Keycode == Key.Backspace && _seedText.Length > 0) _seedText = _seedText[..^1];
        else if (k.Keycode is Key.Enter or Key.KpEnter) StartFromMenu();
    }

    private void StartFromMenu()
    {
        ulong seed = ulong.TryParse(_seedText, out var parsed) && parsed > 0 ? parsed : (ulong)Random.Shared.NextInt64(1, 1_000_000);
        _menuNote = "";
        NewMatch(seed);
    }

    private void Panel(Rect2 r)
    {
        _buttons.Add((r, () => { }));
        DrawRect(r, new Color(0.04f, 0.05f, 0.08f, 0.96f));
        DrawRect(r, new Color(1, 1, 1, 0.3f), false, 1f);
    }

    private void DrawMainMenu()
    {
        DrawString(_font, new Vector2(0, 120), "PLANETFALL", HorizontalAlignment.Center, W, 72, Colors.White);
        DrawString(_font, new Vector2(0, 160), "a war for a whole world", HorizontalAlignment.Center, W, 20, new Color(1, 1, 1, 0.6f));

        if (_settingsOpen) { DrawSettings(); return; }

        // Leader select: one entry in v1, drawn as a real card so more can be added.
        var card = new Rect2(W / 2 - 330, 210, 320, 150);
        DrawRect(card, new Color(0.08f, 0.12f, 0.18f));
        DrawRect(card, new Color(0.5f, 0.8f, 1f, 0.9f), false, 2f);
        DrawRect(new Rect2(card.Position + new Vector2(12, 12), new Vector2(64, 64)), new Color(0.2f, 0.45f, 0.85f));       // portrait placeholder
        DrawString(_font, card.Position + new Vector2(90, 34), "Field Commander", HorizontalAlignment.Left, 220, 20, Colors.White);
        DrawString(_font, card.Position + new Vector2(90, 58), "No bonuses. The standard", HorizontalAlignment.Left, 220, 13, new Color(1, 1, 1, 0.7f));
        DrawString(_font, card.Position + new Vector2(90, 76), "leader for every match.", HorizontalAlignment.Left, 220, 13, new Color(1, 1, 1, 0.7f));
        DrawString(_font, card.Position + new Vector2(12, 110), "Leader", HorizontalAlignment.Left, 296, 12, new Color(0.6f, 0.85f, 1f));
        DrawString(_font, card.Position + new Vector2(12, 130), "More arrive after v1.", HorizontalAlignment.Left, 296, 12, new Color(1, 1, 1, 0.5f));

        var seedBox = new Rect2(W / 2 + 10, 210, 320, 70);
        DrawRect(seedBox, new Color(0.08f, 0.1f, 0.14f));
        DrawRect(seedBox, new Color(1, 1, 1, 0.3f), false, 1f);
        DrawString(_font, seedBox.Position + new Vector2(12, 24), "Map seed (type digits, or leave empty for random)", HorizontalAlignment.Left, 300, 12, new Color(1, 1, 1, 0.6f));
        DrawString(_font, seedBox.Position + new Vector2(12, 54), _seedText == "" ? "random" : _seedText, HorizontalAlignment.Left, 300, 22, Colors.White);

        DrawString(_font, new Vector2(W / 2 + 10, 305), "Map: Standard, about 700 tiles, wraps east-west", HorizontalAlignment.Left, 320, 13, new Color(1, 1, 1, 0.6f));
        DrawString(_font, new Vector2(W / 2 + 10, 325), "Opponent: one rival AI, plus native minor powers", HorizontalAlignment.Left, 320, 13, new Color(1, 1, 1, 0.6f));

        float y = 400;
        Button(new Rect2(W / 2 - 150, y, 300, 44), "New game", StartFromMenu, new Color(0.15f, 0.3f, 0.2f), 20);
        var latest = SavedFiles().FirstOrDefault();
        if (latest != null) Button(new Rect2(W / 2 - 150, y + 54, 300, 44), "Continue latest save", () => StartLoad(latest), new Color(0.12f, 0.2f, 0.3f), 18);
        Button(new Rect2(W / 2 - 150, y + 108, 300, 44), "Settings and key bindings", () => _settingsOpen = true, new Color(0.12f, 0.2f, 0.3f), 18);
        Button(new Rect2(W / 2 - 150, y + 162, 300, 44), "Quit", () => GetTree().Quit(), null, 18);
        if (_menuNote != "") DrawString(_font, new Vector2(0, y + 240), _menuNote, HorizontalAlignment.Center, W, 15, Bad);
    }

    private void DrawLoading()
    {
        DrawString(_font, new Vector2(0, 340), "Loading saved game...", HorizontalAlignment.Center, W, 30, Colors.White);
        float frac = _loadTotal <= 0 ? 0 : Mathf.Clamp(_loadDone / (float)_loadTotal, 0f, 1f);
        var bar = new Rect2(W / 2 - 200, 380, 400, 14);
        DrawRect(bar, new Color(0.2f, 0.2f, 0.25f));
        DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * frac, bar.Size.Y)), new Color(0.4f, 0.8f, 0.5f));
    }

    private void DrawPauseMenu()
    {
        _buttons.Add((new Rect2(0, 0, W, H), () => { }));
        DrawRect(new Rect2(0, 0, W, H), new Color(0, 0, 0, 0.5f));
        var p = new Rect2(W / 2 - 170, 150, 340, 420);
        Panel(p);
        DrawString(_font, p.Position + new Vector2(0, 40), "PAUSED", HorizontalAlignment.Center, p.Size.X, 28, Colors.White);
        float x = p.Position.X + 30, y = p.Position.Y + 70, w = p.Size.X - 60;
        Button(new Rect2(x, y, w, 40), "Resume", () => _menuOpen = false, new Color(0.15f, 0.3f, 0.2f), 18);
        Button(new Rect2(x, y + 50, w, 40), "Save game (F5)", () => { QuickSave(); _menuOpen = false; }, new Color(0.12f, 0.2f, 0.3f), 18);
        Button(new Rect2(x, y + 100, w, 40), "Load game (F9)", () => { _menuOpen = false; QuickLoad(); }, new Color(0.12f, 0.2f, 0.3f), 18);
        Button(new Rect2(x, y + 150, w, 40), "Settings and keys", () => _settingsOpen = true, new Color(0.12f, 0.2f, 0.3f), 18);
        Button(new Rect2(x, y + 200, w, 40), "Surrender", () => { _pending.Add(new SurrenderCommand(0)); _menuOpen = false; }, null, 18);
        Button(new Rect2(x, y + 250, w, 40), "Main menu", () => { _menuOpen = false; _screen = Screen.Menu; }, null, 18);
        Button(new Rect2(x, y + 300, w, 40), "Quit game", () => GetTree().Quit(), null, 18);
    }

    // ---- settings -------------------------------------------------------------------------

    private void CaptureRebind(InputEventKey k)
    {
        if (k.Keycode == Key.Escape) { _rebinding = ""; _menuNote = "Rebinding cancelled."; return; }
        string name = OS.GetKeycodeString(k.Keycode);
        if (name == "" || (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9)) { _menuNote = "That key is reserved."; return; }
        if (_keys.TryRebind(_rebinding, name, out var conflict))
        {
            _menuNote = $"{Bindings.LabelOf(_rebinding)} is now {name}.";
            _rebinding = "";
            SaveBindings();
        }
        else _menuNote = $"{name} is already used by \"{conflict}\". Pick another key.";
    }

    private void DrawSettings()
    {
        _buttons.Add((new Rect2(0, 0, W, H), () => { }));
        DrawRect(new Rect2(0, 0, W, H), new Color(0, 0, 0, 0.6f));
        var p = new Rect2(W / 2 - 330, 30, 660, 660);
        Panel(p);
        DrawString(_font, p.Position + new Vector2(20, 34), "KEY BINDINGS", HorizontalAlignment.Left, 400, 24, Colors.White);
        DrawString(_font, p.Position + new Vector2(20, 56), "Click a key, then press the new one. Esc cancels. A key can drive only one action.",
            HorizontalAlignment.Left, 620, 12, new Color(1, 1, 1, 0.6f));

        const float rowH = 24f;
        float y = p.Position.Y + 72;
        int half = (Bindings.Actions.Length + 1) / 2;
        for (int i = 0; i < Bindings.Actions.Length; i++)
        {
            var (action, label, _) = Bindings.Actions[i];
            float cx = p.Position.X + 20 + (i / half) * 320;
            float cy = y + (i % half) * rowH;
            bool waiting = _rebinding == action;
            DrawString(_font, new Vector2(cx, cy + 16), label, HorizontalAlignment.Left, 150, 13, Colors.White);
            string act = action;
            Button(new Rect2(cx + 160, cy, 130, rowH - 2), waiting ? "press a key..." : _keys.KeyOf(action),
                () => { _rebinding = act; _menuNote = ""; }, waiting ? new Color(0.4f, 0.3f, 0.05f) : new Color(0.12f, 0.2f, 0.3f), 12);
        }

        float by = y + half * rowH + 14;
        if (_menuNote != "") DrawString(_font, new Vector2(p.Position.X + 20, by + 8), _menuNote, HorizontalAlignment.Left, 620, 14, Info);
        Button(new Rect2(p.Position.X + 20, by + 24, 200, 34), "Reset to defaults", () => { _keys.Reset(); SaveBindings(); _menuNote = "Defaults restored."; }, null, 14);
        Button(new Rect2(p.End.X - 140, by + 24, 120, 34), "Done", () => { _settingsOpen = false; _rebinding = ""; _menuNote = ""; }, new Color(0.15f, 0.3f, 0.2f), 16);

        DrawString(_font, new Vector2(p.Position.X + 20, p.End.Y - 70), "Fixed controls: left-click select / move, right-click move, Shift queues, wheel zoom, middle-drag or arrows pan.",
            HorizontalAlignment.Left, 620, 12, new Color(1, 1, 1, 0.5f));
        DrawString(_font, new Vector2(p.Position.X + 20, p.End.Y - 52), "Ctrl/Cmd+1-9 store a control group, 1-9 recall it, tap twice to centre. Trackpad pinch zooms, two-finger scroll pans.",
            HorizontalAlignment.Left, 620, 12, new Color(1, 1, 1, 0.5f));
        DrawString(_font, new Vector2(p.Position.X + 20, p.End.Y - 30), $"Settings file: {BindingsPath}", HorizontalAlignment.Left, 620, 11, new Color(1, 1, 1, 0.35f));
    }
}
