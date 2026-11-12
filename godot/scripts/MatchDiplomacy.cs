using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planetfall.Sim.Core;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall;

public partial class Match
{
    // ---- diplomacy panel ------------------------------------------------------------------

    private void DrawDiplomacy()
    {
        const float x = 700, w = 570, rowH = 26;
        var dip = _s.Diplomacy;
        var me = _s.Players[0];
        var dist = new int[_s.Map.TileCount];
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        dist[me.Capital] = 0;
        q.Enqueue(me.Capital);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (int n in _s.Map.Neighbors[c]) if (dist[n] == int.MaxValue) { dist[n] = dist[c] + 1; q.Enqueue(n); }
        }
        var others = new List<int> { 1 };
        others.AddRange(_s.Players.Where(p => p.Minor && dip.IntegratedInto[p.Id] == -1).OrderBy(p => dist[p.Capital]).ThenBy(p => p.Id).Select(p => p.Id));
        if (_dipSel == -1 || !others.Contains(_dipSel)) _dipSel = others[0];

        float top = TopBar + 8;
        float height = 24 + others.Count * rowH + 4 + 20 + 6 * 17 + 20 + 6 * 17 + 10;
        _buttons.Add((new Rect2(x, top, w, height), () => { }));      // the panel swallows clicks meant for the map under it
        DrawRect(new Rect2(x, top, w, height), new Color(0.04f, 0.05f, 0.08f, 0.94f));
        DrawRect(new Rect2(x, top, w, height), new Color(1, 1, 1, 0.3f), false, 1f);
        DrawString(_font, new Vector2(x + 10, top + 17), $"DIPLOMACY   Influence {me.Influence / 1000}   Alloy {me.Supply / 1000}" +
            (dip.TreatyCooldownUntil[0] > _s.Tick ? $"   (no treaties for {(dip.TreatyCooldownUntil[0] - _s.Tick) / 10}s)" : ""),
            HorizontalAlignment.Left, -1, 14, Colors.White);

        float y = top + 24;
        foreach (int id in others)
        {
            var p = _s.Players[id];
            var treaty = dip.Get(0, id);
            int op = DiplomacySystem.Opinion(_s, id, 0);
            bool minor = p.Minor;
            string mood = minor ? DiplomacySystem.MoodOf(_s, id, 0).ToString() : "Rival";
            if (id == _dipSel) DrawRect(new Rect2(x + 4, y, w - 8, rowH - 2), new Color(1, 1, 1, 0.10f));
            int pick = id;
            _buttons.Add((new Rect2(x + 4, y, 320, rowH - 2), () => _dipSel = pick));
            string label = minor ? $"{NameOf(id)} (hop {dist[p.Capital]})" : $"RIVAL  {_ai.Personality.Name}";
            DrawString(_font, new Vector2(x + 10, y + 17), $"{label}  {mood}  {TreatyName(treaty)}  op {op:+#;-#;0}", HorizontalAlignment.Left, 320, 12, ColorOf(id).Lightened(0.5f));

            int bx = (int)(x + w - 240);
            Button(new Rect2(bx, y, 62, rowH - 3), "Gift 15", () => _pending.Add(new GiftCommand(0, pick, 15)), new Color(0.15f, 0.3f, 0.2f), 12);
            if (treaty < Treaty.Alliance)
            {
                var next = treaty + 1;
                long cost = DiplomacySystem.TreatyInfluence[(int)next] / 1000;
                Button(new Rect2(bx + 66, y, 104, rowH - 3), $"{ShortTreaty(next)}{(cost > 0 ? $" {cost}i" : "")}",
                    () => _pending.Add(new ProposeTreatyCommand(0, pick, next)), new Color(0.12f, 0.2f, 0.3f), 12);
            }
            else if (minor && !dip.Integrations.Any(i => i.Minor == id))
                Button(new Rect2(bx + 66, y, 104, rowH - 3), $"Integrate {DiplomacySystem.IntegrateInfluence / 1000}i", () => _pending.Add(new IntegrateCommand(0, pick)), new Color(0.2f, 0.25f, 0.1f), 12);
            else if (minor)
            {
                var it = dip.Integrations.First(i => i.Minor == id);
                DrawString(_font, new Vector2(bx + 70, y + 17), $"joining {it.TicksLeft / 10}s", HorizontalAlignment.Left, -1, 12, new Color(0.8f, 1f, 0.6f));
            }
            if (treaty >= Treaty.NonAggression)
                Button(new Rect2(bx + 174, y, 62, rowH - 3), "Break", () => _pending.Add(new BreakTreatyCommand(0, pick)), null, 12);
            else if (treaty != Treaty.War)
                Button(new Rect2(bx + 174, y, 62, rowH - 3), "War", () => _pending.Add(new DeclareWarCommand(0, pick)), null, 12);
            else if (!minor)
                Button(new Rect2(bx + 174, y, 62, rowH - 3), "Demand", () => _pending.Add(new DemandSurrenderCommand(0, pick)), new Color(0.35f, 0.2f, 0.05f), 12);
            y += rowH;
        }

        // Why they feel the way they do.
        y += 4;
        var lines = new List<OpinionLine>();
        int total = DiplomacySystem.Opinion(_s, _dipSel, 0, lines);
        var tr = dip.Get(0, _dipSel);
        string need = tr < Treaty.Alliance ? $"{ShortTreaty(tr + 1)} needs opinion {DiplomacySystem.TreatyNeed[(int)tr + 1]}" : $"integration needs {DiplomacySystem.IntegrateOpinion}";
        DrawString(_font, new Vector2(x + 10, y + 14), $"{NameOf(_dipSel)} thinks of you: {total:+#;-#;0}   ({need})", HorizontalAlignment.Left, -1, 13, Colors.White);
        y += 20;
        for (int i = 0; i < 6; i++)
        {
            if (i < lines.Count)
                DrawString(_font, new Vector2(x + 20, y + 13), $"{lines[i].Points:+#;-#;0}  {lines[i].Label}", HorizontalAlignment.Left, -1, 12, lines[i].Points >= 0 ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.6f, 0.6f));
            y += 17;
        }
        var task = dip.Tasks.FirstOrDefault(tk => tk.Minor == _dipSel && (tk.Player == -1 || tk.Player == 0));
        if (task != null)
            DrawString(_font, new Vector2(x + 10, y + 13), task.Kind == "gift" ? $"Task: give {task.Amount / 1000} alloy (+{task.Reward} opinion), {(task.Expires - _s.Tick) / 10}s left"
                                                         : $"Task: destroy their raiders (+{task.Reward} opinion), {(task.Expires - _s.Tick) / 10}s left",
                HorizontalAlignment.Left, -1, 12, new Color(1f, 0.9f, 0.5f));
        y += 20;
        foreach (var line in _log.TakeLast(6))
        {
            DrawString(_font, new Vector2(x + 10, y + 13), line, HorizontalAlignment.Left, w - 20, 12, new Color(1, 1, 1, 0.75f));
            y += 17;
        }
    }
}
