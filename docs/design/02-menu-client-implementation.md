# OpenSWOS Menu Client — Implementation Notes

**Status:** BUILT & verified 2026-07-10 (Session 29). Replaces the old plain-text
menu with a SWOS-styled, stack-navigated front-end that launches real matches.
This is the "our menu instead of theirs" client the user asked for.

## What it is

A from-scratch SWOS-look menu system: gradient buttons with beveled frames, a
flashing selection cursor, a title bar and a control-hint footer — rendered with
the real SWOS charset (`SwosFont`) at 1:1 pixels, at the game's 384×272 viewport.
Colours are **SWOS-derived but deliberately shifted** ("similar but different")
so OpenSWOS has its own identity.

Screens implemented:
- **HOME** — PLAY MATCH · COMPETITIONS* · MULTIPLAYER* · TEAM SETUP · OPTIONS · EXIT
- **FRIENDLY MATCH** — HOME/AWAY team changers (held ‹ › fast-scrolls the 1730-team
  list), SWAP TEAMS, PITCH, OPPONENT, HALF LENGTH, GAME SPEED, KICK OFF
- **VERSUS** — big `HOME  V  AWAY` pre-match screen → PLAY launches the match
- **TEAM SETUP** — hand-crafted squad table (number, name, position, the 7 skills)
  + home/away/keeper kit swatches + team switcher
- **OPTIONS** — length / speed / opponent / pitch (more planned)
- **COMING SOON** stubs for Competitions & Multiplayer (see design doc 01)

`*` = stub screen pointing at `docs/design/01-menu-and-game-modes.md`.

## Files (all new, self-contained)

```
game/scripts/Menu/
  MenuTheme.cs    — palette, 7 radial gradients, button/cursor texture baking
  MenuModels.cs   — MenuEntry (Button/Option/Label), IMenuHost interface
  MenuClient.cs   — screen stack, layout, render, input, all screen builders
game/scripts/
  Main.MenuHost.cs — Main's IMenuHost bridge (match setup + team data), a
                     partial so Main.cs stays lean
```

Edits to `Main.cs` were kept minimal: one field, client creation in `_Ready`,
route the `AppState.Menu` branch of `_PhysicsProcess` to `_menuClient.Tick()`,
hide the old label in `UpdateUi`, and a `_Process` + `--menu-shot` harness.

## The SWOS look (MenuTheme.cs)

Buttons are **radial gradients**: darkest at the top-left corner, brightest
toward the bottom-right. Algorithm paraphrased from swos-port
`menuItemRenderer.cpp:18-40`:
```
d = w² + h² ;  s = (x²+y²)/d * 32 ;  colour = lerp(stops[⌊s⌋], stops[⌈s⌉], frac s)
```
Each `Style` (PlayPrimary, PlaySecondary, Tool, Header, Info, Accent, Danger,
Value, Plain) supplies two gradient endpoints (+ optional mid), an outer frame
colour, an optional inner frame, and a text colour. Backgrounds are baked once
into `ImageTexture`s and cached by `(w,h,style)`.

The selection cursor is a 2 px hollow frame whose colour pulses white↔gold
(our take on the original `drawMenu.cpp:201` shine table).

Licence note: the **algorithm** is paraphrased; all colour constants are ours.
Palette lineage cited in comments (`color.cpp`, `menuItemRenderer.cpp`).

## How navigation works

- One screen = a vertical list of `MenuEntry` (centred column under the title).
- UP/DOWN move the cursor (skips non-selectable Labels).
- LEFT/RIGHT step an **Option**'s value; team rows set `FastScroll=true` for the
  held-repeat accelerator (same cadence as the old menu, ~5/tick when held long).
- FIRE activates a **Button** (or nudges an Option forward).
- ESC/BACK pops the screen stack; the covered screen's nodes are destroyed on
  push and rebuilt on pop, so only one screen's sprites exist at a time (fixes an
  early "ghost bleed-through" bug).
- On (re)appearing — boot, or return from a match — the client snaps back to HOME.

