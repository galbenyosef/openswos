# Porting plan — switching from vibe-coded mechanics to ported SWOS algorithms

**Started: 2026-05-13. Strategy pivot 2026-05-19:** primary reference is now a
**running local build** of `external/swos-port/` (see
`docs/reference/swos-port-build.md`) plus its C++ source under
`external/swos-port/src/game/`. We observe behaviour live in swos-port, read
the relevant C++/asm, paraphrase into our C# under `game/scripts/Sim/`, cite
`file:line` in code comments.

Up to 2026-05-13 the gameplay layer was hand-tuned on intuition, breaking the
project's stated goal of **1:1 fidelity**. 2026-05-19 audit reverted the last
intentional drifts (kick power × 0.75 → SWOS-authentic 2208/2688/2560,
SlidePunchPower 1920 → kPlayerTacklingSpeed 1792).

## Sources (ordered by usefulness)

1. **`zlatkok/swos-port`** — C++ "port" of SWOS, **no licence** (see CLAUDE.md
   correction 2026-05-16; was wrongly noted as MIT earlier). Easiest read, already
   one layer of interpretation removed from assembly. **Primary source.**
2. **`starwindz/original-amiga-swos`** — 68k assembly decomp of the Amiga binary.
   Authoritative for raw constants and edge cases. **Verification source.**
3. **`zlatkok/swospp`** — SWOS++ binary patcher with working netcode. **Reference for
   netplay layer**, not gameplay.
4. **Ghidra + `SWS.EXE`** — DOS/4GW LE binary. Last resort if (1) and (2) leave gaps.

License rule (from `CLAUDE.md`): we **paraphrase algorithms and copy constants**,
we do **not** copy code verbatim. Comments in our C# should cite the source file +
line in `external/` so a reviewer can trace the lineage.

## What we keep from the current scaffold

- Asset pipeline (ADF, RNC v1/v2, TEAM.\*, sprite atlas, pitch tile-map).
- Deterministic Q24.8 `Fixed` math throughout the sim — fundament for lockstep netcode.
- Scene/menu/UI architecture and Godot wiring.
- Match flow state-machine skeleton (PreKickoff/Play/PostGoal/HalfTime/FullTime).
  Timings may change after the port; the skeleton stays.

## What we throw out / replace

- **`BallSim` constants**: friction (242/256), KickPower (3072), MinKickPower (1500),
  KickLoft (1024), Gravity (48), DribbleOffset (10), InteractionRadius (14),
  MaxKickHoldTicks (30), SlidePunchPower (2000). All guessed.
- **`PlayerSim` constants**: WalkSpeed (1024), SlideSpeedNum (384),
  SlideDurationTicks (12), animation phase pacing. Guessed.
- **`AiSim` v2** — possession-kick / chase / defensive-position / dribble-slide
  heuristics. SWOS uses role-per-player + tactic-based base positions + per-tick
  decisions. Full replacement.
- **Goalkeeper** — current `always-Kick yellow blob` is a placeholder. SWOS keeper
  has explicit dive / catch / throw-out / kick-out states and its own kit
  (`AwayKit` in TeamRecord, OR a goalkeeper-specific field — TBD during port).
- **`MatchTimings.HalfDurationTicks` = 750** — SWOS half is 4 min (≈ 3000 ticks
  @ 25 Hz). All other phase durations to verify.
- **Power kick** with visual bar — SWOS doesn't have a hold-for-power mechanic.
  Kick power comes from direction modifier + button combo. Replace.

## What stays for now (developer-only)

- Demo mode (AI vs AI autoplay) — useful for AI tuning.
- Match-length presets in menu — keep for testing; the SWOS default (4 min) will
  be one of the presets.
- Pause menu (ESC) — not in original SWOS but harmless and useful in dev.

## Order of porting (one module per session)

Each step lives in its own `docs/porting/<module>.md` mapping doc and bumps a
single sub-system at a time. **Don't port everything at once** — concurrent
changes make it impossible to isolate "this constant feels wrong" feedback.

| # | Module                | Effort  | Status      | File                              |
|---|-----------------------|---------|-------------|-----------------------------------|
| 1 | Ball physics          | small   | **ported**  | `docs/porting/ball-physics.md`    |
| 2 | Player physics        | small   | **ported**  | `docs/porting/player-physics.md`  |
| 3 | Match timings + flow  | small   | **partial** | `docs/porting/match-flow.md`      |
| 4 | Set-pieces            | medium  | **partial** | `docs/porting/set-pieces.md`      |
| 4b | Kick types (Pass / Shot / HighShot) | small | **ported** | `docs/porting/kick-types.md` |
| 5 | AI (role + tactics)   | **big** | not started | _TBD_                             |
| 6 | Goalkeeper system     | medium  | **partial** | `docs/porting/goalkeeper.md`      |
| 7 | Sprite anchors        | medium  | **ported**  | `docs/porting/sprite-anchors.md`  |
| 8 | Audio (RJP1 + WAVs)   | medium  | not started | _TBD_                             |

Number "small" / "medium" / "big" is rough effort estimate per session, assuming
the relevant files in `external/` are already located.

## Repository layout for porting

```
external/
├── swos-port/                    ← cloned, gitignored, read-only
├── original-amiga-swos/          ← cloned, gitignored, read-only
└── (others as needed)

docs/porting/
├── README.md                     ← this file
├── ball-physics.md               ← per-module mapping doc + status
├── player-physics.md
├── ...
```

Each per-module doc is a table: **our-name | swos-port file:line | starwindz
address | extracted value | comment**, followed by a "status" note (not started /
in progress / ported / verified).

## What "ported" means

A module is **ported** when:
- Our constants come from `external/` (cited in code comments).
- Our algorithm reads like a paraphrase of the source (not verbatim).
- A side-by-side play test (our game vs. `swos-port` if it builds locally, or our
  game vs. WinUAE running the Amiga ADF) doesn't show obvious behavioural
  differences in that module.

When in doubt, copy the constant exactly and paraphrase the algorithm. Save the
"could we tune this?" question for after 1:1 fidelity is achieved.
