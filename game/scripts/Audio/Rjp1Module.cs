using Godot;
using OpenSwos.Tools.RncDecode;
using System.Collections.Generic;
using System.IO;

namespace OpenSwos.Audio;

/// <summary>
/// Parser for an AMIGA SWOS RJP1 ("Richard Joseph Player") MUSIC module — the
/// song + sample-bank pair that drives the menu/title tunes (MENU.SNG+MENU.INS,
/// HERO.SNG+HERO.INS, …). Paraphrase of our RE of the original 68k player in
/// external/original-amiga-swos (asm line citations inline; PARAPHRASE only —
/// no code copied). Consumed by <see cref="Rjp1Player"/>, a software synth.
///
/// This is the sibling of <see cref="AmigaSfxBank"/> (the in-match SFX reader):
/// same file-discovery + big-endian + instrument-field idiom, but this one keeps
/// the FULL song/pattern/sequence data so a sequencer can play the tune, rather
/// than slicing a handful of one-shot SFX samples.
///
/// SNG layout (all BIG-ENDIAN): 8-byte magic "RJP1SMOD", then 7 sections, each a
/// u32 length + payload:
///   sec0 = instrument table       (32 bytes/entry)
///   sec1 = volume-ramp entries     (6 bytes/entry)
///   sec2 = songs                   (4 bytes/entry = seqNum per channel)
///   sec3 = sequence-offset table   (u32 each → offset into sec5)
///   sec4 = pattern-offset table    (u32 each → offset into sec6)
///   sec5 = sequence byte-data
///   sec6 = pattern byte-code
///
/// INS = the sample bank: 8-bit SIGNED PCM. insBase = 4 skips the "RJP1" magic.
/// If the INS starts with "RNC" it is RNC-compressed → decode first (HERO.INS is
/// RNC; MENU.INS is a bare "RJP1"). insBase stays 4 on the decoded output.
///
/// Pure host-side asset helper: never touches sim state, degrades to null when
/// the files are absent / malformed.
/// </summary>
public sealed class Rjp1Module
{
    public const int InsBase = 4;              // skip "RJP1" magic of the sample bank
    private const int InstrumentStride = 32;
    private const int SectionCount = 7;

    /// <summary>One sec0 instrument entry (all fields big-endian; asm 19112, 18234).</summary>
    public readonly struct Instrument
    {
        public readonly int SampleOffset;          // +0x00 u32, relative to insBase
        public readonly int PitchEnvStreamOffset;  // +0x04 u32 (0 = none)
        public readonly int VolEnvStreamOffset;    // +0x08 u32 (0 = none)
        public readonly int RampByteOffset;        // +0x0C u16 byte-offset into sec1 (entry = /6)
        public readonly int DefaultVolume;         // +0x0E u16 (0..64)
        public readonly int OneShotStart;          // +0x10 u16 (WORDS)
        public readonly int OneShotLength;         // +0x12 u16 (WORDS; 1 = dead slot)
        public readonly int LoopStart;             // +0x14 u16 (WORDS)
        public readonly int LoopLength;            // +0x16 u16 (WORDS; 1 = no loop)

        public int RampEntry => RampByteOffset / 6;
        public bool HasLoop => LoopLength > 1;
        public bool Dead => OneShotLength <= 1;

        public Instrument(int sampleOffset, int pitchEnv, int volEnv, int rampByteOffset,
                          int defVol, int oneShotStart, int oneShotLength, int loopStart, int loopLength)
        {
            SampleOffset = sampleOffset; PitchEnvStreamOffset = pitchEnv; VolEnvStreamOffset = volEnv;
            RampByteOffset = rampByteOffset; DefaultVolume = defVol;
            OneShotStart = oneShotStart; OneShotLength = oneShotLength;
            LoopStart = loopStart; LoopLength = loopLength;
        }
    }

    /// <summary>One sec1 volume-ramp entry (6 bytes; envelope loc_10799E @18924).</summary>
    public readonly struct Ramp
    {
        public readonly int StartVol, AttackVol, AttackSpeed, SustainVol, DecaySpeed, ReleaseSpeed;
        public Ramp(int startVol, int attackVol, int attackSpeed, int sustainVol, int decaySpeed, int releaseSpeed)
        {
            StartVol = startVol; AttackVol = attackVol; AttackSpeed = attackSpeed;
            SustainVol = sustainVol; DecaySpeed = decaySpeed; ReleaseSpeed = releaseSpeed;
        }
    }

