using Godot;

namespace OpenSwos.Audio;

/// <summary>
/// Commentary engine — paraphrase of swos-port src/audio/comments.cpp
/// (playEnqueuedSamples comments.cpp:180-210, PlayXxxComment comments.cpp:310-468,
/// end-game comments.cpp:110-138). ONE logical commentary channel on
/// <see cref="MatchAudio"/>: "big" comments interrupt whatever is playing,
/// "small" comments yield (skip if anything is playing).
///
/// Group→sample tables (with 2× weighting duplicated as repeated entries) are
/// transcribed from docs/SWOS/sound.txt:337-639 / swos.asm:222750-222987.
/// Preloaded groups live in SFX\PIERCE; on-demand (enqueued) groups live in HARD.
/// Uses its own audio RNG; only READS sim state. Missing files degrade silently.
/// </summary>
internal sealed class CommentaryEngine
{
    private readonly MatchAudio _audio;
    private readonly System.Random _rng;

    // filename → loaded stream (null = missing). "" entries are silent slots.
    private readonly System.Collections.Generic.Dictionary<string, AudioStreamWav?> _cache = new();
    private string _last = "";

    // AMIGA has no spoken commentary (verified from asm — the only playback path
    // is the RJP1 player). When disabled every Play/Enqueue/Tick no-ops.
    private bool _enabled = true;
    public void SetEnabled(bool on) => _enabled = on;

    // ---- Preloaded groups (SFX\PIERCE) — sound.txt:443-564 --------------------
    private static readonly string[] Goal =
        { "M158_1", "M158_1", "M158_2", "M158_2", "M158_3", "M158_4", "M158_5", "M158_7" };
    private static readonly string[] OwnGoal =
        { "M158_P", "M158_Q", "M158_S", "M158_S", "M158_T", "M158_T", "M158_X", "M158_Y" };
    private static readonly string[] KeeperSaved =
        { "M313_7", "M313_7", "M313_A", "M313_B", "M196_W", "M196_X", "M196_Z", "M233_1" };
    private static readonly string[] KeeperClaimed =
        { "M313_6", "M313_8", "M313_9", "M313_C", "M313_D", "", "M313_G", "M313_H" };
    private static readonly string[] NearMiss =
        { "M10_J", "M10_K", "M10_D", "M10_E" };
    private static readonly string[] PostHit =
        { "M10_D", "M10_E", "M10_F", "M10_G", "M10_H", "M10_R", "M10_S", "M10_T" };
    private static readonly string[] BarHit =
        { "M10_I", "M10_U" };
    private static readonly string[] GoodTackle =
        { "M349_7", "M349_8", "M349_E", "M365_1" };
    private static readonly string[] GoodPass =
        { "M313_I", "M313_J", "M349_4", "M313_O" };
    private static readonly string[] Headers =
        { "M33_2", "M33_3", "M33_4", "M33_5", "M33_6", "M33_8", "M33_A", "M33_B" };
    private static readonly string[] Foul =
        { "M158_Z", "M158_Z", "M195", "M196_1", "M196_2", "M196_4", "M196_6", "M196_7" };
    private static readonly string[] DangerousPlay =
        { "M158_Z", "M158_Z", "M195", "M196_2" };
    private static readonly string[] PenaltyGiven =
        { "M196_K", "M196_L", "M196_M", "M196_N" };
    private static readonly string[] PenaltySaved =
        { "M196_T", "M196_U", "M196_V", "M313_A", "M196_Z", "M233_1", "M196_W", "M313_7" };
    private static readonly string[] PenaltyMissed =
        { "M233_2", "M233_4", "M233_5", "M233_6" };
    private static readonly string[] PenaltyGoal =
        { "M233_7", "M233_8", "M233_9", "M233_C" };

