# Phase B-FULL — mechaniczny port całej rozgrywki

**Decyzja: 2026-05-26.** User explicitly rejected piecemeal porting.
Committed to FULL mechanical port of the whole SWOS gameplay pipeline.

## Dlaczego (motywacja — KONIECZNA do zachowania)

### User's direct experience (UAE port)
> "stracilem ze 2 dni probujac portowac czesciowo, a potem sportowalismy
> razem calosc i sie odpalila dosycz szybko (<10 prob zeby uzyskac obraz
> dyskietki kicka 1.3 w amidze)."

Tracking: dwa dni porażki z partial porting → przełączenie na full port →
working Amiga Kickstart 1.3 disk image w <10 iteracjach. **Pattern proven
on the actual user's prior project.**

### Research consensus (3 agents, 2026-05-23)
- **OpenTTD**: Ludvig Strigeus translated TTD asm linia-po-linii do C, then
  SDL underneath. Refactor to C++ started 2007 — **mechanical port is the
  start, not the end**.
- **OpenRCT2**: patched rct2.exe to call into openrct2.dll, swapped functions
  one at a time but kept memory layout identical for years. Only refactored
  out of `RCT2_ADDRESS(0x009E2920, ...)` macros after entire game ported.
- **Devilution**: byte-for-byte faithful first; DevilutionX (refactor) is a
  separate fork. Two-tier missions kept distinct.
- **ScummVM**: "Do not assume the original engine does not contain bugs —
  small irrelevant changes can have a larger cascade effect".
- **Decomp community**: "Compile first, refactor later" — produce byte-
  identical output BEFORE cleaning up.

### Counter-AI advice (research findings)
1. **Keep `goto`** in mechanically-ported functions — don't auto-refactor.
2. **Do NOT write per-function unit tests** before integration replay test —
   they pass while integrated game still diverges.
3. **Preserve original bugs verbatim** — players depend on quirks.
4. **Emit "useless" flag updates by default** — let liveness analysis remove
   later, not by hand.
5. **Magic numbers are CONTENT, not smells** — tag with `// from swos.asm:NNN`.

### What kept going wrong in OpenSWOS sessions
Despite knowing all the above, I (Claude) kept slipping back to piecemeal:
- 2026-05-21: added heuristic bounce mechanics to BallSim
- 2026-05-22: KeeperSim Phase A/B/C-light (heuristic state machine)
- 2026-05-23: lots of audit-driven fixes to ports + Main.cs
- 2026-05-24: B4.7 wire-up tried "undo BallSim motion" hybrid → failed → reverted
- 2026-05-24: dribble probability 25%/3% piecemeal hack → user complained "ball lost every few seconds"
- 2026-05-26: user called me out on this pattern (4th time in session)

**This pattern stops here.**

## Plan — Phase B-FULL execution

### Hard rules during B-FULL

1. **Old Sim path UNTOUCHED**. BallSim/KeeperSim/PlayerSim/Main.cs gameplay
   logic = stable fallback. NO heuristic fixes, NO behavior patches.
2. **All new behavior** goes via SwosVm Memory + ported functions.
3. **Compile-first, debug-later** — getting the whole pipeline to compile
   (with stubs for audio/comments/render/etc.) is the FIRST milestone.
4. **Use agents aggressively** — user explicitly confirmed: "uzywanie agentow
   nie jest jakimkolwiek problemem mamy najwyzszy indywidualny plan".
   Spawn many parallel agents for asm-translated chunks.

### Scope (gap between current state and end state)

| Component | Status | Estimate |
|---|---|---|
| Memory + Flags + Sprite views | ✅ DONE | – |
| `updateBall` Section 1-7 | ✅ DONE | – |
| `applyBallAfterTouch` (spin) | ✅ DONE | – |
| Keeper functions (4 ported) | ✅ DONE | – |
| `CalculateDeltaXAndY` + tables | ✅ DONE | – |
| `calculateNextBallPosition` | ✅ DONE | – |
| **`updateBall` Section 8 (goal detection)** | ❌ TODO | ~1500 LOC asm, 1 session |
| **`updatePlayers` main loop** | ❌ TODO | ~20 000 LOC asm, 2-3 sessions |
| **Input → kick handler** | ❌ TODO | medium, 1 session |
| **AI (off-ball + on-ball decisions)** | ❌ TODO | ~5 000 LOC asm, 2 sessions |
| **Set pieces** (corner, throw-in, goal-kick) | ❌ TODO | ~3 000 LOC, 1 session |
| **Match clock / score / phases** | ❌ TODO | small, 0.5 session |
| **Render frame selection** | partial | 1 session |
| **Wire-up: `_useSwosPort=true` bypasses Old Sim entirely** | ❌ TODO | 1 session |
| **Debug iteratively** | – | 1-3 sessions |

