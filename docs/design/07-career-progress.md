# Career mode — implementation progress

## UPDATE 2026-07-16 (evening) — S32 feedback wave COMPLETE (tasks #211-#225)

All user play-test feedback implemented and verified (each step: build 0/0,
--competition-test PASSED, windowed menu-shot eyeballed, .backups/*.7z):

- **Career squads reach the MATCH** (#211): `Career/CareerMatchTeam.cs` synthesizes
  a TeamRecord from the live CareerWorld (slot0 keeper, XI, bench, cloned kits),
  passed as overrides through `IMenuHost.StartCompetitionMatch` →
  `Main.EffectiveTeam()`. Bought players play, sold ones are gone; stadium
  lineups show the live squad too.
- **Lineup editor** (#212+#222): SWOS-style — screen opens directly on the 16
  rows (inline table mode), FIRE marks A (purple), FIRE on B swaps + saves,
  keeper-only slot 0 guard; AUTO restores. DEFAULT order = ORIGINAL TEAM.*
  LineupOrder (real club XI), career additions appended. PreferredLineup
  persists in the save.
- **Faithful transfers** (#213): `Career/TransferOffers.cs` — transfer list
  (max 5), offers arrive PER PLAYED MATCH (11/256 listed, 1/256 unlisted,
  swos.asm constants), amount 85-119% of value, up to 2 escalations
  (105-124%), expiry 1-6 matches, OFFERS screen (accept/demand more/reject),
  "!"-prefix while unseen, TimeToNegotiate=6/season + buy/sell quotas, free
  transfer, bid-based buying (midpoint counter). Deterministic, asserted in
  the harness.
- **Market/scout freeze fixed** (#214): per-screen caches + decorate-sort;
  VIEW RESULT (#215): instant simulated result choice on the versus screen,
  full post-match pipeline reused; harness uses it (menu-shot ~8s).
- **List pickers everywhere** (#217): pushed picker (scrolling window,
  fast-scroll) for nation/team/SORT; INLINE table-select for PLAYER/OFFER/
  CANDIDATE fields (fire on field OR down past last entry → selection moves
  into the visible table, gold field accent). Picker body redraw fix: the
  picker/table branches must LayoutAndBuild (Refresh never re-runs s.Body).
- **Regen names** (#218): first+surname pools harvested separately from real
  players; youths always "FIRST SURNAME", (pair+surname) uniqueness across
  17438 generated — 0 dups, 0 single-token (asserted).
- **Market UX** (#219): full list (was top-200 cut), MAX PRICE filter, NAT
  column, EFF→SKILL, narrower buttons; **SKILL = SUM 0-49** (#224) not the
  flattening 0-7 average (keepers: EffectiveOverall*7).
- **Row visuals** (#220/#223/#224): 7x7 head busts cropped from the CJCTEAM
  atlas, recoloured with the CLUB's HomeKit via KitPalette (cache per
  face+kit; grey only for free agents); REAL flag PNGs (153 nations,
  game/data/flags, flagcdn PD + Wikimedia YUG/ZAI, fetch script
  tools/flags/fetch_flags.py), Lanczos-resized into a UNIFORM 12x8 box (all
  flags same size; square ones like SUI get a 6x6 centred field with thicker
  border), 3-letter code overlaid on the flag's lower part (x1 charset with
  black outline on a dedicated scale-1 fine-print CanvasLayer;
  FinePrintTextCentered), top-3 skill letters column (P V H T C S F).
- **Player nationality decode FIXED** (#221): the player byte uses SWOS's own
  153-entry numbering (18=ITA, 70=BRA) — new `Assets/PlayerNationNames.cs`
  (Code/FullName/Continent, cites teams.txt:109-161 + swos.asm:186797);
  NationNames stays for TEAM.*-suffix indices. KnownAges hint + regen
  continents switched; ages match rate 11916→12037.
- **Menu colour pass** (#225): Header royal-blue/white/gold frame, Value/Field
  blues, Plain→amber (BACK/EXIT), no grey-on-grey anywhere; title text raised
  6px; POSSKL header split. Real-SWOS reference language documented in
  MenuTheme.cs.

### Last-minute fixes (2026-07-16 late, all verified)
- Flags: UNIFORM 10x6 field for every nation (BEL's odd 13:15 ratio no longer
  narrows it); square flags (SUI, ratio<=1.05) keep a 6x6 centred field;
  frame REMOVED (transparent padding → natural gaps; user preferred it).
- SCOUT PLAYER market: PRICE column added (AskingPrice, right-aligned);
  rows-per-page corrected (headers use 25px not 15 — last row no longer sits
  on the panel border).
- Nation code on flag: +4px down, centred (w/2 truncation fixed); screen title
  raised 6px.

### Open items
- **#216 (ONLY remaining)**: career dashboard + league view redesign, 2-column
  modern-manager layout — user explicitly wants the orchestrator to design a
  WEB MOCKUP first, then reproduce in the 576x408 menu.
- User reported an unexplained one-off FREEZE ("zawiesił się") while testing
  — no repro steps yet; ask for details if it recurs.
- AI clubs still never buy from the market (money piles up long-term); fatigue
  distance surrogate + in-match toggle still awaiting the user play-test
  session; nationality byte for a few players shows LIB/CUS oddities worth a
  spot-check someday.

## UPDATE 2026-07-16 — UI + transfers + hooks COMPLETE (plan executed)

Everything below the old status is DONE and superseded. Shipped since:
- **World wiring**: `MenuClient.EnsureCareerWorld` — the world materializes on
  real career creation from the menu (and lazily for legacy saves/slots). Proven
  in the real menu path: `[career] world materialized: 1730 clubs, 27680 players`.
- **SQUAD screen** (dashboard → SQUAD): NO/NAME/POS/AGE/EFF/POT/STA/F/FIT/VAL,
  integer-scale charset (fractional scaling of the bitmap font is FORBIDDEN —
  caused a garbled-text regression, fixed), compact money format, full 16-char
  names, paging, focus `*` marker, sell action per player.
- **TRANSFER MARKET** (dashboard → TRANSFERS): browse/sort (VALUE/EFF/AGE),
  buy with budget + refusal reasons, sell to free agency; club names resolved
  via a GlobalId→name map from the full master list; NATIONAL teams excluded
  (real discriminator: TEAM.080–085 are national squads, per CheckIsTeamNational)
  so stars no longer appear twice (club + country).
- **STAFF** (hire/fire coaches, 3 deterministic candidates, wages/fees, cap 3;
  TRAINING FOCUS pick ≤3 players) and **SCOUTING** (watchlist, paid scout of any
  market player, IMPROVE SCOUTING quality upgrade).
- **Per-match hooks**: `Career/MatchEffects.cs` called from
  `MenuClient.PollCompetitionResult` — a REAL played fixture applies form,
  growth (with coach bonus) and fatigue to both clubs' XIs + weekly recovery.
  Fatigue uses a flat distance surrogate (TODO: real distance metric when the
  in-match toggle is wired in the play-test session).
- **Keeper ability chain fixed end-to-end**: ValueCode copied into the world,
  `EffectiveOverall()` derives keeper EFF the SWOS way ((value+3)/7), Finance
  values keepers on real ability (a 0-rated backup keeper was priced 150M —
  fixed), keepers develop via ValueCode growth (asserted: EFF 1→6 young keeper).
- **Regen quality**: nationality-correct surnames harvested from the real 27680
  loaded players (Yugoslav youths are DODIC/JOVIC/RAICKOVIC..., not SMITH);
  position-shaped intake (≈1 GK per squad), squads hold 16–18.
- **20-season soak** (career-report): budgets 38M→106M (no runaway), ages 18–34,
  one keeper per squad, scouting ranges everywhere, PASSED test suite (54 checks).

### Known balance items (not blockers, revisit later)
- AI clubs never BUY from the market → money piles up and world quality drifts
  toward the regen average once the original stars retire (top clubs converge to
  EFF 2–3 after ~15 seasons). Needs an AI transfer pass + regen quality scaled by
  club stature.
- Fatigue distance surrogate + in-match toggle still pending the user play-test.
- FORM stays near 0 outside played fixtures (only real matches move it) — fine,
  but AI-vs-AI league games don't nudge form (SimulateAiRound path).

# (superseded) night run, 2026-07-15

Executed via Codex (gpt-5.6-terra, high) with headless verification by the
orchestrator. Every step: build 0/0 + `--competition-test` assertions. Full
suite: **51 OK checks, 0 fail, COMPETITION ENGINE TEST PASSED**. Determinism +
JSON save/load preserved throughout. Codex weekly usage after the run: ~85%.

## DONE (all pure-logic, headless-verified)
| Step | What | Verified |
|------|------|----------|
| Phase 1 | `CareerWorld/CareerClub/CareerPlayer` model + `CareerWorldBuilder` materializes the WHOLE world | 1730 clubs / 27680 players + save/load round-trip |
| A | Age / Stamina / Potential assignment (`AgeModel`, `StaminaModel`, `PotentialModel`) | ages 17–36, stamina 0–7, youth pot ≥ current, gems ~6% |
| 07 | Per-season ageing + decline retirement (`SeasonProgression`) | sample 19→27 over 8 seasons; retirements happen |
| 10 | Youth regen + free-agent pool (`RegenModel`) | every club ≥11, pool capped 300, world stays populated |
| 09 | Position-weighted growth toward potential, doubles (`GrowthModel`) | young rises, veteran 5.00→4.14 declines |
| 13 | Finances: budgets + wages (`Finance`) | budgets bounded, richer clubs richer |
| 14 | Coaches (staff economy) + targeted training (`StaffModel`, `CareerClub.TrainingFocusIds`) | coached+focused youth 4.51 vs 3.63 uncoached |
| 11-12 | Scouting network + FUZZY potential reveal (`Scouting`) | q7 range 0.70 vs q1 3.88; repeat tightens; never certain |
| 17 | Simple form (`FormModel`) | win-streak +3/+1 skill, loss −3/−1, idle decays |
| 15-16 | Fatigue MODEL (`FatigueModel`, int in-match, double recovery) | low-stamina tires more, penalty 0/−1/−2, young recover faster |

All new code lives in `game/scripts/Competition/Career/*` (Codex-owned).
Season rollover order in `CompetitionEngine.AdvanceCareerSeason`:
age→retire → regen → staff AI → growth → finances → scouting.
The `--competition-test` harness (in `Main.cs`) exercises + asserts every model.

## NOT DONE — needs the user / non-Codex work (the stopping boundary)
1. **In-match fatigue wiring + FEEL test (manual, user).** The `FatigueModel`
   int drain/penalty must be wired into the 70 Hz match tick in `Sim/Port/`
   (netplay-critical, single-editor = orchestrator, NOT Codex) behind the OPTIONS
   toggle. Then the USER must play a career match and judge whether tiredness
   FEELS right (only a human can judge feel). This is the manual test the user
   flagged.
2. **Per-match growth + form hooks for the player's club.** `ApplyMatchGrowth`
   and `FormModel.UpdateFormAfterMatch` need calling from the real full-time hook
   in `Main.cs` (AI clubs already develop at season level). Mechanical wiring.
3. **Real menu wiring.** `CareerWorldBuilder.BuildWorld` + progression currently
   run only in the test harness. Wire `BuildWorld` into the actual career-create
   menu path (+ progress bar), and ensure the menu's season rollover calls
   `AdvanceCareerSeason` (it may already). Orchestrator work in `Main.cs`/menu.
4. **Phase 2 UI (deferred for usage).** Hi-res 640×480 menu layer + font x2 +
   Squad/Development / Scouting / Staff screens surfacing age/pot-estimate/form/
   fatigue. Needs the screenshot review loop → do when Claude usage affords it.
5. **Balancing soak (Step 18).** 20-season headless soak to tune rates:
   growth-per-match scale, regen intake vs squad cap (squads drift to cap 22 —
   mild inflation), `MatchFatigueGain` scale (returns ~7000 for a full match →
   must be calibrated to the real integer distance metric before wiring), coach
   bonus, retirement ages, finance numbers.
6. **Real-age table (Step 00 research).** `KnownAges1997` from openfootball +
   Wikidata (see `06-player-data-sources.md`) to enrich derived ages.

## Backups this run
`.backups/20260714_200403_pre_career_impl_step01.7z`,
`20260715_015220_phase1_done_pre_attributes.7z`,
`20260715_023638_career_logic_complete_9models.7z`.

## Codex step prompts
`_research/codex_steps/STEP_*.md` (reusable / re-sendable).
