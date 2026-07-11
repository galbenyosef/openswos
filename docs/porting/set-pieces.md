# Porting #4 — Set pieces (throw-in, corner, goal kick)

**Status: minimum-viable scaffolding wired (2026-05-14).** Boundary crossings now
trigger a real set-piece phase with the correct team awarded the kick. Auto-release
after a fixed timer — there is no player-walks-to-ball state machine yet.

Goal of this session: kill the unrealistic ball-bouncing-off-touchline behaviour.
The previous `TickPlay` block in `game/scripts/Main.cs` reflected `_ball.VelocityX`
on a touchline crossing and `_ball.VelocityY` on a back-line crossing. Replaced
with throw-in / corner / goal-kick detection, plus phase-aware ball positioning
and a stubbed auto-release.

## SWOS reference

| SWOS GameState                 | Value | Source                                           | Our MatchPhase |
|--------------------------------|------:|--------------------------------------------------|----------------|
| `kKeeperHoldsTheBall`          | 3     | `external/swos-port/src/swos/swos.h:571`         | `GoalKick` *   |
| `kCornerLeft` / `kCornerRight` | 4, 5  | `external/swos-port/src/swos/swos.h:572-573`     | `CornerKick`   |
| `kThrowInForwardRight`         | 15    | `external/swos-port/src/swos/swos.h:576`         | `ThrowIn`      |
| `kThrowInCenterRight`          | 16    | `external/swos-port/src/swos/swos.h:577`         | `ThrowIn`      |
| `kThrowInBackRight`            | 17    | `external/swos-port/src/swos/swos.h:578`         | `ThrowIn`      |
| `kThrowInForwardLeft`          | 18    | `external/swos-port/src/swos/swos.h:579`         | `ThrowIn`      |
| `kThrowInCenterLeft`           | 19    | `external/swos-port/src/swos/swos.h:580`         | `ThrowIn`      |
| `kThrowInBackLeft`             | 20    | `external/swos-port/src/swos/swos.h:581`         | `ThrowIn`      |
| `kInProgress`                  | 100   | `external/swos-port/src/swos/swos.h:592`         | `Play`         |
| `kStopped`                     | 101   | `external/swos-port/src/swos/swos.h:593`         | (any non-Play) |

\* SWOS has **no explicit goal-kick state** — it uses `kKeeperHoldsTheBall` and
walks the keeper out to punt the ball. We don't have a keeper-state machine yet,
so we stand in a separate `GoalKick` phase that plants the ball ~30 px in front of
the goal mouth and auto-releases upfield. To be merged into a real keeper-holds
state once `docs/porting/goalkeeper.md` lands.

The throw-in variants in SWOS (Forward/Center/Back × Left/Right) only differ in
which animation plays — the resulting gameplay is identical. We collapse to a
single `ThrowIn` phase and rely on the actual crossing Y for ball position.

## Detection logic (vs SWOS)

SWOS detection lives in `external/swos-port/src/game/ball/ball.cpp` around line
3460..3998. The flow we paraphrase:

1. **Back-line check** (`l_check_for_corner_goal_out:` at line 3522). Compare
   `D2` (ball Y, 16-bit word top half) against the upper back-line threshold
   `129` (cmp at line 3534) or the lower back-line threshold `769` (cmp at line
   3576). If neither, fall through to `l_ball_in_pitch:` at line 3795 — which is
   the throw-in branch.
2. **Corner vs goal-kick split.** SWOS compares `A6` (the team in possession
   pointer) against `lastTeamPlayed` (`g_memByte[523092]`, line 3538). Same
   team → goal kick (`l_upper_goal_out`/`l_lower_goal_out`, lines 3686, 3717);
   different team → corner (`l_lower_left_corner`/`l_left_upper_corner` etc.,
   lines 3627, 3658, 3649, 3686).
3. **Corner side** (left vs right) is just `bx < pitch_centre_X` (cmp word ptr
   D1, 336 at line 3554).
4. **Throw-in variant** is picked by which third of the pitch the ball is in:
   `cmp word ptr D2, 342` then `cmp word ptr D2, 556` (lines 3830, 3844 etc.) —
   the three Y-bands map to Forward / Center / Back. We ignore this and just
   pin the ball at the touchline Y where it actually crossed.

Our port (`game/scripts/Main.cs` step 5 of `TickPlay`) is the same shape:

```
if (bx out)                           → ThrowIn (taking = !lastTouched)
if (by_top out AND outside goal X)
   lastTouched == attacker            → GoalKick (taking = defender)
   else                                → CornerKick (taking = attacker, side=bx<centre)
if (by_bot out AND outside goal X)    → mirror, sides-swap-aware
```

## Mapping table

| Concept                                  | Our value                          | SWOS source                              |
|------------------------------------------|------------------------------------|------------------------------------------|
| Field bounds (touchlines)                | `FieldLeft=-130, FieldRight=482`   | (our coords; SWOS pitch X 1..672)        |
| Field bounds (back-lines)                | `GoalLineNorth=-160, GoalLineSouth=472` | (our coords; SWOS Y 129..769)         |
| Goal-mouth half-width                    | `GoalMouthHalfWidth=48`            | (our coords; SWOS uses pitch-centre ± goal half-width) |
| Pitch centre X                           | `KickoffBallX=176`                 | SWOS uses `336` for the same split, ball.cpp:3554 |
| `LastTouchedByPlayer`                    | `MatchState` field                 | `lastTeamPlayed` (`g_memByte[523092]`), ball.cpp:3796 |
| Throw-in hold timer                      | `ThrowInHoldTicks=100` (2.0 s)     | not from SWOS — SWOS waits on player input |
| Corner hold timer                        | `CornerHoldTicks=150` (3.0 s)      | not from SWOS                            |
| Goal-kick hold timer                     | `GoalKickHoldTicks=100` (2.0 s)    | not from SWOS                            |
| Throw-in auto-release power              | `1500` raw Q8.8 (~5.9 px/tick)     | not from SWOS — picked weaker than kick  |
| Corner auto-release power                | `BallSim.HighKickPower=2688` raw   | `swos.asm:203956 kHighKickBallSpeed`     |
| Goal-kick auto-release power             | `BallSim.NormalKickPower+200=2760` | scaled from `swos.asm:203961 kNormalKickBallSpeed` |
| Auto-release loft                        | `BallSim.KickLoft=320` (corner) / `KickLoft/2` (throw) | `swos.asm:203950 kBallKickingDeltaZ` |

