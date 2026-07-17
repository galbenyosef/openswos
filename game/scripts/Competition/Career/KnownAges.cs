using System.Collections.Generic;

namespace OpenSwos.Competition.Career;

/// <summary>
/// Real-age lookup for career players, built from a committed offline table
/// (<c>game/data/known_ages_1997.csv</c>, harvested from Wikidata — CC0 — by
/// <c>tools/wikidata-ages/fetch_ages.py</c>). Maps a normalised player name
/// (+ optional country hint) to a birth year so the career builder can assign a
/// player's REAL 1996/97 age instead of deriving one from skill tier.
///
/// This class is deliberately PURE (no Godot dependency) so the parser and the
/// matching logic can be unit-tested. The Godot-aware caller resolves the CSV's
/// on-disk path (res:// → OS path) and hands it to <see cref="LoadFromFile"/>,
/// or sets <see cref="ResolvePath"/> once for lazy loading.
/// </summary>
public static class KnownAges
{
    // name (normalised) -> distinct (birthYear, country) rows.
    private static readonly Dictionary<string, List<(int Year, string Country)>> kByName = new();
    private static bool _loaded;
    private static int _rowCount;

    /// <summary>
    /// Optional path resolver, set once by a Godot-aware bootstrap (e.g. it may
    /// call <c>ProjectSettings.GlobalizePath("res://data/known_ages_1997.csv")</c>).
    /// When set, the first lookup lazy-loads from the returned path.
    /// </summary>
    public static System.Func<string?>? ResolvePath;

    /// <summary>True once a table has been loaded (even if it was empty/missing).</summary>
    public static bool IsLoaded => _loaded;

    /// <summary>Number of (name;year;country) rows currently indexed.</summary>
    public static int RowCount => _rowCount;

    // ── Loading ──────────────────────────────────────────────────────────────

    /// <summary>Parses the CSV at <paramref name="csvPath"/>. Missing file is a no-op.</summary>
    public static void LoadFromFile(string? csvPath)
    {
        _loaded = true;
        if (string.IsNullOrEmpty(csvPath)) return;
        try
        {
            if (!System.IO.File.Exists(csvPath)) return;
            LoadFromLines(System.IO.File.ReadLines(csvPath));
        }
        catch { /* unreadable table — leave empty, callers fall back to derivation */ }
    }

