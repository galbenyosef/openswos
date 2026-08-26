# 03 — Career depth plan (research + roadmap)

**Date:** 2026-08-21 · **Status:** Tier 1 COMPLETE (#1-#5) and Tier 2 COMPLETE (#6-#8, #10)
as of 2026-08-26, plus a WEEKLY TRAINING system that was not in this plan at all
(`04-training-and-development.md`). Remaining: #9 derbies, and Tier 3.
**Rendered version (Polish):** https://claude.ai/code/artifact/59f9ba64-a281-4cf4-8ad5-a53c1fca6e67
Its HTML source is not in the repo — recover it with the Artifact tool's `read` action on
that URL before editing, then republish to the SAME url so the link keeps working.

Applies to BOTH front-ends. Everything here is engine-side (`game/scripts/Competition/`),
so the desktop menu and the browser client get each feature from the same code — the

> **Note on the browser client.** Several entries below say "Web: the X tab".
> OpenSWOS has a second, browser-based career front-end that talks to this same
> engine over HTTP. It is **not part of this repository** — the engine and the
> desktop client are. Everything described here is engine-side by design, which
> is exactly why a second client can exist at all: the rules live in
> `game/scripts/Competition/`, and a client only renders them.

thin-client rule from `02-match-streaming-and-multiplayer.md` still holds.

---

## The finding that shapes this plan

**The original SWOS career already had a manager-progression layer we never ported.**
Strings recovered directly from `external/original-amiga-swos/original-amiga-swos.asm`:

```
'WELL DONE %a, YOU DID AN EXCELLENT JOB'
'A GOOD SEASON %a, WE ARE ALL VERY'
'AN UP AND DOWN SEASON %a, WE ALL HOPE YOU'
'NOT A VERY GOOD SEASON %a, WE HOPE YOU CAN'
'A VERY DISAPPOINTING SEASON %a, WE DEMAND' / 'MUCH BETTER FROM YOU NEXT SEASON'
'RENEW YOUR CONTRACT FOR NEXT SEASON - GOOD LUCK'
'3 MATCHES TO TURN THIS CLUB AROUND OR YOU WILL BE SACKED'
"CLEAR THE OVERDRAFT IMMEDIATELY OR YOU'RE SACKED"
'WE WILL HAVE TO RECONSIDER YOUR POSITION AS MANAGER'
"DECIDED TO RELIEVE YOU OF YOUR DUTIES - YOU'RE SACKED"     '- SACKED -' '- RESIGNED -' '- RETIRED -'
'WE HERE AT %a WOULD LIKE TO OFFER YOU THE' / 'COACHING JOB AT OUR CLUB FOR NEXT SEASON'
'JOB OFFERS' 'NO JOB OFFERS' 'JOB OFFERS BEING CONSIDERED' 'JOB OFFER FROM %a WITHDRAWN' 'NEW JOB'
'INTERNATIONAL JOB OFFER FROM %a' 'AS COACH OF THE NATIONAL TEAM' 'AN ANNUALLY REVIEWABLE CONTRACT'
'ADD INTERNATIONAL PLAYER (HOME)' 'ADD INTERNATIONAL PLAYER (ABROAD)' 'SELECT %0 MORE INTERNATIONAL SQUAD PLAYERS'
'BALANCE AT START OF SEASON:' 'ADDITIONAL INVESTMENT FROM THE CHAIRMAN:' 'SEASON FROM SENSISOFT PLC:'
'ANY FURTHER PLAYER PURCHASES THIS SEASON' / '... SALES THIS SEASON'   (board transfer embargo)
"SEASON'S TOP SCORER" / "SEASON'S TOP SCORERS"
'%a EXCHANGE OFFER FOR %b'  (part-exchange transfers)
```

Independently, the research below says these same mechanics — board expectations, moving
between clubs, long-run narrative — are what football-management players name as most
engaging. **Fidelity and fun point the same way**, so Tier 1 is not "improving SWOS",
it is finishing the port.

---

## Research summary

**Loved, and casual-safe**
- *Youth intake day* — repeatedly named one of the best moments in FM; "youth academy save"
  is its own popular play style. (fmprojects.substack.com; SI.com on FM20 Development Centre)
- *Board expectations / getting sacked* — core of FIFA & FM career; adds stakes at zero
  input cost.
- *Journeyman careers* — moving club to club is one of the most-described FM save styles.
- *Player development / wonderkids* — the reason long saves keep going.
- *Save-game storytelling* — CM 01/02 is still played in 2026 as "a world to retreat into";
  retelling seasons is the community's main activity. (TechRadar; leriohub)
- *Fast seasons* — Football Chairman finishes a season in minutes and reviewers name that as
  the addiction; Retro Bowl markets "five minutes or a 20-season dynasty".

**Why people bounce off FM** (all corroborated)
- Depth is the barrier as much as the draw; players quit for lack of TIME, not lack of interest.
  (VideoGamer; CTNet)
- Press conferences / media / team talks are the most-cited fatigue source.
- FM Touch existed precisely to serve "simpler than full FM, deeper than mobile" — and SEGA/SI
  discontinued it. That niche is now unserved, and SWOS sits exactly in it, with an advantage
  FM never had: **you play the match yourself, in two minutes.**
- CM 01/02's stated appeal: no social media, no weekly press conferences, and *even a
  completionist can handle everything in finite time*. **That is our design bar.**

**Browser angle** — Hattrick keeps managers for 10–15 years on async play, no pay-to-win,
and a human-populated world. Relevant only after network Phase 3; noted so it is not lost.

---

## Audit — what we already have

`game/scripts/Competition/` = 1 696 LOC engine + 3 304 LOC career model.

| Area | State | File |
|---|---|---|
| Seasons, league + cup, promotion/relegation | done | `CompetitionEngine.cs` |
| Transfers: offers, escalations, transfer list | done, from swos.asm | `TransferOffers.cs` |
| Scouting, staff, budget | done | `Scouting.cs`, `StaffModel.cs` |
| Ageing, retirement, potential, growth | done | `SeasonProgression.cs`, `GrowthModel.cs` |
| **Youth regeneration each season** | **works, but invisible to the player** | `RegenModel.cs` |
| **Form, fatigue, injuries** | **computed, barely surfaced** | `FormModel.cs`, `FatigueModel.cs` |
| Season finances | **DONE 2026-08-21** — full statement + prize money | `Finance.cs`, `SeasonFinances.cs` |
| National teams | **DONE 2026-08-23** | `NationalJob.cs` |
| Chairman / sacking | **DONE 2026-08-21** | `ChairmanModel.cs` |
| Job offers from other clubs | **DONE 2026-08-22** | `JobMarket.cs` |
| Season chronicle, top scorer, derbies | **absent** | — |

The cheapest wins in this whole document are the two **bold** rows: the machinery runs, the
player just never sees it.

---

## Tier 1 — faithful 1:1 AND loved. Do these first.

**1. Prize money + season balance — DONE 2026-08-21.** Shipped on BOTH clients.
`Career/SeasonFinances.cs` builds the ORIGINAL game's ledger (line items and wording lifted
verbatim from `original-amiga-swos.asm`; the coefficients are ours and say so):

    BALANCE AT START OF SEASON / GATE RECEIPTS / COMPETITION BONUSES + TV RIGHTS /
    SPONSORSHIP FROM SENSISOFT PLC / ADDITIONAL INVESTMENT FROM THE (NEW) CHAIRMAN /
    PLAYER SALES / PLAYER PURCHASES / PLAYER WAGES BILL / TOTAL PROFIT|LOSS / NEW BALANCE

