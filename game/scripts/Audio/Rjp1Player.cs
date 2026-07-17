using Godot;
using System;
using System.IO;

namespace OpenSwos.Audio;

/// <summary>
/// Software synth + sequencer for an AMIGA RJP1 MUSIC module (see
/// <see cref="Rjp1Module"/>). Paraphrase of the original 68k player in
/// external/original-amiga-swos (asm citations inline; PARAPHRASE only — no code
/// copied). Device-independent: a single PULL mixer (<see cref="GenerateStereo"/>)
/// drives both realtime playback and offline WAV rendering, so it can be verified
/// headless.
///
/// Key routines paraphrased:
///   • VBlank driver         sub_107738  @18520 — advance-count per 50 Hz tick
///   • pattern advance        sub_107840  @18643 — double-counter note stepping
///   • note-on               sub_107AB2  @19112 — sample restart + envelope init
///   • volume calc           sub_107944  @18871 — env * chanVol/64 * cap/64
///   • envelope / ramp       loc_10799E  @18924 — attack/decay/sustain/release
///   • pitch-slide (cmd 0x86) sub_10796C  @18898 / sub_107A54 @19066
///   • period table          unk_107EC0  @19832
///   • fade-in               sub_1070E2  @17510 — cap 0→0x23 in +2 steps
///   • hard-stop             sub_107092  @17465 — short fade to silence
/// </summary>
public sealed class Rjp1Player
{
    // ── constants ───────────────────────────────────────────────────────────────
    private const int PaulaClock = 3546895;     // PAL Paula: rateHz = clock / period
    private const int TickHz = 50;              // sequencer runs at 50 Hz
    private const int GlobalSpeed = 0x0D;       // documented driver speed (sub_107738)
    private const int FadeStep = 2;             // cap ramps ±2 per tick (sub_1070E2)
    private const int FadeCapTarget = 0x23;     // 35/64 — settled menu-music volume
    private const int StopFadeTicks = 3;        // short fade-out on Stop() (sub_107092)

    // Envelope phase values double as jump-table offsets in the asm (loc_10799E):
    private const int PhaseHold = 0;            // sustain / idle
    private const int PhaseDecay = 2;           // attackVol → sustainVol
    private const int PhaseAttack = 4;          // startVol → attackVol
    private const int PhaseRelease = 0xFF;      // → 0 (negative → clears on finish)

    // Note-period table (36 × u16 BE), baked from unk_107EC0 @19832. A note byte b
    // indexes this as PeriodTable[b/2] (b is an even BYTE offset into the u16 table).
    private static readonly ushort[] PeriodTable =
    {
        0x01C5,0x01E0,0x01FC,0x021A,0x023A,0x025C,0x0280,0x02A6,0x02D0,0x02FA,0x0328,0x0358,
        0x00E2,0x00F0,0x00FE,0x010D,0x011D,0x012E,0x0140,0x0153,0x0168,0x017D,0x0194,0x01AC,
        0x0071,0x0078,0x007F,0x0087,0x008F,0x0097,0x00A0,0x00AA,0x00B4,0x00BE,0x00CA,0x00D6,
    };

    // ── state ───────────────────────────────────────────────────────────────────
    private readonly Rjp1Module _mod;
    private readonly int _mixRate;
    private readonly int _samplesPerTick;
    private readonly sbyte[] _bank;             // private copy (note-on mutates slice heads)
    private readonly Channel[] _chan = new Channel[4];

    private int _samplesUntilTick;              // countdown to next SequencerTick
    private bool _active;                       // a song is playing
    private bool _halfSpeedToggle;              // sub_107738 &3==0 branch
    private int _cap = FadeCapTarget;           // global channel cap 0..64
    private int _capTarget = FadeCapTarget;
    private bool _stopping;
    private int _stopTicks;

    public int NoteOnCount { get; private set; }

    public Rjp1Player(Rjp1Module module, int mixRate = 44100)
    {
        _mod = module ?? throw new ArgumentNullException(nameof(module));
        _mixRate = mixRate;
        _samplesPerTick = Math.Max(1, mixRate / TickHz);   // 882 @44100
        _bank = (sbyte[])module.Bank.Clone();              // own copy: click-suppression mutates it
        for (int i = 0; i < 4; i++) _chan[i] = new Channel(_bank);
    }

    // ── public API ───────────────────────────────────────────────────────────────

