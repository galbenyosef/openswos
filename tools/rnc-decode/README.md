# rnc-decode

C# decoder for **Rob Northen ProPack v1** (RNC1, `RNC\x01` magic). Needed to access the Amiga SWOS executable and several RNC-compressed graphics files.

## Status: ⚠️ **broken — bit-stream alignment bug**

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
