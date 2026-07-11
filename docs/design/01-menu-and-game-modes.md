# Menu & Game Modes — Design Plan (OpenSWOS)

**Status:** PLAN ONLY (no code yet). Drafted 2026-07-10.
**Scope:** Rebuild the SWOS front-end menu from scratch, reorganised around
**modern game modes and modern rules**, keeping the SWOS feel but dropping the
mid-90s limitations. **Career mode is deliberately out of scope for this pass**
(planned as a later, separate doc).
**Non-goal here:** pixel-exact recreation of the original menu art. We keep the
SWOS look-and-feel (charset font, coloured button frames, two-column layout,
D-pad navigation) but the *content tree* is redesigned.

This doc is the target we design toward. Nothing below is built yet — the
current front-end is the single-screen placeholder in `game/scripts/Main.cs`
(6 cycleable slots: pitch / home / away / opponent / length / speed). That
placeholder is throwaway; this plan replaces it.

---

## 0. What "modern" means here (and what it doesn't)

The user's steer: *modern gameplay and rules — but **no** drinks break; just
**more substitutions** and **more tournaments** like the competitions that
exist today.*

So "modern" = three things, layered **on top of** the faithful 1:1 sim, never
replacing it:

1. **Modern competition formats** — real-world 2024/25-era tournament shapes
   (Swiss-model Champions League, 48-team World Cup, single-table leagues,
   two-legged knockouts, group→knockout hybrids). See §5 and §6.
2. **Modern rules, as toggles** — 5 substitutions, concussion sub, extra time +
   shootout, optional VAR, optional away-goals (off by default now that UEFA
   scrapped it), realistic added time. See §10.
3. **Netplay as a first-class menu section** — a hard project constraint
   (`docs/decisions/01-netcode-model.md`), so it gets a real home in the tree
   from day one, not bolted on. See §7.

**Explicitly NOT wanted:** cooling/"drinks" breaks, rush-goalie, multi-ball, and
other novelty rules. If we ever add them they live behind an off-by-default
"Fun rules" sub-panel and never touch a ranked/faithful match.

**Fidelity rule (unchanged from CLAUDE.md):** every modern toggle defaults to
the setting that reproduces original SWOS behaviour. A user who never opens the
Rules panel gets a faithful match. Modern behaviour is opt-in.

---

## 1. Design principles

- **Built programmatically, not in the Godot editor.** Every screen is
  composed in C# (`_Ready`/factory methods), fed by data resources. No
  editor-authored `.tscn` scene trees for menus. (CLAUDE.md locked decision.)
- **Data-driven menus.** A screen = a list of entry descriptors (label, colour,
  neighbours for D-pad nav, on-select action). This mirrors the original SWOS
  menu-as-data structure (`docs/SWOS/menus.txt`: 22-byte header + 56-byte
  entries) and lets us define new screens without new plumbing.
- **Variant-aware.** Everything respects the `Variant` boundary (PC vs Amiga:
  pitch size, constants, asset layout). Team DB, pitch list, and rule defaults
  can differ per variant.
