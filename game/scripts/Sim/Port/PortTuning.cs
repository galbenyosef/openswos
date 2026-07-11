namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Variant-mode tuning setters ported from
// external/swos-port/src/game/updatePlayers/updatePlayers.cpp.
//
// Four module-static `int`s declared at updatePlayers.cpp:9-12 carry per-
// variant timing constants:
//
//   m_clearResultInterval         (default 660) — stoppage-ticks the final
//      result screen sits before AI auto-presses fire to advance.
//   m_clearResultHalftimeInterval (default 385) — same idea, halftime banner.
//   m_playerDownTacklingInterval  (default 55)  — playerDownTimer seed when a
//      slide tackle goes prone (used by playerBeginTackling at L14852).
//   m_playerDownHeadingInterval   (default 55)  — playerDownTimer seed when a
//      jump-header attempt launches (used by playerAttemptingJumpHeader
//      at L15262).
//
// Setters at updatePlayers.cpp:10458-10476 are trivial — each just writes
// the corresponding static. amigaMode.cpp:30-51 calls all four to switch
// between Amiga and PC tunings (Amiga: 50/50/600/350, PC: 55/55/660/385).
// OpenSWOS hard-locks to PC mode at boot (CLAUDE.md), so these defaults
// are seeded in Memory.Init alongside the existing 55/55 entries for the
// player-down intervals. The setters are exposed here so a future
// Amiga-mode toggle can call them without leaking memory-layout details.
public static class PortTuning
{
    // updatePlayers.cpp:10458-10461 — m_clearResultHalftimeInterval = interval.
    // Stored word-wide at Memory.Addr.m_clearResultHalftimeInterval.
    public static void SetClearResultHalftimeInterval(int interval)
    {
        Memory.WriteWord(Memory.Addr.m_clearResultHalftimeInterval, interval);
    }

    // updatePlayers.cpp:10463-10466 — m_clearResultInterval = interval.
    public static void SetClearResultInterval(int interval)
    {
        Memory.WriteWord(Memory.Addr.m_clearResultInterval, interval);
    }

    // updatePlayers.cpp:10468-10471 — m_playerDownTacklingInterval = interval.
    public static void SetPlayerDownTacklingInterval(int interval)
    {
        Memory.WriteWord(Memory.Addr.m_playerDownTacklingInterval, interval);
    }

    // updatePlayers.cpp:10473-10476 — m_playerDownHeadingInterval = interval.
    public static void SetPlayerDownHeadingInterval(int interval)
    {
        Memory.WriteWord(Memory.Addr.m_playerDownHeadingInterval, interval);
    }
}
