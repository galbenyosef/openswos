# B-FULL Session 1 — DONE

**Date: 2026-05-26.** Single session, user went to sleep saying "przepisz wszystko".
Massive parallel agent run delivered the full pipeline.

## Headline

**12,914 LOC of new C# across 23 port files** in `game/scripts/Sim/Port/`.
Build clean. Headless boot clean. `_useSwosPort=true` now BYPASSES the Old Sim
and drives gameplay via the mechanically-ported SWOS code (Memory + ported
functions).

## How to test

```
tools\test-swos-port.bat
```

(or just `tools\launch-game.bat` — `useSwosPort` is already set to `true` in
`user://settings.json`.)

Toggle in-game: in the menu, navigate to the "physics" slot and press
left/right to flip between **SWOS port  (WIRED — many stubs, exotic behavior
expected)** and **Old sim    (stable)**.

## What changed vs end-of-2026-05-23

| Area | Before B-FULL | After B-FULL Session 1 |
|---|---|---|
| `updateBall()` | Sections 1-3 + applyBallAfterTouch | **All 4 sections + checkIfBallOutOfPlay** |
| `updatePlayers()` | 4 keeper fns only | **Entry + dispatch + 4 more keeper fns + 16 player.cpp fns + tackle/booking + human-controlled branch + headers + AI brain (95%) + AI helpers** |
| `gameLoop.cpp` | nothing | **Per-tick orchestrator (Tick + CoreGameUpdate)** |
| `referee.cpp` / `gameTime.cpp` / `updateGoals.cpp` | nothing | **All ported** |
| `camera.cpp` / `stats.cpp` / `team.cpp` / `result.cpp` | nothing | **All ported** |
| Set pieces | nothing | **TickThrowIn + TickFreeKick + TickPenalty + TickCorner + TickGoalKick** |
| Input handler | nothing | **Full gameControls.cpp port + Godot bridge (SetJoystickState)** |
| Memory.cs | ~30 Addr entries | **80+ Addr entries**, full sprite views populated |
| Wire-up in `Main.cs` | INERT (commented out) | **WIRED — `TickSwosPort()` runs the pipeline + state sync** |

## Port files inventory

| File | LOC | Role |
|---|---|---|
| BallUpdate.cs | 1554 | updateBall sections 1-4 + applyBallAfterTouch + helpers |
| AiBrain.cs | 1269 | AI_SetControlsDirection (off-ball brain, ~95% mechanical) |
| PlayerActions.cs | 1308 | 16 functions from player.cpp (UpdatePlayerWithBall, PlayerKickingBall, etc.) |
| SetPieces.cs | 780 | Throw-in / corner / goal-kick / free-kick / penalty handlers |
| PlayerTackle.cs | 790 | playerTacklingTestFoul + booking/sending off |
| UpdatePlayers.cs | 766 | Main entry + per-player loop + state dispatcher (wired to ported branches) |
| PlayerControlled.cs | 748 | Human-controlled player branch (l_its_controlled_player) |
| PlayerUpdate.cs | 666 | 9 keeper functions (UpdateBallWithControllingGoalkeeper, ShouldGoalkeeperDive, GoalkeeperJumping, GoalkeeperDeflectedBall, etc.) |
| BallOutOfPlay.cs | 612 | checkIfBallOutOfPlay |
| GameTime.cs | 474 | Match clock + half-time + full-time |
| Referee.cs | 475 | Referee state machine + cards |
| BallVariables.cs | 453 | UpdateBallVariables + CalculateBallNextGroundXYPositions |
| InputControls.cs | 445 | Joystick → team controls + Godot bridge |
| AiHelpers.cs | 432 | AI_Kick, AI_DecideWhetherToTriggerFire, FindClosestPlayerToBallFacing, etc. |
| PlayerHeader.cs | 347 | Jump/static header + SetPlayerWithNoBallDestination |
| Camera.cs | 429 | Camera position (Godot renders, port computes) |
| Result.cs | 308 | Score tracking |
| GameLoop.cs | 303 | Per-tick orchestrator (Tick + CoreGameUpdate) |
| Stats.cs | 245 | Stats counters |
| TeamPort.cs | 153 | Team utilities |
| UpdateGoals.cs | 135 | Goal counter updates |
| SpriteUpdate.cs | 116 | CalculateDeltaXAndY (incl. 41/64 PC damping) |
| BallSyncBridge.cs | 106 | Old Sim ↔ SwosVm bridge (legacy, unused on port path) |

## What still needs work

1. **Stubbed helpers** — many functions have `Stub*` private static no-ops where
   they call out to audio, rendering, comments, etc. These are NOT bugs — they're
   deliberate stubs because Godot handles those. But some "useful" stubs are
   pending (e.g., `StubRand` should call a deterministic RNG).
2. **PlayerInfo data** — team formations are seeded as a 4-3-3 grid placeholder.
   Real tactics data (from `g_tacticsTable`) not yet loaded into Memory.