    /// <summary>(Re)start a song: initialise the 4 channels from Songs[songIndex].</summary>
    public void PlaySong(int songIndex)
    {
        _active = true;
        _stopping = false;
        _halfSpeedToggle = false;
        _samplesUntilTick = 0;   // first GenerateStereo does the first tick
        _cap = FadeCapTarget;
        _capTarget = FadeCapTarget;

        int[] song = (songIndex >= 0 && songIndex < _mod.Songs.Length)
            ? _mod.Songs[songIndex] : new[] { 0, 0, 0, 0 };

        for (int c = 0; c < 4; c++)
        {
            var ch = _chan[c];
            ch.Reset();
            ch.Pan = (c == 0 || c == 3) ? Pan.Left : Pan.Right;   // Paula 0&3 L, 1&2 R
            int seqNum = c < song.Length ? song[c] : 0;
            if (seqNum <= 0 || seqNum >= _mod.SequenceOffsets.Length) { ch.Active = false; continue; }
            ch.Active = true;
            ch.SeqPtr = _mod.SequenceOffsets[seqNum];
            if (!SequenceAdvance(ch)) ch.Active = false;   // load first pattern
        }
    }

    /// <summary>Begin the menu-music fade-in: cap 0 → 0x23 in +2 steps (sub_1070E2).</summary>
    public void StartFadeIn()
    {
        _cap = 0;
        _capTarget = FadeCapTarget;
    }

    /// <summary>Set the global channel-cap target directly (0..64). Ramped ±2/tick.</summary>
    public void SetCap(int cap0to64) => _capTarget = Math.Clamp(cap0to64, 0, 64);

    /// <summary>Hard stop with a short 2-3 tick fade-out to avoid clicks (sub_107092).</summary>
    public void Stop()
    {
        if (!_active) return;
        _stopping = true;
        _stopTicks = StopFadeTicks;
    }

