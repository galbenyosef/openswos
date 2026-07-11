# SwosVm port — strategy + progress

**Started: 2026-05-22.** Mechanical translation of swos-port C++/asm into C#
via an emulated memory layer (`SwosVm.Memory`), so we can chip away at the
~20,000 LOC of asm-translated soup that drives all the user-observed gameplay
mechanics (keeper catches with feet not hands, ball doesn't spin, dribble is
100% glued, etc.).

See `C:\Users\Admin\.claude\plans\lazy-crunching-finch.md` for the approved
plan.

## Architecture

```
game/scripts/SwosVm/
  Memory.cs       — 320 KB byte[] backing + Read*/Write* helpers + Addr.*
  Flags.cs        — 68k flag-register emulation (Zero/Carry/Sign/Overflow)
  BallSprite.cs   — view nad Memory @ 0x4F800 (sprite struct fields by offset)
  TeamData.cs     — view nad Memory @ 0x4F900 / 0x4FB00 (top/bottom teams)

game/scripts/Sim/Port/
  BallUpdate.cs   — ports from external/swos-port/src/game/ball/ball.cpp
  KeeperUpdate.cs — (Phase B5) ports from updatePlayers.cpp keeper sections
  PlayerUpdate.cs — (Phase B6) ports from updatePlayers.cpp dribble sections
```

**Toggle**: menu slot 7 "physics : SWOS port / Old sim". Default Old sim.
Persisted to `user://settings.json` (`useSwosPort` field).

## Translation protocol — how to port a function

When porting an asm-translated function from swos-port:

1. **Find the function** in `external/swos-port/src/game/ball/ball.cpp` (or
   wherever).
2. **Decide if it's hand-written or asm-translated.** Hand-written (clean C++,
   uses `swos.foo.bar`) → direct port via BallSprite/TeamData properties.
   Asm-translated (uses `g_memByte`, `A0/eax/esi`, `flags.zero`, `goto l_*`) →
   mechanical translation via Memory + Flags.
3. **Map locals**:
   - `A0`, `esi`, `ebx` → `int spritePtr`, `int local`, etc.
   - `ax`, `bx`, `dx` → `short`/`int`
   - `eax` → `int` (32-bit dword)
4. **Map memory reads**:
   - `readMemory(esi + 44, 2)` → `Memory.ReadWord(spritePtr + 44)` or
     `BallSprite.Speed` if `esi` is ballSprite
   - `*(word *)&g_memByte[NNN]` → `Memory.ReadWord(Addr.foo)` (lookup the
     symbolic name)
5. **Map memory writes** — symmetric.
6. **Map flags**:
   - `flags.zero = ax == 0;` → `Flags.SetFromTest16(ax);` (sets all flags)
   - sub/cmp sequences → `Flags.SetFromSub16(dst, src)` returns result
7. **Map gotos** — C# supports `goto label;` natively, USE IT for first port.
   Refactor to structured control flow as second pass.

## Section inventory — ball.cpp:updateBall (B4 target)

Roughly 25 logical blocks across ball.cpp:13-3006. Track progress here:

| # | Section | Lines | Status |
|---|---|---|---|
| 1 | Hide/show ball + frame index pick | ball.cpp:13-90 | TODO |
| 2 | Goal post bounce | ball.cpp:91-180 | TODO |
| 3 | Direction quantisation (Sprite.direction) | ball.cpp:181-239 | TODO |
| 4 | Linear friction (ground/air/pitch factor) | ball.cpp:240-297 | DONE in BallSim |
| 5 | Apply deltaX/Y/Z to position | ball.cpp:300-350 | TODO |
| 6 | Z clamp + bounce-on-ground (XY+Z damping per pitch) | ball.cpp:351-420 | DONE in BallSim |
| 7 | Possession check (player/keeper) | ball.cpp:421-530 | DONE in BallSim |
| 8 | Various ball update helpers | ball.cpp:531-2247 | TODO |
| 9 | **applyBallAfterTouch (ball spin)** | ball.cpp:2248-3005 | **DONE 2026-05-23** ← USER PAIN #5 (port done, wire-up pending) |
| 10 | checkIfBallOutOfPlay (set pieces) | ball.cpp:3007-4021 | PARTIAL (corners ported) |
| 11 | calculateNextBallPosition (motion predictor) | ball.cpp:4206-4501 | TODO |
| 12 | CalculateDeltaXAndY (destX/destY+speed → deltaX/deltaY) | updateSprite.cpp:355 | TODO (tables in swos.asm:236334 extracted) |

