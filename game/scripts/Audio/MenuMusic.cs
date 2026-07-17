using Godot;
using System.Collections.Generic;

namespace OpenSwos.Audio;

/// <summary>
/// Front-end (menu) music hub — a scene Node owned by Main.cs, mirroring the
/// singleton style of <see cref="MatchAudio"/>. Plays ONE of four sources while
/// the app is on a menu screen, and hard-stops the moment a match/pause begins:
///   • AMIGA  — the original RJP1 "MENU" song, synthesised live through
///     <see cref="Rjp1Player"/> into an <see cref="AudioStreamGenerator"/>.
///   • PC     — the PC CD-audio track 2 (looping AudioStreamWav via CdMusicImport).
///   • CUSTOM — a shuffled playlist of the bundled Suno mp3s under res://data/music.
///   • OFF    — silence.
///
/// Every engine call degrades to null safely, and Main never creates this node
/// headless, so no determinism / audio-bus concerns in the harness paths.
/// The <see cref="System.Random"/> here is AUDIO-only (menu shuffle) — never the
/// lockstep sim RNG.
/// </summary>
public partial class MenuMusic : Node
{
    public enum MusicSource { Amiga, Pc, Custom, Off }

    public static MenuMusic? Instance { get; private set; }

    // ── availability (cheap, cached by the underlying engines) ────────────────
    public static bool AmigaAvailable() => Rjp1Module.Available("MENU");
    public static bool PcAvailable() => OpenSwos.Assets.CdMusicImport.Available();
    public static bool CustomAvailable()
    {
        using var dir = DirAccess.Open("res://data/music");
        if (dir == null) return false;
        foreach (var name in dir.GetFiles())
            if (name.ToLowerInvariant().EndsWith(".mp3")) return true;
        return false;
    }

    // ── volume constants (documented; user tunes by ear later) ────────────────
    // AMIGA: the RJP1 synth already self-limits to cap 35/64 with 0.5 pan
    // headroom, so the generator player runs at unity.
    // PC: raw CDDA is full-scale → pull down.
    // CUSTOM: loud-mastered Suno mp3 → pull down further.
    private const float PcDb = -8f;
    private const float CustomDb = -10f;

    // ── nodes ─────────────────────────────────────────────────────────────────
    private AudioStreamPlayer _gen = null!;   // AMIGA (generator)
    private AudioStreamPlayer _wav = null!;   // PC + CUSTOM (plain stream)
    private AudioStreamGeneratorPlayback? _genPb;
    private Rjp1Player? _rjp;
    private MusicSource _current = MusicSource.Off;
    private System.Random _rng = new();   // AUDIO rng only (menu shuffle)

    // ── custom playlist ───────────────────────────────────────────────────────
    private List<AudioStreamMP3> _playlist = new();
    private bool _playlistBuilt;
    private int _customIndex;

    public MusicSource Current => _current;

    public override void _Ready()
    {
        Instance = this;

        _gen = new AudioStreamPlayer { Name = "MenuMusicGen" };
        _wav = new AudioStreamPlayer { Name = "MenuMusicWav" };
        AddChild(_gen);
        AddChild(_wav);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Start (or keep) the requested source. Idempotent: calling with the
    /// current source while it is still producing sound is a no-op, so it is safe
    /// to call every frame from the menu driver.
    /// </summary>
    public void Play(MusicSource src)
    {
        if (src == _current && _current != MusicSource.Off)
            return;   // already the current source → keep it running

        StopInternal();

        switch (src)
        {
            case MusicSource.Amiga:
            {
                var m = Rjp1Module.Load("MENU");
                if (m == null) { _current = MusicSource.Off; return; }
                _rjp = new Rjp1Player(m, 44100);
                _rjp.PlaySong(0);
                _rjp.StartFadeIn();
                _gen.Stream = new AudioStreamGenerator { MixRate = 44100, BufferLength = 0.2f };
                _gen.VolumeDb = 0;
                _gen.Play();
                _genPb = (AudioStreamGeneratorPlayback)_gen.GetStreamPlayback();
                _current = MusicSource.Amiga;
                GD.Print("[music] amiga: menu music started (fade-in)");
                break;
            }
            case MusicSource.Pc:
            {
                var s = OpenSwos.Assets.CdMusicImport.Load();
                if (s == null) { _current = MusicSource.Off; return; }
                _wav.Stream = s;
                _wav.VolumeDb = PcDb;
                _wav.Play();
                _current = MusicSource.Pc;
                break;
            }
            case MusicSource.Custom:
            {
                BuildPlaylist();
                if (_playlist.Count == 0) { _current = MusicSource.Off; return; }
                _customIndex = _rng.Next(_playlist.Count);
                PlayCustomTrack();
                _current = MusicSource.Custom;
                GD.Print($"[music] custom: {_playlist.Count} tracks");
                break;
            }
            default:
                _current = MusicSource.Off;
                break;
        }
    }

    public void Stop()
    {
        StopInternal();
        _current = MusicSource.Off;
    }

    private void StopInternal()
    {
        _gen.Stop();
        _wav.Stop();
        _rjp = null;
        _genPb = null;
    }

    public override void _Process(double delta)
    {
        // AMIGA: pull-mixer — feed the generator as buffer space frees up.
        if (_current == MusicSource.Amiga && _genPb != null && _rjp != null)
        {
            int frames = _genPb.GetFramesAvailable();
            if (frames > 0)
            {
                var buf = new float[frames * 2];
                _rjp.GenerateStereo(buf, frames);
                var v = new Godot.Vector2[frames];
                for (int i = 0; i < frames; i++)
                    v[i] = new Godot.Vector2(buf[2 * i], buf[2 * i + 1]);
                _genPb.PushBuffer(v);
            }
        }

        // CUSTOM: advance to the next track when the current one finishes (loops
        // the playlist forever).
        if (_current == MusicSource.Custom && !_wav.Playing && _playlist.Count > 0)
        {
            _customIndex = (_customIndex + 1) % _playlist.Count;
            PlayCustomTrack();
        }
    }

    // ── custom playlist helpers ───────────────────────────────────────────────
    private void BuildPlaylist()
    {
        if (_playlistBuilt) return;
        _playlistBuilt = true;

        using var dir = DirAccess.Open("res://data/music");
        if (dir == null) return;

        var names = new List<string>();
        foreach (var name in dir.GetFiles())
        {
            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".mp3")) continue;   // skip .import/.remap entries
            names.Add(name);
        }
        names.Sort(System.StringComparer.Ordinal);   // deterministic list order

        foreach (var name in names)
        {
            // Raw-bytes decode path: works in dev AND exported pck without a Godot
            // import (the mp3s are force-included via export_presets.cfg).
            var bytes = Godot.FileAccess.GetFileAsBytes("res://data/music/" + name);
            if (bytes.Length > 0)
            {
                _playlist.Add(new AudioStreamMP3 { Data = bytes });
                GD.Print($"[music] custom track: {name}");
            }
        }
    }

    private void PlayCustomTrack()
    {
        _wav.Stream = _playlist[_customIndex];
        _wav.VolumeDb = CustomDb;
        _wav.Play();
    }
}
