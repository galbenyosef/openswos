#!/usr/bin/env python3
"""Harvest real 1996/97 footballer ages from Wikidata (CC0) for OpenSWOS.

Queries the Wikidata SPARQL endpoint for association-football players
(P106 = Q937857) with a date of birth (P569) in 1950-1981, plus their
country of citizenship (P27). Output is normalised into the game's name
space (uppercase, diacritics stripped) so the career builder can match
players from the TEAM.* rosters and assign their REAL age instead of a
skill-derived guess.

Two files are written next to this script's data targets:
  * tools/wikidata-ages/raw_players.csv         -- raw harvested rows
  * game/data/known_ages_1997.csv               -- normalised lookup table

Data source: Wikidata (https://www.wikidata.org), licensed CC0 1.0 -- safe
to redistribute / commit. See docs/design/06-player-data-sources.md.

Usage:  python fetch_ages.py
"""

import csv
import http.client
import json
import os
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

ENDPOINT = "https://query.wikidata.org/sparql"
USER_AGENT = (
    "OpenSWOS-age-harvest/1.0 (https://github.com/angree/openswos; "
    "angree80@gmail.com) python-urllib"
)

# Association football player = Q937857; date of birth = P569; citizenship = P27.
YEAR_START = 1950
YEAR_END = 1981          # inclusive; season start is autumn 1996

SLEEP_BETWEEN = 2.0      # be a good citizen to the shared endpoint
MAX_RETRIES = 5

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
RAW_PATH = os.path.join(HERE, "raw_players.csv")
OUT_PATH = os.path.join(REPO, "game", "data", "known_ages_1997.csv")


def build_query(year: int) -> str:
    # One birth-year band per request keeps each query well under the WDQS
    # 60s timeout. A player with several citizenships yields several rows
    # (kept on purpose -- more country hints for the game-side matcher).
    return f"""
SELECT ?personLabel ?year ?countryLabel WHERE {{
  ?person wdt:P106 wd:Q937857 .
  ?person wdt:P569 ?dob .
  BIND(YEAR(?dob) AS ?year)
  FILTER(?year = {year})
  OPTIONAL {{ ?person wdt:P27 ?country. }}
  SERVICE wikibase:label {{ bd:serviceParam wikibase:language "en". }}
}}
"""


def fetch_year(year: int):
    """Return list of (name, year, country) for one birth year, JSON-parsed."""
    params = urllib.parse.urlencode({"query": build_query(year), "format": "json"})
    url = f"{ENDPOINT}?{params}"
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": USER_AGENT,
            "Accept": "application/sparql-results+json",
        },
    )
    delay = 3.0
    for attempt in range(1, MAX_RETRIES + 1):
        try:
            with urllib.request.urlopen(req, timeout=180) as resp:
                payload = resp.read()
            data = json.loads(payload.decode("utf-8", "replace"))
            rows = []
            for b in data["results"]["bindings"]:
                name = b.get("personLabel", {}).get("value", "")
                yr = b.get("year", {}).get("value", "")
                country = b.get("countryLabel", {}).get("value", "")
                rows.append((name, yr, country))
            return rows
        except urllib.error.HTTPError as e:
            code = e.code
            if code in (429, 500, 502, 503, 504) and attempt < MAX_RETRIES:
                wait = delay * attempt
                # honour Retry-After when the server sends it
                ra = e.headers.get("Retry-After") if e.headers else None
                if ra:
                    try:
                        wait = max(wait, float(ra))
                    except ValueError:
                        pass
                sys.stderr.write(
                    f"  year {year}: HTTP {code}, retry {attempt}/{MAX_RETRIES} "
                    f"in {wait:.0f}s\n"
                )
                time.sleep(wait)
                continue
            raise
        except (urllib.error.URLError, OSError, http.client.HTTPException,
                TimeoutError, json.JSONDecodeError) as e:
            if attempt < MAX_RETRIES:
                wait = delay * attempt
                sys.stderr.write(
                    f"  year {year}: {type(e).__name__}, retry "
                    f"{attempt}/{MAX_RETRIES} in {wait:.0f}s\n"
                )
                time.sleep(wait)
                continue
            raise
    return []


# Latin letters that do NOT decompose under NFD (a stroke / ligature / distinct
# letter, not a base + combining mark). SWOS stored its rosters in plain ASCII
# with these already folded to their base letter (e.g. Polish "MIROSŁAW" is
# "MIROSLAW" in TEAM.*), so we fold them the same way to make names match.
TRANSLIT = {
    "Ł": "L", "ł": "L",   # Ł ł
    "Ø": "O", "ø": "O",   # Ø ø
    "Đ": "D", "đ": "D",   # Đ đ
    "Ð": "D", "ð": "D",   # Ð ð (eth)
    "Þ": "TH", "þ": "TH", # Þ þ (thorn)
    "Æ": "AE", "æ": "AE", # Æ æ
    "Œ": "OE", "œ": "OE", # Œ œ
    "ß": "SS",                 # ß
    "Ħ": "H", "ħ": "H",   # Ħ ħ
    "ı": "I", "İ": "I",   # ı İ (Turkish dotless/dotted i)
    "Ŋ": "N", "ŋ": "N",   # Ŋ ŋ
    "Ŧ": "T", "ŧ": "T",   # Ŧ ŧ
    "Ə": "E", "ə": "E",   # Ə ə (Azeri schwa)
    "×": " ",                  # stray multiplication sign, just in case
    # typographic punctuation -> plain ASCII (SWOS uses straight quotes/hyphen)
    "’": "'", "‘": "'", "ʻ": "'", "´": "'", "`": "'",
    "“": "", "”": "", "–": "-", "—": "-",
}


