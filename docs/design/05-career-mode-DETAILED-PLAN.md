# Career mode — DETAILED implementation plan (Codex-chunked)

Source of decisions: `docs/design/04-career-mode-plan-CHOICES.md` (user-filled).
Scope locked = **B (Manager-lite)**. This plan turns each choice into concrete,
Codex-sized STEPS. Every step is one `offload_to_codex.sh` call; after each I
build + run a headless/visual check and screenshot the menu before continuing.

> STATUS: PLAN ONLY. Nothing here is implemented yet. Execution starts after the
> pre-flight backup (below). Steps are meant to be sent to Codex IN ORDER.

---

## 0. Locked decisions (from your choices)

| # | Feature | Decision | Notes from your comments |
|---|---------|----------|--------------------------|
| 0 | Scope | **B Manager-lite** | age+retire+regen+growth+potential+coaches+scouting+fatigue |
| 1 | Age | **A derive** + real-age enrichment | research free data/API for real 96/97 ages AND modern players |
| 2 | Potential | **C visible estimate after scouting** | estimate is FUZZY — accuracy depends on scout quality + luck, never 100% |
| 3 | Growth | **C per-match** + **iii position-weighted** | use double/float for fine-grained accumulation |
| 4 | Coaches | **C hire staff (economy)** | needs finances (#8) |
| 5 | Fatigue | **B toggle** + **persist** + new **Stamina** stat | recovers over time; faster for young + high-stamina |
| 6 | Ageing/retire | **B decline-driven** | age +1/season, retire on age OR skill-floor |
| 7 | Regen | **C both** (club intake + free-agent pool); Scouting **C network+regions+cost** | SWOS career ALREADY had transfers — build on it, don't reinvent |
| 8 | Finances | **B budget + wages-lite** | enables coaches + scouting |
| 9 | Form | **B simple form** | small temporary +/- from recent results |
| 10 | UI | **B dedicated Squad/Development screen** | TEST VISUALLY; modern/CSS-like look; RAISE resolution (no lores overscan) |

### Determinism policy (critical — resolves your float note in #3)
- The **match sim** (`game/scripts/Sim/`, `SwosVm/`) stays **integer/fixed-point**
  — it is the lockstep-netplay path and must never touch float.
- The **career layer** (`game/scripts/Competition/`) ALREADY uses `double`
  (`CompetitionEngine.SimulateResult` → Poisson). Growth, potential, fatigue
  recovery, finances therefore MAY use `double` freely — they run at
  match/season boundaries, not inside the 70 Hz tick.
- Bridge rule: anything the match reads must be **quantized to int before the
  match** (skills → 0..7, stamina → 0..100 int). In-match fatigue drain
  accumulates in an **integer** counter derived from the already-integer
  distance-covered value, so the tick stays deterministic. Double never crosses
  into the sim.
- All randomness flows through the existing xorshift32 `CompetitionState.RngState`
  (one stream) so saves replay identically.

### Data-model policy
- New per-player career data lives ONLY in the save (`CareerWorld`, below), never
  in the read-only `TeamRecord`/`PlayerRecord` (those are `init`-only, loaded from
  TEAM.*). Career players are seeded FROM `PlayerRecord` at career creation, then
  evolve independently.
- Materialization scope: **materialize the ENTIRE world — every club, every
  player (all ~1616/1730 teams)**. Modern hardware (and even current retro
  handhelds) dwarfs the MC68000 SWOS targeted; any per-season processing cost is
  invisible under a career menu. Show a **progress bar** during world build /
  season rollover if it ever takes a beat. No scope trimming.

### Resolution / UI policy (choice 10)
- Match view keeps its native SWOS-res viewport (the pitch art is authored for it
  — do NOT rescale the pitch base).
- MENUS move to a **hi-res UI layer** independent of the 384×272 match viewport.
  **Design target resolution: 640×480 (4:3)** — matches budget retro handhelds
  like the R36S (640×480 screen); scales cleanly to desktop/Deck. Menu drawn in a
  640×480 design space (own SubViewport or canvas layer), match path untouched.
  This is Phase 2 so every new career screen is built in the new look from the
  start.
- EXECUTION ORDER NOTE (usage-driven): the pure-logic gameplay phases (3-9) are
  verified cheaply HEADLESS (assertions, no screenshots) and are executed FIRST
  via Codex; the visual UI framework (Phase 2) — which needs the screenshot
  review loop — is deferred to a session where that loop is affordable.
- **Font: for now use the ORIGINAL SWOS charset at x2** (the original menus used
  x3; x2 fits more rows on screen). Later we source similar-looking modern fonts
  and swap them in. Keep the SWOS identity (our palette).
- Modern look = paraphrase CSS idioms already partly present in `MenuTheme.cs`
  (gradients, rounded frames): add panel cards, drop shadows, consistent spacing
  scale. Keep the SWOS identity (our palette).

---

## Pre-flight (do ONCE before Step 1)

```bash
# 1. Backup the whole project (timestamped .7z, per project convention)
powershell -File "I:/GITHUB/W_OPEN_SWOS/tools/backup.ps1"   # or the repo's backup script

# 2. Baseline build must be green before we touch anything
dotnet build game/OpenSwos.csproj

# 3. Baseline menu screenshots (so we can diff the look after Phase 2)
.tools/godot/Godot_v4.6.2-stable_mono_win64_console.exe --headless --path game --menu-shot -- --shotdir "I:/GITHUB/W_OPEN_SWOS/_research/shots/baseline"
```

## How each step is sent to Codex

```bash
bash "I:/GITHUB/W_OPEN_SWOS/_codex_worker/offload_to_codex.sh" -m gpt-5.6-terra -e high -s danger-full-access -d "I:/GITHUB/W_OPEN_SWOS" @"I:/GITHUB/W_OPEN_SWOS/_research/codex_steps/STEP_XX.md"
```
> NOTE (this machine): the repo lives on drive `I:`, which the Codex OS sandbox
> does not map — `-s workspace-write` fails with "no mapped I: drive". Always
> pass **`-s danger-full-access`** so Codex runs directly on the host (which has
> I:). Effort: `high` for real code steps, drop to `low`/`medium` for trivial ones.
Each step below has a **CODEX PROMPT** block — save it as `STEP_XX.md` and pipe it.
Codex edits files + reports a diff; then I run the step's **VERIFY** commands,
read the screenshots, and only then move on.

## My verification loop (after every step)
1. `dotnet build game/OpenSwos.csproj` — must be green.
2. Engine steps: `... --headless --path game --competition-test` (+ any new
   assertions the step adds) — read the log.
3. Visual steps: `... --headless --path game --menu-shot -- --shotdir <dir>` →
   I `Read` the PNGs, judge the look, and decide the next click / any fix.
4. If broken, I send Codex a focused fix prompt before advancing.

---

# PHASE 0 — Research spike (I do this, not Codex)

**STEP 00 — Real-age + player-data source research.** Deliverable:
`docs/design/06-player-data-sources.md` deciding where 96/97 real ages come from
and whether a modern-player API is viable. Candidate free sources to verify with
WebFetch/WebSearch:
- **openfootball / football.json** (GitHub, public domain) — historical squads,
  some with birthdates.
- **Wikidata SPARQL** (free) — footballer birthdates by name+nationality; good
  for "famous players in 96/97".
- **TheSportsDB** (free tier) — modern players/teams, has an API.
- **football-data.org** (free tier, rate-limited) — modern competitions/squads.
- **API-Football / RapidAPI** — richest but PAID (note cost, keep optional).
- Transfermarkt has NO official API + scraping ToS issues → avoid.

Output: (a) chosen offline table format for a `KnownAges1997` lookup
(name+nation → birth year), seeded from a free dataset; (b) a go/no-go + design
sketch for an OPTIONAL "import modern squads from API" module (stretch, Phase 9+).
No code yet. **This gates Step 03's real-age enrichment but not the derivation
fallback**, so implementation can start at Phase 1 in parallel.

---

# PHASE 1 — Career world foundation (persistent squads + save/load)

Everything else needs a persistent, evolving roster. Today career only stores
`TeamRef` (name+strength). We add `CareerWorld`.

**STEP 01 — Data model: `CareerWorld` / `CareerClub` / `CareerPlayer`.**
- New file `game/scripts/Competition/Career/CareerWorld.cs` (POCOs only, no Godot
  types, System.Text.Json-serializable — mirrors `CompetitionModels.cs` style).
- `CareerPlayer`: stable `int Id`; identity (`Name`, `Position`, `Nationality`,
  `ShirtNumber`, `Face`); skills as **7 doubles** on the 0..7 scale
  (`Passing`..`Finishing`); `int Age`; `double Potential` (hidden true ceiling,
  ≤7); `int Stamina` (0..7, NEW); `double[] GrowthCarry` (len 7); `int Form`
  (-3..+3); `int FatigueCarry` (0..100); scouting fields
  (`int ScoutAccuracy`, `double EstLow`, `double EstHigh`, `bool Scouted`);
  `bool Retired`.
- `CareerClub`: `ushort GlobalId`; `List<CareerPlayer> Squad`; `long Budget`;
  `List<Coach> Coaches`; `ScoutingState Scouting`.
- `CareerWorld`: `Dictionary<ushort,CareerClub> Clubs`; `List<CareerPlayer>
  FreeAgents`; `int NextPlayerId`.
- Add `CareerWorld? World { get; set; }` to `CareerState` (CompetitionModels.cs).
- Helper `CareerPlayer.QuantizedSkills()` → int[7] clamped 0..7 (round) for
  match/display. Add unit-ish self-check method callable from the test harness.
- FILES: new `Career/CareerWorld.cs` + 3-line add in `CompetitionModels.cs`.
- VERIFY: build green; JSON round-trip (serialize→deserialize a hand-made world,
  assert equality) exercised via a new `--career-model-test` flag added to
  `Main.cs` **by me** (keep Codex out of Main.cs here).

**STEP 02 — Materialize the world at career creation + persist.**
- New file `game/scripts/Competition/Career/CareerWorldBuilder.cs`:
  `BuildWorld(CompetitionState career, IReadOnlyList<TeamRecord> masterTeams,
  IProgress<float>? progress)` — for **EVERY club in the master team list**
  (all ~1616/1730), seed a `CareerClub` whose `Squad` is built from that team's
  `PlayerRecord`s (skills copied to doubles; Age/Potential/Stamina left 0 for now
  — filled in Phase 2/3). Assign `Id`s from `NextPlayerId`. Report progress via
  the `IProgress` callback so the UI can show a bar.
- Hook: `CompetitionEngine.CreateCareer` (or the menu creation path) calls
  `BuildWorld` and stores it in `career.Career.World`. Confirm `CompetitionStore`
  already serializes the whole `CompetitionState` (it does — JSON) so `World`
  persists automatically; add a format-version bump + a load migration that
  tolerates old saves with `World == null` (rebuild lazily).
- FILES: new `Career/CareerWorldBuilder.cs`; edits to `CompetitionEngine.cs`
  (CreateCareer) + `CompetitionStore.cs` (version/migration). Disjoint from UI.
- VERIFY: `--competition-test` extended to create a career and assert
  `World.Clubs[playerClub].Squad.Count == 16` and that a save→load preserves it.

---

# PHASE 2 — Hi-res UI framework + Squad/Development screen skeleton (choice 10)

Do the look-and-feel upgrade BEFORE the gameplay systems so each new screen is
born modern, and so we have a place to SEE ages/potential as we add them.

**STEP 03 — Hi-res menu CanvasLayer + MenuTheme card/shadow additions.**
- Introduce a hi-res UI layer for menus (render Control/drawn UI at window
  resolution, not the 384×272 match viewport). Keep the match path untouched.
  Likely: a dedicated `CanvasLayer` with its own `follow_viewport`/scale, or
  switch menu drawing to window-space coords; document the exact mechanism in a
  header comment.
- Extend `MenuTheme.cs`: `Panel(w,h)` card style (rounded, subtle inner border +
  drop shadow via a cached texture), a spacing scale (`Pad`, `Gap`), and text
  rendered with the **original SWOS charset at x2 scale** (was x3 — x2 fits more
  rows). No behavior change to existing screens' logic.
- FILES: `MenuTheme.cs` (+ a new `Menu/MenuLayout.cs` for spacing/panel helpers).
  Menu wiring that lives in `MenuClient.cs`/`Main.cs` is done by **me** (shared
  god-files, single-editor rule) — Codex only touches Theme/Layout here.
- VERIFY (VISUAL): `--menu-shot` → read `03_*` screenshots. Compare to baseline;
  confirm crisper text + card panels, SWOS identity intact. I decide tweaks.

**STEP 04 — Squad/Development screen (read-only skeleton).**
- New file `game/scripts/Menu/Screens/SquadScreen.cs`: renders the player club's
  `CareerWorld` squad as a modern table — columns: No, Name, Pos, the 7 skills,
  and PLACEHOLDER columns Age / Pot / Sta / Form (blank until later phases).
  Row hover/selection using the new theme.
- I wire a "SQUAD" entry into the career dashboard (`MenuClient.cs`) + route the
  `--menu-shot` harness to visit it (my edit to Main.cs harness).
- FILES: new `Menu/Screens/SquadScreen.cs` (Codex); dashboard/harness wiring (me).
- VERIFY (VISUAL): `--menu-shot` → read the squad screenshot; confirm the table
  is legible and aesthetic at hi-res. Decide layout tweaks before adding data.

---

# PHASE 3 — Age model + ageing + retirement (choices 1, 6)

**STEP 05 — Age assignment (derive + real-age enrichment).**
- New file `game/scripts/Competition/Career/AgeModel.cs`:
  `AssignInitialAge(CareerPlayer p, CompetitionState s)` — deterministic
  derivation from skill/value tier (RNG from RngState): stars → 24..30, squad →
  20..28, cheap → 17..23 or 30+. Age bands: youth 16..18, prime 24..29, decline
  30+, retirement window from Step 07.
- If STEP 00 delivered a `KnownAges1997` table, look up by name+nation and use the
  real age when matched; else derivation. Table lives in
  `Career/KnownAges1997.cs` (data-only) if produced.
- Call from `CareerWorldBuilder` (fill Age on every squad + free-agent player).
- FILES: `Career/AgeModel.cs` (+ optional `KnownAges1997.cs`), 1 call in
  `CareerWorldBuilder.cs`.
- VERIFY: `--competition-test` asserts every career player has Age in [16,40] and
  distribution isn't degenerate (min<20 and max>32 exist). Then Squad screen shows
  the Age column populated → `--menu-shot`, read it.

**STEP 06 — Stamina stat assignment.**
- Extend `AgeModel`/a new `StaminaModel.cs`: assign `Stamina` 0..7 at creation
  (deterministic; loosely correlated with Speed + youth). Fill in builder.
- FILES: `Career/StaminaModel.cs`, 1 call in `CareerWorldBuilder.cs`.
- VERIFY: Squad screen Sta column populated; `--menu-shot` read.

**STEP 07 — Per-season ageing + decline-driven retirement (choice 6B).**
- New file `game/scripts/Competition/Career/SeasonProgression.cs`:
  `AgeAndRetire(CareerWorld w, CompetitionState s)` at season rollover —
  every player Age+1; retire if Age ≥ hard cap (≈36..40 with RNG) OR
  (Age ≥ 33 AND overall skill < floor). Retired players leave the squad (hole to
  be filled in Phase 5). Record retirements for a season-summary screen.
- Hook into the existing career rollover: `CompetitionEngine.AdvanceCareerSeason`
  (or a new `OnSeasonRollover` the menu calls) invokes `AgeAndRetire` BEFORE
  rebuilding fixtures.
- FILES: new `Career/SeasonProgression.cs`; edit `CompetitionEngine.cs`
  (AdvanceCareerSeason).
- VERIFY: a headless multi-season loop (extend `--competition-test` to advance
  5 seasons) asserts ages advance, some players retire, squads don't go empty
  (guard: if squad < 11, Phase 5 must refill — until then, allow temporary
  under-count and log it).

---

# PHASE 4 — Hidden potential + per-match growth (choices 2, 3)

**STEP 08 — Hidden potential assignment.**
- `Career/PotentialModel.cs`: `AssignPotential(CareerPlayer p, ...)` — hidden
  true ceiling (double ≤7), skewed by youth (young players can have high pot;
  older players' pot ≈ current). Deterministic. Never displayed directly.
- Fill in builder.
- VERIFY: `--competition-test` asserts Potential ≥ current overall for youths,
  distribution sane. (Not shown in UI yet — that's scouting, Phase 6.)

**STEP 09 — Per-match, position-weighted growth (choice 3C + iii, doubles).**
- `Career/GrowthModel.cs`: `ApplyMatchGrowth(CareerPlayer p, MatchParticipation
  mp, ...)` called after EACH career match for players who featured. Growth per
  attribute = f(minutes played, age curve, distance to Potential,
  position weight for that attribute) accumulated into `GrowthCarry[i]` (double);
  when carry ≥ threshold, the integer-facing skill ticks up. Old players can
  drift DOWN (negative growth) past decline age. Uses RngState for small noise.
- `MatchParticipation` (who played, minutes) comes from the match result hook.
  Provide a minimal struct now; wire real minutes from the match in this step
  (I handle the Main.cs full-time hook; Codex writes `GrowthModel` + struct).
- FILES: new `Career/GrowthModel.cs`; full-time→growth call (me, Main.cs).
- VERIFY: headless 5-season loop asserts a high-potential youth's overall rises
  over seasons and a 34-yo's declines; Squad screen shows a growth arrow (▲/▼)
  in the Pot column area. `--menu-shot` read.

---

# PHASE 5 — Youth regen + free-agent pool (choice 7 gen = C)

**STEP 10 — Youth intake per season (club) + free-agent pool.**
- `Career/RegenModel.cs`: at rollover, each club generates N youths (age 16..18,
  low current skill, hidden potential spread — most modest, few gems);
  additionally seed a global free-agent `FreeAgents` pool (released + regens).
  Names generated from existing `Assets/NationNames.cs` tables (real SWOS name
  pools) + deterministic RNG. Assign Age/Stamina/Potential via the Phase 2-4
  models so a regen is a full `CareerPlayer`.
- Squad refill: if `AgeAndRetire` left a club short, fill from its youths first,
  then free agents (AI clubs auto-fill; player is prompted — but auto-fill by
  default so nothing stalls; a later UI lets the player choose).
- FILES: new `Career/RegenModel.cs`; called from `SeasonProgression`.
- VERIFY: 10-season headless loop — team sizes stay ≥16, free-agent pool grows &
  drains, no null/empty squads, deterministic (same seed → same regens; assert by
  hashing world). Squad screen shows new young players. `--menu-shot` read.

---

# PHASE 6 — Scouting network + fuzzy potential reveal (choice 7 scout = C, 2C)

**STEP 11 — Scouting state + region model.**
- `Career/Scouting.cs`: `ScoutingState` (regions the club can scout, active
  assignments, accumulated knowledge). Regions map to nations/nation-groups.
  Scouting a region over time (per season/round) surfaces candidate youths
  (generated on demand from that region via RegenModel) and RAISES `ScoutAccuracy`
  on watched players.
- Cost: scouting consumes budget (ties to Phase 7). For this step, model the
  state + candidate discovery; wire cost when finances land (or stub cost=0 with
  a clearly-marked TODO that Phase 7 fills — NOT a user-facing stub).
- VERIFY: headless — assigning a scout to a region yields candidate players with
  rising accuracy over rounds; deterministic.

**STEP 12 — Fuzzy potential estimate (choice 2C: never 100%).**
- `PotentialModel.RevealEstimate(CareerPlayer p, int scoutQuality)` → sets
  `EstLow/EstHigh` as a RANGE around true Potential whose WIDTH shrinks with
  scout quality + time watched, plus RNG "luck" so it's never exact and can be
  wrong. `Scouted=true` once seen. UI shows the RANGE (e.g. "POT 5-7"), never the
  exact hidden value.
- Squad/Scouting screen shows the estimate for scouted players only.
- FILES: `PotentialModel.cs` (extend), Scouting screen render (new
  `Menu/Screens/ScoutScreen.cs`; dashboard wiring by me).
- VERIFY (VISUAL): scout a youth, `--menu-shot` the scout screen, confirm a fuzzy
  range is shown and tightens with a better scout. Read screenshots.

---

# PHASE 7 — Coaches (staff economy) + finances/wages-lite (choices 4C, 8B)

These are coupled (coaches cost money), so one phase.

**STEP 13 — Finances: budget + wages-lite.**
- `Career/Finance.cs`: `CareerClub.Budget`; season income (prize money by
  league position + cup run, from the existing result data) minus wages
  (derived from squad skill/age) and staff/scouting costs. Transfers spend/earn
  budget (build on SWOS's existing price model — you already have `GetPlayerPrice`
  in `Sim/Port/SkillScaling.cs`; reuse its valuation). Deterministic.
- FILES: new `Career/Finance.cs`; season-rollover call in `SeasonProgression`.
- VERIFY: headless multi-season — budgets move sensibly (winners richer),
  never NaN, transfers debit/credit correctly.

**STEP 14 — Coaches (hire staff) + training bonus to growth.**
- `Career/Staff.cs`: `Coach` (quality, wage, specialty e.g. attack/defence/youth).
  Hiring debits budget + adds wage. A coach's quality/specialty multiplies
  `GrowthModel` output for matching players (bigger for youth/high-pot) —
  fulfilling "focus training on promising players".
- Training-focus selection: player picks which promising players a youth coach
  concentrates on (bounded slots), boosting their growth.
- FILES: new `Career/Staff.cs`; `GrowthModel` reads active coaches; new
  `Menu/Screens/StaffScreen.cs` (hire/fire + assign focus); dashboard wiring (me).
- VERIFY (VISUAL): hire a youth coach, assign focus to a gem, run 3 seasons
  headless → focused gem grows faster than an unfocused peer (assert). Staff
  screen `--menu-shot` read.

---

# PHASE 8 — In-match fatigue + stamina (choice 5B, persist)

**STEP 15 — Fatigue toggle + in-match integer drain.**
- OPTIONS toggle "IN-MATCH FATIGUE" (default OFF globally; recommend ON in career
  — a career-scoped default is fine). When ON, each player accumulates an INTEGER
  tiredness counter from the already-integer distance-covered value (with & w/o
  ball) scaled by (8 - Stamina). Above a threshold, apply a small INTEGER skill
  penalty (speed/accuracy) for the sim — stays deterministic (no float in tick).
- Fatigue at match end writes `FatigueCarry` (persist) into the CareerPlayer.
- FILES: sim-side fatigue counter (me — this touches the match tick /
  `Sim/Port/`, which is the netplay-critical single-editor zone); OPTIONS toggle +
  persistence (me/Main.cs). Codex may prototype the `FatigueModel.cs` pure-math
  helper (int in → int penalty out) that the sim calls.
- VERIFY: headless match with fatigue ON — late-match sprint speed drops for a
  low-stamina player vs high; determinism preserved (same match twice = identical
  log — reuse the existing byte-identical smoke check).

**STEP 16 — Between-match stamina recovery (choice 5 comment).**
- `Career/FatigueModel.RecoverBetweenMatches(CareerPlayer p, int daysRest)` —
  `FatigueCarry` decays over time; faster for younger + higher-Stamina players
  (double math, career layer). Rotation matters: a tired player starts the next
  match handicapped.
- Squad screen shows a Sta/tiredness bar; player can see who needs rest.
- FILES: `Career/FatigueModel.cs` (extend); recovery call at fixture advance
  (SeasonProgression / round advance).
- VERIFY (VISUAL): play two quick career matches headless with the same XI →
  second match the un-rotated players show higher FatigueCarry; Squad screen
  tiredness bars `--menu-shot` read.

---

# PHASE 9 — Form + final polish/balancing (choice 9B)

**STEP 17 — Simple form.**
- `Career/FormModel.cs`: recent results/minutes give a small temporary skill +/-
  (`Form` -3..+3) applied as an integer nudge to the quantized match skills.
  Decays toward 0. Deterministic.
- FILES: new `Career/FormModel.cs`; applied where match skills are quantized.
- VERIFY: headless — a winning streak nudges form up; Squad screen Form column
  populated. `--menu-shot` read.

**STEP 18 — Balancing pass + season-summary screen + docs.**
- Tune all curves (growth rate, regen gem frequency, retirement ages, finance
  numbers, fatigue penalty) from a 20-season headless soak so the world stays
  believable (no runaway inflation, squad ages stable, gems appear
  occasionally). Add a SEASON SUMMARY screen (retirements, promotions,
  new signings, finances). Update `RESUME-HERE.md` + a `docs/design` writeup.
- FILES: tuning constants (their own `Career/CareerTuning.cs` so they're easy to
  find); new `Menu/Screens/SeasonSummaryScreen.cs`; docs.
- VERIFY (VISUAL + DATA): 20-season soak log reviewed; summary screen
  `--menu-shot` read; full career playthrough smoke.

---

## Optional / stretch (NOT in scope B unless you say so)
- **Modern-squad import from API** (your #1 comment "pull current players from an
  API"): a separate importer that materializes real modern squads into the team
  list. Depends on STEP 00's go/no-go and a (likely paid) API. Kept OUT of the
  core career build; revisit after Phase 9.

## File-ownership summary (so Codex never collides with me)
- **Codex writes**: everything under new `game/scripts/Competition/Career/*.cs`
  and new `game/scripts/Menu/Screens/*.cs`, plus `MenuTheme.cs`/`MenuLayout.cs`.
- **I (orchestrator) write**: `game/scripts/Main.cs`, `MenuClient.cs`, and any
  `Sim/Port/` match-tick edits (netplay-critical, single-editor). I also add the
  `--career-model-test` / harness routes and read all screenshots.
- Each Codex step above lists disjoint files; never send two steps that touch the
  same file in parallel.

## Build order recap (matches your PROPOSED order)
Foundation(1) → UI framework+screen(2) → age/retire(3) → potential/growth(4) →
regen/pool(5) → scouting(6) → coaches/finances(7) → fatigue/stamina(8) →
form/polish(9).

<!-- end of plan -->
