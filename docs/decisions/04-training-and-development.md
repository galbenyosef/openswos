# 04 — Weekly training and player development

**Date:** 2026-08-26 · **Status:** SHIPPED on BOTH clients
**Engine:** `game/scripts/Competition/Career/TrainingModel.cs`
**Desktop:** DASHBOARD → TRAINING (+ TRAINING REPORT) · **Web:** the TRAINING tab (`/api/training`)


> **Note on the browser client.** Several entries below say "Web: the X tab".
> OpenSWOS has a second, browser-based career front-end that talks to this same
> engine over HTTP. It is **not part of this repository** — the engine and the
> desktop client are. Everything described here is engine-side by design, which
> is exactly why a second client can exist at all: the rules live in
> `game/scripts/Competition/`, and a client only renders them.

---

## Why this exists, and why the plan said not to

`03-career-depth-plan.md` listed **"deep tactics and training schedules"** under *what we
deliberately do NOT build*, with this reasoning:

> In SWOS you execute tactics with the pad, during play, in real time — a training form would be
> a second and worse copy of something the match already does better, and the original had neither.

The user overruled it on 2026-08-26, and the distinction he is drawing is real:

* **Tactics** are indeed executed with the pad. A tactics form would be a worse copy — that half
  of the old ruling still stands and nothing here touches it.
* **Development** is not tactics. It is the one part of a career the manager had *no lever on at
  all*: players grew or declined at the rollover according to age and potential, and nothing the
  manager did between August and May changed it by a single point.

The old ruling is therefore narrowed, not deleted. What is still ruled out is a tactics form.

It still passes the plan's three tests:

1. **Integer state?** Yes — the whole feature is a drill index, an intensity, a list of player ids
   and a report, all on `CareerState`.
2. **One screen?** Yes. Nine drills, three intensities, one squad table.
3. **Skippable?** Yes, completely. Skip every session for twenty seasons and the career runs; the
   squad simply develops at the rate it always did, because everyone not in the session still gets
   the light team session.

---

## What was researched (2026-08-26)

### FIFA 18 career mode