`Main` never leaks into the menu: the client talks to the game only through
`IMenuHost` (team list, setup steppers, `StartMatch()`, `QuitGame()`, team data
for the squad screen). `StartMatch()` runs the exact same
`NewMatch → EnterPreKickoff → AppState.Match` path the old menu used, so the
faithful SwosVm sim is untouched.

## Verifying it (self-test harness)

`--menu-shot <dir>` boots the game **windowed** (needs a display, not
`--headless`), deterministically drives the menu via the client's `Debug*` hooks,
and saves a PNG of each screen — then launches a match and screenshots that too —
before quitting. Used to visually verify the look/nav without a human:

```
.tools/godot/Godot_v4.6.2-stable_mono_win64_console.exe \
    --path game -- --menu-shot C:/path/to/out
```
Produces `01_home … 07_match`. Confirmed 2026-07-10: all screens render, the
cursor is visible, and the full HOME → PLAY → VERSUS → PLAY → live match flow
works (e.g. "WEST BROMWICH 0-0 CHELSEA" kicked off from the menu). Headless
`--swos-smoke` still passes (sim unaffected).

## Competitions, career & local multiplayer (Session 29b — no stubs left)

The COMING SOON stubs were replaced the same night with fully working systems:

- **Engine** (`game/scripts/Competition/`): `CompetitionModels.cs` (serializable
  contract), `CompetitionEngine.cs` (Berger league scheduler incl. odd-count
  byes; single-elimination cup 4/8/16/32 with lazily-drawn next rounds and
  simulated penalty shootouts on level ties; tournament = groups → knockout with
  cross-pairing; CAREER = double-RR league + domestic cup interleaved per
  season, promotion/relegation on rollover, trophies + season history;
  deterministic xorshift32 everywhere; AI results from strength-weighted
  Poisson), `CompetitionStore.cs` (one active save at `user://competition.json`).
- **Menu**: COMPETITIONS hub → NEW LEAGUE / NEW CUP / NEW TOURNAMENT /
  NEW CAREER setups → DASHBOARD (round label + "YOU ARE nTH", mini-table with
  the player's row in gold, knockout fixture lists, PLAY NEXT MATCH / CONTINUE /
  NEXT SEASON / TABLE / FIXTURES / ABANDON) + full TABLE and FIXTURES screens.
  HOME gains a green CONTINUE banner when a save exists. MULTIPLAYER is a real
  LOCAL MULTIPLAYER screen (P2 = WASD).
- **Result loop**: `IMenuHost.StartCompetitionMatch` plays the fixture in the
  real sim (human on the home slot); the FullTime→Menu accept hands
  `(player, opponent)` goals back via `TakeLastCompetitionResult`; the menu maps
  them onto the fixture, simulates the round's AI games, saves, and rebuilds the
  dashboard. ESC-abandon keeps the fixture unplayed.
- **Verified**: `--competition-test` (headless engine test, 18 checks) and the
  extended `--menu-shot` walk — league (56 fixtures) + career (248 fixtures)
  created, a career match played to FULL TIME by the real sim, result recorded,
  ROUND 1/30 → 2/30, standings updated. Full sim regression battery still green.

## Known limitations / next steps

- Faithful-fidelity follow-ups from the source analysis: port the original
  AI-result probability tables (CalculateViewResult, swos.asm:32548 — strength
  diff + 2 home bonus), 2-legged ties + away goals + replays, preset
  competitions (World Cup / Euro / European club cups), career job offers &
  manager status, 2/3-points-per-win option.
- Online multiplayer (netcode) — design in `docs/decisions/01-netcode-model.md`.
- TEAM SETUP is currently **read-only** (view squad + kits). Editing (swap
  starters, cycle tactics, edit kits) needs sim-side plumbing — planned.
- OPTIONS exposes only the settings the sim actually supports today; the modern
  RULES panel (5 subs, extra time, VAR…) waits on those rules existing in the sim.
- The legacy plain-text menu (`MenuOverlayText`/`TickMenu`/`_menuLabel`) is dead
  but left as a compile-time fallback; safe to delete later.
