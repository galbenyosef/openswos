# Playable MVP snapshot

Last updated: **2026-05-22** (end-of-session, ~27% context left before compact).

## Where we are right now

- **Old sim** (BallSim + KeeperSim + PlayerSim) **= STABLE**, playable end-to-end.
- **SWOS port path** (SwosVm bridge) = **foundation laid, port WIP**. Menu
  slot 7 lets you toggle but the port stub throws — keep default "Old sim".
- Build OK, headless smoke OK. Two backups bracketing this session:
  - `.backups/20260522_201526_pre_swosvm_port_phase_B1.7z`
  - `.backups/20260522_202612_swosvm_foundation_B1_B2_B3_toggle_done.7z`

## Current playable feature set (Old sim path)

### Match flow
- Menu → kickoff → first half → halftime → second half → full time → menu.
- Goals, corners, throw-ins, goal-kicks all trigger correctly.
- Pause via ESC.

### Physics (PC mode @ 70 Hz, all SWOS-cited)
- **Tick rate 70 Hz** (`timer.h:3 kTargetFpsPC`); project.godot physics 70.
- **41/64 sprite delta damping** on player + ball XY motion
  (`updateSprite.cpp:323-328`).
- **Linear ball friction** ground=13 / air=4 (PC values, `amigaMode.cpp:53-54`).
- **Gravity** = 13 Q24.8 (PC value 3291 Q16.16, `amigaMode.cpp:55`).
- **Bounce mechanics**: XY speed × (256-factor)/256 per pitch
  (Normal: factor 64 → 75% kept). Z reflected × factor/256. Sticky threshold
  vz<160 → settle. Once-per-impact (Z transition >0→≤0).
- **Pitch friction modulation**: `decay = GroundFriction + pitchFactor[pitch]`
  when ball is free. PC table {-3, 4, 1, 0, 0, -1, -1}.
- **Kick power**: 100% SWOS-authentic. KickPower 2208, HighKick 2688,
  NormalKick 2560. SlidePunch 1792 (kPlayerTacklingSpeed).
- **PostKick reduction**: cardinal × 0.75, diagonal × 0.875.
- **Diagonal normalisation**: 181/256.

### Player movement
- `PlayerSpeedTable.InProgress` = {928..1250} Q8.8 per skill 0..7.
- BallCarrier × 87.5%, RunBack × 62.5%, HumanSlide × 78%, CpuSlide × 50%.
- `MovementScaleNum = 256` (full speed).
- Slide duration 34 ticks (50 Hz × 1.4 for 70 Hz).
- `kBallPlOffsets` unit-vector dribble (1 px in facing direction).
- Sprite anchors per (Direction, frame) from PC TEAM1.DAT.

### Keeper (Phase A from 2026-05-22)
- State machine: Normal → DivingHigh/Low → CatchingBall → Claimed.
- ShouldDive(): Y±SaveDistance + reach check (skill-indexed dive delta × 75
  ticks).
- `kGoalkeeperDiveDeltas[skill 0..7]` PC values.
- Catch (timer ≥ 60) vs Parry (timer < 60).
- Auto-release at 150 ticks → kick targeted at closest teammate.
- Per-team keeper kit (Phase B): shorts/socks from team primary; body yellow.
- Sprite state placeholder: dive → Slide frame, claimed → Fallen.
- **Known issue**: ball pinned at feet not hands (Phase B5 will fix via real
  port).

### Match controls
| Player 1 | Player 2 |
|----------|----------|
| Arrow keys | WASD |
| Space — tap=pass, hold=shot, hold+back=high shot | L-Shift — same logic |
| X — slide tackle | C — slide tackle |
| ESC — pause | (P2 has no pause) |

### Menu options (8 slots)
1. Pitch variant (5 of 6 available — SWCPICH2.MAP missing)
2. Home team (1730 PC teams)
3. Away team
4. Opponent (AI / Player 2 / Demo)
5. Match length (30 s – 10 min/half)
6. AI difficulty (Easy/Normal/Hard)
7. Speed multiplier (x0.10..x3.00, default x1.00)
8. **Physics**: SWOS port (WIP) / Old sim (stable)

## Run

```pwsh
I:\GITHUB\W_OPEN_SWOS\.tools\godot\Godot_v4.6.2-stable_mono_win64.exe --path I:\GITHUB\W_OPEN_SWOS\game
```

Side-by-side reference:
```pwsh
I:\GITHUB\W_OPEN_SWOS\external\swos-port\bin\x64\swos-port-x64-Release.exe
```

## Open user pain points → Phase B sequencing

User reported these gameplay deviations from real SWOS. Each maps to a Phase B
port milestone:

| # | User pain | Source in swos-port | Phase | Status |
|---|---|---|---|---|
| 1 | Keeper lies down immediately (Fallen pose) | updatePlayers.cpp keeper SM | B5 | TODO |
| 2 | Ball pinned at keeper feet, not hands | player.cpp:200 updateBallWithControllingGoalkeeper | B5 | TODO |
| 3 | Keeper doesn't release ball properly | updatePlayers.cpp:16656 stoppageTimerActive | B5 | TODO |
| 4 | Dribble 100% glued — no Ball Control variation | updatePlayers.cpp dribble path | B6 | TODO |
| 5 | Ball doesn't spin | ball.cpp:2248-3006 applyBallAfterTouch | B4 | TODO |
| 6 | Catch radius / dive distance off in subtle ways | ball.cpp + updatePlayers keeper | B4+B5 | TODO |

## Backups

`.backups/` (gitignored). Most recent + relevant:

- **2026-05-22 end**: `20260522_202612_swosvm_foundation_B1_B2_B3_toggle_done.7z`
  ⭐ resume from here
- 2026-05-22: `20260522_201526_pre_swosvm_port_phase_B1.7z`
- 2026-05-22: `20260522_110602_pre_keeper_full_port_phase_A.7z`
- 2026-05-21: `20260521_182537_pre_bounce_and_pitch_friction_port.7z`
- 2026-05-21: `20260521_103748_pre_pc_mode_switch_and_keeper_z_fix.7z`
- 2026-05-19: `20260519_211022_pre_swos_port_first_audit.7z`
- 2026-05-15: `20260515_195639_pre_compact_2026_05_15.7z`

## Resume guidance for next session

Read **first**:
1. `C:\Users\Admin\.claude\plans\lazy-crunching-finch.md` — approved plan
2. `docs/porting/swosvm-port.md` — translation protocol + section inventory
3. `memory/project_swosvm_port_strategy.md` — what's built, what's not

Then continue with **Phase B4** (port `updateBall()` + `applyBallAfterTouch()`
from `external/swos-port/src/game/ball/ball.cpp:13-3006`). The
`game/scripts/Sim/Port/BallUpdate.cs` already has the section inventory in
comments — chip away section-by-section.

**Use agents heavily** for the asm-translation grunt work — user explicitly
noted that small-talk porting eats context fast and parallel agents work
better.
