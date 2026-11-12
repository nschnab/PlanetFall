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
    /// <summary>One line in the alert feed. Clicking it jumps the camera to <see cref="Tile"/>.</summary>
    private sealed record Alert(string Text, int Tile, int Until, Color Colour);

    private const int AlertTicks = 120;               // 12 s on screen
    private const int AlertCooldownTicks = 100;       // same kind at most every 10 s
    private readonly List<Alert> _alerts = new();
    private readonly HashSet<int> _engaged = new();
    private readonly HashSet<int> _seenEnemy = new();
    private readonly Dictionary<string, int> _lastAlert = new();

    private static readonly Color Bad = new(1f, 0.5f, 0.4f), Good = new(0.55f, 1f, 0.55f), Info = new(1f, 0.95f, 0.6f);

    private void AddAlert(string kind, string text, int tile, Color colour)
    {
        if (_lastAlert.TryGetValue(kind, out int last) && _s.Tick - last < AlertCooldownTicks) return;
        _lastAlert[kind] = _s.Tick;
        _alerts.Insert(0, new Alert(text, tile, _s.Tick + AlertTicks, colour));
        if (_alerts.Count > 6) _alerts.RemoveAt(_alerts.Count - 1);
    }

    /// <summary>Alerts derived from the world rather than a single event: contact, siege.</summary>
    private void ScanAlerts()
    {
        _alerts.RemoveAll(a => a.Until <= _s.Tick);

        var now = new HashSet<int>();
        foreach (var st in _s.Stacks)
        {
            if (st.Owner != 0 || !Simulation.Engaged(_s, st)) continue;
            now.Add(st.Id);
            if (!_engaged.Contains(st.Id)) AddAlert("attack", "Under attack", st.Tile, Bad);
        }
        _engaged.Clear();
        foreach (int id in now) _engaged.Add(id);

        if (_s.Tick % 10 == 0)
        {
            var seen = new HashSet<int>();
            foreach (var st in _s.Stacks)
            {
                if (st.Owner == 0 || _s.Players[st.Owner].Minor || !_s.AtWar(0, st.Owner) || !_m.Fog.CanSee(_s, st)) continue;
                seen.Add(st.Id);
                if (!_seenEnemy.Contains(st.Id)) AddAlert("sight", "Enemy sighted", st.Tile, Info);
            }
            _seenEnemy.Clear();
            foreach (int id in seen) _seenEnemy.Add(id);
        }

        if (_s.CapitalHeld[0] > 0) AddAlert("siege", "YOUR CAPITAL IS UNDER SIEGE", _s.Players[0].Capital, Bad);
    }

    private string NameOf(int p) => _s.Players[p].Leader.Name;

    private static string TreatyName(Treaty t) => t switch
    {
        Treaty.War => "War", Treaty.Neutral => "Neutral", Treaty.NonAggression => "Non-Aggression",
        Treaty.TradePact => "Trade Pact", _ => "Alliance",
    };

    private static string ShortTreaty(Treaty t) => t switch
    {
        Treaty.Neutral => "Peace", Treaty.NonAggression => "Non-Agg.", Treaty.TradePact => "Trade", _ => "Alliance",
    };

    private void Note(string msg)
    {
        _log.Add($"{_s.Tick / 600}:{_s.Tick / 10 % 60:00}  {msg}");
        if (_log.Count > 60) _log.RemoveAt(0);
        _toast = msg;
        _toastUntil = _s.Tick + 90;
    }

    private void OnEvent(SimEvent ev)
    {
        switch (ev)
        {
            case PlayerDefeated d: _endReason = d.Reason; break;
            case StackDied d when d.Owner == 0: AddAlert("died", "Stack lost", d.Tile, Bad); break;
            case TileCaptured c when c.PreviousOwner == 0: AddAlert("tilelost", "Territory lost", c.Tile, Bad); break;
            case TileCaptured c when c.NewOwner == 0 && c.PreviousOwner != -1: AddAlert("tilewon", "Territory captured", c.Tile, Good); break;
            case StructureBuilt b when b.Owner == 0: AddAlert("built", $"{b.Structure} built", b.Tile, Good); break;
            case StructureLost l when _s.TileOwner[l.Tile] == 0 || _m.Fog.Visible(l.Tile): AddAlert("structlost", $"{l.Structure} destroyed", l.Tile, Bad); break;
            case ProposalAnswered a when a.From == 0: Note($"{NameOf(a.To)} on {TreatyName(a.Treaty)}: {a.Reason}"); break;
            case SurrenderAnswered a when a.To == 0: Note($"{NameOf(a.From)}: {a.Reason}"); break;
            case TreatyChanged c when c.A == 0 || c.B == 0: Note($"{NameOf(c.A == 0 ? c.B : c.A)}: now {TreatyName(c.New)}"); break;
            case Treacherous t: Note(t.Player == 0 ? "You are now TREACHEROUS: everyone trusts you less, and no treaties for 5 minutes." : $"{NameOf(t.Player)} broke a pact and is Treacherous."); break;
            case TaskPosted p when p.Kind == "gift": Note($"{NameOf(p.Minor)} asks for a gift of {DiplomacySystem.TaskGiftAlloy} alloy."); break;
            case RaidLaunched r:
                Note($"{NameOf(r.Minor)} sends raiders at {(r.Target == 0 ? "you" : NameOf(r.Target))}.");
                if (r.Target == 0) AddAlert("raid", "Raiders incoming", _s.FindStack(r.StackId)?.Tile ?? _s.Players[0].Capital, Bad);
                break;
            case TaskCompleted c when c.Player == 0: Note($"{NameOf(c.Minor)} is grateful."); break;
            case IntegrationStarted i when i.Player == 0: Note($"Integrating {NameOf(i.Minor)}: 60 s."); break;
            case IntegrationCancelled i when i.Player == 0: Note($"Integration of {NameOf(i.Minor)} fell through: {i.Reason}."); break;
            case MinorIntegrated i when i.Player == 0: Note($"{NameOf(i.Minor)} has joined you."); break;
        }
    }
}