Prize money now discriminates by league place and cup run for EVERY club in the competition,
not just the player's, so the AI economy reacts to results too. A per-club season ledger
(`CareerClub.Season*`) tracks transfers and staff spending, so the sheet reconciles EXACTLY
against the club's real balance; `SeasonAccount.Unreconciled` is asserted to be 0 by
`--competition-test`, which is how an untracked money path gets caught (it caught one during
implementation). Desktop: DASHBOARD -> FINANCES, two pages. Web: FINANCES tab over
`/api/finances`. 29 new i18n keys x 19 languages. Effect on balance: a 20-season career now
ends on ~62M instead of ~98M under the old flat income — a tighter, results-driven economy.

**2. Chairman review, ultimatum, sacking — DONE 2026-08-21.** Shipped on BOTH clients.
`Career/ChairmanModel.cs`. Every memo is the original's wording, recovered in order from the
asm string pool; only the trigger thresholds are ours (they are gathered at the top of the file
and say so). The original runs TWO parallel three-stage ladders, and so do we:

    LEAGUE     note -> final warning -> vote of confidence ("3 MATCHES TO TURN THIS CLUB
               AROUND OR YOU WILL BE SACKED") -> dismissal
    OVERDRAFT  note -> final warning -> crisis board meeting -> dismissal

plus the five-grade end-of-season verdict (EXCELLENT / GOOD / UP AND DOWN / NOT VERY GOOD /
VERY DISAPPOINTING) and the contract-not-renewed memo.

The board judges the season against **where the squad's strength ranks it in its own league**,
so a promoted village side is not sacked for finishing last with the weakest squad. An outright
dismissal needs a catastrophe (roughly a relegated favourite); ordinary failure stacks on a
consecutive-bad-seasons counter instead, because being sacked after one poor season with no
prior letter reads as a bug rather than a verdict. OPTIONS gains **BOARD: PATIENT / NORMAL /
RUTHLESS** (default NORMAL, persisted in settings). Sacking sets `Career.Sacked` and reuses the
existing career-over path; the desktop and the browser both show "SACKED" rather than "RETIRED".
Desktop: DASHBOARD -> CHAIRMAN (flashes "!" like OFFERS) and the memo is PUSHED automatically
after a match or a rollover, opening on the verdict rather than the pleasantry. Web: CHAIRMAN
tab over `/api/chairman`, plus a standing-order chip in the header. 48 new i18n keys x 19
languages.

Two bugs the implementation surfaced and fixed, worth not re-introducing:
* **The verdict must not judge an unplayed season.** An untouched fixture list still yields a
  full table (everyone on zero, ordered by name), so the strongest club "finished last" and the
  chairman sacked a manager who never took charge of a match. `--career-report` hit this
  instantly. There is now an explicit "no league fixtures played" guard.
* **The ladders are MONOTONIC within a season.** Resetting the stage whenever the club escaped
  the drop zone meant a side bobbing in and out received the same opening letter four times and
  never reached the later stages. Escaping now silences the board without rewinding it; the
  separate `LeagueInDanger` / `InOverdraft` flags decide whether the standing order still shows.

**3. Job offers from other clubs — DONE 2026-08-22.** Shipped on BOTH clients.
`Career/JobMarket.cs`. Every letter is the original's wording, recovered in order from the asm
string pool; the reputation model, the club-standing formula, the timing gate and the
withdrawal rule are ours (gathered at the top of the file, which says so).

    DEAR SIR/MADAM
    WE HERE AT %a WOULD LIKE TO OFFER YOU THE
    COACHING JOB AT OUR CLUB FOR NEXT SEASON
    WE CAN OFFER YOU A VERY COMPETITIVE SALARY AND
    WE HOPE TO BE ABLE TO MAKE FUNDS AVAILABLE
    IN THE REGION OF %b FOR IMPROVING THE TEAM
    WE LOOK FORWARD TO HEARING FROM YOU

Reading the pool first paid again: the letter **names a transfer budget**, which the plan above
never mentioned. It is real money — half of what that club actually holds in the career world —
so the promise is one the new job can keep.

**Manager reputation** (0-100, `CareerState.Reputation`) starts as a discounted version of the
club that hired you and then moves with the same season score the chairman judges you on, plus
4 a trophy. Clubs are ranked by **standing** (squad strength first, division second, 8..80) and
only approach a manager whose reputation sits in their band, so the offers are always a
believable step. Suitors arrive once the club is 60 % through its league programme; the sacked
manager gets his the moment the dismissal is filed, from clubs a rung lower, after his standing
takes an 8-point hit. Accepting BOOKS the move — you finish the season where you are, which is
what 'FOR NEXT SEASON' means — and the rollover then rewrites club, nation and division, files
the farewell and the welcome, and writes the move into the management record. Desktop: DASHBOARD
-> JOB OFFERS (flashes "!" while a letter is unread) and, when you have been sacked, a NEW JOB
button on the career-over screen. Web: JOBS tab over `/api/jobs`. 37 new i18n keys x 19 languages.

**This is what makes a sacking survivable.** Before it, being dismissed simply ended the career.

Three things the implementation surfaced, worth not re-introducing:
* **An offer has to survive to the rollover.** The first cut rolled a 6-12 match countdown, so
  most letters lapsed before the only moment a move can actually happen — the player never got
  to answer. The wait is now taken from the remaining league programme, and what kills an offer
  early is the manager's own board turning on him ('JOB OFFER FROM %a WITHDRAWN'), which is
  what that string is for.
* **Last season's letters must lapse AT the rollover**, or a manager could accept a job a year
  late. They are offers to coach the season that has just started.
* **The books close once per season** (`CareerState.SeasonBooksClosed`). A sacked manager who
  then takes a job re-enters `AdvanceCareerSeason` to do it; without the flag the world would
  age and the wages be paid twice.

**4. National-team job — DONE 2026-08-23.** Shipped on BOTH clients.
`Career/NationalJob.cs`. The committee's letter, the acceptance and the two squad screens are
the original's words; the reputation gate, the squad size, the tournament and the annual review
are ours (gathered at the top of the file, which says so).

    THE MEMBERS OF THE FOOTBALLING COMMITEE
    OF %a WOULD LIKE TO OFFER YOU THE JOB
    AS COACH OF THE NATIONAL TEAM
    WE CAN PROMISE YOU AN EXCELLENT SALARY AND
    AN ANNUALLY REVIEWABLE CONTRACT
    WE LOOK FORWARD TO HEARING FROM YOU

The job is held **alongside the club** — 'AN ANNUALLY REVIEWABLE CONTRACT' is a side job, and
the original kept the club menus alive throughout. A committee calls when the manager is WELL
REGARDED **and has actually won something**, and it offers the country he is currently working
in. Squad selection is the original's: the eligible pool is every player in the world carrying
that nationality, split into **ADD INTERNATIONAL PLAYER (HOME)** and **(ABROAD)**, named 16 at
a time, with the original's own 'SELECT %0 MORE INTERNATIONAL SQUAD PLAYERS' counting down.

**The squad IS the team.** Each season the side plays its continental tournament (EUROPEAN
CHAMPIONSHIP / COPA AMERICA / AFRICAN NATIONS CUP / GOLD CUP / ASIAN CUP / OCEANIA CUP) against
the continent's strongest nations, simulated by the same engine the career's AI fixtures use —
and the rating it plays at is the average ability of the 16 the manager named. Winning it is a
trophy in the management record and worth 6 reputation; a first-round exit, or a manager the
game has stopped rating, ends the annually reviewable contract.

Two things the implementation surfaced:
* **The gate had to be measured, not guessed.** Set at 55 first, it made the whole feature
  unreachable: six seasons at JUVENTUS with two trophies never got past 48. It is 48 now — the
  WELL REGARDED band — plus at least one trophy.
* **Picking purely by ability named eleven strikers and one goalkeeper.** AUTO PICK now names a
  balanced 2-5-6-3, which is what a committee would announce.

**5. Season's top scorer** — XS — both — *original* — **DONE 2026-08-24**
`Competition/Career/ScorerModel.cs`. Desktop: DASHBOARD -> TOP SCORERS. Web: the SCORERS tab
(`/api/scorers`). The original's own strings specify the whole feature, and all of it is in:

    asm:283007  SEASON'S TOP SCORER        asm:283027  SEASON'S TOP SCORERS (a tie)
    asm:283048  "%a  %0"                   asm:283055  CAREER TOTAL
    asm:295043  LEADING COMPETITION GOAL SCORERS
    asm:295076  TOP GOAL SCORERS           asm:295104  HIGHEST SCORER LIST
    asm:295659  GOALS   asm:295686  OWN GOALS   asm:295696  EX. PLAYER GOALS

'EX. PLAYER GOALS' is the tell that the original keeps a CLUB list as well as a competition one:
goals scored by a player who has since left still belong to the club that got the points, so
they are folded into one anonymous row rather than deleted. We do that at display time.

The estimate of "XS: scorers are already recorded, sum and display" was wrong in one important
way. **A simulated scoreline has no scorers at all**, and all but the manager's own fixtures are
simulated — so a leaderboard built only from what the result bar records would list one club.
Every simulated goal is therefore attributed to a real member of the scoring club's XI, weighted
by position line and finishing ability, drawn from the SAME competition RNG as the scoreline, so
a save reloads to the identical table. A PLAYED fixture keeps its true scorers.

The weights are ours and were MEASURED, not felt (`--competition-test` STEP 06f prints the
split): a 545-goal season came out 41 % forwards / 26 % wide / 14 % central midfield / 14 %
defence / 3 % own goals. Two things there look wrong and are not — the defence share is spread
over four times as many players (46 different defenders scored against 32 forwards), and a
4-4-2 has four defenders to two central midfielders, so defence out-scoring central midfield in
TOTAL is arithmetic.

**And it found a real gameplay bug.** Defenders were taking 23 % of the goals because the
automatic lineup was fielding **no strikers at all**: `CareerMatchTeam.BuildAuto` sorted the
outfield by position group and took the first ten, and the position group is an enum with A last
(RB=0 ... A=6). Sixteen of sixteen clubs in the test league benched their entire forward line.
The same ordering plugged gaps in a projected lineup, so an injured forward was replaced by a
full-back. Fixed (`PickOutfieldXI` picks a 4-4-2, short lines topped up by ability) — a gameplay
fix, not a cosmetic one.

---

## Tier 2 — cheap, because the machinery already runs

**6. Youth intake day — DONE 2026-08-26.** Shipped on BOTH clients. Desktop: DASHBOARD → YOUTH
INTAKE (flashes "!" the summer it happens). Web: the YOUTH tab (`/api/youth`). The academy's new
players, each with a scouted RANGE rather than a number, and TRAIN / RELEASE against each.

It uncovered a real defect rather than being the "screen over data that already exists" this
line promised. `RegenModel` gave a club at or below the 16-man target ONE youth and a club at
the 18-man cap NONE — so once squads settled, which takes about three seasons, the academy went
permanently silent and the intake screen would have read "the academy produced nobody" every
summer for the rest of the career. Every club now produces at least one, and a full squad makes
room by releasing its weakest senior — never one of this summer's arrivals, or the youth would
arrive and leave in the same breath and nothing would ever change.

**7. Season chronicle — DONE 2026-08-26.** Shipped on BOTH clients as the CLUB DIARY.
`Career/Chronicle.cs`. Desktop: DASHBOARD → CLUB DIARY (filter by season, paged). Web: the DIARY
tab (`/api/chronicle`). Read-only, exactly as this line asked: no unread counter, nothing blocks
a screen, ignoring it for twenty seasons costs nothing.

What it records: signings, sales and free transfers; appearance and goal milestones for the club
(50/100/150… and 25/50/100…); a win or a defeat by three or more; a run of five wins, ten
unbeaten or five without a win; the academy intake; each training session and any breakthrough or
training injury; the chairman's verdict or a sacking; and the season's finish, cup win and top
scorer. A season of a real career writes 25-40 lines.

**Storage rule that keeps it translatable:** an entry stores a TEMPLATE in the original's
placeholder style (`%a`, `%b`, `%0`) plus its arguments — never a finished sentence — so the same
save renders Polish in a Polish client and English in an English one. The list is capped at 600.

**8. Player counters and club legends — DONE 2026-08-26.** Shipped on BOTH clients.
`Career/CareerRecords.cs`. APP and GLS columns in the squad table (both clients), and a CLUB
RECORDS screen / tab of the all-time appearance list, marking who is still at the club.

Three rules the implementation needed:
* an appearance is credited from the SAME XI the match was simulated or played with
  (`CareerMatchTeam.BuildOrder`), for BOTH clubs of EVERY fixture — a counter built only from
  matches the manager watched would describe one club, exactly as the scorer table would have;
* club counters reset LAZILY (`ClubStatsClubId` vs `ClubId`), so a transfer needs no hook at any
  of the four places a club changes hands, and a pre-feature save simply starts counting now;
* legends are kept only for clubs the manager has actually worked at. A row for each of ~29 000
  players across 1730 clubs would grow every save for a screen nobody can open.

**9. Derbies and rivals** — S — both — **NOT STARTED**
One or two flagged rivals per club; the fixture is highlighted, the result weighs more in the
chairman's verdict, and it gets its own chronicle entry. Rivals derived deterministically
(same nation, similar strength) — no hand-authored database. Now that the diary exists this is
cheaper than it was: the rival result already has a place to be written.

**10. Surface form and fatigue — DONE (desktop and web both had it before 2026-08-26).**
The squad table has carried a form column (−3…+3) and a FIT column since the squad screen was
written; the web `PlayerDto` has carried `form` and `fitness` since the browser client shipped.
This entry was simply stale — worth knowing, because it was the headline of the "reorder by
visibility" proposal and it turned out to be already done.

What was NOT true until 2026-08-26 is that either number reached the pitch:
`FormModel.FormSkillDelta` had no caller outside a test. Form and sharpness now both apply, in
`CareerMatchTeam.ToPlayerRecord`, capped at one SWOS level either way.

---

## Tier 3 — bigger, later

| # | Item | Why | Weight | Where |
|---|---|---|---|---|
| 11 | Part-exchange transfers *(original)* | `%a EXCHANGE OFFER FOR %b` exists in the asm; deepens negotiation without new screens | M | both |
| 12 | Morale as ONE number | Driven by minutes + results; read-only plus one action. **No inbox, no player conversations** — that is where FM fatigue starts | M | both |
| ~~13~~ | ~~Manager reputation~~ | **DONE 2026-08-22** — landed inside #3 (`JobMarket.Reputation`, 5..100, moved by the chairman's own season score plus silverware) and #4 reads it as its gate | S | both |
| 14 | European competition | League → continental qualification. SWOS's pride was worldwide depth | L | both |
| 15 | Spectate a rival's fixture | Streaming already works; watching the leaders drop points is browser-native | S | WEB |
| 16 | Shareable season report | Table + chronicle + record on a stable link; directly serves save-storytelling | S | WEB |
| 17 | Async league | Hattrick model, one round per real day, human managers. Only after network Phase 3 | XL | WEB |

---

## What we deliberately do NOT build

**One principle does not cover all of these** — an earlier draft of this section claimed
everything below followed from "the match is played by hand", which is only true of the first
item. The real reasons differ, and each has to stand on its own:

**Press conferences, media, team talks.** THIS is where the manual match matters. A SWOS season
is roughly 34 matches of about two minutes: a bit over an hour of football. A ritual before and
after each one doubles the season and puts the cost in the one place the game cannot afford it —
between the player and kick-off. It is also the most-cited FM fatigue source; FM Touch existed
largely to delete it.

**Deep tactics.** Not a time argument. In SWOS you execute tactics with the pad, during play, in
real time — a tactics form would be a second and worse copy of something the match already does
better, and the original had neither. This still stands.

**~~Training schedules~~ — OVERRULED BY THE USER, 2026-08-26.** This clause used to read "deep
tactics AND TRAINING SCHEDULES", on the reasoning above. The user overruled it, and the
distinction he drew is right: tactics are executed with the pad, but DEVELOPMENT is not tactics —
it was the one part of a career the manager had no lever on at all. A weekly training session
shipped the same day; the full research, design and invariants are in
`04-training-and-development.md`. It passes all three tests below, including the third: skip every
session for twenty seasons and the career still runs.

**Contract negotiation, agents, appearance bonuses.** These have nothing to do with playing by
hand; the connection was made up. Three real reasons: the original has **no contracts at all**
(a player carries a flat price), it is the one kind of admin that **cannot be skipped** — ignore
expiries and the squad evaporates, so it fails test 3 below — and it is a great many clicks for
very few genuine decisions (pay him or lose him, several times a season, for ever).

**An inbox.** A UX argument, not a fidelity or a time one: a chronicle is read when the player
feels like it, an inbox demands attention and turns an unread counter into a chore.

Note what this does **not** rule out. Nothing here is forbidden for ever, and anything on this
list could still arrive as an OPTIONS toggle that is off by default, the way engine-quirk fixes
do. What is ruled out is making any of it **compulsory**.

## Not in the plan — needs a decision (raised 2026-08-25)

The user asked for a straight comparison with CM 01/02 and it exposed a third camp: features
that are neither built, nor planned, nor deliberately rejected — nobody has ruled on them. They
are listed here so the gap is visible instead of implicit. **None of these should be started
without the user picking them.**

| Feature | Note |
|---|---|
| Loans | The original has no loan system; CM's is a big part of squad building. |
| Suspensions | Cards exist IN the match but do not accumulate over a season and nobody is banned. |
| Transfer deadline day | Today the window is a per-season negotiation budget, not a date. |
| Player search with filters | The market is browsable but not queryable (age, position, price). |
| Asking the board for budget | The chairman talks TO you; you cannot ask him for anything. |
| Awards | Player of the month / team of the season. Cheap now that scorers are tracked. |
| Stats beyond goals | Assists, average rating, clean sheets. Needs per-match capture. |
| Assistant manager advice | A recommended XI. Fits the "skippable" test easily. |
| Squad status / rotation | Who counts as a starter; CM's squad-status contract. |
| Stadium, expansion, sponsors | A second money loop next to the season statement. |
| Work permits / foreign limits | Real 96/97 rules, and a genuine constraint on transfers. |
| Tutoring / mentoring | Veterans developing youngsters. Sits naturally on `GrowthModel`. |

The user's own framing of why this matters: after five Tier 1 features he said the changes were
hard to see. Three of the five are always visible; two are EARNED and so invisible for years.
Weigh VISIBILITY when picking what comes next — see the reordering proposal in RESUME-HERE.

## What the 2026-08-26 research added

Searched again for what the CM 01/02 community actually values (TechRadar, "Why people are still
playing Championship Manager: Season 01/02 two decades later"; leriohub's 2025/2026 guides;
fmprojects.substack.com; champman0102.net). It CONFIRMED the existing finding rather than
changing it — simplicity, no press conferences, "you think there isn't much to the game and then
you are addicted" — but it surfaced one thing this plan had never considered:

**The community's main activity is DATA, not play.** CM 01/02 is kept alive by annually updated
databases (the 2025/26 update slipped from October to December 2025 and people waited for it).
The equivalent for OpenSWOS would be **editable, shareable team/player data packs**: the squads
are already loaded from TEAM.* files, so a documented, exportable, importable data format would
let the community do to OpenSWOS what it has done to CM 01/02 for twenty-five years. That is a
tooling feature, not a career feature, and it is not in any tier — it is recorded here so the
option exists.

### Three tests every new feature must pass
1. **Expressible as integer state** on `CareerWorld` / `CareerState`? If not it will not save
   deterministically and will not survive the thin web client.
2. **Fits on one screen?** The CM 01/02 bar: a completionist still finishes in finite time.
3. **Skippable?** Ignore scouting, staff and youth and the season still completes.

Test 3 does most of the work above, and it is the one that actually separates what has been
built from what has not: the chairman costs zero clicks, a job offer is one decision a season,
and both can be ignored entirely — expiring contracts cannot.

---

## What playing a career actually found (2026-08-23)

Before starting #4 the user asked for a season to be **played**, not reasoned about — the career
is a menu, so an agent can play it through the web client's own JSON API exactly as a person
plays it through the page. Four real defects came out of two careers (WIDZEW LODZ in Poland,
JUVENTUS in Italy), none of which any test was ever going to catch:

1. **The board's expectation always favoured the player's own club.** TeamRef.Strength is a
   1..7 average, so in a modest league half the clubs tie on it, and the tie-break fell back to
   the Teams index — where `CareerFactory` always puts the managed club FIRST. A manager tied
   for the strongest squad was permanently "expected to win the league": 3rd of 16 with a weak
   Widzew side scored **-2 and COST reputation**, so a career in a level league could never
   build one at all. Ranked by live squad ability now, tie-broken club-blind.
2. **The expectation was taken from the END-of-season squad.** Sign a striker in August and the
   bar moved retroactively; sell one in the last week and it dropped. It is now fixed when the
   season's first match is played, and BOTH clients show it while there is still time to clear
   it ("THE BOARD EXPECTS 4TH OF 16").
3. **18 national squads were on sale in the club transfer market** — you could sign ROBERTO
   BAGGIO from ITALY. The 80..85 continental files are not the only place SWOS stores national
   sides: TEAM.074 holds the 24 USA '94 squads and TEAM.068 another set, under nation bytes
   that say nothing. The roll is now taken from the data (the names in 80..85), which found all
   of them; a save written earlier is repaired on load.
4. **Simulated results ignored the career world entirely.** `SimulateResult` read the 1996
   strength snapshot, so transfers, ageing, growth, retirements and youth intakes changed
   nothing: season 2 at Widzew played out to an identical table whether or not a 9.4M striker
   had been signed. Now `CompetitionEngine.LiveStrength` reads the actual squad — the same A/B
   is 9th/39pts without the signing and 5th/43pts with it.

The fourth is the big one: it is most of what a career *is*, and it had been invisible since
the career mode was written.

---

## Order of work

1. ~~Prize money (`Finance.cs` TODO)~~ — **DONE 2026-08-21**, see Tier 1 #1 above.
2. ~~Season objective + chairman verdict~~ — **DONE 2026-08-21**, see Tier 1 #2 above.
3. ~~Job offers~~ — **DONE 2026-08-22**, see Tier 1 #3 above.
4. ~~Top scorer~~ — **DONE 2026-08-24**, see Tier 1 #5 above. NOT the "two screens over data
   that already exists" this line assumed: simulated fixtures have no scorers, so the goals had
   to be attributed. It also uncovered the strikerless auto lineup.
5. ~~Youth intake day~~ — **DONE 2026-08-26**, see Tier 2 #6.
6. ~~Season chronicle~~ — **DONE 2026-08-26**, see Tier 2 #7 (shipped as the CLUB DIARY).
7. ~~National team~~ — **DONE 2026-08-23**, see Tier 1 #4 above.
8. ~~Player counters and club legends~~ — **DONE 2026-08-26**, see Tier 2 #8.
9. ~~Weekly training~~ — **DONE 2026-08-26**, not from this plan at all: a user directive.
   `04-training-and-development.md`.

**Where the work stands after 2026-08-26.** Tier 1 and Tier 2 are complete except #9 (derbies).
The open candidates are, in rough order of visible value per unit of work: **#9 derbies and
rivals** (small, and the diary now gives a rival result somewhere to live), **#12 morale as one
number**, **#11 part-exchange transfers** (the original's own `%a EXCHANGE OFFER FOR %b`), and
then the big one, **#14 European competition**. Anything from "Not in the plan — needs a
decision" still needs the user to pick it.

**Execution rules**
- Engine only, never JavaScript. Rules land in `Competition/`; both clients inherit them.
- Deterministic: `CareerRng` only, never `System.Random` — otherwise saves and the future
  network mode diverge.
- Test after every step: `--competition-test` and `--career-report` (both headless, fast).
- **Anything that takes time must say so.** A career save is the whole world (~29 000 players)
  and a season rollover simulates 1730 clubs; both were freezing the client silently until
  2026-08-24. Desktop work goes through `MenuClient.RunBusy` (a PLEASE WAIT screen, deferred two
  frames so Godot can actually paint it); the web page raises a busy overlay for any request
  that outlives 200 ms. Measure before optimising — half the save cost was JSON indentation.

**~~Open decision: should fatigue recover between seasons?~~ SETTLED 2026-08-26: YES.**
`FatigueModel.PreSeason` divides carried fatigue by five at the rollover. It had to be settled
because the training screen prints condition for the whole squad, and building it exposed
something worse than the open question: recovery between MATCHES was flat, so it could never
catch a big enough per-match gain, and the entire first XI of every career had been pinned at 100
fatigue — i.e. playing at `FatigueModel.SkillPenalty`'s worst tier, −2 skill, for whole careers.
Recovery now also sheds 35 % of carried fatigue over a week. Full write-up in
`04-training-and-development.md`.

**~~Open decision (2026-08-25): reorder by VISIBILITY?~~ ANSWERED BY DOING IT (2026-08-26):
everything on the shortlist below is now shipped, and #10 turned out to have been done all
along.** The order above is by cost and by dependency. The user's reaction to finished Tier 1 was that the changes are hard to see, which
is fair: three of the five features are always visible, two are EARNED and therefore invisible
until a career has run for years, and the rest was depth with no screen. The alternative order
put to him, cheapest-visible first:

1. **#10 form + energy in the SQUAD screen** — the smallest job in the whole plan. Both values
   ALREADY drive the match; the player just cannot see them, so he picks a lineup blind. Highest
   visibility per unit of work by a distance.
2. **#8 appearance and goal counters** — turns an anonymous striker into *your* striker on the
   screen the player looks at most.
3. **#6 youth intake day** — one memorable moment a season.
4. **#14 European competition** — the largest visible change in the plan, and the most work.

Not started: waiting on the user.