    // ── parsed fields (public, consumed by Rjp1Player) ──────────────────────────
    public Instrument[] Instruments { get; private set; } = System.Array.Empty<Instrument>();
    public Ramp[] Ramps { get; private set; } = System.Array.Empty<Ramp>();
    public int[][] Songs { get; private set; } = System.Array.Empty<int[]>();      // each = int[4] channel→seqNum
    public int[] SequenceOffsets { get; private set; } = System.Array.Empty<int>(); // → offset into SequenceData
    public int[] PatternOffsets { get; private set; } = System.Array.Empty<int>();  // → offset into PatternData
    public byte[] SequenceData { get; private set; } = System.Array.Empty<byte>();  // sec5
    public byte[] PatternData { get; private set; } = System.Array.Empty<byte>();   // sec6
    public sbyte[] Bank { get; private set; } = System.Array.Empty<sbyte>();        // decoded INS, 8-bit signed

    // ── discovery (mirrors AmigaSfxBank) ────────────────────────────────────────

    private static readonly HashSet<string> SkipDirs =
        new(System.StringComparer.OrdinalIgnoreCase)
        { ".git", ".godot", ".import", "bin", "obj", "node_modules", ".vs", ".idea", ".backups" };

    private static IEnumerable<string> DiscoveryRoots()
    {
        foreach (string r in OpenSwos.Assets.DataPaths.OutputSearchRoots())
            yield return r;
        yield return ProjectSettings.GlobalizePath("res://../assets/extracted/amiga");
    }

