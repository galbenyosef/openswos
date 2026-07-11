# Porting #1 — Ball physics

**Status: constants ported (2026-05-13).** Algorithm port (linear-decay friction +
destination-based ball model) is deferred to a follow-up session.

Goal of this session: replace every guessed numeric constant in `game/scripts/Sim/BallState.cs`
with the value used by the original SWS.EXE (via `external/swos-port`). Cross-checked
against `external/original-amiga-swos` is not yet done — TODO.

## Scale notes

SWOS positions are 32-bit Q16.16 fixed-point (high word = whole pixels). SWOS
"speed" is a 16-bit Q8.8 scalar (high byte = whole px/tick). Our `Fixed` is
Q24.8, so:

- **SWOS speed (Q8.8) plugs directly into `Fixed.FromRaw()`** — same fractional
  resolution (8 bits). `kBallKickingSpeed = 2208` raw → 8.625 px/tick.
- **SWOS deltaZ / gravity (Q16.16)** has more fractional bits than Q24.8. Convert
  by `value >> 8`. `kBallKickingDeltaZ = 0x14000` (81920) → `0x140` (320) in Q24.8.

## Mapping table — DONE entries

| Our name              | Was       | Ported value | swos-port source (file:line / asm address) | Notes |
|-----------------------|-----------|--------------|--------------------------------------------|-------|
| `BallSim.KickPower`   | 3072 raw  | **2208**     | `swos/swos.asm:203952  kBallKickingSpeed dw 2208` | Q8.8 = 8.625 px/tick |
| `BallSim.HighKickPower` | (new)   | **2688**     | `swos/swos.asm:203956  kHighKickBallSpeed dw 2688` | Q8.8 = 10.5 px/tick — lob |
| `BallSim.NormalKickPower` | (new) | **2560**     | `swos/swos.asm:203961  kNormalKickBallSpeed dw 2560` | Q8.8 = 10.0 px/tick — pass |
| `BallSim.SlidePunchPower` | 2000  | **2560**     | (no separate SWOS const — reused NormalKickPower) | Tackles punt ~= pass distance |
| `BallSim.KickLoft`    | 1024 raw  | **320**      | `swos/swos.asm:203950  kBallKickingDeltaZ dd 0x14000` | Q16.16 → Q24.8 (>>8). 1.25 px/tick. |
| `BallSim.HeaderJumpZ` | (new)     | **160**      | `swos/swos.asm:203810  kBallJumpHeaderDeltaZ dd 0xA000` | Q16.16 → Q24.8 (>>8). 0.625 px/tick. |
| `BallSim.Gravity`     | 48 raw    | **18**       | `src/game/amigaMode.cpp:37  kGravityConstant = 4608` | Amiga value; PC is 3291. Q16.16 → Q24.8. |
| `BallSim.GroundFrictionPerTick` | (new) | **16**  | `src/game/amigaMode.cpp:35  kBallGroundConstant = 16` | Amiga value; PC is 13. Used by future linear-decay refactor. |
| `BallSim.AirFrictionPerTick`    | (new) | **10**  | `src/game/amigaMode.cpp:36  kBallAirConstant = 10`    | Amiga value; PC is 4. |

## Mapping table — TBD entries

| Our name              | Was       | Target source                                          | Note |
|-----------------------|-----------|--------------------------------------------------------|------|
| `BallSim.InteractionRadius` | 14  | `kBallPlOffsets` table in swos.asm                     | Need to grep for ball-player attach radius |
| `BallSim.DribbleOffset`     | 10  | `kBallPlOffsets` (~25 entries, indexed by direction)   | Per-direction offset, not single value |
| Bounce coefficient per pitch | 80/256 | `kBallSpeedBounceFactorTable[] = { 24,80,80,72,64,40,32 }` and `kBallBounceFactorTable[] = { 88,112,104,104,96,88,80 }` (game.cpp:1391-1392) | Per-pitch-type, indexed by pitchType |

## Algorithm port — DEFERRED

Currently kept as-is (exponential decay `velocity *= 242/256` per tick). SWOS uses
**linear** decay: `speed -= GroundFrictionPerTick (16)` per tick on the ground,
`-= AirFrictionPerTick (10)` in the air, plus an extra `pitchBallSpeedFactor` when
no team has possession (ball.cpp:240–292). That requires switching `BallSim` from
a Cartesian-velocity model to a scalar-speed + destination-vector model — bigger
refactor, separate session.

Other algorithm differences identified but not yet ported:
- SWOS ball uses `destX/destY` (16-bit) + `speed` (16-bit) → `CalculateDeltaXAndY` (in `swos.asm`)
  recomputes `deltaX/deltaY` (32-bit Q16.16) every tick. Our model stores cartesian
  `VelocityX/VelocityY` directly.
- After-touch: post-kick, SWOS lets the player nudge ball direction by holding
  left/right (with fels table at `spin_factor_already`, see `docs/SWOS/game.txt`).
  Our model has no after-touch.
- Kick speed reduction after kick: 87.5% if diagonal, 75% otherwise
  (`docs/SWOS/game.txt`). Not in our model.
- Pass speed bump: ball speed × 112.5% after a pass. Not in our model.
- Spin: left/right spin curves trajectory mid-flight. Not in our model.

These all hinge on the destination-based model. Port them together in the
follow-up.

## Port checklist

- [x] Clone `zlatkok/swos-port` to `external/swos-port/`
- [x] Clone `starwindz/original-amiga-swos` to `external/original-amiga-swos/`
- [x] Locate ball physics file (`src/game/ball/ball.cpp` + `swos/swos.asm`)
- [ ] Cross-check 5 most impactful constants in starwindz 68k asm (deferred — same era binary, values should match)
- [x] Fill in mapping table for scalar constants (KickPower, KickLoft, Gravity, etc.)
- [ ] Trace `kBallPlOffsets` for `InteractionRadius` and `DribbleOffset`
- [x] Replace `BallSim` constants
- [x] Add code comments citing source files
- [ ] Refactor friction model to linear (separate session)
- [ ] Refactor to destination-based ball (separate session)
- [ ] Smoke test in editor — does the ball feel like SWOS? (manual, user side)