Throw-in / corner / goal-kick power values are **hand-tuned within the SWOS power
band**, not extracted from a specific table — there's no equivalent in SWOS because
the human player sets power via the kick button. Update once we have power
modulation.

## What's authentic vs what's stub

**Authentic** (matches SWOS):
- SWOS GameState names mapped onto MatchPhase. Enum values cited per source.
- `lastTouchedByPlayer` flips correctly per `lastTeamPlayed` semantics: outfielder
  kicks update it; **keepers do not** (ball.cpp logic flips
  `lastTeamPlayed` only inside outfielder code paths, and bench.cpp:134 confirms
  the keeper's role around throw-ins is special-cased).
- Touchline crossing → throw-in, defender takes.
- Back-line crossing (outside goal mouth) → corner for the attacker if the
  defender last touched, goal kick for the defender if the attacker last touched
  — exactly as SWOS dispatches in ball.cpp:3522..3793.
- Sides swap at half time (corner is awarded based on who attacks which goal in
  the current half).
- Ball position pinned at the touchline (X = `FieldLeft`/`FieldRight`, Y =
  crossing Y) or at the corner flag (X = touchline edge, Y = back-line edge),
  matching SWOS's set-piece spotting in the `l_*` blocks of ball.cpp.

**Stub** (deferred):
- **No player-walks-to-ball state machine.** Real SWOS picks the nearest player
  on the awarded team, runs them to the ball, switches their `PlayerState` to
  `kThrowIn` (see external/swos-port/src/game/bench/bench.cpp:134 for the existence
  of that state), and waits for the human to press kick. We auto-release on a
  fixed timer and don't change any `PlayerState` — outfielders just idle in place
  during the freeze.
- **No throw-in animation.** SWOS has a dedicated throw-in sprite ordinal
  (`PlayerState::kThrowIn` in the player state enum) — we don't have it loaded.
  The PC SPRITE.DAT ordinals are sitting in `tools/sprite-anchors-extract`'s
  output but the throw-in pose isn't tagged yet.
- **No keeper-holds-the-ball state.** SWOS's actual goal-kick logic runs through
  the keeper picking up the ball and punting/throwing out. We bypass the keeper
  entirely and just plant the ball ~30 px in front of the goal mouth. This is
  visually wrong but mechanically fine for a single-player test rally.
- **No throw-in Y-band animation variants.** SWOS picks one of 6 throw-in
  animations based on `(left/right touchline) × (forward/center/back Y-band)` —
  we use a single phase and ignore the variant.
- **No foul, no penalty, no off-side, no advantage.** `kFoul=13` and `kPenalty=14`
  in `swos.h:574-575` remain unimplemented.
- **Set-piece auto-release uses non-deterministic float `Math.Sqrt`** for vector
  normalisation in `ComputeKickVector`. Fine for the stub release (single-player
  only), but **must be replaced with the deterministic `IntSqrt` from BallSim
  before any netcode work** — see CLAUDE.md determinism constraint.

## Files touched

- `game/scripts/Sim/MatchState.cs` — add `ThrowIn` / `CornerKick` / `GoalKick`
  enum values, `LastTouchedByPlayer`, `TakingTeamIsPlayer`, set-piece-velocity
  fields, and the three `MatchTimings.*HoldTicks` constants.
- `game/scripts/Main.cs` — replace boundary bounce in `TickPlay` with set-piece
  detection; add `EnterThrowIn` / `EnterCornerKick` / `EnterGoalKick`,
  `TickSetPieceHold`, `ReleaseSetPiece`, `ComputeKickVector`; extend the
  `MatchPhase` switch in `_PhysicsProcess`; extend `OverlayText`.

## Port checklist

- [x] Read SWOS GameState enum (`swos.h:568-595`)
- [x] Trace boundary detection (`ball.cpp:3460..3998`)
- [x] Cite source for last-touched flip semantics
- [x] Extend `MatchPhase` + `MatchState` fields
- [x] Replace bounce with phase dispatch in `TickPlay`
- [x] Auto-release with timer + empty-influence BallSim tick
- [x] `OverlayText` displays "THROW-IN" / "CORNER" / "GOAL KICK"
- [x] `dotnet build` clean
- [x] `--headless --quit-after 3` boots clean
- [ ] Replace `Math.Sqrt` in `ComputeKickVector` with `BallSim.IntSqrt`
      equivalent (determinism)
- [ ] Player-walks-to-ball state machine: pick nearest player, walk, animate, kick on input
- [ ] Wire `PlayerState::kThrowIn` (and matching sprite ordinal) into our `PlayerState` / `PlayerFrames`
- [ ] Merge `GoalKick` into a real `KeeperHoldsTheBall` phase once the goalkeeper port lands
- [ ] Throw-in animation variants (forward/center/back) — needs the throw-in sprite ordinal first
- [ ] Foul (`kFoul`), penalty (`kPenalty`) — separate epic
- [ ] Cross-check set-piece auto-release power values against a play test of `swos-port` (manual)
