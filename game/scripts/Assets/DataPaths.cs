using Godot;
using System.Collections.Generic;
using System.IO;

namespace OpenSwos.Assets;

/// <summary>
/// Central, export-safe resolver for the original SWOS asset directories.
///
/// The game needs three source folders:
///   • Amiga <c>grafs/</c> — sprites, pitch tile-maps, charset (mandatory for visuals).
///   • Amiga <c>data/</c>  — Amiga TEAM.* (optional; RNC-compressed).
///   • PC <c>DATA/</c>     — PC TEAM.* + POOLPLYR.DAT (the main 1730-team list).
///
/// "Bring your own game files" is deliberately forgiving. There are two user folders,
/// both alongside the game binary (and, as a fallback, under <c>user://</c>):
///   • <c>original_swos_adf/</c>   — drop your SWOS 96/97 Amiga floppy images (*.adf) here.
///   • <c>original_swos_files/</c> — auto-extracted output; ALSO accepts already-loose
///                                   game files (e.g. a WHDLoad / hard-disk install).
///
/// Each source is resolved by SEARCHING RECURSIVELY (depth-capped) under the
/// <c>original_swos_files/</c> roots for the folder that actually contains its key file,
/// so it works whether the importer wrote <c>.../amiga/disk2/grafs/...</c> OR the user
/// dumped loose files directly into <c>original_swos_files/</c>. If neither user root
/// has it, we fall back to the in-repo dev tree as EXACT direct paths (unchanged), so
/// the repo dev run + headless smoke resolve with zero setup.
///
/// All returned paths are GLOBALIZED absolute OS paths (never res:// / user://),
/// so callers can hand them straight to System.IO / the CLI-shared loaders.
/// </summary>
public static class DataPaths
{
    /// <summary>User INPUT folder — drop *.adf floppy images here.</summary>
    public const string InputFolderName = "original_swos_adf";

    /// <summary>User OUTPUT folder — extracted files land here; also accepts loose files.</summary>
    public const string OutputFolderName = "original_swos_files";

    /// <summary>
    /// OPTIONAL user folder — drop the PC (DOS) SWOS <c>DATA/</c> folder here. It only
    /// adds a slightly larger team list (~1730 PC vs ~1616 Amiga teams); it is NOT
    /// required and does not change the graphics (those always come from the Amiga files).
    /// </summary>
    public const string PcInputFolderName = "original_swos_pc";

    // Key files that prove a candidate directory is the real thing.
    private const string AmigaGrafsKey = "CJCTEAM1.RAW";
    private const string AmigaDataKey = "TEAM.000";
    private const string PcDataKey = "TEAM.000";