def strip_diacritics(text: str) -> str:
    # NFD decomposition, drop combining marks, then keep the base letters.
    decomposed = unicodedata.normalize("NFD", text)
    return "".join(c for c in decomposed if not unicodedata.combining(c))


def normalize(text: str) -> str:
    # fold non-decomposing Latin letters BEFORE NFD so Ł/Ø/Đ become ASCII
    text = "".join(TRANSLIT.get(c, c) for c in text)
    text = strip_diacritics(text)
    text = text.upper()
    # collapse any run of whitespace to a single space, trim ends
    text = " ".join(text.split())
    return text


def looks_like_qid(name: str) -> bool:
    # Wikidata returns the bare entity id (e.g. "Q12345") when there is no
    # English label -- useless for name matching, so drop those.
    return len(name) > 1 and name[0] == "Q" and name[1:].isdigit()


def already_done_years():
    """Years already present in a partial raw_players.csv (for resume)."""
    done = {}
    if not os.path.exists(RAW_PATH):
        return done
    try:
        with open(RAW_PATH, "r", newline="", encoding="utf-8") as f:
            r = csv.reader(f)
            next(r, None)  # header
            for row in r:
                if len(row) < 2:
                    continue
                try:
                    yr = int(row[1])
                except ValueError:
                    continue
                done[yr] = done.get(yr, 0) + 1
    except OSError:
        pass
    return done


def harvest():
    print(f"Harvesting Wikidata footballers born {YEAR_START}-{YEAR_END} ...")
    print(f"  endpoint: {ENDPOINT}")
    done = already_done_years()
    resuming = bool(done)
    total = sum(done.values())
    if resuming:
        print(f"  resuming: {len(done)} year(s) already in {RAW_PATH} "
              f"({total} rows)")
    # append when resuming so completed years survive; else start fresh
    mode = "a" if resuming else "w"
    with open(RAW_PATH, mode, newline="", encoding="utf-8") as raw_f:
        w = csv.writer(raw_f)
        if not resuming:
            w.writerow(["name", "year", "country"])
        for year in range(YEAR_START, YEAR_END + 1):
            if done.get(year, 0) > 0:
                print(f"  {year}: skipped (already have {done[year]} rows)")
                continue
            rows = fetch_year(year)
            for name, yr, country in rows:
                w.writerow([name, yr, country])
            total += len(rows)
            print(f"  {year}: {len(rows):6d} rows  (running total {total})")
            raw_f.flush()
            time.sleep(SLEEP_BETWEEN)
    print(f"Raw harvest complete: {total} rows -> {RAW_PATH}")
    return total


def build_table():
    print("Normalising into the game name space ...")
    # dedup on the full (NAME;YEAR;COUNTRY) triple, but KEEP every distinct
    # birth year for a name -- the game side disambiguates by country.
    seen = set()
    ordered = []
    kept_qids = 0
    with open(RAW_PATH, "r", newline="", encoding="utf-8") as raw_f:
        r = csv.reader(raw_f)
        header = next(r, None)
        for row in r:
            if len(row) < 2:
                continue
            name, yr = row[0], row[1]
            country = row[2] if len(row) > 2 else ""
            if not name or looks_like_qid(name):
                kept_qids += 1
                continue
            try:
                year_int = int(yr)
            except ValueError:
                continue
            nname = normalize(name)
            ncountry = normalize(country)
            if not nname:
                continue
            key = (nname, year_int, ncountry)
            if key in seen:
                continue
            seen.add(key)
            ordered.append((nname, year_int, ncountry))

    ordered.sort(key=lambda t: (t[0], t[1], t[2]))
    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    with open(OUT_PATH, "w", newline="", encoding="utf-8") as out_f:
        out_f.write(
            "# OpenSWOS real-age lookup: NAME;BIRTHYEAR;COUNTRY (normalised: "
            "uppercase, diacritics stripped).\n"
        )
        out_f.write(
            "# Source: Wikidata (https://www.wikidata.org), CC0 1.0. "
            "Generated by tools/wikidata-ages/fetch_ages.py.\n"
        )
        out_f.write(
            f"# Footballers (P106=Q937857) born {YEAR_START}-{YEAR_END}. "
            "Lines beginning with '#' are comments.\n"
        )
        for nname, year_int, ncountry in ordered:
            out_f.write(f"{nname};{year_int};{ncountry}\n")
    print(f"Normalised table: {len(ordered)} unique rows "
          f"(dropped {kept_qids} unlabelled) -> {OUT_PATH}")
    return len(ordered)


def main():
    harvest()
    build_table()


if __name__ == "__main__":
    main()