When a section is moved from BallSim → BallUpdate, mark DONE here.

## 2026-05-23 — Plan PIVOT (research-driven)

After porting sections 1-7 of updateBall, ran 4-agent research + audit
covering: (a) game porting best practices, (b) VM-bridge C# idioms, (c)
fidelity testing patterns, (d) critical audit of current port code.

**Verdict**: Pivot from "keep porting sections" to "build fidelity harness
+ fix known bugs + lock player memory layout, THEN port keeper". See
`C:\Users\Admin\.claude\plans\lazy-crunching-finch.md` section "2026-05-23
UPDATE" for the modified plan.

### Bugs found in BallUpdate.cs Section3 (NEED FIX in B4.9)

| # | File:line | Bug | Fix |
|---|---|---|---|
| 1 | `BallUpdate.cs:104-115` | Substitution truth table inverted | Re-read ball.cpp:338-368, match exactly: skip-keeper-update only when (substituteInProgress AND teamThatSubs==lastTeam AND controlledPlayer==0) |
| 2 | `BallUpdate.cs:124` | Missing `if (ball.speed == 0) goto check_keeper_z` guard | Add guard before stubbed updateBallWithControllingGoalkeeper call |
| 3 | `BallUpdate.cs:158-164` | Keeper-z sign convention unverified | Add unit test verifying +131076 = rise toward keeper hand, -65538 = fall |
| 4 | `BallUpdate.cs:208` | Signed `speed * bounceXY` 32-bit multiply | Use `uint` operands or `>>>` for unsigned semantics matching `mul` |
| 5 | `BallUpdate.cs:229` | Signed `shiftedDz * bounceZ` 32-bit multiply | Same as #4 |

### Risk hotspots flagged for B4.7 wire-up

- **`BallSprite.XPixels` write loses sub-pixel** — sync layer between BallState (Q24.8) ↔ BallSprite (Q16.16) must use FULL 32-bit X/Y/Z, not whole-pixel accessors.
- **`gameState == ST_KEEPER_HOLDS_BALL` (3) never propagated** from MatchState — keeper-z branch (BallUpdate.cs:95-167) is dead code today.
- **`gameStatePl` always 100** in our sim — X/Y barrier reflection (lines 247-272) unreachable.
- **`CalculateNextBallPosition` never called by Tick** — dead code today (will be used by future keeper AI).

### Section inventory inconsistency (FIX in B4.9)

My port labeled "Sections 5-7" actually covers `ball.cpp:299-735` (3
distinct logical sections fused). Update section inventory table at top of
this doc to match the SUFFICIENT granularity actually used in code.

### Counter-AI advice to bake into future ports

(From `feedback-treat-as-senior-request-research` memory — research findings)

1. Keep `goto` — don't refactor mechanical ports to structured control flow until fidelity tests pass.
2. Don't write per-function unit tests before integration replay exists.
3. Preserve original bugs verbatim — players depend on quirks.
4. Emit "useless" flag updates by default; let liveness analysis remove later.
5. Magic numbers are the CONTENT, not smells — cite line, don't rename.

## 2026-05-23 progress — Option III chosen, B4 grinding section by section

User explicitly approved **Option III** (full mechanical port through SwosVm)
on 2026-05-23 after seeing the three options laid out. Reasoning: only Option
III delivers 1:1 fidelity that the project's hard constraint requires.

Session progress (sub-task IDs in parens):

- B4.1 ✅ Port `CalculateDeltaXAndY` + kSineCosineTable[256] + kAngleTangent[32×32]
  in `SwosVm/Tables.cs` and `Sim/Port/SpriteUpdate.cs`. Foundation for any
  SWOS-style sprite motion (ball, keeper, AI players). PC 41/64 damping
  embedded here, matching updateSprite.cpp:323-329.
- B4.2 ✅ Port `reverseDestXDirection`, `reverseDestYDirection`,
  `calculateNextBallPosition` (ball.cpp:4206-4583) into BallUpdate.cs.
  Reverse helpers reflect destination around current position (used by
  goal-post bounce in Section4). Predictor uses variable substep based on
  ball height for fast-forward landing prediction (keeper AI input).