    /// <summary>
    /// PULL mixer: fill <paramref name="frameCount"/> stereo frames [L,R,L,R,…] in
    /// ~[-1,1]. Internally advances the 50 Hz sequencer. Returns frames written.
    /// This is the ONLY audio path (realtime + offline both use it).
    /// </summary>
    public int GenerateStereo(float[] interleavedLR, int frameCount)
    {
        int written = 0;
        while (written < frameCount)
        {
            if (_samplesUntilTick <= 0)
            {
                SequencerTick();
                _samplesUntilTick = _samplesPerTick;
            }
            int run = Math.Min(frameCount - written, _samplesUntilTick);
            for (int f = 0; f < run; f++)
            {
                float l = 0f, r = 0f;
                if (_active)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        var ch = _chan[c];
                        if (!ch.Voice.Playing) continue;
                        float s = ch.Voice.NextSample();
                        // soft pan: 0.75/0.25 split toward the panned side.
                        if (ch.Pan == Pan.Left) { l += s * 0.75f; r += s * 0.25f; }
                        else { l += s * 0.25f; r += s * 0.75f; }
                    }
                    float g = 0.5f * StopGain();     // headroom + fade-out
                    l *= g; r *= g;
                }
                int o = (written + f) * 2;
                interleavedLR[o] = Math.Clamp(l, -1f, 1f);
                interleavedLR[o + 1] = Math.Clamp(r, -1f, 1f);
            }
            written += run;
            _samplesUntilTick -= run;
        }
        return written;
    }

    private float StopGain() => _stopping ? (float)_stopTicks / StopFadeTicks : 1f;

    // ── sequencer ─────────────────────────────────────────────────────────────────

    private void SequencerTick()
    {
        if (!_active) return;

        // Stop fade-out (sub_107092): silence after StopFadeTicks ticks.
        if (_stopping)
        {
            if (_stopTicks > 0) _stopTicks--;
            if (_stopTicks == 0) { _active = false; foreach (var ch in _chan) ch.Voice.Playing = false; return; }
        }

        // Fade-in / cap ramp (sub_1070E2): move cap toward target in ±FadeStep steps.
        if (_cap < _capTarget) _cap = Math.Min(_cap + FadeStep, _capTarget);
        else if (_cap > _capTarget) _cap = Math.Max(_cap - FadeStep, _capTarget);

        // Advance-count per tick (sub_107738): normally GlobalSpeed&3 (==1 for 0x0D).
        int adv = GlobalSpeed & 3;
        if (adv == 0)                       // half-speed branch: advance every other tick
        {
            _halfSpeedToggle = !_halfSpeedToggle;
            adv = _halfSpeedToggle ? 1 : 0;
        }

        for (int c = 0; c < 4; c++)
        {
            var ch = _chan[c];
            if (!ch.Active) continue;
            for (int i = 0; i < adv; i++) PatternAdvance(ch);
            EnvelopeTick(ch);
            SlideTick(ch);
            CommitVoice(ch);
        }
    }

    // Double-counter note stepping (sub_107840 @18643): a note lasts tickSpeed*noteDur
    // ticks. Only when both counters expire do we read the next pattern token(s).
    private void PatternAdvance(Channel ch)
    {
        if (!ch.Active || ch.PatternPtr < 0) return;

        ch.TickSpeedCtr = (ch.TickSpeedCtr - 1) & 0xFF;
        if (ch.TickSpeedCtr != 0) return;

        ch.NoteDurCtr = (ch.NoteDurCtr - 1) & 0xFF;
        if (ch.NoteDurCtr != 0) { ch.TickSpeedCtr = ch.TickSpeed; return; }

        ReadTokens(ch);
        ch.NoteDurCtr = ch.NoteDur;
        ch.TickSpeedCtr = ch.TickSpeed;
    }

    // Read pattern tokens until one consumes the note slot (a note, release, or hold).
    private void ReadTokens(Channel ch)
    {
        byte[] pat = _mod.PatternData;
        int guard = 0;
        while (ch.Active && guard++ < 4096)
        {
            if (ch.PatternPtr < 0 || ch.PatternPtr >= pat.Length) { StopChannel(ch); return; }
            int b = pat[ch.PatternPtr++];

            if (b < 0x80)                                   // NOTE
            {
                int idx = b >> 1;                           // even byte offset → u16 index
                int period = (idx >= 0 && idx < PeriodTable.Length) ? PeriodTable[idx] : 0;
                NoteOn(ch, period);
                return;
            }

            switch (b)
            {
                case 0x80:                                  // end-of-pattern → sequence advance
                    if (!SequenceAdvance(ch)) return;       // stopped
                    continue;                               // read on from the new pattern
                case 0x81:                                  // release: envelope ramps → 0
                    EnterRelease(ch);
                    return;                                 // voice keeps ringing
                case 0x82:                                  // set tickSpeed
                    ch.TickSpeed = NextByte(ch, pat);
                    continue;
                case 0x83:                                  // set noteDur
                    ch.NoteDur = NextByte(ch, pat);
                    continue;
                case 0x84:                                  // set instrument
                    SetInstrument(ch, NextByte(ch, pat));
                    continue;
                case 0x85:                                  // set pattern channel volume
                    ch.PatternChanVol = NextByte(ch, pat);
                    continue;
                case 0x86:                                  // pitch slide (5 operand bytes)
                    ch.SlideCount = NextByte(ch, pat);
                    ch.SlideInc = NextI32(ch, pat);
                    continue;
                case 0x87:                                  // hold / rest: keep ringing, no note-on
                    return;
                default:
                    continue;
            }
        }
        StopChannel(ch);   // runaway guard
    }

    private byte NextByte(Channel ch, byte[] pat)
    {
        if (ch.PatternPtr < 0 || ch.PatternPtr >= pat.Length) return 0;
        return pat[ch.PatternPtr++];
    }

    private int NextI32(Channel ch, byte[] pat)
    {
        int v = 0;
        for (int i = 0; i < 4; i++) v = (v << 8) | NextByte(ch, pat);   // signed BE int32
        return v;
    }

    // Sequence advance on 0x80 (paraphrase sub_107840's sequence branch). Loops until
    // it loads a pattern or stops the channel. Returns false when the channel stops.
    private bool SequenceAdvance(Channel ch)
    {
        byte[] seq = _mod.SequenceData;
        int guard = 0;
        while (guard++ < 256)
        {
            if (ch.SeqPtr < 0 || ch.SeqPtr >= seq.Length) { StopChannel(ch); return false; }
            int b = seq[ch.SeqPtr++];
            if (b != 0)                                     // pattern number
            {
                if (b < 0 || b >= _mod.PatternOffsets.Length) { StopChannel(ch); return false; }
                ch.PatternPtr = _mod.PatternOffsets[b];
                return true;
            }
            // b == 0 → look at the FOLLOWING marker byte (do not post-increment it).
            int nn = (ch.SeqPtr < seq.Length) ? seq[ch.SeqPtr] : 0;
            if (nn == 0) { StopChannel(ch); return false; }         // STOP
            if (nn >= 0x80)                                          // new sequence number follows
            {
                int newSeq = (ch.SeqPtr + 1 < seq.Length) ? seq[ch.SeqPtr + 1] : 0;
                if (newSeq <= 0 || newSeq >= _mod.SequenceOffsets.Length) { StopChannel(ch); return false; }
                ch.SeqPtr = _mod.SequenceOffsets[newSeq];
            }
            else
            {
                ch.SeqPtr -= nn;                                     // relative loop-back
            }
            // loop: read the pattern at the new sequence position
        }
        StopChannel(ch);
        return false;
    }

    private static void StopChannel(Channel ch)
    {
        ch.Active = false;
        ch.PatternPtr = -1;
        ch.Voice.Playing = false;
    }

    // Bind an instrument to a channel (paraphrase sub_107840's 0x84 handler). 0 = no-op;
    // resets pattern channel volume to the instrument default.
    private void SetInstrument(Channel ch, int nn)
    {
        if (nn == 0) return;
        if (nn < 0 || nn >= _mod.Instruments.Length) return;
        ch.Instrument = nn;
        ch.PatternChanVol = _mod.Instruments[nn].DefaultVolume;
    }

    // Note-on (paraphrase sub_107AB2 @19112): set base period, clear slide, init the
    // ramp envelope, restart the sample from its one-shot region (loop points baked in).
    private void NoteOn(Channel ch, int period)
    {
        NoteOnCount++;
        ch.BasePeriod = period;
        ch.SlideAccum = 0;                      // clr.l $46
        ch.SlideCount = 0;

        // Envelope init from the bound instrument's ramp entry.
        if (ch.Instrument >= 0 && ch.Instrument < _mod.Instruments.Length)
        {
            var it = _mod.Instruments[ch.Instrument];
            if (it.RampEntry >= 0 && it.RampEntry < _mod.Ramps.Length)
            {
                var r = _mod.Ramps[it.RampEntry];
                ch.EnvTarget = r.AttackVol;
                ch.EnvDelta = r.AttackVol - r.StartVol;
                ch.EnvTotal = ch.EnvRemaining = r.AttackSpeed;
                ch.EnvPhase = PhaseAttack;
                ch.EnvCurrent = r.StartVol;
            }
            else
            {
                ch.EnvPhase = PhaseHold;
                ch.EnvCurrent = it.DefaultVolume;
            }

            // Sample slice (8-bit signed; byte index == sample index).
            int byteStart = Rjp1Module.InsBase + it.SampleOffset;
            int oneShotStart = byteStart + 2 * it.OneShotStart;
            int oneShotEnd = oneShotStart + 2 * it.OneShotLength;
            int loopStart = byteStart + 2 * it.LoopStart;
            int loopEnd = loopStart + 2 * it.LoopLength;

            // Click suppression: zero the first WORD of the one-shot region (asm clr.w).
            if (oneShotStart >= 0 && oneShotStart + 1 < _bank.Length)
            {
                _bank[oneShotStart] = 0;
                _bank[oneShotStart + 1] = 0;
            }

            ch.Voice.Start(oneShotStart, oneShotEnd, loopStart, loopEnd, it.HasLoop);
        }
    }

    private void EnterRelease(Channel ch)
    {
        int rel = 1;
        if (ch.Instrument >= 0 && ch.Instrument < _mod.Instruments.Length)
        {
            var it = _mod.Instruments[ch.Instrument];
            if (it.RampEntry >= 0 && it.RampEntry < _mod.Ramps.Length)
                rel = _mod.Ramps[it.RampEntry].ReleaseSpeed;
        }
        ch.EnvTarget = 0;
        ch.EnvDelta = 0 - ch.EnvCurrent;
        ch.EnvTotal = ch.EnvRemaining = rel;
        ch.EnvPhase = PhaseRelease;
    }

    // Per-tick envelope (paraphrase loc_10799E @18924). vol = target - delta*remaining/total;
    // on countdown-wrap, advance the phase (attack→decay→hold, release→silence).
    private void EnvelopeTick(Channel ch)
    {
        if (ch.EnvPhase == PhaseHold) return;   // sustain: hold EnvCurrent

        int d0;
        int delta = (sbyte)ch.EnvDelta;         // signed
        if (delta == 0) d0 = 0;
        else
        {
            int rem = ch.EnvRemaining & 0xFF;
            int tot = ch.EnvTotal & 0xFF;
            if (rem == 0 || tot == 0) d0 = 0;
            else d0 = (delta * rem) / tot;
        }
        ch.EnvCurrent = ((ch.EnvTarget - d0) & 0xFF);

        int before = ch.EnvRemaining & 0xFF;
        ch.EnvRemaining = (before - 1) & 0xFF;
        if (before == 0)                         // wrapped 0 → 0xFF: phase finished
        {
            if ((sbyte)ch.EnvPhase < 0)          // release: clear
                ch.EnvPhase = PhaseHold;
            else
                AdvancePhase(ch);
        }
    }

    // Phase transition jump-table (word_1079F0): attack(4) → decay(2), decay(2) → hold(0).
    private void AdvancePhase(Channel ch)
    {
        if (ch.Instrument < 0 || ch.Instrument >= _mod.Instruments.Length) { ch.EnvPhase = PhaseHold; return; }
        var it = _mod.Instruments[ch.Instrument];
        if (it.RampEntry < 0 || it.RampEntry >= _mod.Ramps.Length) { ch.EnvPhase = PhaseHold; return; }
        var r = _mod.Ramps[it.RampEntry];

        if (ch.EnvPhase == PhaseAttack)          // → decay: attackVol → sustainVol
        {
            ch.EnvTarget = r.SustainVol;
            ch.EnvDelta = r.SustainVol - r.AttackVol;
            ch.EnvTotal = ch.EnvRemaining = r.DecaySpeed;
            ch.EnvPhase = PhaseDecay;
        }
        else                                     // decay → hold at sustainVol
        {
            ch.EnvPhase = PhaseHold;
        }
    }

    // 0x86 pitch slide accumulation (paraphrase sub_107A54's $40/$42/$46 block).
    private void SlideTick(Channel ch)
    {
        if (ch.SlideCount > 0) { ch.SlideAccum += ch.SlideInc; ch.SlideCount--; }
    }

    // Compute this tick's constant voice gain + resample step from the current
    // envelope, pattern channel volume, cap and (slid) period.
    private void CommitVoice(Channel ch)
    {
        // Final volume (sub_107944 @18871): env * chanVol/64 * cap/64, clamped 0..64.
        int vol = ch.EnvCurrent & 0xFF;
        vol = (vol * ch.PatternChanVol) >> 6;
        vol = (vol * _cap) >> 6;
        vol = Math.Clamp(vol, 0, 64);
        ch.Voice.Gain = vol / 64f;

        int period = ch.BasePeriod + (ch.SlideAccum >> 16);   // sub_107A54: period + accum.hi
        if (period < 1) period = 1;
        double rate = (double)PaulaClock / period;
        ch.Voice.Step = rate / _mixRate;
    }

    // ── offline render / verification ────────────────────────────────────────────

    /// <summary>
    /// Render a song to a 16-bit stereo WAV for headless verification. Returns
    /// (frames, rmsL, rmsR, peak, noteOns) and logs the stats.
    /// </summary>
    public static (int frames, double rmsL, double rmsR, float peak, int noteOns)
        RenderSongToWav(Rjp1Module module, int songIndex, double seconds, string outWavPath, int mixRate = 44100)
    {
        var player = new Rjp1Player(module, mixRate);
        player.PlaySong(songIndex);
        player.StartFadeIn();

        int total = (int)(seconds * mixRate);
        var pcm = new byte[total * 4];          // 16-bit stereo
        var block = new float[2048 * 2];
        double sumL = 0, sumR = 0;
        float peak = 0f;
        int done = 0;
        while (done < total)
        {
            int want = Math.Min(2048, total - done);
            player.GenerateStereo(block, want);
            for (int f = 0; f < want; f++)
            {
                float l = block[f * 2], r = block[f * 2 + 1];
                sumL += (double)l * l; sumR += (double)r * r;
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                short sl = (short)Math.Clamp((int)MathF.Round(l * 32767f), -32768, 32767);
                short sr = (short)Math.Clamp((int)MathF.Round(r * 32767f), -32768, 32767);
                int o = (done + f) * 4;
                pcm[o] = (byte)(sl & 0xFF); pcm[o + 1] = (byte)((sl >> 8) & 0xFF);
                pcm[o + 2] = (byte)(sr & 0xFF); pcm[o + 3] = (byte)((sr >> 8) & 0xFF);
            }
            done += want;
        }

        WriteWav(outWavPath, pcm, mixRate);

        double rmsL = Math.Sqrt(sumL / Math.Max(1, total));
        double rmsR = Math.Sqrt(sumR / Math.Max(1, total));
        GD.Print($"[music] render: {total} frames, rmsL={rmsL:F4} rmsR={rmsR:F4} " +
                 $"peak={peak:F4} noteOns={player.NoteOnCount} → {outWavPath}");
        return (total, rmsL, rmsR, peak, player.NoteOnCount);
    }

    private static void WriteWav(string path, byte[] pcm, int mixRate)
    {
        const int channels = 2, bits = 16;
        int byteRate = mixRate * channels * bits / 8;
        int blockAlign = channels * bits / 8;
        using var fs = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + pcm.Length);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                        // fmt chunk size
        w.Write((short)1);                  // PCM
        w.Write((short)channels);
        w.Write(mixRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(pcm.Length);
        w.Write(pcm);
    }

    // ── per-channel state ──────────────────────────────────────────────────────────

    private enum Pan { Left, Right }

    private sealed class Channel
    {
        public bool Active;
        public Pan Pan;

        // sequencer position
        public int SeqPtr;
        public int PatternPtr = -1;

        // double counters (sub_107840): note length = TickSpeed * NoteDur ticks.
        public int TickSpeed = 6, TickSpeedCtr = 1;
        public int NoteDur = 1, NoteDurCtr = 1;

        public int Instrument = -1;
        public int PatternChanVol = 64;

        // envelope (loc_10799E)
        public int EnvPhase = PhaseHold;
        public int EnvTarget, EnvDelta, EnvTotal, EnvRemaining;
        public int EnvCurrent;

        // period + 0x86 slide (sub_107A54)
        public int BasePeriod = 1;
        public int SlideCount;
        public int SlideInc;
        public int SlideAccum;

        public readonly Voice Voice;

        public Channel(sbyte[] bank) => Voice = new Voice(bank);

        public void Reset()
        {
            SeqPtr = 0; PatternPtr = -1;
            TickSpeed = 6; TickSpeedCtr = 1;
            NoteDur = 1; NoteDurCtr = 1;
            Instrument = -1; PatternChanVol = 64;
            EnvPhase = PhaseHold; EnvTarget = EnvDelta = EnvTotal = EnvRemaining = EnvCurrent = 0;
            BasePeriod = 1; SlideCount = SlideInc = SlideAccum = 0;
            Voice.Playing = false;
        }
    }

    // Software Paula voice: linear-interpolating 8-bit-signed resampler with baked
    // one-shot → loop points, at a per-tick constant gain.
    private sealed class Voice
    {
        private readonly sbyte[] _bank;
        public bool Playing;
        public double Pos;
        public double Step;
        public float Gain;

        private int _oneShotEnd, _loopStart, _loopEnd;
        private bool _hasLoop, _inLoop;

        public Voice(sbyte[] bank) => _bank = bank;

        public void Start(int oneShotStart, int oneShotEnd, int loopStart, int loopEnd, bool hasLoop)
        {
            Pos = oneShotStart;
            _oneShotEnd = oneShotEnd;
            _loopStart = loopStart;
            _loopEnd = loopEnd;
            _hasLoop = hasLoop && loopEnd > loopStart;
            _inLoop = false;
            Playing = true;
        }

        public float NextSample()
        {
            if (!Playing) return 0f;
            int i = (int)Pos;
            if (i < 0 || i >= _bank.Length) { Playing = false; return 0f; }

            int j = i + 1;
            if (_inLoop) { if (j >= _loopEnd) j = _loopStart; }
            else if (j >= _oneShotEnd) j = _hasLoop ? _loopStart : i;
            if (j < 0 || j >= _bank.Length) j = i;

            float frac = (float)(Pos - i);
            float s0 = _bank[i] / 128f;
            float s1 = _bank[j] / 128f;
            float sample = (s0 + (s1 - s0) * frac) * Gain;

            Pos += Step;
            if (!_inLoop)
            {
                if (Pos >= _oneShotEnd)
                {
                    if (_hasLoop) { Pos = _loopStart + (Pos - _oneShotEnd); _inLoop = true; }
                    else Playing = false;
                }
            }
            else
            {
                double span = _loopEnd - _loopStart;
                if (span > 0) while (Pos >= _loopEnd) Pos -= span;
            }
            return sample;
        }
    }
}