    // ---- On-demand groups (HARD) — sound.txt:566-639 -------------------------
    private static readonly string[] Corner =
        { "M10_V", "M10_W", "M10_W", "M10_Y", "M10_Y", "M313_1", "M313_2", "M313_3" };
    private static readonly string[] TacticsChanged =
        { "M10_5", "M10_5", "M10_7", "M10_7", "M10_8", "M10_9", "M10_A", "M10_B" };
    private static readonly string[] Substitute =
        { "M233_J", "M233_J", "M233_K", "M233_K", "M233_L", "M233_M", "M10_3", "M10_4" };
    private static readonly string[] RedCard =
    {
        "M196_8", "M196_8", "M196_9", "M196_9", "M196_A", "M196_A", "M196_B", "M196_B",
        "M196_C", "M196_D", "M196_E", "M196_F", "M196_G", "M196_H", "M196_I", "M196_J",
    };
    private static readonly string[] YellowCard =
    {
        "M443_7", "M443_7", "M443_8", "M443_8", "M443_9", "M443_9",
        "M443_A", "M443_B", "M443_C", "M443_D", "M443_E", "M443_F", "M443_G", "M443_H", "M443_I", "M443_J",
    };
    private static readonly string[] ThrowIn =
        { "M406_3", "M406_7", "M406_8", "M406_9" };
    private static readonly string[] EndGameRout =
        { "M406_H" };
    private static readonly string[] EndGameSensational =
        { "M406_I", "M406_J" };

    // ---- Enqueue delays (comments.cpp:11-12) ---------------------------------
    private const int DelayShort = 70;   // throw-in, substitute, tactics
    private const int DelayLong = 100;   // corner, yellow card, red card
    private const int DelayGoodPass = 10;

    // Per-kind countdown timers (-1 = inactive). comments.cpp:180-205.
    private int _tYellow = -1, _tRed = -1, _tGoodPass = -1,
                _tThrowIn = -1, _tCorner = -1, _tSub = -1, _tTactics = -1;

    // comments.cpp:387-391 — set by Penalty(), rewrites goal/miss/save outcomes
    // to their penalty variants until cleared by the outcome.
    private bool _performingPenalty;

    public CommentaryEngine(MatchAudio audio, System.Random rng)
    {
        _audio = audio;
        _rng = rng;
    }

    /// <summary>Preload the PIERCE commentary groups. Returns (loaded, missing).</summary>
    public (int loaded, int missing) Load()
    {
        int loaded = 0, missing = 0;
        foreach (string[] grp in new[]
                 {
                     Goal, OwnGoal, KeeperSaved, KeeperClaimed, NearMiss, PostHit, BarHit,
                     GoodTackle, GoodPass, Headers, Foul, DangerousPlay, PenaltyGiven,
                     PenaltySaved, PenaltyMissed, PenaltyGoal,
                 })
            foreach (string f in grp)
            {
                if (f.Length == 0) continue;   // silent slot
                if (Cache(f) != null) loaded++; else missing++;
            }
        return (loaded, missing);
    }

    public void Reset()
    {
        _tYellow = _tRed = _tGoodPass = _tThrowIn = _tCorner = _tSub = _tTactics = -1;
        _performingPenalty = false;
        _last = "";
    }

    // Load-or-fetch a sample (cached, so on-demand HARD groups only hit disk once).
    private AudioStreamWav? Cache(string file)
    {
        if (_cache.TryGetValue(file, out AudioStreamWav? s)) return s;
        s = RawSample.Load(file);
        _cache[file] = s;
        return s;
    }

    // Pick a random (2×-weighted) sample from a group, avoiding an immediate
    // repeat of the last-played filename (comments.cpp:100-106,344).
    private AudioStreamWav? Pick(string[] group)
    {
        if (group.Length == 0) return null;
        string file = group[_rng.Next(group.Length)];
        for (int tries = 0; tries < 4 && group.Length > 1 && file == _last; tries++)
            file = group[_rng.Next(group.Length)];
        _last = file;
        return file.Length == 0 ? null : Cache(file);
    }

