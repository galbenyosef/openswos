# 01 — Netcode model

**Status:** open. Defer decision until first gameplay loop runs locally; until then the code is shape-agnostic (input → tick → output) and isn't blocked.

**Date:** 2026-05-12.

Three serious candidates for OpenSWOS netcode. All are documented for 2-player play (matching original SWOS); 4-player local is a stretch goal and is noted where relevant.

---

## A — Deterministic lockstep

Each peer sends only its **input** each simulation tick. All peers simulate identically and arrive at the same world state.

### Plusy
- **Najprostsze do zaprojektowania i zaimplementowania.** Topologia trywialna, kod cienki.
- **Najmniejsze pasmo** — kilka bajtów per tick (parę bitów inputu).
- **Determinizm = zero desyncu** po starcie meczu.
- **Powtórki za darmo** — zapisz tylko strumień inputów, odtwórz simulację.
- **Dziedzictwo pasuje:** oryginalny SWOS używał IPX-lockstep; SWOS++ Karakasa (też lockstep) działa po internecie produkcyjnie od lat.
- Bardzo dobrze pasuje do gameplay'u Sensible (krótkie ticki, mała ilość obiektów, determinizm naturalny).

### Minusy
- **Latencja całego meczu = pasmo najwolniejszego peer'a.** Wszyscy czekają na ostatnich.
- **Wymaga ŚCISŁEGO determinizmu:** żadnych `float`-ów w gameplay'u (albo wszystkie peers na tej samej platformie), własny seedowany RNG, deterministyczna kolejność iteracji kolekcji.
- **Godot fizyka jest niedeterministyczna domyślnie** — gameplay musi obchodzić `PhysicsServer2D`, własny prosty integrator (co dla SWOS jest OK — fizyka jest banalna).
- **Cross-platform determinizm trudny:** `Math.Sin`/`Cos` różnią się między .NET-amem na różnych arch, kolejność JIT-owanego kodu, etc. Trzeba fixed-point math albo własna trig table.
- **Jeden disconnect zatrzymuje mecz** — nie ma autorytatywnego stanu do wznowienia.
- Skalowanie do 4 graczy: 4× latencja worst-case (zsynchronizowane czekanie).

### Pasuje gdy
- 2-osobowa rozgrywka jest dominującym przypadkiem.
- Determinizm da się utrzymać (małe state, prosta fizyka).
- Latencja ~50–100ms jest akceptowalna (SWOS jest średnio-szybki).

---

## B — Authoritative client-server (host-based)

Jeden peer jest **hostem** (serwerem), drugi(-rzy) **klientami**. Host trzyma autorytatywny stan świata, klient lokalnie predyktuje swój input i potem się reconciluje z autorytetem.

### Plusy
- **Brak wymogu determinizmu** — można używać Godot fizyki, float'ów, czegokolwiek bez paniki.
- **Lokalna latencja hosta = 0** (host symuluje na sobie).
- **Host może odrzucić nielegalne ruchy** — anti-cheat naturalne (cheater nie może wymusić stanu).
- **Disconnect klienta nie blokuje hosta** — host kończy mecz albo czeka na rejoin.
- **Spectator mode** trywialny (dodatkowy klient bez inputu).
- **Server-side replay** — host loguje pełną sekwencję, można replay'ować z dowolnej perspektywy.

### Minusy
- **Asymetryczne doświadczenie:** klient ma więcej latencji, lag widoczny dla niego.
- **Większe pasmo** — pełen world state lub delty (player positions, ball, score), nie tylko inputy.
- **Client-side prediction + server reconciliation = nietrywialny kod.** Trzeba dobrze ogarnąć rollback lokalny przy korekcie.
- **"Host advantage"** — host ma zerową latencję; klient widzi siebie z prediction-lag. Konkurencyjna scena SWOS (Lubin championships et al.) by to zauważyła.
- **Wymaga dedicated serwera lub designated host** dla 4-osobowej rozgrywki, co zwiększa złożoność topologii.

### Pasuje gdy
- Determinizm jest blokerem (Godot fizyka jest pożądana).
- Chcemy spectator mode / centralne replay.
- Asymetria host-klient jest akceptowalna.

---

## C — Rollback netcode

Każdy peer **predyktuje** input zdalnego (np. "powtórz poprzedni"), symuluje naprzód, a gdy prawdziwy input dotrze i różni się od predykcji — **wycofuje się** o N klatek i resimulate'uje.

### Plusy
- **Najlepszy odbiór latencji** — input gracza widoczny natychmiast (w jego instancji), latencja zdalnego ukryta.
- **Ukrywa jitter sieciowy** — przepływowe doświadczenie nawet przy zmiennym pasmie.
- **Industry-standard dla fighting games** (GGPO, Yomi, RetroArch netplay).

### Minusy
- **Najtrudniejsze do poprawnej implementacji** — łatwo wprowadzić subtelne desyncy.
- **Wymaga determinizmu (jak lockstep) PLUS możliwości rollbacku** do dowolnego stanu z ostatnich N klatek.
- **Pamięciożerne:** N × pełen world snapshot (dla N=8 klatek to ~kilkaset KB w typowej grze 2D).
- **Overkill dla SWOS.** Gra jest średnio-szybka (15-20 ticks/sec efektywnie), nie 60fps-fighting-game-speed. Marginalny zysk percepcyjny względem złożoności.
- **Brak precedensu w 2D-football.** Żadna sport game tego nie używa (sport jest tolerancyjny na 100ms latencji, fighting nie).

### Pasuje gdy
- Gra wymaga reakcji <16ms.
- Mamy juniora od netcode na pełen etat (to nie jest weekendowa robota).

**Nie pasuje do OpenSWOS w obecnym scope.**

---

## Rekomendacja (nie blokująca, do potwierdzenia)

**A (lockstep) dla MVP i 2-player.** Cechy oryginału + SWOS++ udowodnioną drogę. Niska komplikacja, niski koszt kodu. Determinizm jest osiągalny dla SWOS-poziomu fizyki (płaska plansza, parę cech ruchu, znany RNG).

**Awaryjne przesiadanie na B (client-server)** jeśli determinizm Godota okaże się męczący w cross-platform (web vs desktop vs Android). Decyzja może być podjęta dopiero gdy mamy działający single-player gameplay loop i da się empirycznie sprawdzić, czy `dotnet` zachowuje się tak samo na 3 platformach.

**C (rollback) poza scope.** Może wrócić jako "v2 netcode" po latach, jeśli kompetytywna scena tego potrzebuje.

## Co trzeba zrobić, żeby tę decyzję zafiksować

1. Mieć pierwszy gameplay loop działający single-player (player + ball + boisko). 2D physics zaimplementowane jako prosty integrator (nie Godot `PhysicsServer2D`).
2. Sprawdzić empirycznie, czy `dotnet` symuluje deterministycznie na (Windows, Linux, web/wasm).
3. Wtedy: lockstep jeśli tak; client-server jeśli nie.
