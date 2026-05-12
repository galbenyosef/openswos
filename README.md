# OpenSWOS

Open-source recreation of **Sensible World of Soccer 96/97** (Amiga + PC) targeting modern platforms — desktop, web, Android, ARM single-board computers, Linux handhelds.

Status: **scaffolding** — no playable code yet.

Stack: Godot 4 + C# + MIT.

See [`CLAUDE.md`](CLAUDE.md) for the project brief, decisions of record, and references to prior art.

## Layout

- [`game/`](game/) — Godot 4 + C# project (to be scaffolded).
- [`tools/`](tools/) — CLI utilities for asset extraction and format conversion.
- [`docs/`](docs/) — design notes, reverse-engineered asset format docs, architecture decisions.
- [`assets/extracted/`](assets/) — gitignored output of the extraction pipeline (PC + Amiga).
- `Swos9697_Amiga/`, `Swos9697_PC/` — original game data, **gitignored** (proprietary, not redistributed).

## Licence

MIT — see [`LICENSE`](LICENSE).