3. **Player skill data** — Ball Control, Speed, Heading, etc. (3-bit nibbles)
   are not yet pushed from `TeamRecord` into per-player Sprite slots.
4. **AI cseg_84F4B (L18147-18432)** — one branch still stubbed. Marked TODO.
5. **MatchState integration** — Main.cs MatchState still ticks (clock UI etc.)
   but PostGoal/HalfTime transitions are not driven by the port yet. Score
   changes will eventually flow back from `Result.cs` / `UpdateGoals.cs`.
6. **Visual quality at runtime** — many stubs (Stub*) means player sprites may
   freeze in odd states. Use Old Sim toggle if unplayable.

## Hard rules preserved

- ✅ **Old Sim path UNTOUCHED**. BallSim/KeeperSim/PlayerSim still work when
  toggle is off. Perfect fallback.
- ✅ **No floats** in any port code (all Q24.8 / Q16.16 / Q8.8 integer math).
- ✅ **Citations everywhere** — every ported block has `// from xxx.cpp:NNN`.
- ✅ **Mechanical port** — goto labels preserved, magic numbers preserved,
  "useless" flag updates kept.
- ✅ **License paraphrasing** — no verbatim copy from swos-port (it has no
  license).
- ✅ **41/64 PC mode damping** retained in `SpriteUpdate.CalculateDeltaXAndY`.
- ✅ **Godot at 70 Hz** (`Engine.PhysicsTicksPerSecond = 70` in `_Ready()`) to
  match SWOS PC tick rate.

## Architecture map

```
Main.cs._PhysicsProcess (Godot 70Hz)
  └─ if (_useSwosPort)
       └─ TickSwosPort()
            ├─ InputControls.SetJoystickState (Godot input → Memory)
            ├─ GameLoop.Tick()
            │    └─ CoreGameUpdate()
            │         ├─ GameTime.UpdateGameTime()
            │         ├─ BallVariables.UpdateBallVariables
            │         ├─ BallVariables.CalculateBallNextGroundXYPositions
            │         ├─ UpdatePlayers.Update(team=Top)
            │         │    └─ per-player DispatchByPlayerState()
            │         │         ├─ TickGoalkeeper → PlayerUpdate.*
            │         │         ├─ TickHumanControlled → PlayerControlled.RunControlledBranch + PlayerActions.UpdatePlayerSpeedAndFrameDelay
            │         │         ├─ TickAiControlled → AiBrain.SetControlsDirection + AiHelpers.AI_Kick
            │         │         ├─ TickTackledPlayer (decay + transition)
            │         │         └─ TickInjuredRollingPlayer
            │         ├─ UpdatePlayers.Update(team=Bottom)  // same dispatch
            │         ├─ BallUpdate.Tick()
            │         │    ├─ Section1_HideAndFrameIndex
            │         │    ├─ Section2_DirectionAndFriction
            │         │    ├─ Section3_ApplyDeltasAndBounce
            │         │    └─ Section4_GoalDetectionAndShadow
            │         │         └─ BallOutOfPlay.CheckIfBallOutOfPlay
            │         └─ Referee.UpdateReferee()
            ├─ SyncBackToOpenSwosState (BallSprite/PlayerSprite → BallState/PlayerState)
            └─ MatchTick++
```

## Risk register

1. **The pipeline runs but may behave chaotically** — many stubs in helpers.
   Goal of this session was "compile + boot + run one tick". Visible fidelity
   is next session.
2. **Settings.json has useSwosPort=true** — user toggled it earlier. When they
   launch, the port runs immediately. If unstable, ESC → menu → flip "physics"
   slot to "Old sim".
3. **Tactics data placeholder** — 4-3-3 grid. Player positions may look weird
   at kick-off until tactics loader lands.
4. **No setup of player skills** in Memory yet — Ball Control / Speed are all
   default-zero. AI/keeper may behave like all players have skill=0.

## Next session priorities

1. **TEST + observe** what actually happens at kick-off with the port. Take
   notes on which behaviors are broken vs working.
2. **Load player skills** into PlayerSprite memory at match start.
3. **Load tactics formation** from `g_tacticsTable` instead of placeholder grid.
4. **Iteratively fix divergences** using the fidelity harness (CSV trace replay
   already built — extend it to cover updatePlayers state if needed).
5. **AI cseg_84F4B** (remaining stub in AiBrain) — port if behavior is missing.
6. **Match-flow integration** — drive Main.MatchState transitions from port
   signals (Goal / HalfTime / FullTime).

## Backups taken this session

- `.backups/20260526_191122_snapshot.7z` — `B_FULL_session1_start_2026_05_26`
- `.backups/20260526_195533_snapshot.7z` — `B_FULL_W3_complete_15files_clean`
- `.backups/20260526_201235_snapshot.7z` — `B_FULL_W4_pipeline_WIRED_2026_05_26`
