# Player-data sources — real ages (96/97) + modern-squad import

Research for career-mode choice #1 ("derive ages + enrich with real data; also
maybe pull current players from an API"). Saved so it survives compaction.
Status: **research notes + go/no-go**, no code yet. Verified via web July 2026.

## Goal split
1. **Real 96/97 ages** for the enrichment table `KnownAges1997` (name+nation →
   birth year), layered on top of deterministic age derivation.
2. **Modern-squad import** (optional stretch): pull real *current* players/squads
   to play with modern teams.

## Candidate sources (free first)

| Source | Cost | What it gives | Fit |
|--------|------|---------------|-----|
| **openfootball / football.db** (GitHub, `openfootball/players`, `openfootball/leagues`) | **Free, public domain** | Historical teams/players, some birthdates; strong on World Cups + recent leagues, patchy on 1990s club squads | Best for a shippable offline table we can COMMIT (public domain = license-safe). Primary for #1 where coverage exists. |
| **Wikidata (SPARQL endpoint)** | **Free** | Footballer birthdates by name+nationality; huge coverage incl. 1990s stars | Best for filling 96/97 ages of *famous* players the openfootball set misses. One-off SPARQL export → static table. License: CC0. |
| **TheSportsDB** | **Free tier** (+ paid patron) | Modern teams/players, images, some birthdates; REST/JSON | Good cheap option for modern-squad import (#2). Coverage/accuracy varies. |
| **football-data.org** | **Free forever for top comps**, paid for more | Squads, lineups, fixtures, tables (machine-readable) | Solid for #2 modern top-league squads; rate-limited free tier. |
| **API-Football** (api-football.com / RapidAPI) | **Free plan (limited reqs)** + paid | +1,200 leagues, players/squads endpoint (birthdate incl.), transfers | Richest for #2; free plan enough for occasional imports, watch request cap. |
| **Sportmonks** | **Paid** (from ~€29/mo) | Enterprise squads/stats | Overkill; skip unless we go commercial. |
| Transfermarkt | — | Ages/values | **No official API + scraping ToS issues → AVOID.** |

## Decisions
- **#1 real 96/97 ages → offline COMMITTED table.** Build `KnownAges1997`
  (name+nation → birthYear) from **openfootball (public domain)** + a **Wikidata
  SPARQL** export for gaps (CC0). Both are license-safe to redistribute. No live
  API dependency at runtime — the table ships with the game; unmatched players
  fall back to deterministic derivation. This is what Step 03/05 consumes.
- **#2 modern-squad import → OPTIONAL, runtime, user-triggered.** A separate
  importer module (post-Phase 9). Default source **TheSportsDB or football-data.org
  free tier**; API-Football as the richer opt-in (user supplies their own free
  key so we ship no secret and stay within their cap). Kept OUT of the core
  career build; behind an explicit "Import modern squads" action.
- Runtime never hard-depends on a paid API. Anything shipped is public-domain/CC0.

## TODO when this phase executes
- Export a Wikidata SPARQL query for footballers active 1996-1997 with
  birthdate + nationality; reconcile names against our TEAM.* roster (fuzzy match
  by surname + nation). Store as a compact CSV/`.cs` table under
  `game/scripts/Competition/Career/KnownAges1997.cs`.
- Spike TheSportsDB + football-data.org free endpoints for a modern-squad JSON
  shape; sketch the importer that maps them onto `TeamRecord`/`PlayerRecord`.

## Sources
- [openfootball/players](https://github.com/openfootball/players)
- [openfootball/leagues](https://github.com/openfootball/leagues)
- [football.db home](https://openfootball.github.io/)
- [football-data.org API](https://www.football-data.org/documentation/api)
- [API-Football players/squads](https://www.api-football.com/news/post/football-players-squads)
- [TheSportsDB free API](https://www.thesportsdb.com/free_sports_api)
- [Guide to football data & APIs (jokecamp)](https://www.jokecamp.com/blog/guide-to-football-and-soccer-data-and-apis/)
