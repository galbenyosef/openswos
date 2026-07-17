# Career mode — design BRIEF (input for the plan)

**Status: BRIEF only. The plan is produced AFTER this.** This file captures the
user's vision so it survives chat compaction. Do NOT start implementing from
this; it is the raw request + workflow.

## Workflow the user asked for
1. First a **fairly GENERAL plan** that, per feature, **proposes several possible
   solutions/approaches** (options A/B/C with trade-offs), not one fixed design.
2. The **user then decides** what goes in and what stays out.
3. THEN a **detailed plan** for the chosen subset.
Keep scope sane: **a hybrid between a modern football-manager career (FIFA-style)
and the original SWOS career. NOT super-bloat.** Small, tasteful additions on top
of SWOS, each optional, preserving the original feel (see optional-fixes philosophy
in memory / [[optional-fixes-philosophy]]).

## The vision
A career mode that is a **modification of the original SWOS career** (which we
already have a base of) blended with modern-manager depth. Modernise carefully;
do not bury SWOS's simplicity under menus.

## Feature ideas the user floated (raw list, to be turned into options)
- **Player age** (each player has an age).
- **Player development / growth** (players can improve over time).
- **Hidden potential stat** (a ceiling the player can grow toward, not shown or
  only hinted).
- **Coaches**: hire coaches and **focus training on promising players** (invest
  training in specific young/high-potential players).
- **In-match fatigue** that **slightly lowers stats** during a match (ties to the
  stamina idea already in memory: stamina scaled by overall skill, drains with
  match time + distance covered with and without the ball).
- **Age advances each season**; players can **retire** (age-driven decline +
  retirement).
- **New-player generation** (youth/regen intake each season).
- **Scouting young talents** (find promising youngsters, hidden potential).
- ...and similar manager-lite systems.

## Existing base to modify (grounding for the plan)
- Current career engine: `game/scripts/Competition/CompetitionEngine.cs` +
  `CompetitionModels.cs` (CareerState) + `CompetitionStore.cs` (save slots).
  Already has: multi-season double round-robin league + domestic cup,
  promotion/relegation, manager name/title, trophies, MANAGEMENT RECORD, RETIRE
  (manager), season rollover, AI results strength-weighted, deterministic
  xorshift32 RNG in state. UI in `game/scripts/Menu/MenuClient.cs` (career
  dashboard) + `Main.MenuHost.cs`.
- Team/player data: `TeamRecord`/`PlayerRecord` (Assets), skills 0..7, position,
  value/price, face; PC 1730 / Amiga 1616 teams. Players currently have NO age /
  potential / growth fields — those must be added to the career-save layer (not
  the read-only TEAM.* files).
- Related open items: #199 (career transfers + job offers + club business) and
  #198 (class-C backlog incl. transfers/finances). The manager career is the
  natural home for those.
- Sim already deterministic (integer/fixed-point) — keep any new career maths
  deterministic and in the save state (xorshift32), so it stays netplay-safe and
  reproducible.

## Constraints / guard-rails for the plan
- Each new system should be OPTIONAL where reasonable (toggle), default tasteful.
- New per-player career attributes (age, potential, growth, fatigue-carry,
  form, morale, etc.) live in the CAREER SAVE, never in the read-only TEAM.* files.
- No "coming soon" stubs (CLAUDE.md rule) — only plan what we will actually build.
- Deterministic RNG for regen/scouting/development so saves are reproducible.

## After compaction: produce the GENERAL plan
Turn each feature above into 2-3 concrete design OPTIONS with trade-offs
(complexity, feel, data needed), plus a recommended default, and a rough
build-order. Let the user pick before any detailed spec.
