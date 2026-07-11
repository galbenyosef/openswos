# Porting #2 — Player physics

**Status: in progress (2026-05-13).**

Goal: replace hand-tuned `PlayerSim.WalkSpeed` (and proportional AI/keeper speeds)
with the per-skill speed table from SWOS. Add ball-carrier / slide / runback
modifiers. Tie animation pacing to the formula `(1280 - speed)/128 + 6`.

## Source: `external/swos-port/docs/SWOS/game.txt`

> Speed of the player that controls the ball is reduced to **87.5%** of capacity
> (that's why defenders catch up so easily). When the goal is scored, and the
> players are running back to their positions, their speed is reduced to **62.5%**
> of capacity.
>
> Based on players speed skill (range normalized to 0..7), and whether the game
> is in progress or not, effective speed is calculated using the following tables:
> ```
> dw 928, 974, 1020, 1066, 1112, 1158, 1204, 1250  ; game in progress
> dw 1136, 1152, 1168, 1184, 1200, 1216, 1232, 1248  ; game stopped
> ```
>
> 1280 seems to be the maximum player speed. Number of game frames between next
> animation frame switch is determined by subtracting player speed from maximum
> speed (1280). Result is further divided by 128, increased by 6 and that's how
> a delay between animation frames for the players is formed.
>
> Sliding player speed is multiplied by **0.78125**, unless it's CPU, then it's
> **0.5**.

## Mapping

| Our name (was)                                        | Was       | Ported value                                        | Source |
|-------------------------------------------------------|-----------|-----------------------------------------------------|--------|
| `PlayerSim.WalkSpeed` (single constant 1024 raw)     | 1024      | **PlayerSpeedTable.InProgress[skill]** (928..1250)  | game.txt |
| (none)                                                |           | **PlayerSpeedTable.Stopped[skill]** (1136..1248)    | game.txt |
| (none)                                                |           | **MaxSpeed = 1280**                                 | game.txt |
| (none)                                                |           | **BallCarrierMul = 224/256** (87.5%)                | game.txt |
| (none)                                                |           | **RunbackMul = 160/256** (62.5%)                    | game.txt |
| `PlayerSim.SlideSpeedNum = 384` (1.5× walk, vibe)    | 384       | **HumanSlideMul = 200/256** (78.125%)               | game.txt |
| (none)                                                |           | **CpuSlideMul = 128/256** (50%)                     | game.txt |
| `PlayerSim.AnimationPhase` (hardcoded `/6` divisor)  | /6        | **Frame delay = (1280-speed)/128 + 6**              | game.txt |

## Algorithm differences

SWOS slide previously in our model: 1.5× walk speed for 12 ticks. Real SWOS:
slide is **less** than walk (0.78× for humans), slide duration TBD (no source
yet — keep our 34 ticks @ 70 Hz value as placeholder).

Per-skill speed: in our scaffold every player walks at the same speed. After
port, skill-1 player walks ~3.6 px/tick (~254 px/s), skill-7 walks ~4.88 px/tick
(~342 px/s). Difference ≈ 35% — defenders WILL catch up if they're skill-7 and
you control a skill-1.

## Port checklist

- [x] Read game.txt for the speed tables and modifiers
- [ ] Add `PlayerSpeedTable` class with `InProgress`, `Stopped`, `MaxSpeed`, multipliers
- [ ] Add `PlayerState.EffectiveSpeed` (Q8.8 ushort) — written each tick by Tick
- [ ] Replace `PlayerSim.Tick`'s `speedScale` param with `effectiveSpeed`
- [ ] `AnimationPhase` reads `EffectiveSpeed` for frame-delay formula
- [ ] `Main.TickPlay` computes effectiveSpeed per side based on:
    - team's primary player skill (or average)
    - ball-carrier status → ×0.875
    - difficulty modifier for AI (non-canonical layer on top)
- [ ] Slide: use `HumanSlideMul = 200/256` of effective speed; CPU 128/256
- [ ] Smoke test: player movement should be **slower** than now (was 280 px/s, now 254..342 px/s by skill, ×0.875 if carrying ball)
