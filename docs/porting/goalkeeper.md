# Porting #6 — Goalkeeper

**Status: partial (2026-05-14).** Catch + auto-hold + auto-punt wired. Dive / catch
animation / per-team kit / shot anticipation still TODO.

## What's ported

- **Per-keeper hold counter** in `Sim/MatchState.cs`:
  `HomeKeeperHoldTicks`, `AwayKeeperHoldTicks` (byte each, max 255 ≈ 5 s).
- **Hold duration constant**: `MatchTimings.KeeperHoldDurationTicks = 100` (2 s @ 50 Hz).
- **HandleKeeperHold** in `Main.cs`:
   - Increments while ball within `BallSim.InteractionRadius` (14 px) AND on the ground (`Z<8`).
   - After threshold, applies SWOS-style punt:
     - `VelocityX = spread × NormalKickPower / 16` (deterministic ±3 px/tick spread from `MatchTick`)
     - `VelocityY = ±NormalKickPower` (N keeper kicks south, S keeper kicks north)
     - `VelocityZ = HeaderJumpZ` (high lob to clear defenders)
   - Resets counter; ball flies out of possession on next tick.

## SWOS reference (cited in code)

- `external/swos-port/src/swos/swos.h:571` — `kKeeperHoldsTheBall = 3` game state.
- `external/swos-port/src/game/ball/ball.cpp:330-372` — keeper-controlled ball flow,
  Sprite.z handling, deltaZ resets when team that substitutes is the same as
  lastTeamPlayedBeforeBreak (corner-case stub).

## What's stub

- **No keeper state-machine animation** — SWOS keeper has `kGoalieCatchingBall`,
  `kGoalieDivingHigh/Low`, `kGoalieClaimed` from `Sprite.h:21-29`. We just play the
  standing sprite while holding.
- **No dive** — keeper doesn't attempt to intercept a fast shot before it crosses
  the goal line; relies on the proximity glue from `BallSim` dribble branch.
- **No throw-out** — keeper always punts long. Real SWOS sometimes throws short
  to a teammate (depends on `kThrowOut` decision tree).
- **Hard-coded yellow kit** (KeeperKit `{0,6,6,0,6}`) — SWOS has a per-team
  goalkeeper kit field in TEAM.\* records that we haven't surfaced yet.
- **Excludes keeper kicks from `LastTouchedByPlayer`** — by design, so set-piece
  awards stay correct (clearance ≠ corner gift). SWOS does the same.

## TODO

- Per-team goalkeeper kit (TeamRecord field — re-parse the unknown bytes around
  +0x32 in the TEAM.\* layout to find which holds goalkeeper colours).
- Dive state: when ball.VelocityY toward own goal AND ball.Z low AND distance < N → keeper.SlideTicks=12, slide toward ball.
- Throw-out vs punt decision (low-effort: punt always; medium: throw if
  nearest teammate within 80 px and unmarked).
- Catch animation — needs goalkeeper sprite descriptors (already extracted to
  99 entries in `memory/reference_swos_descriptor_streams.md`, just need mapping).
