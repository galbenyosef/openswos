# rnc-decode

C# decoder for **Rob Northen ProPack** v1 (`RNC\x01`) and v2 (`RNC\x02`) — the two compression formats used by SWOS on Amiga.

## Status: ✅ v1 + v2 both working

**v1 (`RNC\x01`)**: SWOS / SWOS2 m68k binaries (165 735 → 351 596 B and 168 061 → 357 226 B respectively) decode with CRC OK. `HERO.INS` audio also decodes.

**v2 (`RNC\x02`)**: All 8 tested Amiga graphics files decode with CRC OK — `CJCTEAM[1-3].RAW` (player atlases), `CJCTEAMG.RAW` (goalkeeper), `CJCBENCH.RAW`, `CJCBITS.RAW`, `CJCGRAFS.RAW`, `MENUBG.RAW`. All Amiga `.RAW` graphics files and pitch `.MAP` files in the disk2 set are RNC v2.

Auto-dispatch via `Rnc.Decode(bytes)` — checks the magic byte and routes to v1 or v2.

## Implementation notes

- **Header parsing and CRC-16/ARC** are shared between v1 and v2 (`RncHeader.cs`).
- **Bit reader differs**: v1 is LSB-first within 16-bit LE words with eager refill on `bitcount < 16`; v2 is MSB-first byte-at-a-time with lazy 1-byte refill.
- **Body algorithm differs**: v1 uses Huffman tables; v2 uses a flat prefix opcode stream (`0` literal, `10` 4-9-byte match, `110` 2-byte, `1110` 3-byte, `1111+byte` longer match or EOS).
- **v2 multi-section model** was the trick: files have multiple sections separated by the `1111 0x00 +pad` EOS marker. After each EOS the outer loop continues; only output-buffer-full terminates decode.
- Reference was `lab313ru/rnc_propack_source` for v2 (aybe/RNCUnpacker is v1-only).

---

## Historical bug notes

- v1 was initially broken at output byte ~57 (2026-05-12). Fixed by switching to aybe/RNCUnpacker's bit-reader model: 2-byte init (not 4-byte), `p` starts at offset 0, eager refill on `bitcount < 16`.
- v2 was initially broken with CRC mismatch at full decode. Fixed by adding the outer-loop / inner-section structure: the `1111 0x00` EOS marker terminates a **section**, not the whole file. The outer loop continues until output is full.

## Original bug log (pre-fix, kept for reference)

The header parse, CRC check, and high-level decode loop are in place. Decoding the SWOS Amiga executable (`assets/extracted/amiga/disk1/SWOS`, 165 735 bytes RNC1-compressed) **fails after ~53–57 output bytes** with a "match distance exceeds output position" error. Symptoms suggest the bit-buffer alignment drifts during or after the first literal run.

Variations tried (none successful so far):
1. Resync at byte cursor without advancing (per spec from lab313ru/aybe write-ups) — fails at byte 57, distance ~3258.
2. Resync advancing `_bytePos += 2` — fails at byte 57, distance ~1586.
3. No resync — fails at byte 53, distance ~8139.
4. MSB-first bit-result assembly — produces invalid Huffman `numLeaves=28` (worse).

All variations fail at the same general region, suggesting either (a) a subtle off-by-one in the bit buffer / refill window, or (b) something in the algorithm I haven't grokked from the spec write-ups. Resolution likely requires reading the actual reference C source (`zlatkok`-style line-by-line port from `lab313ru/rnc_propack_source` or `aybe/RNCUnpacker`) rather than re-deriving from English-prose spec.

This is **not blocking** team-decode or Variant-abstraction work — Amiga assets that don't pass through RNC (extracted ADF directory tree, uncompressed bytes) are already usable. Returning to this once the rest of the slice is moving.

## Usage (when fixed)

```
dotnet run --project tools/rnc-decode -- <input.rnc1> [-o <output.bin>] [--verify]
```

Header decode + CRC check works today; only the body decode is broken.
