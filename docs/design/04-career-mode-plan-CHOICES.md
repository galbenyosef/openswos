# Career mode — GENERAL plan + YOUR CHOICES

> How to use this file:
> - Each feature has 2-3 OPTIONS (A/B/C), a **REKOMENDACJA**, and trade-offs.
> - Fill in the `>> WYBOR:` line (write A / B / C / NIE / własne).
> - Write anything you want in `>> KOMENTARZ:` (I read it back after you save).
> - You do NOT need to fill everything — blanks = "use rekomendacja".
> - This is the GENERAL plan. After you decide, I write the DETAILED plan only
>   for what you picked. Nothing gets implemented until then.

Guard-rails (fixed, not up for vote):
- New per-player career data (age, potential, growth, fatigue...) lives in the
  CAREER SAVE, never in the read-only TEAM.* files.
- Deterministic xorshift32 RNG for everything random (regen, scouting, growth) so
  saves stay reproducible + netplay-safe.
- Each system OPTIONAL where sensible; default tasteful; original SWOS feel first.
- No "coming soon" stubs — I only plan what we actually build.

Base we already have: multi-season league + domestic cup, promotion/relegation,
manager name/trophies/record, RETIRE, season rollover, AI results strength-weighted.
Skills are 0..7 per player (7 attributes: passing, shooting, heading, tackling,
control, speed, finishing... in SWOS terms), plus position, value, face.

===============================================================================
## 0. OVERALL SCOPE — how big is this?
===============================================================================
Pick the ambition level; it frames every choice below.

- **A) SWOS+ (light):** age + retirement + youth regen + simple growth. Feels like
  SWOS with time passing. ~1-2 build sessions.
- **B) Manager-lite (RECOMMENDED):** everything in A + hidden potential + coaches/
  training focus + scouting + in-match fatigue. The "hybrid" you described,
  still no menu-bloat. ~3-5 sessions.
- **C) Deep manager:** everything in B + finances/wages/budgets + morale/form +
  contracts + board expectations. Closer to FM. Risk: buries SWOS simplicity.

>> WYBOR: B
>> KOMENTARZ: 

===============================================================================
## 1. PLAYER AGE
===============================================================================
Every player needs an age to drive growth/decline/retirement.

- **A) Derive on career start from existing data:** guess age from value/skill
  (stars = probably older/prime). No new source data needed. Cheap, but fuzzy —
  a cheap 90-skill youngster and an expensive veteran can look identical.
- **B) Random age band per skill tier (RECOMMENDED):** seed each player an age
  from a deterministic distribution (e.g. weak/cheap players lean 17-23 OR 30+,
  stars lean 24-30). Reproducible via RNG seed. Realistic spread, no real data.
- **C) Hand-authored ages:** author real ages for famous teams. Most accurate,
  huge manual effort for 1616+ teams. Not worth it.

Sub-choice — age range: youth 16-18, prime ~24-29, decline 30+, retire ~33-38.
>> WYBOR: A
>> KOMENTARZ: mozesz latwo znalezc jaki wiek maja gracze w sezonie 96/97, przynajmniej ci bardziej znani.
gdyby bylo jakies api to byloby idealnie, ale pewnie dobre api sa platne. zrob research w tej kwestii.
chcielibysmy tez z api sciagac aktualnych zawodnikow

===============================================================================
## 2. HIDDEN POTENTIAL
===============================================================================
A ceiling a player can grow toward. Drives who's worth training/scouting.

- **A) No potential — growth is pure age curve (simplest):** everyone follows the
  same rise-peak-decline shape scaled by current skill. No "gems". Loses the
  scouting fantasy.
- **B) Hidden numeric potential, never shown (RECOMMENDED):** each player has a
  secret max-overall. Growth approaches it. You only sense it via scouting hints
  ("looks promising"). Classic FM feel, cheap to store (1 byte/player in save).
- **C) Potential + visible star rating after scouting:** like B but scouting
  reveals an estimated potential-stars. More UI, more hand-holding, less mystery.

>> WYBOR: C
>> KOMENTARZ: ale to nie moze byc ze na 100% wiemy jaki ma potencjal. to zalezy od jakosci trenera co go znalazl i troche szczescia 

===============================================================================
## 3. PLAYER DEVELOPMENT / GROWTH
===============================================================================
How skills change season to season (and maybe mid-season with training).

- **A) Season-tick only (RECOMMENDED to start):** once per season rollover, each
  player's skills nudge toward (age curve x potential). Young+high-potential rise,
  old decline. Simple, deterministic, easy to reason about.
- **B) Season-tick + training investment:** as A, but coach/training focus (see #4)
  accelerates chosen players. Ties growth to your decisions. Needs #4.
- **C) Continuous (per-match) growth:** players gain from minutes played. Most
  organic, most complex, hardest to balance. Probably overkill.

Which skills grow: (i) overall only, redistributed; (ii) individual attributes
drift; (iii) attributes grow weighted by position. 
>> WYBOR (A/B/C): C
>> WYBOR (i/ii/iii): iii 
>> KOMENTARZ: we need to implement floats or doubles to handle small scale more accurately

===============================================================================
## 4. COACHES + TRAINING FOCUS
===============================================================================
Hire coaches; point training at promising players.

- **A) No coaches — auto training:** growth from #3 happens automatically. Zero UI.
- **B) Training slots, no hired staff (RECOMMENDED for hybrid):** you pick N players
  each season to "focus train"; they get a growth bonus (bigger if young/high-pot).
  One simple screen. No staff economy.