- B4.3 ✅ Port updateBall Section1 — hideBall flag, moving/static frame table
  switch, animation pacing. ball.cpp:13-178.
- B4.4 ✅ Port updateBall Sections 2-3 — direction recompute via
  CalculateDeltaXAndY + 8-direction quantization + friction selector
  (ground / air / pitch factor) + speed reduction. ball.cpp:180-298.
- B4.5 ✅ Port updateBall Sections 5-7 — apply deltaX/Y to position,
  ST_KEEPER_HOLDS_BALL keeper-hand Z handling, gravity + ground bounce
  with XY damping (ballSpeedBounceFactor) and Z reflection
  (ballBounceFactor), pitch barrier reflection during set pieces.
  ball.cpp:299-735.
- B4.6 ⏳ NEXT — Port updateBall Section 4 (goal detection + post collision +
  ball shadow update). ball.cpp:737-2247. ~1500 LOC asm. Includes upper/lower
  goal Y range checks, in-net detection (l_ball_in_upper_net /
  l_ball_in_net), post + bar collision (reverse_delta_z), penalty/own goal
  branches, ball shadow Sprite update.

### Memory + state additions this session

- `Memory.Addr.*` extended by: kHighKickDeltaZ, kNormalKickDeltaZ (dwords),
  kSpinMultiplierFactor (10×2 = 20 bytes), kKickSpinFactor (8×4×2 = 64 bytes),
  kPassingSpinFactor (64 bytes), gameStatePl, gameState, ballNextX,
  ballNextY, hideBall, ballShadowImageIndex, ballStaticFrameIndices_Addr,
  ballMovingFrameIndices_Addr, _Table variants (32 bytes each),
  g_substituteInProgress, teamThatSubstitutes, lastTeamPlayedBeforeBreak.
- `Memory.Addr.kGravityConstant` widened to DWORD (was WORD, overlapped
  kKeeperSaveDistance — bug fix during section 5-7 port). kKeeperSaveDistance
  moved to 0x24. Bounce + pitch factors moved to 0x28/0x2A/0x2C.
- `BallSprite` extended with animation fields (FrameIndicesTable, FrameIndex,
  FrameDelay, CycleFramesTimer, ImageIndex, FullDirection).
- `TeamData` extended with ControlledPlayer (offset 32) + PlayerHasBall
  (offset 40). Also helper ControlledPlayerFromBase(absoluteAddress) since
  lastTeamPlayedBeforeBreak is a dword pointer.
- `Tables.cs` — new file with verbatim copy of kSineCosineTable (256 entries)
  + kAngleTangent (1024 entries, 32×32 byte array).
- `Sim/Port/SpriteUpdate.cs` — new file. CalculateDeltaXAndY +
  UpdateSpriteDirectionAndDeltas (the latter not yet used by ball — it's a
  helper for future B5/B6 sprite ports).

### Status

`BallUpdate.Tick()` runs sections 1-3 (which collectively handle ALL physics
of a single tick EXCEPT goal detection). The port path therefore:
- Does correct sprite frame switching (moving ↔ static) ✓
- Does correct direction quantization via SWOS sine/tangent tables ✓
- Does correct friction (ground/air/pitch factor) ✓
- Does correct delta application + gravity + ground bounce ✓
- Does correct pitch barrier reflection during set pieces ✓
- Does NOT detect goals (Section4 unported)
- Does NOT collide with goal posts (Section4 unported)
- Does NOT update ball shadow position (Section4 unported)

To wire this in (Task B4.7): Main.cs needs to sync `BallState` ↔ Memory each
tick when `_useSwosPort` is true, call BallUpdate.Tick(), sync back. Until
Section4 lands, the toggle path won't be playable beyond ball motion.

## 2026-05-23 progress — `applyBallAfterTouch` port complete

**What landed**:
- `Memory.Addr.*` extended: spin tables (kSpinMultiplierFactor, kKickSpinFactor,
  kPassingSpinFactor), kick deltaZ constants (kHighKickDeltaZ, kNormalKickDeltaZ),
  global state (gameStatePl @ 100 = ST_GAME_IN_PROGRESS, gameState @ 3 = ST_KEEPER_HOLDS_BALL).
