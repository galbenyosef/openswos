using Godot;
using System;
using System.Globalization;
using System.IO;

namespace OpenSwos.Assets;

/// <summary>
/// One-time importer for the SWOS 96/97 PC CD MENU MUSIC (Red Book audio).
///
/// The PC CD's TRACK 2 is a Red Book (CDDA) audio track containing the menu
/// music. It is NOT part of the ISO9660 data filesystem — it lives past the end
/// of the data track as raw PCM sectors, and only the <c>.cue</c> sheet records
/// where it starts (its <c>INDEX 1 MM:SS:FF</c>). This class:
///   1. Locates a CD image via <see cref="CdSfxImport.FindCdImage"/> (requires a
///      <c>.cue</c>, the only thing that carries the audio-track MSF).
///   2. Parses the cue for the first <c>TRACK n AUDIO</c> and its INDEX 1 MSF,
///      converts MSF→LBA (<c>lba = (M*60 + S)*75 + F</c>), and multiplies by the
///      2352-byte physical sector size to get the raw byte offset into the .img.
///      (Both MODE1/2352 and AUDIO tracks use 2352-byte physical sectors; audio
///      sectors are pure interleaved PCM with no cooking.)
///   3. Reads the remaining <c>fileSize - byteOffset</c> bytes — already 44100 Hz
///      16-bit signed stereo LE, exactly AudioStreamWav's Format16Bits+Stereo
///      layout — wraps them in a 44-byte RIFF/WAVE header, and caches to
///      <c>user://cache/pc_music/track2.wav</c>, once.
///   4. <see cref="Load"/> reads that cache back into a looping AudioStreamWav.
///
/// For the reference SWOS96-97 disc: TRACK 2 AUDIO INDEX 1 20:54:30 →
/// lba 94080 → byte offset 221,276,160; image is 251,586,384 bytes so the slice
/// is 30,310,224 bytes = 12,887 sectors ≈ 171.8 s.
///
/// Writes ONLY to user://; the original CD image is never modified. All IO is
/// pure System.IO + GD.Print (thread-safe), and every failure degrades to null.
/// </summary>
public static class CdMusicImport
{
    private const string CacheRel = "cache/pc_music";
    private const string CacheFileName = "track2.wav";
    private const long MinCacheBytes = 1_000_000;   // >1 MB = a real extraction

    // CDDA / MODE1 physical geometry.
    private const int SectorBytes = 2352;
    private const int SampleRate = 44100;
    private const int BytesPerFrame = 4;            // 16-bit stereo
    private const int WavHeaderBytes = 44;

    private static readonly object s_lock = new();
    private static bool s_probed;
    private static bool s_available;
    private static string s_cacheFile = "";

    /// <summary>
    /// True iff a CD image with a parseable TRACK 2 (first AUDIO track) exists.
    /// Cheap + cached — ensures the track is extracted on first call.
    /// </summary>
    public static bool Available()
    {
        EnsureExtracted();
        return s_available;
    }

