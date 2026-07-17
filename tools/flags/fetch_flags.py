#!/usr/bin/env python3
"""Download real national flags for all 153 SWOS player-nations.

Index/code table is the authoritative PlayerNationNames.cs (0=ALB..152=CUS).
Flags come from flagcdn.com (public-domain flag images). A few special/historical
cases have no ISO-2 code and are handled explicitly (see SPECIAL below).

Output: game/data/flags/{index:03d}_{CODE}.png  (all <=128 px wide, aspect kept).

Flags are national symbols in the public domain. flagcdn.com serves PD raster
flags; Wikimedia Commons files used here are PD as noted in SOURCES.txt.
"""
import io
import os
import sys
import time
import hashlib
import urllib.request
import urllib.error

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.abspath(os.path.join(HERE, "..", "..", "game", "data", "flags"))

FLAGCDN_WIDTH = "w80"   # 80 px wide raster, well under the 128 px cap
MAX_WIDTH = 128
DELAY = 0.20            # polite delay between requests (s)
RETRIES = 4
UA = "OpenSWOS-flag-fetch/1.0 (open-source game asset pipeline; contact angree80@gmail.com)"

# (index, SWOS 3-letter code, ISO-3166-1 alpha-2 / flagcdn subdivision code)
# Special non-ISO cases carry the sentinel codes below instead of an ISO code.
NATIONS = [
    (0,   "ALB", "al"), (1,   "AUT", "at"), (2,   "BEL", "be"), (3,   "BUL", "bg"),
    (4,   "CRO", "hr"), (5,   "CYP", "cy"), (6,   "TCH", "cz"), (7,   "DEN", "dk"),
    (8,   "ENG", "gb-eng"), (9, "EST", "ee"), (10, "FAR", "fo"), (11, "FIN", "fi"),
    (12,  "FRA", "fr"), (13,  "GER", "de"), (14,  "GRE", "gr"), (15,  "HUN", "hu"),
    (16,  "ISL", "is"), (17,  "ISR", "il"), (18,  "ITA", "it"), (19,  "LAT", "lv"),
    (20,  "LIT", "lt"), (21,  "LUX", "lu"), (22,  "MLT", "mt"), (23,  "HOL", "nl"),
    (24,  "NIR", "gb-nir"), (25, "NOR", "no"), (26, "POL", "pl"), (27, "POR", "pt"),
    (28,  "ROM", "ro"), (29,  "RUS", "ru"), (30,  "SMR", "sm"), (31,  "SCO", "gb-sct"),
    (32,  "SLO", "si"), (33,  "SWE", "se"), (34,  "TUR", "tr"), (35,  "UKR", "ua"),
    (36,  "WAL", "gb-wls"), (37, "YUG", "SPECIAL_YUG"), (38, "BLS", "by"), (39, "SVK", "sk"),
    (40,  "ESP", "es"), (41,  "ARM", "am"), (42,  "BOS", "ba"), (43,  "AZB", "az"),
    (44,  "GEO", "ge"), (45,  "SUI", "ch"), (46,  "IRL", "ie"), (47,  "MAC", "mk"),
    (48,  "TRK", "tm"), (49,  "LIE", "li"), (50,  "MOL", "md"), (51,  "CRC", "cr"),
    (52,  "SAL", "sv"), (53,  "GUA", "gt"), (54,  "HON", "hn"), (55,  "BHM", "bh"),
    (56,  "MEX", "mx"), (57,  "PAN", "pa"), (58,  "USA", "us"), (59,  "BAH", "bs"),
    (60,  "NIC", "ni"), (61,  "BER", "bm"), (62,  "JAM", "jm"), (63,  "TRI", "tt"),
    (64,  "CAN", "ca"), (65,  "BAR", "bb"), (66,  "ELS", "bz"), (67,  "SVC", "vc"),
    (68,  "ARG", "ar"), (69,  "BOL", "bo"), (70,  "BRA", "br"), (71,  "CHL", "cl"),
    (72,  "COL", "co"), (73,  "ECU", "ec"), (74,  "PAR", "py"), (75,  "SUR", "sr"),
    (76,  "URU", "uy"), (77,  "VNZ", "ve"), (78,  "GUY", "gy"), (79,  "PER", "pe"),
    (80,  "ALG", "dz"), (81,  "SAF", "za"), (82,  "BOT", "bw"), (83,  "BFS", "bf"),
    (84,  "BUR", "bi"), (85,  "LES", "ls"), (86,  "ZAI", "SPECIAL_ZAI"), (87, "ZAM", "zm"),
    (88,  "GHA", "gh"), (89,  "SEN", "sn"), (90,  "CIV", "ci"), (91,  "TUN", "tn"),
    (92,  "MLI", "ml"), (93,  "MDG", "mg"), (94,  "CMR", "cm"), (95,  "CHD", "td"),
    (96,  "UGA", "ug"), (97,  "LIB", "ly"), (98,  "MOZ", "mz"), (99,  "KEN", "ke"),
    (100, "SUD", "sd"), (101, "SWA", "sz"), (102, "ANG", "ao"), (103, "TOG", "tg"),
    (104, "ZIM", "zw"), (105, "EGY", "eg"), (106, "TAN", "tz"), (107, "NIG", "ng"),
    (108, "ETH", "et"), (109, "GAB", "ga"), (110, "SIE", "sl"), (111, "BEN", "bj"),
    (112, "CON", "cg"), (113, "GUI", "gn"), (114, "SRL", "sc"), (115, "MAR", "ma"),
    (116, "GAM", "gm"), (117, "MLW", "mw"), (118, "JAP", "jp"), (119, "TAI", "tw"),
    (120, "IND", "in"), (121, "BAN", "bd"), (122, "BRU", "bn"), (123, "IRA", "iq"),
    (124, "JOR", "jo"), (125, "SRI", "lk"), (126, "SYR", "sy"), (127, "KOR", "kr"),
    (128, "IRN", "ir"), (129, "VIE", "vn"), (130, "MLY", "my"), (131, "SAU", "sa"),
    (132, "YEM", "ye"), (133, "KUW", "kw"), (134, "LAO", "la"), (135, "NKR", "kp"),
    (136, "OMA", "om"), (137, "PAK", "pk"), (138, "PHI", "ph"), (139, "CHI", "cn"),
    (140, "SGP", "sg"), (141, "MAU", "mu"), (142, "MYA", "mm"), (143, "PAP", "pg"),
    (144, "TAD", "tj"), (145, "UZB", "uz"), (146, "QAT", "qa"), (147, "UAE", "ae"),
    (148, "AUS", "au"), (149, "NZL", "nz"), (150, "FIJ", "fj"), (151, "SOL", "sb"),
    (152, "CUS", "SPECIAL_CUS"),
]

