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

    // Bundled CUSTOM tracks. These mp3s are Godot-IMPORTED (each has a .import),
    // so inside an exported PCK/APK the raw path res://data/music/X.mp3 is
    // REMAPPED to .godot/imported/*.mp3str — DirAccess.GetFiles() will NOT list
    // them and FileAccess on the raw path is unreliable. The export-safe path is
    // ResourceLoader.Exists / GD.Load, which follow the import remap. We resolve a
    // hardcoded manifest that way, and (dev only) union in anything DirAccess finds.
    private const string MusicDir = "res://data/music";
    private static readonly string[] kBundledTracks =
    {
        "Sensible Menu 2.mp3",
        "Sensible Menu 3b.mp3",
    };

    // Union of the hardcoded manifest and any *.mp3 DirAccess reports (dev/editor),
    // de-duplicated, ordinal-sorted for a deterministic playlist order.
    private static List<string> EnumerateTrackNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        void Add(string n) { if (n.ToLowerInvariant().EndsWith(".mp3") && seen.Add(n)) names.Add(n); }

        foreach (var n in kBundledTracks) Add(n);

        using var dir = DirAccess.Open(MusicDir);   // null in an exported build → manifest only
        if (dir != null)
            foreach (var n in dir.GetFiles()) Add(n);

        names.Sort(System.StringComparer.Ordinal);
        return names;
    }

    // A track is present if the imported resource resolves (export + editor) OR the
    // raw force-included file exists (a bare, un-imported mp3).
    private static bool TrackExists(string name)
    {
        string path = MusicDir + "/" + name;
        return ResourceLoader.Exists(path) || Godot.FileAccess.FileExists(path);
    }

    // Loads a track export-safely: prefer the Godot-imported AudioStreamMP3 (follows
    // the PCK remap), fall back to raw force-included bytes for an un-imported mp3.
    private static AudioStreamMP3? LoadTrack(string name)
    {
        string path = MusicDir + "/" + name;
        if (ResourceLoader.Exists(path))
        {
            var res = GD.Load<AudioStreamMP3>(path);
            if (res != null) return res;
        }
        var bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes != null && bytes.Length > 0) return new AudioStreamMP3 { Data = bytes };
        return null;
    }

    // Memoized: this is polled per-frame by the music resolver.
    private static bool? s_customAvail;
    public static bool CustomAvailable()
    {
        if (s_customAvail.HasValue) return s_customAvail.Value;
        bool avail = false;
        foreach (var name in EnumerateTrackNames())
            if (TrackExists(name)) { avail = true; break; }
        s_customAvail = avail;
        return avail;
    }

    // Verification probe (used by Main's --music-custom-test flag). Exercises the
    // EXACT static availability + load path the live playlist uses (no Node/audio
    // driver needed), so a green result here proves CUSTOM works for playback too —
    // including inside an exported PCK/APK where res:// paths are import-remapped.
    public static int TestCustomLoad()
    {
        int n = 0;
        foreach (var name in EnumerateTrackNames())
        {
            var s = LoadTrack(name);
            if (s != null) { n++; GD.Print($"[music] custom track: {name}"); }
        }
        GD.Print($"[music] custom available={CustomAvailable()}");
        GD.Print($"[music] custom: {n} tracks");
        return n;
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

    // AMIGA RJP1 mix runs on a dedicated background thread with reused buffers so
    // it never allocates per-frame and never underruns behind a busy main thread
    // (Android menu-stutter fix). The generator + player are created on the main
    // thread; StopInternal joins this thread BEFORE nulling _rjp/_genPb so the
    // thread never touches a torn-down object.
    private const int kMixChunk = 1024;
    private System.Threading.Thread? _mixThread;
    private volatile bool _mixRun;

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
        // Ensure the mix thread is stopped even if _ExitTree fires without a Stop().
        _mixRun = false;
        _mixThread?.Join(200);
        _mixThread = null;

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
                _gen.Stream = new AudioStreamGenerator { MixRate = 44100, BufferLength = 0.5f };
                _gen.VolumeDb = 0;
                _gen.Play();
                _genPb = (AudioStreamGeneratorPlayback)_gen.GetStreamPlayback();

                // One-time self-profile of the mix cost (main thread, negligible; the
                // extra chunk advances RJP state inaudibly). Prints once per start.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var probe = new float[kMixChunk * 2];
                _rjp.GenerateStereo(probe, kMixChunk);
                sw.Stop();
                GD.Print($"[music] GenerateStereo x{kMixChunk} = {sw.Elapsed.TotalMilliseconds:0.00} ms");

                // Hand the pull-mix off to the background thread (reused buffers).
                _mixRun = true;
                _mixThread = new System.Threading.Thread(MixLoop)
                    { IsBackground = true, Name = "MenuMusicMix" };
                _mixThread.Start();

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
        // Join the mix thread BEFORE tearing down the objects it captured, so no
        // background frame touches a nulled _rjp / _genPb.
        _mixRun = false;
        _mixThread?.Join(200);
        _mixThread = null;

        _gen.Stop();
        _wav.Stop();
        _rjp = null;
        _genPb = null;
    }

    // Background pull-mixer for the AMIGA RJP1 source. Generates AND pushes with
    // buffers allocated ONCE here (zero per-frame allocation). Captures the current
    // playback/player locals each pass; StopInternal's join-before-null guarantees
    // they stay valid for the lifetime of a pass.
    private void MixLoop()
    {
        var fbuf = new float[kMixChunk * 2];
        var vbuf = new Godot.Vector2[kMixChunk];
        while (_mixRun)
        {
            var pb = _genPb;
            var rjp = _rjp;
            if (pb == null || rjp == null) { System.Threading.Thread.Sleep(5); continue; }
            try
            {
                int avail = pb.GetFramesAvailable();
                while (avail >= kMixChunk && _mixRun)
                {
                    rjp.GenerateStereo(fbuf, kMixChunk);
                    for (int i = 0; i < kMixChunk; i++)
                        vbuf[i] = new Godot.Vector2(fbuf[2 * i], fbuf[2 * i + 1]);
                    pb.PushBuffer(vbuf);
                    avail -= kMixChunk;
                }
            }
            catch { /* generator torn down mid-frame; loop guard handles it */ }
            System.Threading.Thread.Sleep(3);
        }
    }

    public override void _Process(double delta)
    {
        // AMIGA: the pull-mix now runs on the dedicated background thread (MixLoop),
        // off the main thread, so nothing to do here for that source.

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

        foreach (var name in EnumerateTrackNames())
        {
            // Export-safe load (imported resource first, raw bytes fallback).
            var stream = LoadTrack(name);
            if (stream != null)
            {
                _playlist.Add(stream);
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