**Total: ~10 000–15 000 LOC new C# code via mechanical translation.**

**Sessions estimate: 3-7 intense work sessions.**

### Strategy details

1. **Port ordering**:
   - Goal detection (Section 8 — smallest, completes updateBall)
   - Then updatePlayers main loop skeleton + per-player update body
   - Then input handler (joystick → kick decision)
   - Then AI logic (controlled player + off-ball positioning)
   - Then set pieces
   - Then render frame selection
   - Then wire-up

2. **Agent allocation**:
   - Each asm function with > 100 LOC → 1 dedicated agent
   - Each smaller logical chunk → 1 agent with multiple chunks
   - User confirmed unlimited agent usage acceptable

3. **Compile checkpoint protocol**:
   - After each agent's port lands, run `dotnet build` immediately
   - Stub anything that doesn't compile (audio, comments, menu callbacks)
   - Stubs throw `NotImplementedException` so they're visible at runtime
   - Goal: every commit compiles

4. **Fidelity harness**:
   - Already built (`tests/OpenSwos.Tests/`)
   - 10169 enter→exit pairs already match swos-port
   - Extend CSV trace as more functions are ported
   - Re-run after each major port to catch regressions

5. **Wire-up plan**:
   - Once `updatePlayers` main loop is ported + ALL its callees stubbed-or-ported
   - Modify Main.cs `_PhysicsProcess` to branch:
     - `_useSwosPort=false`: BallSim/KeeperSim path (Old Sim, unchanged)
     - `_useSwosPort=true`: sync state to Memory, call SwosVm pipeline (updatePlayers + updateBall + checkIfBallOutOfPlay), sync back
   - Old Sim path = perfect fallback during port debugging

### Status when B-FULL starts (resume reference)

Last backup: `.backups/20260524_232747_snapshot.7z`

**Working state**:
- Build OK (`dotnet build game/OpenSwos.csproj`)
- Headless smoke OK
- Test harness 6/7 pass (1 expected GameState mismatch — Section 8 not ported)
- Old Sim playable end-to-end via toggle OFF
- SWOS port toggle = inert (B4.7 reverted as wire-up unworkable without
  BallSim split, which we're now avoiding entirely in favor of full port)

**Recent piecemeal patches kept** (no point reverting Old Sim):
- Main.cs:786 — `_ball.Z = 5` for keeper Claimed state (user pain #2 — keeper
  hand height visualisation)
- Main.cs:1207 — keeper Claimed sprite uses Standing pose (was Fallen)
- KeeperSim.ClaimAutoReleaseTicks = 90 (was 150)
- BallSim dribble loss probability (25% turn / 3% per-tick)

These stay because they help Old Sim approximate SWOS while we build the full
port. They will become **irrelevant** once SWOS port path is wired and toggle
goes ON (Old Sim becomes pure-fallback).

### Sessions plan (rough)

**Session 1 (next session)**: Port updateBall Section 8 (goal detection + post
collision + ball shadow). Heavy use of agents. Get whole `updateBall` compiling
with goal events triggering. ~1500 LOC asm.

**Session 2-3**: Port updatePlayers main loop entry + per-player body skeleton.
~5000-8000 LOC asm.

**Session 4**: Port input → kick handler + AI on-ball decisions.

**Session 5**: Port AI off-ball positioning + set pieces.

**Session 6**: Wire-up: `_useSwosPort=true` → run SwosVm pipeline. First
end-to-end test. Will be broken. Document everything that's broken.

**Session 7+**: Iterative debug using harness + side-by-side with swos-port.

### Mental discipline

Throughout B-FULL: **when user reports a behavior gap**, the only acceptable
response is:
1. Identify the swos-port function that owns that behavior
2. Port it mechanically
3. Wire into SwosVm pipeline
4. Do NOT add a heuristic patch to Old Sim

If port is blocked by un-ported dependency: port the dependency too.
Recursively follow dependencies until pipeline compiles.

### Backup discipline

- Backup before each agent batch (no risk of losing successful ports)
- Backup after each compile-clean checkpoint
- Tag backups clearly so resume after crash is easy

### When does B-FULL end

**Definition of done**: with `_useSwosPort=true` toggled, user can play a full
match end-to-end matching swos-port behavior in:
- Ball physics (motion, bounce, spin)
- Keeper (catch with hands, hold, kick out)
- Dribble (per-player Ball Control variation)
- Goal detection + set pieces
- Match timer + half-time

At that point Old Sim becomes the "OpenSWOS classic" fallback and SWOS port
becomes default. Future work = OpenSWOS-specific improvements layered ON TOP
of the working port (separate phase).
