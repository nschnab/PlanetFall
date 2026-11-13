using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Planetfall.Sim.Presentation;

namespace Planetfall;

/// <summary>Adaptive music (three looping layers cross-faded by the MusicDirector) and short effects, all synthesised at start-up.</summary>
public partial class Match
{
    private const int SfxVoices = 6;

    private MusicDirector _music = new();
    private readonly AudioStreamPlayer[] _layers = new AudioStreamPlayer[3];
    private readonly AudioStreamPlayer[] _voices = new AudioStreamPlayer[SfxVoices];
    private readonly Dictionary<string, AudioStreamWav> _sfx = new();
    private readonly Dictionary<int, int> _flash = new();
    private int _nextVoice;
    private bool _audioReady;
    private float _musicVol = 0.6f, _sfxVol = 0.8f;

    private static string SettingsPath => ProjectSettings.GlobalizePath("user://settings.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("music", out var m)) _musicVol = Math.Clamp((float)m.GetDouble(), 0f, 1f);
            if (doc.RootElement.TryGetProperty("sfx", out var s)) _sfxVol = Math.Clamp((float)s.GetDouble(), 0f, 1f);
        }
        catch (Exception) { /* keep defaults */ }
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { music = _musicVol, sfx = _sfxVol })); }
        catch (Exception e) { GD.PushWarning("Could not save settings: " + e.Message); }
    }

    private static AudioStreamWav ToStream(short[] pcm, bool loop)
    {
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Synth.Rate,
            Stereo = false,
            Data = bytes,
            LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled,
            LoopBegin = 0,
            LoopEnd = pcm.Length,
        };
    }

    private void InitAudio()
    {
        if (DisplayServer.GetName() == "headless") return;      // nothing to hear, and CI should stay quick
        for (int i = 0; i < _layers.Length; i++)
        {
            _layers[i] = new AudioStreamPlayer { Stream = ToStream(Synth.Music(Synth.MusicLayers[i]), true), VolumeDb = -80f };
            AddChild(_layers[i]);
            _layers[i].Play();
        }
        foreach (var name in Synth.SfxNames) _sfx[name] = ToStream(Synth.Sfx(name), false);
        for (int i = 0; i < _voices.Length; i++) { _voices[i] = new AudioStreamPlayer(); AddChild(_voices[i]); }
        _audioReady = true;
    }

    private void Sfx(string name)
    {
        if (!_audioReady || _sfxVol <= 0f || !_sfx.TryGetValue(name, out var stream)) return;
        var v = _voices.FirstOrDefault(p => !p.Playing) ?? _voices[_nextVoice++ % _voices.Length];
        v.Stream = stream;
        v.VolumeDb = Mathf.LinearToDb(_sfxVol);
        v.Play();
    }

    /// <summary>What the player can currently see decides the mood; runs a few times a second.</summary>
    private void SenseMusic()
    {
        if (_s.Tick % 5 != 0) return;
        var (fighting, threat) = MusicDirector.Sense(_s, _m.Fog);
        _music.Update(_s.Tick, fighting, threat);
        if (fighting && _engaged.Count > 0 && _s.Tick % 10 == 0) Sfx("hit");
    }

    private void UpdateAudio(double delta)
    {
        if (!_audioReady) return;
        _music.Fade(delta);
        for (int i = 0; i < _layers.Length; i++)
        {
            float lin = (float)_music.Layers[i] * _musicVol * 0.5f;
            _layers[i].VolumeDb = lin <= 0.001f ? -80f : Mathf.LinearToDb(lin);
        }
    }

    private static string VolText(float v) => v <= 0f ? "off" : $"{(int)Math.Round(v * 100)}%";

    private static float NextVol(float v) => v <= 0f ? 0.25f : v < 0.3f ? 0.5f : v < 0.55f ? 0.75f : v < 0.8f ? 1f : 0f;
}