- `Memory.Init()` now populates all spin tables (kKickSpinFactor[8 dirs × 4 words]
  = 64 bytes, kPassingSpinFactor same layout, kSpinMultiplierFactor 10 words).
- `TeamData.cs` extended with all `applyBallAfterTouch` fields:
  opponentsTeam (+0), currentAllowedDirection (+44), controlledPlDirection (+56),
  goalkeeperSavedCommentTimer (+76), spinTimer (+118), leftSpin (+120),
  rightSpin (+122), longPass (+124), longSpinPass (+126), passInProgress (+128).
- `BallUpdate.ApplyBallAfterTouch(bool topTeam)` — full semantic port of
  ball.cpp:2248-3005 (~750 LOC asm → ~250 LOC clean C#). Implements:
  - kick branch (spin direction detection + curve + high/normal kick boost at tick 4)
  - pass branch (spin direction + longPass / longSpinPass boost)
  - 4 helpers: DetermineKickSpinSide, ApplySpinOffsetToDest, ApplyTick4HighOrNormalKickBoost, ApplyLongPassBoostIfNeeded

**What's NOT wired up yet** (architectural decision required from user):

`ApplyBallAfterTouch` writes to `BallSprite.DestX / DestY` (Sprite destination
fields). In SWOS, ball motion is computed each tick via `CalculateDeltaXAndY`
(updateSprite.cpp:355) — takes (x, y, destX, destY, speed) → (direction,
deltaX, deltaY). Our `BallSim` uses DeltaX/DeltaY DIRECTLY without a destination,
so the ported spin function has no effect unless we either:

**Option I** — Adopt SWOS' destX/destY motion model in `BallSim` (port
`CalculateDeltaXAndY` + change motion calculation to recompute deltas
each tick from destination). Cleaner, mechanically faithful, lets future
ports drop straight in. Requires architectural change to BallState.

**Option II** — Add a conversion shim: after `ApplyBallAfterTouch`, read
destination DELTA (newDest - oldDest), translate to deltaX/Y nudge,
apply to BallState. Smaller change, less faithful, may produce subtle differences.

**Option III** — Don't wire it in yet. Continue porting the rest of
updateBall (sections 1, 2, 3, 5, 8, 11 in inventory) so we can swap the
whole ball physics over at once via the menu toggle. Largest effort but
matches the original plan.

User decides. Both Option I and II would deliver visible ball curl in
~1 session of follow-up work. Option III is multi-session.

### Tables ready for B4 follow-up

When the user picks Option I or III, `kSineCosineTable` (256×2 bytes, swos.asm:236334-236361)
and `kAngleTangent[32][32]` (updateSprite.cpp:16-49) are needed by CalculateDeltaXAndY.
Both have been located but not yet transcribed into `SwosVm.Tables`.

## Section inventory — updatePlayers.cpp keeper sections (B5)

| # | Function | Lines | Status |
|---|---|---|---|
| 1 | shouldGoalkeeperDive | 10485+ | TODO |
| 2 | goalkeeperJumping | 10829+ | TODO |
| 3 | Catch state (kGoalieCatchingBall) | 11050+ | TODO |
| 4 | Claim + release (kKeeperHoldsTheBall) | 16656+ | TODO |
| 5 | updateBallWithControllingGoalkeeper | player.cpp:200 | TODO ← USER PAIN #2 (ball in hands) |

## Section inventory — updatePlayers.cpp dribble (B6)

| # | Function | Lines | Status |
|---|---|---|---|
| 1 | Player-with-ball update (Ball Control loss) | updatePlayers.cpp:~12000 | TODO ← USER PAIN #4 |
| 2 | kBallPlOffsets dribble pin | player.cpp:~140 | DONE in BallSim |

## Verification protocol

Each completed section ports against:
1. Build: `dotnet build game/OpenSwos.csproj`
2. Smoke headless: `Godot --headless --quit-after 3`
3. Toggle menu → "SWOS port" → start match → observe behaviour
4. Side-by-side with running swos-port (`external/swos-port/bin/x64/swos-port-x64-Release.exe`)

When B4 section 9 ("applyBallAfterTouch") ports: spin should be visible
(ball curls after off-axis kicks).

When B5 section 5 ("updateBallWithControllingGoalkeeper") ports: keeper holds
ball in HANDS not at feet.

When B6 section 1 ports: ball control stat affects dribble retention.
