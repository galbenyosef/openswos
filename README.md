# OpenSWOS

Open-source recreation of **Sensible World of Soccer 96/97**, targeting modern
platforms (desktop, web, Android, ARM handhelds). Built with **Godot 4 + C#**.

## Status — early, and honestly still worse than the original

This is an **early work-in-progress**. Right now the original SWOS is the better
game — OpenSWOS is not there yet.

- **On the pitch: ~99% recreated.** The match engine and pitches are a faithful,
  near-1:1 port — movement, ball physics, keepers, tackling, headers, set pieces,
  injuries, referee, half-time, results.
- **Everything around it is placeholder.** The menus, the team-setup and
  substitution screens, etc. are rough stand-ins, not finished UI. They work, but
  they don't yet match the original's quality or completeness.

The goal is 1:1 fidelity with the original first; improvements come later, and
where the original had quirks we intend to offer **optional** fixes rather than
bake them in — you'll be able to keep the authentic feel.

## Requirements — bring your own game files

OpenSWOS ships **no game assets** (they're proprietary) and redistributes nothing
from the original game. You must own an original copy and provide its files.

**Required version: Sensible World of Soccer 96/97 — Amiga edition.** Two floppy
disk images (`.adf`). Any filenames are fine; OpenSWOS scans every `.adf` and
finds the files it needs (only disk 2 is used, but include both). Earlier SWOS
editions (94/95 etc.) are **not** compatible.

On first start, OpenSWOS creates two folders next to the game (and, if that
location is read-only, under its user data dir). Pick whichever you have:

1. **You have floppy images.** Drop your `.adf` files into **`original_swos_adf/`**
   and restart. OpenSWOS auto-extracts what it needs into `original_swos_files/`.
   A `HOW_TO_ADD_SWOS_FILES.txt` in that folder repeats these steps.
2. **You have loose game files** (e.g. a WHDLoad / hard-disk install). Copy them
   into **`original_swos_files/`** — a nested layout is fine, OpenSWOS searches it
   recursively — and restart. (`.hdf` / `.lha` hard-file images are not auto-read
   yet; unpack them to loose files first.)

- **Optional: the PC (DOS) version.** Drop the PC `DATA/` folder into its own
  dedicated **`original_swos_pc/`** folder. It only adds a slightly larger team
  list (~1730 vs ~1616 teams), is **not required**, and does **not** change the
  graphics (those always come from the Amiga files).
- **PC-only is not supported yet** — the PC graphics aren't decoded, so an Amiga
  copy is needed for now. A PC-or-Amiga choice is planned.

## Layout

- [`game/`](game/) — Godot 4 + C# project (the game).
- [`tools/`](tools/) — CLI utilities for asset extraction / format conversion.
- [`docs/`](docs/) — design notes, reverse-engineered format docs, decisions.

## Licence

MIT — see [`LICENSE`](LICENSE). Original game assets are **not** included and
remain the property of their owners.
