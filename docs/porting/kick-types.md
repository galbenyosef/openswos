# Porting #4b — Kick types (Pass / Shot / HighShot)

**Status: ported (2026-05-15).**

SWOS three-style kick via single button — port from `external/swos-port/docs/SWOS/game.txt`
sections "Sut" (Shot) and "Pass":

| KickType  | Trigger                              | Power formula                | Loft     | SWOS cite |
|-----------|--------------------------------------|------------------------------|----------|-----------|
| **Pass**  | Release before 4-tick wind-up        | `KickPower × 1.125`          | 0 (flat) | game.txt:34 "Nakon dodavanja brzina lopte se povecava na 112,5%" |
| **Shot**  | Auto-fire at 4 ticks of hold         | `KickPower`                  | `KickLoft` | game.txt:84 "Sut: nakon 4 tika se primenjuje sut" |
| **HighShot** | Hold + opposite-of-facing during wind-up | `HighKickPower`         | `KickLoft × 2` | game.txt:84 "proverava da li je igrac cimnuo unazad" |

All three then get SWOS post-kick reduction (`game.txt:86`):
- Cardinal direction: × 0.75 (`PostKickCardinalPct = 192/256`)
- Diagonal direction: × 0.875 (`PostKickDiagonalPct = 224/256`)

## Implementation

`game/scripts/Sim/BallState.cs`:
- `enum KickType { Shot, Pass, HighShot }`
- `PlayerInfluence.KickType` field
- `BallSim.Tick` kick branch dispatches on `inf.KickType`

`game/scripts/Main.cs` per-player wind-up state machine
(`_p1KickHoldTicks` / `_p1PulledBack` / `_p1KickFired`, mirror for P2):
- On `IsActionJustPressed` → reset all
- During `IsActionPressed` (held) → increment hold; check back-press via
  `IsBackPress(dx, dy, facing)` dot-product test
- At hold ≥ 4 ticks → auto-fire SHOT (or HIGH SHOT if pulled back)
- On `IsActionJustReleased` before fire → PASS
- On release → clear state for next press

`IsBackPress`: `dot(input_dir, facing_unit) < 0`. PlayerSim.FacingUnit exposed public.

## Numbers (post-kick reduction applied, vs pitch height 632 px)

| Type     | Cum dist cardinal | Cum dist diagonal |
|----------|-------------------|-------------------|
| Pass     | ~238 px (38 %)    | ~325 px (51 %)    |
| Shot     | ~188 px (30 %)    | ~256 px (41 %)    |
| HighShot | ~279 px (44 %)    | ~380 px (60 %)    |

Cum-distance from linear friction: `power² / (2 × decay × 256)` with decay=16.

## TODO

- Per-player **shooting / finishing skill modulation** (game.txt:121 `dw -384, -270,
  -162, -54, +54, +162, +270, +384` for skill 0..7, plus `ball_speed_finishing`
  table for shots from the penalty area). Requires per-player stats lookup on the
  controlled player — `TeamRecord.Players[i].Shooting/Finishing` already loaded.
- 4-tick wind-up **animation** (currently instant). SWOS draws a kick frame for
  these 4 ticks before the ball flies.
- AI **never fires anything but Shot** (KickType.Shot hard-coded). Could read AI
  intent (distance to goal, defender pressure) to choose Pass/HighShot.