- **C) Hire coaches (staff economy):** coaches cost money, have quality ratings,
  add training capacity / bonus. Needs finances (#8). Most manager-like, most bloat.

>> WYBOR: C
>> KOMENTARZ: 

===============================================================================
## 5. IN-MATCH FATIGUE
===============================================================================
Stamina drains during a match, slightly lowering stats (ties to stamina memory).

- **A) None (keep original SWOS):** no fatigue. Purest 1:1.
- **B) Fatigue as an OPTIONAL toggle, default OFF (RECOMMENDED):** stamina per
  player scaled by skill; drains with match time + distance run (with/without
  ball); slightly lowers speed/accuracy when low. OpenTTD-style optional fix.
  Preserves original feel by default. Deterministic (integer).
- **C) Fatigue always on in career only:** career matches use fatigue, friendlies
  don't. Couples career to sim; less clean toggle story.

Carry-over: does tiredness persist between matches (needs rest/rotation) or reset
each match? (persist = rotation matters, more depth)
>> WYBOR (A/B/C): B
>> WYBOR (persist / reset): persist
>> KOMENTARZ: tiredness is reduced as time goes by. faster for younger players and those with higher stamina (added new stat)

===============================================================================
## 6. AGEING PER SEASON + RETIREMENT
===============================================================================
- **A) Age +1 each season, retire at hard age (RECOMMENDED, simple):** at rollover
  everyone +1 yr; anyone past a threshold (e.g. 35-38, some RNG) retires and leaves
  a squad hole to fill via regen/transfers.
- **B) Age +1 + decline-driven retirement:** retire when skill decays below a floor
  OR age high. More organic (a great 37-yo lingers). Slightly more logic.
- **C) A/B + retirement only affects YOUR club's visible squads:** to save compute,
  only age/retire teams the player interacts with; others static. Cheaper at scale
  (1616 teams), but the world feels less alive.

>> WYBOR: b
>> KOMENTARZ: 

===============================================================================
## 7. NEW-PLAYER GENERATION (YOUTH / REGEN) + SCOUTING
===============================================================================
Where fresh players come from as veterans retire, and how you find gems.

Generation:
- **A) Youth intake per season (RECOMMENDED):** each club gets N generated youths
  at rollover (deterministic names/nationality/skill/potential from RNG). Fills
  retirement holes; keeps team counts stable.
- **B) Global free-agent pool:** regens go into a pool you sign from, not auto-added
  to clubs. Needs transfers (#199). More manager-like.
- **C) Both:** clubs get some, pool gets some.

Scouting:
- **A) No scouting — youths visible immediately.** Simplest.
- **B) Scouting reveals hidden potential gradually (RECOMMENDED):** spend scouting
  (time/slots/money depending on #0) to uncover a youngster's potential hint before
  committing. The "find talents" fantasy.
- **C) Scouting network with regions/cost:** FM-style. Needs finances. Bloat risk.

>> WYBOR generacja (A/B/C): c
>> WYBOR scouting (A/B/C): c
>> KOMENTARZ: piuszesz jakby transfers to byulo cos nie obslugiwanego przez swos career a przeciez tam byly transfwery

===============================================================================
## 8. FINANCES / TRANSFERS  (optional, ties to issue #199)
===============================================================================
SWOS already had a transfer market + player prices. Do we modernise?

- **A) Keep SWOS transfers as-is (buy/sell by price), no budgets.** Least work.
- **B) Add a season budget + wages-lite (RECOMMENDED if scope B/C):** you get a
  transfer/wage budget; buying/keeping players spends it; success/prize money
  refills. Enables coaches (#4C) + scouting (#7C).
- **C) Full finances:** ticket income, board expectations, sponsor, wage
  negotiations. Full FM. Almost certainly too much for now.

>> WYBOR: B
>> KOMENTARZ: 

===============================================================================
## 9. FORM / MORALE  (optional flavour)
===============================================================================
- **A) None (RECOMMENDED for v1):** skip; add later if wanted.
- **B) Simple form:** recent results/minutes give a small temporary skill +/-.
- **C) Form + morale + team chemistry:** more systems, more menus.

>> WYBOR: B
>> KOMENTARZ: uzywaj strzalek w roge, ukos bok, ukos dol

===============================================================================
## 10. UI / PRESENTATION
===============================================================================
How much new screen real-estate.

- **A) Reuse existing career dashboard + team-setup, add a couple columns
  (age, a growth arrow) (RECOMMENDED for v1).** Minimal, SWOS-styled.
- **B) New "Squad / Development" screen:** dedicated page for ages, training focus,
  potential hints, retirements. Cleaner, more work.
- **C) Full manager hub:** office screen with staff, scouting, finances tabs.

>> WYBOR: B
>> KOMENTARZ: ale testuj to wizualnie do cholery. menu jest rozwalone troche - niech to wyglada estetycznie. mozesz kopiowac nowoczesne metody rysowania z CSS zeby byl modern look. zwieksz tez rozdzialke a nie overscan lores z amigi

===============================================================================
## PROPOSED BUILD ORDER (rough — reorderable)
===============================================================================
If you pick scope B (Manager-lite), a sane order:
1. Age model + per-season ageing + retirement (#1, #6)      -- foundation
2. Hidden potential + season growth curve (#2, #3A)         -- makes age matter
3. Youth regen to fill retirements (#7 gen)                 -- keeps world stable
4. Training focus (#4B) + scouting hints (#7 scout)         -- player agency
5. In-match fatigue toggle (#5B)                            -- independent, optional
6. (If chosen) budget/wages-lite (#8B), form (#9)           -- last, optional

>> KOMENTARZ do kolejnosci: 

===============================================================================
## ANYTHING ELSE YOU WANT
===============================================================================
>> 


<!-- end -->