    /// <summary>
    /// Ensure track 2 is extracted+cached, then return a looping AudioStreamWav
    /// wrapping the whole track, or null if unavailable.
    /// </summary>
    public static AudioStreamWav? Load()
    {
        string cache = EnsureExtracted();
        if (cache.Length == 0) return null;

        try
        {
            byte[] file = File.ReadAllBytes(cache);
            if (file.Length <= WavHeaderBytes) return null;

            var pcm = new byte[file.Length - WavHeaderBytes];
            Array.Copy(file, WavHeaderBytes, pcm, 0, pcm.Length);
            int frameCount = pcm.Length / BytesPerFrame;

            return new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                Stereo = true,
                MixRate = SampleRate,
                Data = pcm,
                LoopMode = AudioStreamWav.LoopModeEnum.Forward,
                LoopBegin = 0,
                LoopEnd = frameCount - 1,
            };
        }
        catch (Exception e)
        {
            GD.PrintErr($"[music] pc: load failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // ── extraction ────────────────────────────────────────────────────────────

    // Ensure the cache file exists; return its absolute path, or "" if unavailable.
    // Idempotent + thread-safe; extraction runs at most once.
    private static string EnsureExtracted()
    {
        lock (s_lock)
        {
            if (s_probed) return s_available ? s_cacheFile : "";
            s_probed = true;

            string cacheDir = ProjectSettings.GlobalizePath("user://" + CacheRel);
            string cacheFile = Path.Combine(cacheDir, CacheFileName);

            // Cache hit: existing, non-trivial file — skip extraction.
            try
            {
                var fi = new FileInfo(cacheFile);
                if (fi.Exists && fi.Length > MinCacheBytes)
                {
                    GD.Print("[music] pc: track2 cached (hit)");
                    s_available = true;
                    s_cacheFile = cacheFile;
                    return cacheFile;
                }
            }
            catch { /* fall through to extract */ }

            try
            {
                if (Extract(cacheDir, cacheFile))
                {
                    s_available = true;
                    s_cacheFile = cacheFile;
                    return cacheFile;
                }
            }
            catch (Exception e)
            {
                GD.PrintErr($"[music] pc: extraction failed: {e.GetType().Name}: {e.Message}");
            }
            return "";
        }
    }

    // Locate the cue, parse track 2, slice the .img, write the cached WAV.
    // Returns true on success.
    private static bool Extract(string cacheDir, string cacheFile)
    {
        string cue = FindCueImage();
        if (cue.Length == 0) return false;   // no cue → menu music unavailable

        int lba = ParseAudioTrackLba(cue);
        if (lba < 0) return false;           // no parseable TRACK n AUDIO

        if (lba != 94080)
            GD.Print($"[music] pc: WARNING expected sector 94080 got {lba}");

        long byteOffset = (long)lba * SectorBytes;
        if (lba == 94080 && byteOffset != 221276160L)
            GD.Print($"[music] pc: WARNING expected byte offset 221276160 got {byteOffset}");

        string binary = CdSfxImport.ResolveCueBinary(cue);
        if (binary.Length == 0 || !File.Exists(binary)) return false;

        long fileSize = new FileInfo(binary).Length;
        long length = fileSize - byteOffset;
        if (length <= 0)
        {
            GD.Print($"[music] pc: WARNING audio offset {byteOffset} past image end {fileSize}");
            return false;
        }
        if (lba == 94080 && length != 30310224L)
            GD.Print($"[music] pc: WARNING expected slice 30310224 got {length}");

        Directory.CreateDirectory(cacheDir);
        WriteWav(binary, byteOffset, length, cacheFile);

        double seconds = (double)length / BytesPerFrame / SampleRate;
        GD.Print($"[music] pc: track2 {seconds:F1}s cached");
        return true;
    }

    // Find a .cue image: FindCdImage prefers .cue; if it returns a non-cue, look
    // for a sibling .cue next to it. Returns the cue path, or "" if none.
    private static string FindCueImage()
    {
        string image = CdSfxImport.FindCdImage();
        if (image.Length == 0) return "";

        if (image.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            return image;

        // Non-cue chosen: probe a sibling .cue (same basename, then any .cue).
        try
        {
            string dir = Path.GetDirectoryName(image) ?? "";
            string sameName = Path.Combine(dir, Path.GetFileNameWithoutExtension(image) + ".cue");
            if (File.Exists(sameName)) return sameName;

            foreach (string f in Directory.GetFiles(dir, "*.cue"))
                return f;
        }
        catch { /* fall through */ }
        return "";
    }

    // Parse a cue sheet for the first `TRACK n AUDIO` and its following
    // `INDEX 1 MM:SS:FF`, returning the LBA, or -1 if not found.
    private static int ParseAudioTrackLba(string cue)
    {
        try
        {
            bool inAudioTrack = false;
            foreach (string raw in File.ReadAllLines(cue))
            {
                string line = raw.Trim();

                if (line.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    inAudioTrack = line.EndsWith("AUDIO", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inAudioTrack && line.StartsWith("INDEX 1", StringComparison.OrdinalIgnoreCase))
                {
                    string msf = line.Substring("INDEX 1".Length).Trim();
                    string[] parts = msf.Split(':');
                    if (parts.Length != 3) continue;
                    if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) &&
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) &&
                        int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f))
                    {
                        return ((m * 60 + s) * 75) + f;
                    }
                }
            }
        }
        catch { /* fall through */ }
        return -1;
    }

    // Stream `length` bytes of raw CDDA PCM from `binary` at `byteOffset` into a
    // RIFF/WAVE file at `cacheFile` (44-byte PCM/2ch/44100/16-bit header + data).
    private static void WriteWav(string binary, long byteOffset, long length, string cacheFile)
    {
        using var src = File.OpenRead(binary);
        src.Seek(byteOffset, SeekOrigin.Begin);

        using var dst = File.Create(cacheFile);
        WriteWavHeader(dst, length);

        var buf = new byte[1 << 20];   // 1 MiB chunks
        long remaining = length;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buf.Length, remaining);
            int n = src.Read(buf, 0, want);
            if (n <= 0) break;
            dst.Write(buf, 0, n);
            remaining -= n;
        }
    }

    // 44-byte canonical PCM WAVE header for 44100 Hz / 2ch / 16-bit.
    private static void WriteWavHeader(Stream dst, long dataLen)
    {
        int channels = 2;
        int bits = 16;
        int blockAlign = channels * bits / 8;             // 4
        int byteRate = SampleRate * blockAlign;           // 176400
        uint dataChunk = (uint)dataLen;
        uint riffChunk = 36 + dataChunk;                  // 4 + (8+16) + (8+data)

        using var w = new BinaryWriter(dst, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(riffChunk);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                                      // fmt chunk size
        w.Write((short)1);                                // PCM
        w.Write((short)channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataChunk);
    }
}