    /// <summary>
    /// Core parser (testable): consumes <c>NAME;BIRTHYEAR;COUNTRY</c> lines,
    /// skipping blanks and '#' comment lines. Replaces any current table.
    /// </summary>
    public static void LoadFromLines(IEnumerable<string> lines)
    {
        kByName.Clear();
        _rowCount = 0;
        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string line = raw.Trim();
            if (line[0] == '#') continue;

            string[] parts = line.Split(';');
            if (parts.Length < 2) continue;
            string name = Normalize(parts[0]);
            if (name.Length == 0) continue;
            if (!int.TryParse(parts[1].Trim(), out int year)) continue;
            string country = parts.Length > 2 ? Normalize(parts[2]) : "";

            if (!kByName.TryGetValue(name, out List<(int, string)>? rows))
            {
                rows = new List<(int, string)>();
                kByName[name] = rows;
            }
            // dedup identical (year,country) rows
            bool dup = false;
            foreach (var (y, c) in rows)
                if (y == year && c == country) { dup = true; break; }
            if (dup) continue;
            rows.Add((year, country));
            _rowCount++;
        }
        _loaded = true;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        LoadFromFile(ResolvePath?.Invoke());
        _loaded = true;   // even if ResolvePath was null, don't retry forever
    }

    // ── Lookup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a real age for <paramref name="name"/> from the offline table.
    /// Exact normalised-name match; when several birth years exist, prefers rows
    /// whose country matches <paramref name="countryHint"/>. If still ambiguous
    /// and the surviving years span more than 3, returns false (too risky);
    /// otherwise uses the median year. Age = (season1997 - 1) - birthYear
    /// (season starts autumn 1996), clamped to [15, 45].
    /// </summary>
    public static bool TryGetAge(string? name, string? countryHint, out int age, int season1997 = 1997)
    {
        age = 0;
        EnsureLoaded();
        if (string.IsNullOrEmpty(name)) return false;

        string key = Normalize(name);
        if (!kByName.TryGetValue(key, out List<(int Year, string Country)>? rows) || rows.Count == 0)
            return false;

        // Prefer country-matched rows when a hint is present and matches at least one.
        string hint = Normalize(countryHint);
        List<int> years = new();
        if (hint.Length > 0)
        {
            foreach (var (y, c) in rows)
                if (CountryMatches(c, hint) && !years.Contains(y)) years.Add(y);
        }
        if (years.Count == 0)
        {
            years.Clear();
            foreach (var (y, _) in rows)
                if (!years.Contains(y)) years.Add(y);
        }

        int chosen;
        if (years.Count == 1)
        {
            chosen = years[0];
        }
        else
        {
            years.Sort();
            if (years[^1] - years[0] > 3) return false;   // too risky to guess
            chosen = years[years.Count / 2];               // deterministic median
        }

        age = System.Math.Clamp((season1997 - 1) - chosen, 15, 45);
        return true;
    }

    // ── Country hint matching ────────────────────────────────────────────────

    // Loose match between the game's nation name (NationNames) and the Wikidata
    // citizenship label. Exact, substring, or a few well-known aliases (the game
    // uses UK home-nation and "HOLLAND" names Wikidata records differently).
    private static bool CountryMatches(string fileCountry, string hint)
    {
        if (fileCountry.Length == 0) return false;
        if (fileCountry == hint) return true;
        if (fileCountry.Contains(hint) || hint.Contains(fileCountry)) return true;

        // HOLLAND (SWOS) == NETHERLANDS (Wikidata "KINGDOM OF THE NETHERLANDS").
        if (hint == "HOLLAND" && fileCountry.Contains("NETHERLANDS")) return true;
        // SWOS home nations map onto Wikidata's "UNITED KINGDOM".
        if (fileCountry.Contains("UNITED KINGDOM") &&
            (hint is "ENGLAND" or "SCOTLAND" or "WALES" or "NORTHERN IRELAND")) return true;
        if (fileCountry.Contains("IRELAND") && hint.Contains("IRELAND")) return true;
        return false;
    }

    // ── Normalisation (mirrors tools/wikidata-ages/fetch_ages.py) ─────────────

    private static readonly Dictionary<char, string> kTranslit = new()
    {
        ['Ł'] = "L", ['ł'] = "L", ['Ø'] = "O", ['ø'] = "O",
        ['Đ'] = "D", ['đ'] = "D", ['Ð'] = "D", ['ð'] = "D",
        ['Þ'] = "TH", ['þ'] = "TH", ['Æ'] = "AE", ['æ'] = "AE",
        ['Œ'] = "OE", ['œ'] = "OE", ['ß'] = "SS",
        ['Ħ'] = "H", ['ħ'] = "H", ['ı'] = "I", ['İ'] = "I",
        ['Ŋ'] = "N", ['ŋ'] = "N", ['Ŧ'] = "T", ['ŧ'] = "T",
        ['Ə'] = "E", ['ə'] = "E",
        ['’'] = "'", ['‘'] = "'", ['ʻ'] = "'", ['´'] = "'", ['`'] = "'",
        ['“'] = "", ['”'] = "", ['–'] = "-", ['—'] = "-",
    };

    /// <summary>
    /// Uppercase, fold non-decomposing Latin letters + typographic punctuation to
    /// ASCII, strip combining diacritics, and collapse whitespace — matching how
    /// the harvest script and the game's TEAM.* names are spelled.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        System.Text.StringBuilder pre = new(text.Length);
        foreach (char c in text)
            pre.Append(kTranslit.TryGetValue(c, out string? rep) ? rep : c.ToString());

        string decomposed = pre.ToString().Normalize(System.Text.NormalizationForm.FormD);
        System.Text.StringBuilder sb = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.ToUpperInvariant(c));
        }

        // collapse any run of whitespace to a single space, trim ends
        string upper = sb.ToString();
        System.Text.StringBuilder outp = new(upper.Length);
        bool prevSpace = true;   // trims leading
        foreach (char c in upper)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace) { outp.Append(' '); prevSpace = true; }
            }
            else { outp.Append(c); prevSpace = false; }
        }
        return outp.ToString().TrimEnd();
    }
}
