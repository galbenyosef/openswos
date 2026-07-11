# Porting #3 — Match flow timings

**Status: partial (2026-05-14).** Game-time display done. SWOS gameLength preset
table not adopted (we use our own 5 presets — see why below).

## What's ported

- **Game-time display** in `Main.UpdateUi`:
  - Real `MatchTick` mapped to in-game seconds via
    `gameSec = MatchTick × 45 × 60 / (SecondsPerHalf × TicksPerSecond)`
  - Shown as `MM:SS` clock counting **0:00 → 45:00 in first half, 45:00 → 90:00 in second**
  - Mirrors SWOS `m_secondsSwitchAccumulator` mechanism from
    `external/swos-port/src/game/gameTime.cpp:80-95`
- **Last-minute red flash** in last 60 game-seconds of a half (used to be last 10
  real seconds).
- **Menu match-length tag** now says e.g. `30 s real (45 game-min/half)`.

## SWOS reference

- `external/swos-port/src/game/gameTime.cpp:128-132`:
  `kGameLenSecondsTable = { 30, 18, 12, 9 }` — per-frame "time delta" for the
  accumulator. Higher value = more game-time per real-time tick = faster match.
- @ 70 PC FPS:
  - gameLen=0 (delta 30): ~3 min real per 90-game-min match
  - gameLen=1 (delta 18): ~5 min real
  - gameLen=2 (delta 12): ~7.5 min real
  - gameLen=3 (delta 9):  ~10 min real

## Why we kept our 5 presets

SWOS only has 4 (3 / 5 / 7.5 / 10 min real). Our presets are
`{30 s, 60 s, 2 min, 5 min, 10 min}` real per half. Reasons:
- 30 s/half is invaluable for testing — SWOS never had a sub-minute mode but we
  iterate way too fast to wait 3 min for a half.
- 10 min real = SWOS's longest match. The presets are a superset.

We could replace with SWOS-canonical presets later if it matters for fidelity.

## TODO

- Halftime / Fulltime length is from `swos.gameMinutesPerHalf` which is hard-coded
  to 45 in `external/swos-port/src/swos/swos.h` (constant). We assume 45 too.
- Extra time / penalties on 0-0 draw — `kFirstExtraStarting=27`, `kFirstExtraEnded=28`
  game states exist in `swos.h:588-589`. Not implemented.
- "Stoppage time" — SWOS has `stoppageTimerTotal` / `stoppageTimerActive` in
  `swos.asm`. Last minute is "stretched" before whistle. Not implemented.