    // Index every "<baseName>.SNG"/".INS" under the roots into an UPPER→path map.
    private static Dictionary<string, string> IndexFiles(string baseName)
    {
        string sng = (baseName + ".SNG").ToUpperInvariant();
        string ins = (baseName + ".INS").ToUpperInvariant();
        var index = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string root in DiscoveryRoots())
            IndexRoot(root, sng, ins, index);
        return index;
    }

    private static void IndexRoot(string root, string sng, string ins,
                                  Dictionary<string, string> index, int maxDepth = 6)
    {
        if (string.IsNullOrEmpty(root)) return;
        try { if (!Directory.Exists(root)) return; } catch { return; }

        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = System.Array.Empty<string>(); }
            foreach (string f in files)
            {
                string key = Path.GetFileName(f).ToUpperInvariant();
                if ((key == sng || key == ins) && !index.ContainsKey(key))
                    index[key] = f;
            }

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string s in subs)
                if (!SkipDirs.Contains(Path.GetFileName(s)))
                    queue.Enqueue((s, depth + 1));
        }
    }

    /// <summary>True when both &lt;baseName&gt;.SNG and .INS are discoverable.</summary>
    public static bool Available(string baseName)
    {
        var idx = IndexFiles(baseName);
        return idx.ContainsKey((baseName + ".SNG").ToUpperInvariant())
            && idx.ContainsKey((baseName + ".INS").ToUpperInvariant());
    }

    // ── parse ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Discover, read and parse the &lt;baseName&gt; RJP1 module (e.g. "MENU"),
    /// or null if the files are missing / malformed. Logs a one-line summary.
    /// </summary>
    public static Rjp1Module? Load(string baseName)
    {
        var idx = IndexFiles(baseName);
        if (!idx.TryGetValue((baseName + ".SNG").ToUpperInvariant(), out string? sngPath)) return null;
        if (!idx.TryGetValue((baseName + ".INS").ToUpperInvariant(), out string? insPath)) return null;

        byte[] sng, insRaw;
        try { sng = File.ReadAllBytes(sngPath); insRaw = File.ReadAllBytes(insPath); }
        catch { return null; }

        try { return Parse(sng, insRaw); }
        catch { return null; }
    }

    private static Rjp1Module? Parse(byte[] sng, byte[] insRaw)
    {
        // magic "RJP1SMOD"
        if (sng.Length < 12 || sng[0] != 'R' || sng[1] != 'J' || sng[2] != 'P' || sng[3] != '1'
            || sng[4] != 'S' || sng[5] != 'M' || sng[6] != 'O' || sng[7] != 'D')
            return null;

        // Walk the 7 length-prefixed sections generically.
        var secStart = new int[SectionCount];
        var secLen = new int[SectionCount];
        int pos = 8;
        for (int s = 0; s < SectionCount; s++)
        {
            if (pos + 4 > sng.Length) return null;
            int len = Be32(sng, pos);
            int start = pos + 4;
            if (start + len > sng.Length || len < 0) return null;
            secStart[s] = start;
            secLen[s] = len;
            pos = start + len;
        }

        var m = new Rjp1Module();

        // sec0 — instruments (32 bytes each).
        int nInstr = secLen[0] / InstrumentStride;
        m.Instruments = new Instrument[nInstr];
        for (int i = 0; i < nInstr; i++)
        {
            int e = secStart[0] + i * InstrumentStride;
            m.Instruments[i] = new Instrument(
                Be32(sng, e + 0x00), Be32(sng, e + 0x04), Be32(sng, e + 0x08),
                Be16(sng, e + 0x0C), Be16(sng, e + 0x0E),
                Be16(sng, e + 0x10), Be16(sng, e + 0x12),
                Be16(sng, e + 0x14), Be16(sng, e + 0x16));
        }

        // sec1 — volume-ramp entries (6 bytes each).
        int nRamp = secLen[1] / 6;
        m.Ramps = new Ramp[nRamp];
        for (int i = 0; i < nRamp; i++)
        {
            int e = secStart[1] + i * 6;
            m.Ramps[i] = new Ramp(sng[e], sng[e + 1], sng[e + 2], sng[e + 3], sng[e + 4], sng[e + 5]);
        }

        // sec2 — songs (4 bytes each = seqNum per channel).
        int nSong = secLen[2] / 4;
        m.Songs = new int[nSong][];
        for (int i = 0; i < nSong; i++)
        {
            int e = secStart[2] + i * 4;
            m.Songs[i] = new[] { (int)sng[e], (int)sng[e + 1], (int)sng[e + 2], (int)sng[e + 3] };
        }

        // sec3 — sequence-offset table (u32 → offset into sec5).
        m.SequenceOffsets = ReadU32Table(sng, secStart[3], secLen[3]);
        // sec4 — pattern-offset table (u32 → offset into sec6).
        m.PatternOffsets = ReadU32Table(sng, secStart[4], secLen[4]);

        // sec5 / sec6 — raw byte data.
        m.SequenceData = Slice(sng, secStart[5], secLen[5]);
        m.PatternData = Slice(sng, secStart[6], secLen[6]);

        // Sample bank: transparently RNC-decompress if needed, then 8-bit signed.
        byte[] ins = insRaw;
        if (ins.Length >= 3 && ins[0] == 0x52 && ins[1] == 0x4E && ins[2] == 0x43) // "RNC"
            ins = Rnc.Decode(insRaw);   // global namespace, throws on bad magic
        m.Bank = new sbyte[ins.Length];
        System.Buffer.BlockCopy(ins, 0, m.Bank, 0, ins.Length);

        m.LogSummary();
        return m;
    }

    // ── debug summary ───────────────────────────────────────────────────────────

    private void LogSummary()
    {
        int patterns = PatternOffsets.Length;
        bool loop = SongLoops(0);
        GD.Print($"[music] rjp1: {Instruments.Length} instruments, {Songs.Length} songs, " +
                 $"{patterns} patterns, seqs loop={loop}");

        // Soft sanity checks against our RE of MENU (log MISMATCH like AmigaSfxBank).
        void Check(string name, int got, int expect) =>
            GD.Print($"[music]   SANITY {name}: {got} expected={expect} " +
                     (got == expect ? "OK" : "MISMATCH"));
        Check("instruments", Instruments.Length, 13);
        Check("songs", Songs.Length, 1);
        Check("patterns", patterns, 17);
    }

    /// <summary>
    /// True when every sequence referenced by song <paramref name="songIndex"/>
    /// ends in a "00 nn" (nn&gt;0) loop-back marker — i.e. the tune repeats forever.
    /// </summary>
    public bool SongLoops(int songIndex)
    {
        if (songIndex < 0 || songIndex >= Songs.Length) return false;
        bool loop = true;
        bool any = false;
        foreach (int seqNum in Songs[songIndex])
        {
            if (seqNum <= 0) continue;                 // 0 = unused channel
            if (seqNum >= SequenceOffsets.Length) { loop = false; continue; }
            int i = SequenceOffsets[seqNum];
            while (i < SequenceData.Length && SequenceData[i] != 0) i++;
            // marker byte after the terminating 0x00.
            int nn = (i + 1 < SequenceData.Length) ? SequenceData[i + 1] : 0;
            any = true;
            if (nn <= 0) loop = false;
        }
        return any && loop;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static int[] ReadU32Table(byte[] b, int start, int len)
    {
        int n = len / 4;
        var t = new int[n];
        for (int i = 0; i < n; i++) t[i] = Be32(b, start + i * 4);
        return t;
    }

    private static byte[] Slice(byte[] b, int start, int len)
    {
        var d = new byte[len];
        System.Array.Copy(b, start, d, 0, len);
        return d;
    }

    private static int Be16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static int Be32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
