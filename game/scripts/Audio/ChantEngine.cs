using Godot;
using OpenSwos.SwosVm;

namespace OpenSwos.Audio;

/// <summary>
/// Crowd-chant state machine — paraphrase of swos-port src/audio/chants.cpp
/// (playCrowdChants chants.cpp:122-161, loadIntroChant chants.cpp:41-77,
/// loadCrowdChantSample / getChantSampleIndex chants.cpp:79-94,284-341).
///
/// Owns its own audio-side RNG (never the sim RNG) and only READS sim memory
/// (score, home shirt colour) — never writes it. Advances one branch per sim
/// frame via <see cref="Tick"/>. Drives the dedicated looping chant channel on
/// <see cref="MatchAudio"/>.
/// </summary>
internal sealed class ChantEngine
{
    private readonly MatchAudio _audio;
    private readonly System.Random _rng;

    // chants.cpp:49-51 — intro chant index table, indexed by home team primary
    // shirt colour (0..15). -1 → no intro chant (slot 0 falls back to CHANT4L).
    private static readonly int[] IntroTeamChantIndices =
        { -1, -1, 3, -1, 0, 0, 0, 1, -1, 1, 2, 5, -1, 5, 1, 4 };

    // chants.cpp:49-51 — file table the index above selects into.
    private static readonly string[] IntroChantFiles =
        { "COMBROWN", "COMGREEN", "COMRED", "COMWHITE", "COMYELO", "COMBLUE" };

    // chants.cpp:284-311 / swos.asm:222483-222492 — result chant files, indexed
    // by getChantSampleIndex(): 0..5 by goal margin, 6 EREWEGO, 7 NOTSING.
    private static readonly string[] ResultChantFiles =
        { "ONENIL", "TWONIL", "THREENIL", "FOURNIL", "FIVENIL", "EASY", "EREWEGO", "NOTSING" };

    // Loaded loop samples (null = missing → that slot silently disabled).
    private AudioStreamWav? _chant4l, _chant8l, _chant10l, _introChant, _resultChant;

    // AMIGA mode: the crowd sings the 4 RJP1 chant instruments (instr 60/61/63/67)
    // with NO intro-by-shirt-colour and NO result chants (those are PC-only). The
    // fade/schedule state machine below is shared with the PC path unchanged.
    private bool _amiga;
    private AudioStreamWav?[] _amigaChants = System.Array.Empty<AudioStreamWav?>();

    // State machine timers (chants.cpp:127-159).
    private int _fadeInTimer;
    private int _nextChantTimer;
    private int _fadeOutTimer;
    private int _lastSlot = -1;        // last chant function chosen (-1 = none)
    private bool _interestingGame;

    public ChantEngine(MatchAudio audio, System.Random rng)
    {
        _audio = audio;
        _rng = rng;
    }

    /// <summary>Load the fixed chant samples + the home-team intro chant. Returns
    /// (loaded, missing) counts for the load summary.</summary>
    public (int loaded, int missing) Load()
    {
        int loaded = 0, missing = 0;
        void Track(AudioStreamWav? s) { if (s != null) loaded++; else missing++; }

        _chant4l = RawSample.Load("CHANT4L", loop: true);
        _chant8l = RawSample.Load("CHANT8L", loop: true);
        _chant10l = RawSample.Load("CHANT10L", loop: true);
        Track(_chant4l); Track(_chant8l); Track(_chant10l);

        LoadIntroChant(ref loaded, ref missing);
        return (loaded, missing);
    }

    /// <summary>AMIGA mode: adopt the 4 RJP1 chant samples instead of the PC set.
    /// Returns (loaded, missing) for the load summary.</summary>
    public (int loaded, int missing) LoadAmiga(AudioStreamWav?[]? chants)
    {
        _amiga = true;
        _amigaChants = chants ?? System.Array.Empty<AudioStreamWav?>();
        int loaded = 0, missing = 0;
        foreach (var c in _amigaChants) { if (c != null) loaded++; else missing++; }
        return (loaded, missing);
    }

    // chants.cpp:41-77 — pick the intro chant from the home (top) team's primary
    // shirt colour. TeamGame primary shirt colour is at header offset +2
    // (swos.h:298 / Pitch.cs:197).
    private void LoadIntroChant(ref int loaded, ref int missing)
    {
        short shirtCol = Memory.ReadSignedWord(Memory.Addr.team1InGameTeamHeader + 2);
        if (shirtCol < 0 || shirtCol > 15) { _introChant = null; return; }
        int fileIdx = IntroTeamChantIndices[shirtCol];
        if (fileIdx < 0) { _introChant = null; return;  }   // no intro → CHANT4L fallback
        _introChant = RawSample.Load(IntroChantFiles[fileIdx], loop: true);
        if (_introChant != null) loaded++; else missing++;
    }