Sources: [GameSpot, "FIFA 18: Career Mode's New Features
Revealed"](https://www.gamespot.com/articles/fifa-18-career-modes-new-features-revealed/1100-6453059/) ·
[FIFPlay, FIFA 18 Career Mode](https://www.fifplay.com/fifa-18-career-mode/) ·
[fifacareermodetips.com, "Should I Train Youth
Players?"](https://fifacareermodetips.com/2018/11/13/train-youth-players/) ·
Operation Sports, "Training and player progression in career mode".

* A **weekly session** in which you assign **specific players** to a drill.
* Drills come in **bronze / silver / gold** difficulty. The harder ones pay more and are harder to
  come out of well; the easy ones can be simulated.
* The session is **graded**, and the grade is what feeds development.
* Growth is driven by the **gap between current ability and potential** (a 55-rated player with 85
  potential grows faster than a 68 with 88), by **age**, and by whether the player also plays.
* **Trained youngsters visibly outgrow untrained ones.** This is the reason the feature is loved,
  and the reason to have it at all.
* A player who **outperforms his ceiling** can have that ceiling raised.

### Championship Manager 01/02

Sources: [fm-gamer.blogspot.com, CM 01/02 training
schedules](http://fm-gamer.blogspot.com/2009/05/championship-manager-0102-training.html) ·
[champman0102.net forums](https://champman0102.net/viewtopic.php?t=973) ·
[cm0102dicas.com training guide](https://cm0102dicas.com/en/cm0102-training-schedule-the-best-routine-for-your-team/).

* Five categories — **FITNESS / TACTICS / SHOOTING / SKILLS / GOALKEEPING**.
* Four intensities — **None / Light / Medium / Intensive**.
* Schedules are **per position**: a striker trains shooting intensively, a keeper trains
  goalkeeping intensively, and a youth gets an even spread across everything.
* Intensity is a genuine trade-off — it is what tires and injures players.

### What we took

FIFA 18's **weekly, hand-picked, graded session**; CM 01/02's **intensity dial and
position-shaped drills**. Both mapped onto SWOS's seven 0..7 skills rather than onto attributes
SWOS does not have — there is no "off the ball" or "teamwork" here to train.

---

## The design

**Nine drills.** Deliberately few: they fit one screen, which is the CM 01/02 bar ("even a
completionist can handle everything in finite time").

| Drill | Trains | Suits | Coach who helps |
|---|---|---|---|
| SHOOTING PRACTICE | Shooting + Finishing | A W | ATTACK |
| FINISHING DRILL | Finishing | A | ATTACK |
| PASSING AND CROSSING | Passing + Control | M W | — |
| DRIBBLING CIRCUIT | Control + Speed | M W A | — |
| SPRINTS AND SHUTTLES | Speed | D M W A | — |
| HEADING AND AERIAL | Heading | D A | DEFENCE |
| DEFENDING AND TACKLING | Tackling + Heading | D M | DEFENCE |
| GOALKEEPING | the keeper's ability (ValueCode) | G | DEFENCE |
| RECOVERY AND FITNESS | condition, and heals a knock | all | — |

**Three intensities** (CM 01/02's ladder, minus "None" — not training is just not running the
session): LIGHT · NORMAL · INTENSE. Development ×0.55 / ×1.00 / ×1.60; condition −6 / +5 / +14;
injury risk 2 / 9 / 28 per thousand before modifiers.

**The group** is at most six players. Everyone else does a light team session: a token amount of
development weighted by position and a point of sharpness, for free. That is what keeps "ignore
training entirely" a small loss rather than a cliff.

**The grade** is one 0..100 score, banded into POOR / OK / GOOD / EXCELLENT. It is written out
line by line in the source rather than folded into one expression, so the next person can see why
a player trained badly:

```
42 base
  + coach quality × 3.4          the coach who actually helps with THIS drill
  + 9 / − 6                      the drill suits his position, or does not
  + headroom × 5.0               how far he still is from his own ceiling
  + age score                    +14 at 18, −15 past 33
  + (condition − 60) × 0.25      a tired player learns nothing
  + (sharpness − 50) × 0.08
  + form × 1.5
  + intensity × 4.0              a harder drill is worth more
  + a deterministic roll of ±14
```

**The reward** goes through the SAME `GrowthCarry` mechanism the season growth uses, so a whole
SWOS skill point lands exactly when the fractional carry crosses 1.0 and nothing double-counts.
The FIFA 18 curve is in the multiplier: `gain × (0.25 + headroom)`, so a raw youngster climbs and
a finished player barely moves.

**Potential is the ceiling**, as everywhere else in the career — with one exception, taken
straight from FIFA 18: an EXCELLENT session by a player aged 26 or under has a 6 % chance of
lifting his ceiling by 0.15. It is the best line the report can print and it is rare on purpose.

**Sharpness** (0..100) is new persistent state per player. Playing a match adds 6; a session adds
4..13; sitting out subtracts 3, down to a floor of 25. At 80+ it is worth +1 SWOS skill in a
match, at 25 or below −1.

---

## Invariants — do not break these

**1. Training must never move the competition RNG.** The session draws from `CareerRng` seeded on
`(season, round, player id)`. If it ever drew from the competition stream, running a session would
change the fixtures that follow it, and a save reloaded before training would play a different
season.

**2. The floor under staleness is load-bearing.** `CareerRecords.StaleFloor` (25) is the bottom of
the sharpness penalty band. Without it every reserve at all 1730 clubs would eventually carry the
−1 skill nudge — a global change to the AI that nobody asked for. With it, a permanently benched,
untrained player settles at exactly one level below his rating and can never spiral further.

**3. One session per round, and skipping it loses it.** There is no backlog to clear, because a
backlog is a chore, and a chore fails the plan's third test.

**4. The report must show the SLOW progress, not only the rare whole step.** A skill point is a
big move and lands every few weeks at best. The first version printed "—" for almost everybody
and read as "training does nothing"; it now prints `SHOOTING 3 42%` — where the carry has got to —
and suppresses the line entirely for a skill that is already at its ceiling, because promising
progress that can never arrive is worse than saying nothing.

---

## What building it uncovered

**The whole first XI of every career had been playing at −2 skill.** The training screen prints a
CON (condition) column for the squad, and the first XI was reading **0** while the bench read
96–99. That is not a training bug: `FatigueModel.RecoverBetweenMatches` recovered a FLAT amount
per rest day, while `MatchFatigueGain` is `5 × (8 − stamina)` per match. For any stamina of 4 or
below the flat rate never catches the gain, so fatigue ratchets to 100 and stays there — and
`FatigueModel.SkillPenalty` gives −2 at 80 or more. Every regular in every career had been
carrying it, invisibly, since fatigue was written.

A flat recovery **cannot** have an equilibrium below the cap; a proportional one always does.
Recovery now also sheds 35 % of the carried fatigue over a week's rest, so a stamina-3
professional settles around 15 fatigue instead of 100. `--competition-test` asserts that no
regular is pinned at the cap.

**And it settles the plan's open question.** "Should fatigue recover between seasons?" — yes, it
has to, now that condition is a number the manager reads before every session.
`FatigueModel.PreSeason` divides carried fatigue by five at the rollover: everybody reports back
fit, but a player who was run into the ground still starts a little behind.

**The academy had gone permanently silent.** `RegenModel` produced one youth for a club at or
below the 16-man target and *none* for a club at the 18-man cap, so once squads settled — about
three seasons — no club ever saw another academy player. Youth intake day (feature #6) would have
said "the academy produced nobody" every summer for the rest of the career. Every club now
produces at least one, and a full squad makes room by releasing its weakest senior (never one of
this summer's arrivals, or the youth would arrive and leave in the same breath).

---

## Balance not yet settled

With the fatigue equilibrium fixed, a **simulated** fixture now tires a mid-stamina player less
than a week's rest restores, so condition sits at 90–100 for most AI squads. That is the right
side of the old bug — nobody is pinned at zero any more — but whether simulated fixtures *should*
cost more is a balance question that has been measured and deliberately not tuned. The real
in-match drain metric (used for the human's own club) is unaffected either way.
