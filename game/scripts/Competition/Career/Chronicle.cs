using System;
using System.Collections.Generic;
using OpenSwos.Menu;

namespace OpenSwos.Competition.Career;

// ============================================================================
// THE SEASON CHRONICLE — career depth plan feature #7.
//
// One line per event: a signing, a milestone, a winning run, the chairman's
// verdict, a youth intake, a breakthrough in training. Read-only by design.
//
// It is NOT an inbox and must never become one (03-career-depth-plan.md, "What
// we deliberately do NOT build"): nothing here has an unread counter, nothing
// blocks a screen, and ignoring the chronicle for twenty seasons costs the
// player nothing. It exists because save-game storytelling is what the CM 01/02
// community actually does with a career, and until now the career threw all of
// its own history away.
//
// Storage rules that keep it cheap and translatable:
//   * an entry stores a TEMPLATE in the original's placeholder style (%a, %b,
//     %0) plus its arguments, never a finished sentence, so a client in Polish
//     renders Polish and a client in English renders English from the SAME save;
//   * the list is bounded (Cap) — a 20-season career writes a few hundred lines
//     and must not grow the save without end.
// ============================================================================

/// <summary>One chronicle line. Plain data: it round-trips through the save.</summary>
public sealed class ChronicleEntry
{
    public int Season { get; set; }
    /// <summary>Competition round the event belongs to; -1 = between seasons.</summary>
    public int Round { get; set; } = -1;
    /// <summary>Stable i18n key.</summary>
    public string Key { get; set; } = "";
    /// <summary>English wording with %a / %b / %0 placeholders.</summary>
    public string Template { get; set; } = "";
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public int N { get; set; }
    /// <summary>
    /// "transfer" | "milestone" | "match" | "board" | "youth" | "training" |
    /// "injury" | "season". Clients colour by this; nothing branches on it.
    /// </summary>
    public string Kind { get; set; } = "";
    /// <summary>0 routine, 1 notable, 2 headline. Used for filtering, not logic.</summary>
    public int Weight { get; set; }
}

public static class Chronicle
{
    /// <summary>
    /// Hard cap on stored lines. Twenty seasons of a busy career write roughly
    /// 400; the cap is generous enough never to bite in normal play and small
    /// enough that a runaway writer cannot balloon the save.
    /// </summary>
    public const int Cap = 600;

    public static void Add(CareerState? career, string key, string template,
                           string kind, int weight = 0,
                           string a = "", string b = "", int n = 0, int round = -1)
    {
        if (career is null) return;
        career.Chronicle ??= new List<ChronicleEntry>();
        career.Chronicle.Add(new ChronicleEntry
        {
            Season = career.Season,
            Round = round,
            Key = key,
            Template = template,
            A = a ?? "",
            B = b ?? "",
            N = n,
            Kind = kind ?? "",
            Weight = weight,
        });
        while (career.Chronicle.Count > Cap) career.Chronicle.RemoveAt(0);
    }

    /// <summary>
    /// The finished sentence in the CURRENT language. Substitution happens after
    /// translation, exactly as the chairman's memos do, so a translated template
    /// keeps its placeholders wherever that language wants them.
    /// </summary>
    public static string Render(ChronicleEntry? e)
    {
        if (e is null) return "";
        string text = Loc.Tr(e.Key, e.Template ?? "");
        return Substitute(text, e.A, e.B, e.N);
    }

    /// <summary>The English sentence, for a client that does its own i18n.</summary>
    public static string RenderEnglish(ChronicleEntry? e)
        => e is null ? "" : Substitute(e.Template ?? "", e.A, e.B, e.N);

    private static string Substitute(string text, string a, string b, int n)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("%a", a ?? "");
        text = text.Replace("%b", b ?? "");
        text = text.Replace("%0", n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return text;
    }

    /// <summary>
    /// Newest first, optionally only one season. Returns a fresh list so a
    /// client can page it without touching the save.
    /// </summary>
    public static List<ChronicleEntry> Read(CareerState? career, int season = 0, int limit = 400)
    {
        var list = new List<ChronicleEntry>();
        var src = career?.Chronicle;
        if (src is null) return list;
        for (int i = src.Count - 1; i >= 0 && list.Count < limit; i--)
        {
            var e = src[i];
            if (e is null) continue;
            if (season > 0 && e.Season != season) continue;
            list.Add(e);
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Transfer lines
    // ------------------------------------------------------------------
    // Called from the places a player changes hands. They are one-line calls
    // into the engine rather than text composed in a client, so the desktop
    // menu and the browser get identical history (the thin-client rule).

    public static void Signed(CareerState? c, string name, long fee, int round = -1)
        => Add(c, "chron.signed", "SIGNED %a FOR %b", "transfer", 1,
               a: name, b: Money(fee), round: round);

    public static void Sold(CareerState? c, string name, long fee, int round = -1)
        => Add(c, "chron.sold", "SOLD %a FOR %b", "transfer", 1,
               a: name, b: Money(fee), round: round);

    public static void Released(CareerState? c, string name, int round = -1)
        => Add(c, "chron.released", "RELEASED %a ON A FREE", "transfer", 0,
               a: name, round: round);

    /// <summary>
    /// Compact money, the same shape the menus print (1.2M / 850K). Duplicated
    /// here on purpose: the engine must not depend on a front-end helper, and a
    /// chronicle line is written once and read for twenty seasons.
    /// </summary>
    public static string Money(long amount)
    {
        string sign = amount < 0 ? "-" : "";
        double abs = Math.Abs((double)amount);
        if (abs < 1000) return sign + ((long)abs).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (abs >= 1_000_000)
        {
            double m = abs / 1_000_000.0;
            return sign + (m < 10.0 ? m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                    : m.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + "M";
        }
        double k = abs / 1000.0;
        return sign + (k < 10.0 ? k.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                : k.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + "K";
    }

    /// <summary>Seasons that have at least one line, newest first.</summary>
    public static List<int> Seasons(CareerState? career)
    {
        var seen = new List<int>();
        var src = career?.Chronicle;
        if (src is null) return seen;
        var set = new HashSet<int>();
        for (int i = src.Count - 1; i >= 0; i--)
            if (src[i] is not null && set.Add(src[i].Season)) seen.Add(src[i].Season);
        seen.Sort((x, y) => y.CompareTo(x));
        return seen;
    }
}