    private void PlayBig(string[] group) { if (_enabled) _audio.PlayCommentary(Pick(group), interrupt: true); }
    private void PlaySmall(string[] group) { if (_enabled) _audio.PlayCommentary(Pick(group), interrupt: false); }

    // ---- Big (interrupting) comments -----------------------------------------
    public void PlayGoal()
    {
        if (_performingPenalty) { _performingPenalty = false; PlayBig(PenaltyGoal); }
        else PlayBig(Goal);
    }

    public void PlayOwnGoal() => PlayBig(OwnGoal);

    public void PlayNearMiss()
    {
        if (_performingPenalty) { _performingPenalty = false; PlayBig(PenaltyMissed); }
        else PlayBig(NearMiss);
    }

    public void PlayKeeperSaved()
    {
        if (_performingPenalty) { _performingPenalty = false; PlayBig(PenaltySaved); }
        else PlayBig(KeeperSaved);
    }

    public void PlayKeeperClaimed() => PlayBig(KeeperClaimed);
    public void PlayPostHit() => PlayBig(PostHit);
    public void PlayBarHit() => PlayBig(BarHit);
    public void PlayFoul() => PlayBig(Foul);
    public void PlayDangerousPlay() => PlayBig(DangerousPlay);
    public void PlayInjury() => PlayBig(DangerousPlay);   // injury shares the dirty-play pool
    public void PlayPenalty() { _performingPenalty = true; PlayBig(PenaltyGiven); }

    // ---- Small (yielding) comments -------------------------------------------
    public void PlayGoodTackle() => PlaySmall(GoodTackle);
    public void PlayHeader() => PlaySmall(Headers);

    // ---- Enqueued (on-demand) comments ---------------------------------------
    public void EnqueueCorner()   { if (_enabled) _tCorner = DelayLong; }
    public void EnqueueThrowIn()  { if (_enabled) _tThrowIn = DelayShort; }
    public void EnqueueSubstitute() { if (_enabled) _tSub = DelayShort; }
    public void EnqueueTactics()  { if (_enabled) _tTactics = DelayShort; }
    public void EnqueueRedCard()  { if (_enabled) _tRed = DelayLong; }
    public void EnqueueYellowCard() { if (_enabled) _tYellow = DelayLong; }
    public void EnqueueGoodPass()  { if (_enabled) _tGoodPass = DelayGoodPass; }
    public void CancelGoodPass()   { _tGoodPass = -1; }

    // comments.cpp:180-205 — drain the enqueue timers. Priority order
    // yellow → red → good pass → throw-in → corner → substitute → tactics, with
    // AT MOST ONE comment started per frame.
    public void Tick()
    {
        if (!_enabled) return;
        if (_tYellow >= 0)  { if (--_tYellow < 0) { PlayBig(YellowCard); return; } }
        if (_tRed >= 0)     { if (--_tRed < 0)    { PlayBig(RedCard); return; } }
        if (_tGoodPass >= 0){ if (--_tGoodPass < 0) { PlaySmall(GoodPass); return; } }
        if (_tThrowIn >= 0) { if (--_tThrowIn < 0) { PlayBig(ThrowIn); return; } }
        if (_tCorner >= 0)  { if (--_tCorner < 0)  { PlayBig(Corner); return; } }
        if (_tSub >= 0)     { if (--_tSub < 0)     { PlayBig(Substitute); return; } }
        if (_tTactics >= 0) { if (--_tTactics < 0) { PlayBig(TacticsChanged); return; } }
    }

    // comments.cpp:130-137 — result comment at full time. Rout (|diff|>=3) wins
    // over sensational (total>=4); the draw "so close" line never plays in the
    // original (kept faithful — omitted).
    public void PlayEndGameComment(int g1, int g2)
    {
        if (System.Math.Abs(g1 - g2) >= 3) PlayBig(EndGameRout);
        else if (g1 != g2 && g1 + g2 >= 4) PlayBig(EndGameSensational);
        // draw → "so close" is the original bug: never plays.
    }
}
