# B-FULL: gotowe do testów

**Date: 2026-05-26 late night.** Pełen pipeline SWOS sportowany + wired + załadowane dane + przetestowane 5000 ticków bez crashy.

## TL;DR — jak testować

```
tools\test-swos-port.bat
```

(albo `tools\launch-game.bat` — settings.json ma `useSwosPort=true`)

W menu: arrows = nawigacja, space = wybierz/start. Slot "physics" pokazuje aktualny tryb.

Toggle Old Sim ↔ SWOS port: w menu → najedź na slot "physics" → strzałki lewo/prawo.

**ESC w meczu** = pauza. ESC z pauzy = wróć do menu.

## Status weryfikacji

| Sprawdzenie | Wynik |
|---|---|
| `dotnet build` | ✅ 0 errors |
| Headless boot | ✅ czysty (`useSwosPort=True` ładuje się z settings.json) |
| 100 ticks smoke test | ✅ **PASS** |
| 5000 ticks smoke test (71s gry) | ✅ **PASS bez exceptionów** |
| RNG deterministic | ✅ SWOS-style xor-stream PRNG seeded by gameTick |
| Player skill data loaded | ✅ 22 graczy × 61 bajtów PlayerInfo z TeamRecord |
| Tactics formation loaded | ✅ 4-3-3 z swos.asm:209131 (370 bajtów × 19 slotów) |
| `Engine.PhysicsTicksPerSecond` | ✅ 70 (= SWOS PC) |
| 41/64 PC mode damping | ✅ w SpriteUpdate.CalculateDeltaXAndY |
| Old Sim fallback | ✅ NIETKNIĘTY — toggle off i działa stary kod |

## Co masz teraz

**24 plików portu w `game/scripts/Sim/Port/`**:
- BallUpdate (1554) + BallOutOfPlay (612) + BallVariables (453) — pełne ball physics + goal detection
- BallSyncBridge (106) — legacy bridge (unused na SWOS port path)
- PlayerUpdate (666) + PlayerActions (1308) + PlayerControlled (748) + PlayerHeader (347) + PlayerTackle (790) — pełny per-player update
- UpdatePlayers (766) — entry + dispatch wired do branches
- GameLoop (303) + GameTime (474) + Referee (475) — orchestrator + clock + sędzia
- AiBrain (1419 z ostatnim portem cseg_84F4B) + AiHelpers (432) — pełna logika AI
- InputControls (445) — joystick→memory + Godot bridge
- SetPieces (780) — throw-in/corner/goal-kick/free-kick/penalty
- UpdateGoals (135) + Result (308) + Stats (245) + Camera (429) + TeamPort (153) — UI/state
- TeamDataLoader + TacticsLoader — match-start data populators
- SpriteUpdate (116) — sprite delta calc (zawiera 41/64 damping)

**SwosVm**: Memory (~384 KB) + Flags (68k) + BallSprite/PlayerSprite/TeamData views + Rng + tabele sin/cos.

**Łącznie ~13,500 LOC nowego C# z mechanicznego portu.**

## Test ręczny (proponowany)

1. Otwórz `tools\test-swos-port.bat`.
2. W menu: pitch + Arsenal vs Chelsea (defaults OK).
3. Najedź "physics" → powinno pokazywać `SWOS port  (WIRED — many stubs, exotic behavior expected)`.
4. Najedź "start" → space.
5. **Obserwuj**:
   - Czy piłka wykazuje ruch (powinna — physics jest 100% sportowane)?
   - Czy gracze chodzą lub stoją? Stoją = AI dispatcher OK ale animation table stuby gryzą.
   - Czy keeper się porusza?
   - Czy zaczyna od kick-offu w centrum?
6. **W razie crashe**: ESC → menu → "physics" lewo/prawo → "Old sim" → space → masz stabilny fallback.

## Headless smoke test (CI)

```
.tools\godot\Godot_v4.6.2-stable_mono_win64_console.exe --headless --path game --swos-smoke [TICKS]
```

Domyślnie 100 ticków. Dla dłuższego testu: `--swos-smoke 5000`. Outputuje:
- `RUNTIME TEST PASSED (N ticks completed without exception)` — OK
- `RUNTIME TEST FAILED after K/N ticks: ExceptionType: msg` + stack trace — naprawa

Exit code 0 = pass, 1 = fail.

## Limity (znane)

1. **Animation table stuby** — `StubSetPlayerAnimationTable` w PlayerActions/etc. są no-op. To znaczy że sprite może freezować w jednej klatce zamiast cyklować w chodzeniu. Sprawa kosmetyczna — gameplay tick + AI lecą.
2. **Audio stuby** — kicks/komentarze nie grają (Godot owns audio; integracja na osobnej sesji).
3. **Tylko 4-3-3 formacja** — jedna ekstrakowana z swos.asm. Pozostałe 18 wciąż na zerach. AI używa tactics index 0 więc wszyscy 4-3-3.
4. **OpponentMode częściowo zignorowane** — port hardcoduje team1=P1 human, team2=AI. Demo mode / Player2 mode nie chodzi przez port (zostaje Old Sim path).
5. **MatchState transitions** — Main.cs MatchState (PreKickoff/HalfTime/FullTime) jest niezależne. Port nie sygnalizuje przejść do MatchState. Score i clock z portu są w Memory ale UI Main.cs ich nie czyta jeszcze.
6. **PlayerTackle.cs** — 2 unused labels (`l_out`, `l_left_team`) preserved per source-fidelity. Build warning CS0164 × 2 — nie błąd.

## Co dalej (Session 3+)

Priorytet po obserwacjach z testu ręcznego:
1. **Załaduj animation tables** ze swos.asm → PlayerSprite animTable pointer → renderer cycle frames.
2. **Pozostałe 18 formacji** ze swos.asm:208980-209340.
3. **MatchState ↔ port signals**: Goal scored, HalfTime, FullTime sygnały z Result/GameTime do Main.cs.
4. **OpponentMode honoring** w port path (P2 jako manual, Demo jako AI vs AI).
5. **Performance** — żaden audyt, ale 5000 ticks w <2s real time → OK na 70Hz.
6. **Fidelity harness extension** — extend CSV trace coverage od ball-only do players + match state.

## Backupy z sesji

- `.backups/20260526_191122_snapshot.7z` — start
- `.backups/20260526_195533_snapshot.7z` — W3 complete (15 files)
- `.backups/20260526_201235_snapshot.7z` — W4 pipeline WIRED
- `.backups/[latest]_snapshot.7z` — READY_FOR_TESTING milestone

## Hard rules przestrzeganych

- ✅ **Old Sim NIETKNIĘTY** — toggle off → stabilny fallback
- ✅ **No floats** — wszystkie operacje Fixed Q24.8 / Q16.16 / Q8.8 int
- ✅ **Citations** — każdy port block ma `// from xxx.cpp:NNN` lub `// from updatePlayers.cpp:NNN`
- ✅ **No verbatim copy** — paraphrase tylko, swos-port "All Rights Reserved"
- ✅ **Mechanical port** — goto, magic numbers, useless flag updates wszystko preservowane
- ✅ **41/64 PC damping** retained
- ✅ **Godot 70 Hz** = SWOS PC tick rate
- ✅ **Backupy** przed każdą falą
