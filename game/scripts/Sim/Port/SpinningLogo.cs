namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Spinning-logo animation ported from external/swos-port/src/game/spinningLogo.cpp.
//
// SCOPE: Tiny per-tick state machine that animates the SWOS logo top-right of the
// HUD when not in bench menus and not in post-game stats. Behavioural state
// (m_frameIndex, m_pictureIndex, m_enabled) lives in Memory; the actual draw is
// owned by the renderer (Godot).
//
// Original constants (spinningLogo.cpp:9-10):
//   kLogoXEdgeDist = 35, kLogoY = 14 — these are pure render coordinates,
//   preserved in comments for parity but not used in the deterministic sim.
//
// Sprite IDs (sprites.h:70):
//   kBigSSpriteStart = 1241  (32-frame sequence: 1241..1272)
public static class SpinningLogo
{
    // spinningLogo.cpp:9-10 — pure render constants (renderer reads them).
    public const int kLogoXEdgeDist = 35;
    public const int kLogoY         = 14;

    // sprites.h:70 — kBigSSpriteStart.
    public const int kBigSSpriteStart = 1241;

    // spinningLogo.cpp:17-26 — updateSpinningLogo.
    //   if (m_enabled) {
    //       bool logoSpinning = !inBenchMenus() && !showingPostGameStats();
    //       if (logoSpinning && (swos.currentGameTick & 2))
    //           m_frameIndex = (m_frameIndex + 1) & 0x3f;
    //       m_pictureIndex = kBigSSpriteStart + m_frameIndex / 2;
    //   }
    //
    // Notes:
    // - `inBenchMenus()` is bench.cpp:48-51 — `inBench() && getBenchState() ==
    //   BenchState::kInitial`. Bench port not yet landed; we stub via
    //   g_inSubstitutesMenu (Camera.cs:486-491 uses the same proxy). When the
    //   full bench port lands, this should call into Bench.InBenchMenus().
    // - `showingPostGameStats()` is stats.cpp:69-72 — `statsTimer > 0`,
    //   already ported in Stats.ShowingPostGameStats().
    // - `(currentGameTick & 2)` gates the advance to every 4th tick (bit 1
    //   set → advance). 6-bit counter masked at 0x3f gives a 32-position
    //   wheel (frameIndex / 2 → 0..15 then 16..31 fold to the 32-sprite range).
    public static void UpdateSpinningLogo()
    {
        if (Memory.ReadSignedWord(Memory.Addr.sl_enabled) == 0)
            return;

        // spinningLogo.cpp:20 — gate.
        bool inBenchMenus = StubInBenchMenus();
        bool postGameStats = Stats.ShowingPostGameStats();
        bool logoSpinning = !inBenchMenus && !postGameStats;

        // spinningLogo.cpp:21-22 — bit-1 of currentGameTick gates the advance.
        if (logoSpinning)
        {
            ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
            if ((tick & 2) != 0)
            {
                short fi = Memory.ReadSignedWord(Memory.Addr.sl_frameIndex);
                fi = (short)((fi + 1) & 0x3f);
                Memory.WriteWord(Memory.Addr.sl_frameIndex, fi);
            }
        }

        // spinningLogo.cpp:24 — picture index = kBigSSpriteStart + frameIndex/2.
        short frameIndex = Memory.ReadSignedWord(Memory.Addr.sl_frameIndex);
        Memory.WriteWord(Memory.Addr.sl_pictureIndex, kBigSSpriteStart + frameIndex / 2);
    }

    // spinningLogo.cpp:34-37 — spinningLogoEnabled().
    public static bool SpinningLogoEnabled()
        => Memory.ReadSignedWord(Memory.Addr.sl_enabled) != 0;

    // spinningLogo.cpp:39-42 — enableSpinningLogo(bool).
    public static void EnableSpinningLogo(bool enabled)
        => Memory.WriteWord(Memory.Addr.sl_enabled, enabled ? 1 : 0);

    // Renderer accessor — reads the per-tick picture index.
    public static int GetPictureIndex()
        => Memory.ReadSignedWord(Memory.Addr.sl_pictureIndex);

    // ---- Stubs -------------------------------------------------------------

    // Mechanical port of bench.cpp:47-50 — inBenchMenus(). Original:
    //   bool inBenchMenus() {
    //       return inBench() && getBenchState() == BenchState::kInitial;
    //   }
    // Wired to Bench.InBenchMenus() which now reads the m_benchState slot
    // (defaults to kInitial=0 so the formula behaves correctly even before
    // the menu UI subsystem starts writing it).
    // Source: external/swos-port/src/game/bench/bench.cpp:47-50
    private static bool StubInBenchMenus() => Bench.InBenchMenus();
}