    // chants.cpp:66-76 — the 4 chant "functions". Slot 2 becomes the result
    // chant while the game is "interesting".
    private AudioStreamWav? SlotSample(int slot)
    {
        if (_amiga)
            return _amigaChants.Length == 0 ? null
                : _amigaChants[((slot % _amigaChants.Length) + _amigaChants.Length) % _amigaChants.Length];
        return slot switch
        {
            0 => _introChant ?? _chant4l,                               // intro or fallback
            1 => _chant8l,
            2 => (_interestingGame && _resultChant != null) ? _resultChant : _chant10l,
            _ => _chant4l,                                              // slot 3
        };
    }

    private int Rnd256() => _rng.Next(0, 256);

    /// <summary>Reset to silence at match (re)start; first chant is scheduled here.</summary>
    public void Reset()
    {
        _fadeInTimer = 0;
        _fadeOutTimer = 0;
        _lastSlot = -1;
        _interestingGame = false;
        _resultChant = null;
        // Schedule the first chant to start on the next tick (chants.cpp:214 —
        // initGameLoop kicks the fans chant right after the crowd bed).
        _nextChantTimer = 0;
        _audio.StopChant();
    }

    // chants.cpp:122-161 — one branch per frame.
    public void Tick()
    {
        // Fade-in: volume 0→64 over 128 ticks.
        if (_fadeInTimer > 0)
        {
            _fadeInTimer--;
            int vol = (128 - _fadeInTimer) / 2;   // chants.cpp:127-131
            if (vol > 0) _audio.SetChantVolume127(vol);
            return;
        }

        // Hold: chant loops at full volume until the timer drains.
        if (_nextChantTimer > 0)
        {
            _nextChantTimer--;
            return;   // chants.cpp:132-133
        }

        // Fade-out: 64→0 over 128 ticks; stop the channel at 0.
        if (_fadeOutTimer > 0)
        {
            _fadeOutTimer--;
            int vol = _fadeOutTimer / 2;          // chants.cpp:134-137
            if (vol > 0) _audio.SetChantVolume127(vol);
            else _audio.StopChant();
            return;
        }

        // All timers idle — schedule the next chant (chants.cpp:138-159).
        // 50% chance of a silent pause (0..510 ticks).
        if (Rnd256() >= 128)
        {
            _nextChantTimer = Rnd256() * 2;
            return;
        }

        int slot;
        if (_lastSlot >= 0 && Rnd256() < 128)
            slot = _lastSlot;                     // chants.cpp:142 — 50% repeat last
        else if (_interestingGame)
            slot = 2;                             // chants.cpp:143-146 — force result slot
        else
            slot = _rng.Next(0, 4);               // uniform over the 4 slots

        _audio.PlayChant(SlotSample(slot));
        _lastSlot = slot;
        _fadeInTimer = 128;
        _fadeOutTimer = 128;
        _nextChantTimer = Rnd256() * 2 + 500;     // chants.cpp:159 — 500..1010 ticks
    }

    // chants.cpp:79-94,284-341 — after each goal, recompute the result-chant slot
    // from the current score. Reads sim memory only.
    public void OnGoal()
    {
        // AMIGA has no result chants (ONENIL etc. are PC-only) — just drop the
        // current selection so the next pick is fresh.
        if (_amiga) { _lastSlot = -1; return; }

        int g1 = Memory.ReadWord(Memory.Addr.team1TotalGoals);
        int g2 = Memory.ReadWord(Memory.Addr.team2TotalGoals);

        int idx = GetChantSampleIndex(g1, g2);
        if (idx >= 0)
        {
            _resultChant = RawSample.Load(ResultChantFiles[idx], loop: true);
            _interestingGame = _resultChant != null;   // chants.cpp:334-335
        }
        else
        {
            _resultChant = null;
            _interestingGame = false;                   // chants.cpp:337-339
        }
        // chants.cpp:89-91 — drop the current chant selection so the crowd
        // doesn't keep chanting a now-stale result; the next pick is fresh.
        _lastSlot = -1;
    }

    // chants.cpp:284-311 — map the score to a result-chant index (-1 = none).
    private int GetChantSampleIndex(int g1, int g2)
    {
        // One side is being shut out and the scores differ.
        if ((g1 == 0 || g2 == 0) && g1 != g2)
        {
            int diff = System.Math.Max(g1, g2);
            if (diff > 6) return -1;                    // cap (chants.cpp:292)
            if (_rng.Next(0, 2) == 0) return 6;         // 50% EREWEGO (chants.cpp:293)
            return diff - 1;                            // ONENIL..EASY (chants.cpp:297-300)
        }
        // Equaliser.
        if (g1 == g2 && g1 > 0) return 7;               // NOTSING (chants.cpp:304-307)
        return -1;
    }
}