fallbacks = []   # (index, code, reason)


def http_get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    last = None
    for attempt in range(RETRIES):
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return resp.read()
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, OSError) as e:
            last = e
            time.sleep(DELAY * (attempt + 1) * 2)
    raise last


def wikimedia_thumb_url(filename, width):
    """Deterministic Wikimedia Commons thumbnail PNG URL for an SVG file."""
    filename = filename.replace(" ", "_")
    md5 = hashlib.md5(filename.encode("utf-8")).hexdigest()
    base = "https://upload.wikimedia.org/wikipedia/commons/thumb"
    return f"{base}/{md5[0]}/{md5[0:2]}/{filename}/{width}px-{filename}.png"


def save_resized(img, path):
    if img.mode not in ("RGB", "RGBA"):
        img = img.convert("RGBA")
    if img.width > MAX_WIDTH:
        h = max(1, round(img.height * MAX_WIDTH / img.width))
        img = img.resize((MAX_WIDTH, h), Image.LANCZOS)
    img.save(path, "PNG")


def make_tricolor_horizontal(colors, w=80):
    """Generate a horizontal tricolour PNG (list of RGB tuples)."""
    n = len(colors)
    h = round(w * 2 / 3)          # 3:2 flag ratio
    band = h // n
    img = Image.new("RGB", (w, band * n), colors[-1])
    px = img.load()
    for i, c in enumerate(colors):
        for y in range(i * band, (i + 1) * band):
            for x in range(w):
                px[x, y] = c
    return img


def fetch_flagcdn(iso):
    url = f"https://flagcdn.com/{FLAGCDN_WIDTH}/{iso}.png"
    data = http_get(url)
    return Image.open(io.BytesIO(data))


def handle_special(idx, code):
    """Return a PIL image for the non-ISO special cases."""
    if code == "SPECIAL_YUG":
        # FR Yugoslavia (1992-2003): plain pan-Slavic blue/white/red horizontal
        # tricolour, no emblem. Generated (accurate to the emblem-less flag).
        fallbacks.append((idx, "YUG", "generated blue/white/red FR-Yugoslavia tricolour (no ISO-2 code)"))
        return make_tricolor_horizontal([(12, 40, 130), (240, 240, 240), (200, 20, 20)])
    if code == "SPECIAL_ZAI":
        # Zaire 1971-1997: green field, yellow disc, arm+torch. Fetch PD SVG
        # rendered to PNG via Wikimedia's Special:FilePath thumbnailer.
        try:
            url = ("https://commons.wikimedia.org/wiki/Special:FilePath/"
                   + urllib.request.quote("Flag of Zaire.svg") + "?width=120")
            data = http_get(url)
            fallbacks.append((idx, "ZAI", "Wikimedia Commons PD 'Flag of Zaire.svg' (Special:FilePath, width=120)"))
            return Image.open(io.BytesIO(data))
        except Exception as e:  # fall back to plain green if Wikimedia unreachable
            fallbacks.append((idx, "ZAI", f"generated plain green (Wikimedia fetch failed: {e})"))
            img = Image.new("RGB", (80, 53), (0, 150, 60))
            return img
    if code == "SPECIAL_CUS":
        fallbacks.append((idx, "CUS", "generated plain grey placeholder (custom-team slot)"))
        return Image.new("RGB", (80, 53), (128, 128, 128))
    raise ValueError(f"unknown special {code}")


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    misses = []
    for idx, code, iso in NATIONS:
        out = os.path.join(OUT_DIR, f"{idx:03d}_{code}.png")
        try:
            if iso.startswith("SPECIAL_"):
                img = handle_special(idx, iso)
            else:
                img = fetch_flagcdn(iso)
            save_resized(img, out)
            print(f"OK  {idx:03d} {code} ({iso}) -> {os.path.basename(out)}")
        except Exception as e:
            print(f"ERR {idx:03d} {code} ({iso}): {e}", file=sys.stderr)
            misses.append((idx, code, iso, str(e)))
        time.sleep(DELAY)

    print("\n=== SUMMARY ===")
    print(f"nations: {len(NATIONS)}")
    print(f"fallbacks/special: {len(fallbacks)}")
    for i, c, r in fallbacks:
        print(f"  fallback {i:03d} {c}: {r}")
    if misses:
        print(f"MISSES: {len(misses)}")
        for i, c, iso, e in misses:
            print(f"  MISS {i:03d} {c} ({iso}): {e}")
    else:
        print("MISSES: 0")


if __name__ == "__main__":
    main()