    // Directory names we never want to descend into during a recursive search.
    private static readonly HashSet<string> SkipDirs = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".godot", ".import", "bin", "obj", "node_modules", ".vs", ".idea", ".backups",
    };

    /// <summary>Directory of the running executable (empty when it can't be determined).</summary>
    public static string ExeDir()
    {
        string exe = OS.GetExecutablePath();
        return string.IsNullOrEmpty(exe) ? "" : (Path.GetDirectoryName(exe) ?? "");
    }

    // ── User-folder roots ────────────────────────────────────────────────────

    /// <summary>Recursive-search roots for extracted / loose game files, in priority order.</summary>
    public static IEnumerable<string> OutputSearchRoots()
    {
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
            yield return Path.Combine(exeDir, OutputFolderName);
        yield return ProjectSettings.GlobalizePath("user://" + OutputFolderName);
    }

    /// <summary>
    /// Recursive-search roots for the OPTIONAL PC (DOS) DATA folder, in priority order.
    /// Mirrors <see cref="OutputSearchRoots"/> but points at <c>original_swos_pc/</c>.
    /// </summary>
    public static IEnumerable<string> PcSearchRoots()
    {
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
            yield return Path.Combine(exeDir, PcInputFolderName);
        yield return ProjectSettings.GlobalizePath("user://" + PcInputFolderName);
    }

    /// <summary>Folders scanned for *.adf floppy images, in priority order.</summary>
    public static IEnumerable<string> InputSearchRoots()
    {
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
            yield return Path.Combine(exeDir, InputFolderName);
        yield return ProjectSettings.GlobalizePath("user://" + InputFolderName);
        // Dev fallback — the read-only reference ADFs shipped in the repo.
        yield return ProjectSettings.GlobalizePath("res://../Swos9697_Amiga");
    }

    /// <summary>Preferred (non-creating) display path for the INPUT folder.</summary>
    public static string PreferredInputPath() => PreferredPath(InputFolderName);

    /// <summary>Preferred (non-creating) display path for the OUTPUT folder.</summary>
    public static string PreferredOutputPath() => PreferredPath(OutputFolderName);

    /// <summary>Preferred (non-creating) display path for the OPTIONAL PC folder.</summary>
    public static string PreferredPcInputPath() => PreferredPath(PcInputFolderName);

    private static string PreferredPath(string folder)
    {
        string exeDir = ExeDir();
        return exeDir.Length > 0
            ? Path.Combine(exeDir, folder)
            : ProjectSettings.GlobalizePath("user://" + folder);
    }

    /// <summary>
    /// First writable location for <paramref name="folder"/> (exeDir then user://),
    /// creating it. Some install dirs are read-only, so we fall back automatically.
    /// Returns "" only if even user:// can't be written.
    /// </summary>
    public static string FirstWritableRoot(string folder)
    {
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
        {
            string cand = Path.Combine(exeDir, folder);
            if (CanWrite(cand)) return cand;
        }
        string user = ProjectSettings.GlobalizePath("user://" + folder);
        return CanWrite(user) ? user : "";
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write_probe_" + System.Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ── Recursive search helper ──────────────────────────────────────────────

    // Breadth-first search under `root` (depth-capped) for the first directory that
    // satisfies `match`. Skips unreadable / irrelevant trees. Returns "" if none.
    private static string FindDir(string root, System.Func<string, bool> match, int maxDepth = 4)
    {
        if (string.IsNullOrEmpty(root)) return "";
        try { if (!Directory.Exists(root)) return ""; } catch { return ""; }

        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try { if (match(dir)) return dir; }
            catch { /* unreadable candidate — keep going */ }

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string s in subs)
            {
                if (SkipDirs.Contains(Path.GetFileName(s))) continue;
                queue.Enqueue((s, depth + 1));
            }
        }
        return "";
    }

    private static bool HasFile(string dir, string file)
    {
        try { return File.Exists(Path.Combine(dir, file)); }
        catch { return false; }
    }

    // Search all original_swos_files roots for a dir matching `match`; "" if none.
    private static string SearchOutputRoots(System.Func<string, bool> match)
    {
        foreach (string root in OutputSearchRoots())
        {
            string hit = FindDir(root, match);
            if (hit.Length > 0) return hit;
        }
        return "";
    }

    // Search all original_swos_pc roots for a dir matching `match`; "" if none.
    private static string SearchPcRoots(System.Func<string, bool> match)
    {
        foreach (string root in PcSearchRoots())
        {
            string hit = FindDir(root, match);
            if (hit.Length > 0) return hit;
        }
        return "";
    }

    // Returns `dir` if it exists and contains `keyFile`, else "".
    private static string DirWith(string dir, string keyFile)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        return HasFile(dir, keyFile) ? dir : "";
    }

    // ── Resolvers ────────────────────────────────────────────────────────────

    /// <summary>Resolved Amiga <c>grafs/</c> directory, or "" if none found.</summary>
    public static string AmigaGrafsDir()
    {
        string hit = SearchOutputRoots(d => HasFile(d, AmigaGrafsKey));
        if (hit.Length > 0) return hit;
        // Dev fallback — exact direct path (kept working for repo dev + smoke).
        return DirWith(
            ProjectSettings.GlobalizePath("res://../assets/extracted/amiga/disk2/grafs"),
            AmigaGrafsKey);
    }

    /// <summary>
    /// Resolved Amiga <c>data/</c> directory (Amiga TEAM.* + pitch maps live near grafs),
    /// or "" if none found. Prefers the Amiga tree so it never picks up PC's TEAM.000.
    /// </summary>
    public static string AmigaDataDir()
    {
        string grafs = AmigaGrafsDir();
        if (grafs.Length > 0)
        {
            // Loose all-in-one folder: Amiga TEAM.* sit right next to the graphics.
            if (HasFile(grafs, AmigaDataKey)) return grafs;
            // Extracted tree: `data` is the sibling of `grafs`.
            string? parent = Path.GetDirectoryName(grafs);
            if (!string.IsNullOrEmpty(parent))
            {
                string sibling = Path.Combine(parent, "data");
                if (HasFile(sibling, AmigaDataKey)) return sibling;
            }
        }

        // General recursive fallback: any RNC-compressed TEAM.000 (Amiga TEAM.* are
        // RNC-packed; PC's are raw). POOLPLYR.DAT exists in BOTH the Amiga and PC data
        // folders, so it cannot discriminate — compression is the reliable marker.
        string hit = SearchOutputRoots(d => IsRnc(Path.Combine(d, AmigaDataKey)));
        if (hit.Length > 0) return hit;

        // Dev fallback — exact direct path.
        return DirWith(
            ProjectSettings.GlobalizePath("res://../assets/extracted/amiga/disk2/data"),
            AmigaDataKey);
    }

    /// <summary>Resolved PC <c>DATA/</c> directory, or "" if none found (best-effort).</summary>
    public static string PcDataDir()
    {
        // A PC DATA dir has a RAW (uncompressed) TEAM.000. Both the PC and Amiga data
        // folders contain TEAM.000 + POOLPLYR.DAT, so those cannot discriminate; the
        // Amiga TEAM.* are RNC-compressed while the PC ones are raw — that IS the marker.
        // (Without this check, an Amiga-only import made PcDataDir point at the Amiga
        // data folder, PcAssetSource failed to parse the RNC bytes, _allTeams stayed
        // empty, and the menu refused to start.)

        // 1) The dedicated OPTIONAL folder for the PC (DOS) DATA — preferred.
        string pc = SearchPcRoots(d => HasFile(d, PcDataKey) && !IsRnc(Path.Combine(d, PcDataKey)));
        if (pc.Length > 0) return pc;

        // 2) Back-compat: PC DATA dropped into the general original_swos_files/ tree.
        string hit = SearchOutputRoots(d => HasFile(d, PcDataKey) && !IsRnc(Path.Combine(d, PcDataKey)));
        if (hit.Length > 0) return hit;

        // Dev fallback — exact direct path.
        return DirWith(
            ProjectSettings.GlobalizePath("res://../Swos9697_PC/SensiWs9/SOC/DATA"),
            PcDataKey);
    }

    // True if the file begins with the RNC ProPack signature ("RNC"). Amiga SWOS
    // stores TEAM.* / graphics RNC-compressed; the PC release stores TEAM.* raw.
    private static bool IsRnc(string file)
    {
        try
        {
            using FileStream fs = File.OpenRead(file);
            return fs.ReadByte() == 'R' && fs.ReadByte() == 'N' && fs.ReadByte() == 'C';
        }
        catch { return false; }
    }

    /// <summary>True when the mandatory Amiga graphics (grafs/CJCTEAM1.RAW) are available.</summary>
    public static bool HasAmigaGraphics() => AmigaGrafsDir().Length > 0;
}