- **Deterministic & netplay-safe.** Menu choices resolve to a compact
  **MatchSetup**/**CompetitionSetup** struct that is the *only* thing crossing
  into the sim. Two peers that agree on that struct simulate identically. No
  menu state leaks into gameplay.
- **One input model.** D-pad/stick + fire to navigate; the same on keyboard,
  gamepad, and touch. Held-direction auto-repeat for long team lists (already
  in the placeholder).
- **Reuse the sim we have.** The `SwosVm` port already runs full playable
  matches. The menu's job is to *configure and sequence* matches, plus persist
  competition state between them — not to touch gameplay.

---

## 2. Screen map (the whole tree)

```
HOME (top-level)
├── PLAY NOW ............... instant single match, sensible defaults
├── FRIENDLY .............. full pre-match setup, one-off match or mini-series
├── COMPETITIONS  ▸
│   ├── PRESET COMPETITIONS ▸   (real modern comps, pre-built)
│   │   ├── Domestic Leagues        (single-table, double round-robin)
│   │   ├── Domestic Cups           (single-elimination)
│   │   ├── Champions League        (Swiss league phase → knockout)
│   │   ├── Europa / Conference     (analogous)
│   │   ├── World Cup               (48-team 2026 OR classic 32-team)
│   │   ├── Continental (Euro/Copa) (national teams)
│   │   ├── Nations League          (league + finals four)
│   │   └── Club World Cup / Super Cup
│   ├── CUSTOM COMPETITION ▸       (DIY builder — pick format + teams)
│   ├── SEASON ▸                   (league + its domestic cups, one nation)
│   └── LOAD / CONTINUE            (resume a saved contest)
├── MULTIPLAYER  ▸
│   ├── LOCAL (same screen, 2–4 pads)
│   ├── HOST GAME (LAN / online)
│   ├── JOIN GAME (code / browser)
│   └── NET RULES & LATENCY
├── TEAM & TACTICS  ▸
│   ├── EDIT TACTICS
│   ├── EDIT CUSTOM TEAMS
│   └── KITS / EDITOR PROFILES
├── REPLAYS & HIGHLIGHTS ▸
├── OPTIONS  ▸
│   ├── CONTROLS
│   ├── VIDEO
│   ├── AUDIO
│   ├── GAMEPLAY   (length, pitch, replays, game style PC/Amiga)
│   ├── RULES      (the modern-rules toggle catalogue — §10)
│   └── NETPLAY    (region, port, relay)
└── EXIT
```

**How this maps to the original SWOS main menu** (`src/menus/mnu/main.mnu`):

| Original entry        | New home lives as…                                   |
|-----------------------|------------------------------------------------------|
| FRIENDLY              | PLAY NOW + FRIENDLY                                   |
| DIY COMPETITION       | COMPETITIONS ▸ CUSTOM COMPETITION                    |
| PRESET COMPETITION    | COMPETITIONS ▸ PRESET COMPETITIONS                   |
| SEASON                | COMPETITIONS ▸ SEASON                                |
| CAREER                | *(out of scope this pass — reserved slot)*           |
| CONTINUE / SAVE / LOAD CONTEST | COMPETITIONS ▸ LOAD / CONTINUE + auto-save  |
| EDIT TACTICS          | TEAM & TACTICS ▸ EDIT TACTICS                        |
| EDIT CUSTOM TEAMS     | TEAM & TACTICS ▸ EDIT CUSTOM TEAMS                  |
| REPLAYS               | REPLAYS & HIGHLIGHTS                                  |
| OPTIONS               | OPTIONS (now with RULES + NETPLAY sub-panels)        |
| SAVE DISK FILING (hidden) | dropped — persistence is automatic                |
| EXIT                  | EXIT                                                 |
| *(new)*               | MULTIPLAYER (netplay is a day-one project constraint)|

Everything the original offered is preserved except Career (deferred). The two
structural additions are **MULTIPLAYER** and a dedicated **RULES** panel.

---

## 3. HOME (top-level)

- SWOS two-column button layout, charset font, coloured frames (reuse the
  `SwosFont` + button-frame renderer already in `Main.cs`).
- Spinning-logo / attract background already exists (`Sim/Port/SpinningLogo.cs`).
- Left column = tools (Team & Tactics, Replays, Options). Right column = play
  (Play Now, Friendly, Competitions, Multiplayer). Exit bottom-left like the
  original exit icon (sprite 1334).
- **Persistent "Continue" banner:** if a saved contest exists, the bottom shows
  a green `CONTINUE: <competition> — <round>` bar (mirrors the original
  `continueContest` entry) so the player drops straight back in.
- Home never touches the sim; selecting a play mode pushes a setup sub-screen.

---

## 4. PLAY NOW / FRIENDLY

### PLAY NOW
One-button instant match with last-used (or default) teams, faithful rules,
default pitch, 3-min halves. This is the fast path for testing and casual play —
essentially the current placeholder screen collapsed to a single confirm.

### FRIENDLY
The full match-setup screen. Fields:

- **Home team / Away team** — picked from the 1730-team DB (nation → team
  drill-down; held-arrow fast scroll already implemented). Search-by-letter.
- **Opponent type per side** — Human / CPU / Human+Human (co-op) — decouples
  "who controls this team" from "which team". Enables 2v2 later.
- **Pitch** — 5 (of 6) SWOS variants; "Random"/"Seasonal" option.
- **Match length** — 3 / 5 / 7 / 10 min (SWOS presets) + a "real-time 45"
  option for long-form play.
- **Rules preset** — `Faithful` (default) / `Modern` / `Custom…` (opens §10).
- **Series** — single match, or best-of / two-legged mini-tie (reuses the
  formats engine, §6, at N=2 teams).
- **Kit selection** — home/away with automatic clash resolution (already
  implemented: `KitClash.cs`).

Confirm → **versus screen → stadium screen → kickoff** (§11).

---

## 5. COMPETITIONS hub

The heart of the "more modern tournaments" request. Three ways in — **Preset**,
**Custom**, **Season** — all built on one **formats engine** (§6). A competition
is persisted as a **contest save** and resumed from HOME.

### 5.1 PRESET COMPETITIONS (real, modern shapes)

Pre-authored competition definitions shipped as data resources
(`.tres`/JSON), each just a parameterisation of the formats engine. Team pools
come from the existing DB (grouped by nation/division). Proposed catalogue:

| Preset                 | Modern format modelled                                                        |
|------------------------|-------------------------------------------------------------------------------|
| **Domestic League**    | Single table, **double round-robin** (home+away). Any nation's division.      |
| **Domestic Cup**       | **Single-elimination**, optional two-legged semis, single-match final.        |
| **Champions League**   | **Swiss league phase** (36 teams, 8 matches, one table) → knockout play-off round → R16 → QF → SF (two-legged) → **single-match final**. |
| **Europa / Conference**| Same shape, separate team pool.                                               |
| **World Cup**          | Two presets: **2026 (48 teams → 12 groups of 4 → 32-team knockout)** *and* **classic (32 teams → 8 groups of 4 → R16…)**. National teams. |
| **Continental**        | **Euro / Copa América** — groups → knockout, national teams.                  |
| **Nations League**     | Divisional round-robin groups → **Finals Four**.                              |
| **Club World Cup**     | New **32-team** group→knockout.                                               |
| **Super Cup**          | **Single match** (league champ vs cup champ) — trivial formats-engine case.   |

Each preset screen: pick your controlled team(s), confirm rules preset, draw/seed
the field, then it's a persisted contest.

### 5.2 CUSTOM COMPETITION (DIY builder)

Replaces the original "DIY COMPETITION". A guided builder that exposes the
formats engine directly:

1. **Number of teams** (2 … 64).
2. **Format** — pick a primitive or a two-stage combo (§6): Knockout / League /
   Groups+Knockout / Swiss+Knockout / Two-legged Knockout.
3. **Stage options** — legs (1 or 2), seeding (seeded / random draw / manual),
   third-place play-off (on/off), replays vs extra-time-then-pens for draws.
4. **Team selection** — hand-pick, "fill from nation", or "random N from DB".
5. **Rules preset** (§10) and **match length**.
6. **Name it** → saved as a contest.

The builder validates (e.g. Swiss/groups need a compatible team count) and shows
a live preview of the bracket/table before commit.

### 5.3 SEASON

A single nation's **league + its domestic cup(s)** run concurrently across a
season calendar, with the same table/fixtures machinery as Preset. Promotion/
relegation between divisions if the nation has multiple. (No transfers/finance —
that's Career, deferred.)

### 5.4 LOAD / CONTINUE

Lists saved contests (name, format, current round, your team, standings
snapshot). Resume, or delete. Auto-save after every match so a crash never loses
a competition.

---

## 6. Competition **formats engine** (the shared core)

One data-driven engine powers Friendly series, Preset, Custom, and Season. A
**Competition** = an ordered list of **Stages**; each Stage is one **format
primitive** plus **qualification rules** that feed the next stage.

**Format primitives:**

- **Single match** — one game (Super Cup, final).
- **Two-legged tie** — home + away, aggregate score.
- **Single round-robin** — each plays each once (groups).
- **Double round-robin** — home + away (domestic leagues).
- **Swiss-system league phase** — N teams, fixed K matches each, single table,
  no rematches (the new UCL model). Pairing by ranking each round.
- **Single-elimination bracket** — seeded or drawn; per-round leg count.
- **Group stage** — M parallel round-robin groups.

**Cross-cutting resolution rules** (all data-driven, all rule-preset-aware):

- **Draw/seeding:** seeded, random draw (deterministic from a shared seed so
  netplay peers draw identically), or manual.
- **Tie-breakers (league/group):** points → goal difference → goals for →
  head-to-head → (optional) away goals → drawn lots (seeded RNG).
- **Knockout tie resolution when level:** extra time → penalty shootout; OR
  replay; OR away-goals-then-ET (retro). Chosen by the Rules preset (§10).
- **Qualification:** "top X of table/group advance", "winners meet losers", etc.

This means "Champions League" and "my mate's 12-team knockout" are the *same
code*, different data. It also keeps the sim untouched: the engine only decides
**which MatchSetup to simulate next** and **records results**.

**Determinism note:** all randomness (draws, coin-tosses, lots) draws from a
seeded RNG stored in the contest save, so a resumed or networked competition is
reproducible — consistent with the lockstep netcode assumption.

---

## 7. MULTIPLAYER / NETPLAY

Netplay is a **day-one hard constraint** (CLAUDE.md; model in
`docs/decisions/01-netcode-model.md`, working assumption = deterministic
lockstep over Godot's `MultiplayerAPI` + ENet, WebRTC for browser). The menu
must expose it as a top-level section, not hide it.

- **LOCAL** — 2–4 controllers on one screen (couch play; co-op teams).
- **HOST GAME** — start a lobby; get a short join code / room. Choose LAN or
  online (relay/WebRTC). Host owns the MatchSetup/CompetitionSetup.
- **JOIN GAME** — enter code, or browse LAN games; browser client via WebRTC.
- **LOBBY** — both peers see the same versus screen; each picks their side and
  confirms; host locks rules; ready-check → deterministic kickoff.
- **NET RULES & LATENCY** — input-delay frames, region, whether spectators
  allowed, reconnect handling. Surfaces the lockstep model's knobs.
- **Networked competitions** — a whole Custom/Preset competition can be played
  online: the contest save + seeded RNG travel with the lobby so brackets and
  draws stay in sync.

The menu's contract with netcode: **the only thing serialised across the wire is
the setup struct + inputs.** Everything the player picks resolves to that.

---

## 8. TEAM & TACTICS

- **EDIT TACTICS** — formation/tactics editor (original had 8-position grid
  tactics; `TacticsLoader.cs` already parses tactics). Modern nicety: more
  presets and per-competition saved tactics.
- **EDIT CUSTOM TEAMS** — roster/kit/skills editor writing back to the team DB
  (`TeamRecord`/`PlayerRecord`, `TeamFile.cs`).
- **KITS** — home/away kit colour editing; preview against the clash-resolver
  (`KitClash.cs`, `KitPalette.cs`).
- **PROFILES** — save/name editor sets so a player's custom teams/tactics
  persist and can be loaded into any competition.

Faithful to original (`editTactics` / `editCustomTeams` entries) — just a
cleaner home and profile persistence.

---

## 9. REPLAYS & HIGHLIGHTS

- Browse auto-saved replays/highlights (the original had AUTO REPLAYS / AUTO
  SAVE HIGHLIGHTS gameplay toggles — see §10 Gameplay).
- Play back, scrub, save/export.
- **Modern:** per-competition highlight reel; shareable clips (later).
- Depends on the replay system, which depends on the deterministic sim already
  being in place — a natural fit for lockstep (record inputs, re-simulate).

---

## 10. OPTIONS — and the modern RULES catalogue (**zasady**)

Options mirrors the original tree (`options.mnu`: CONTROLS / VIDEO / AUDIO /
GAMEPLAY) plus two new panels: **RULES** and **NETPLAY**.

### 9-a. GAMEPLAY (faithful to original `gameplayOptionsMenu`)
- **Game length** — 3 / 5 / 7 / 10 min (SWOS) + real-time 45.
- **Pitch type** — Seasonal / Random / Frozen / Muddy / Wet / Soft / Normal /
  Dry / Hard (original list).
- **Auto replays / Auto-save highlights / Auto-save replays** — on/off.
- **All player teams equal** — on/off (original).
- **Show pre-match menus** — on/off (skip versus/stadium screens).
- **Game style** — **PC / Amiga** (variant switch — the reference target is
  Amiga; already a `Variant` in the codebase).

### 9-b. RULES (new — the modern-rules toggle catalogue)

Presented as three presets plus a custom panel:
- **Faithful** (default) — every toggle set to reproduce original SWOS.
- **Modern** — 2024/25 real-football defaults.
- **Custom…** — the full grid below.

| Rule                    | Options                                   | Faithful | Modern | Notes |
|-------------------------|-------------------------------------------|----------|--------|-------|
| **Substitutions**       | 3 / 5 (3 windows) / 7 (cup) / unlimited   | as-original | **5** | the user's "more changes" — bench UI already supports subs (`Bench.cs`, task #168/#190) |
| **Concussion sub**      | off / on (extra permanent)                | off      | on     | extra sub, doesn't count to limit |
| **Extra time**          | off / 2×15 then pens / straight to pens   | as-original | 2×15 then pens | knockout ties |
| **Penalty shootout**    | ABAB / ABBA order                         | ABAB     | ABAB   | shootout UI = later work |
| **Away-goals rule**     | off / on                                  | off*     | **off** | *UEFA abolished 2021; retro fans can turn on |
| **Added (stoppage) time** | off / fixed / realistic-accumulate      | off      | realistic | display + play added time |
| **VAR**                 | off / on (goal/pen/red/ID review)         | off      | off**  | **optional; visual stub first, review flow later |
| **Offside**             | off / on                                  | on       | on     | (sim already handles set pieces/OOB) |
| **Match-length source** | halves vs real-time                       | halves   | halves | ties into Gameplay length |
| **Fun rules** (panel)   | rush-goalie / multi-ball / **no drinks break** | all off | all off | novelty; **drinks break intentionally absent** |

Every RULES value flows into the **MatchSetup** struct, so it's netplay-safe and
recorded in replays/contest saves. **CPU difficulty note:** the SwosVm port
removed the old difficulty/physics menu toggles (task #183) in favour of faithful
AI; if we re-introduce CPU strength it must be a *faithful* lever (e.g. the
original's team-quality scaling), not the old speed hack — flagged as an open
item, not assumed.

### 9-c. NETPLAY
Region, host port, relay/STUN config, default input-delay frames, reconnect
policy. Backs the MULTIPLAYER section (§7).

### 9-d. CONTROLS / VIDEO / AUDIO
As original (`controlOptionsMenu` / `videoOptionsMenu` / `audioOptionsMenu`):
keyboard redefine, gamepad config/test, window mode, volumes. Reuse the
original menu shapes; low RE risk.

---

## 11. Pre-match flow (versus → stadium → kickoff → result)

Faithful to the original sequence (`versus.mnu`, `stadium.mnu`,
`docs/SWOS/preGameMenus.txt`):

1. **VERSUS screen** — "`<competition> — <round>`" header, `TEAM A  V  TEAM B`,
   ~1.5 s hold (original: 100 vertical retraces). Skippable via "Show pre-match
   menus = off".
2. **STADIUM screen** — both line-ups: 11 player sprites + names each side, kits
   shown, stadium backdrop. Last chance to change tactics/subs.
3. **KICKOFF** → hand off `MatchSetup` to the `SwosVm` sim (already runs full
   matches).
4. **RESULT bar** — scoreline + scorers + minutes (already implemented,
   tasks #167/#175), then back to the competition (advance bracket/table) or menu.

For competitions, steps 3–4 loop per fixture; the formats engine (§6) picks the
next `MatchSetup` and the result feeds standings.

---

## 12. Persistence

- **Contest saves** — one per running competition (format state, standings,
  fixtures, seeded RNG, your team, rules preset). Auto-saved each match;
  resumable from HOME "Continue" and COMPETITIONS ▸ LOAD.
- **Profiles** — custom teams, tactics, kits, control bindings, default rules
  preset.
- **Settings** — Options values (already partly present: `swos.ini`).
- Plain-text/JSON where practical (agent-friendly, diffable), consistent with
  the project's text-first stance.

---

## 13. Mapping to current code (what exists vs new)

| Menu need                        | Already in repo                                   | New work |
|----------------------------------|---------------------------------------------------|----------|
| Button/entry rendering, font     | `SwosFont.cs`, button frames in `Main.cs`         | Generalise into a reusable data-driven menu widget |
| Team DB + drill-down             | `TeamRecord`/`PlayerRecord`, `TeamFile.cs`, 1730 teams loaded | Nation/division grouping, search |
| Pitch variants                   | `AmigaPitch.cs`, pitch-decode (5/6 variants)      | "Seasonal/Random" selection |
| Kit clash                        | `KitClash.cs`, `KitPalette.cs`                    | Kit editor UI |
| Match run                        | `SwosVm` port (full playable matches)             | `MatchSetup` struct as the sim's sole input |
| Subs/bench                       | `Bench.cs` (tasks #168/#190)                       | Sub-count rules, windows |
| Result/scorers                   | `Result.cs` (tasks #167/#175)                      | Feed results into standings |
| Tactics                          | `TacticsLoader.cs`                                 | Tactics editor UI |
| Netcode                          | decision doc `01-netcode-model.md`                | Lobby, transport, lockstep loop |
| Formats engine                   | —                                                 | **All new** (§6) |
| Competition/contest saves        | `swos.ini` (settings only)                        | **All new** (§12) |
| Modern rules toggles             | difficulty/physics toggles *removed* (#183)       | **RULES panel + MatchSetup fields** (§10) |

The single-screen placeholder in `Main.cs` (fields around the `_menuFocus` /
`MatchLengthPresets` / `OpponentMode` block) is the throwaway this plan retires.

---

## 14. Implementation phasing (suggested order)

1. **Menu framework** — data-driven entry/screen widget (label, colour,
   neighbours, on-select), SWOS look, D-pad nav, held-repeat. Port the
   placeholder's 6 fields onto it as the FRIENDLY screen. *Foundation for
   everything.*
2. **MatchSetup struct + Rules preset (Faithful only)** — one clean struct into
   the sim; wire versus/stadium/result loop around a single match.
3. **Formats engine core** — single match, round-robin, single-elim; standings
   + tie-breakers; deterministic seeded draw. Ship **Custom Competition**
   (knockout + league) first — smallest surface that proves the engine.
4. **Preset competitions** — author data for Domestic League/Cup, then
   Champions League (Swiss+knockout), then World Cup. Reuses step 3.
5. **Modern RULES panel** — 5 subs + concussion + extra-time/shootout +
   realistic added time; away-goals toggle; VAR as a later stub.
6. **Persistence** — contest saves, Continue banner, Load screen, profiles.
7. **Multiplayer** — local first, then host/join lobby on the lockstep loop,
   then networked competitions.
8. **Season**, then (separate doc) **Career**.

Each phase is independently testable and leaves the game playable.

---

## 15. Open decisions (defer to user)

- **Menu visual direction** — faithful SWOS charset/colour look (recommended,
  reuses `SwosFont`) vs a cleaner modern re-skin. This plan assumes *SWOS look,
  modern content*.
- **World Cup default** — ship 48-team (2026) as the headline, or classic
  32-team, or both? (Plan: both, 48 as default.)
- **UCL format** — new Swiss league phase (recommended, it's the current real
  format) vs classic 8×4 groups. (Plan: Swiss, with classic as a Custom option.)
- **VAR depth** — cosmetic stub (banner + delay) first, or full review flow?
  (Plan: stub first.)
- **CPU difficulty** — leave removed (faithful only), or re-introduce as a
  *faithful* team-strength lever? (Plan: leave removed until a faithful design
  exists.)
- **Career scope** — confirmed out for now; this plan reserves its slot.

---

*Next step after sign-off: build Phase 1 (data-driven menu framework) and lift
the current `Main.cs` placeholder onto it. No sim code changes required.*
