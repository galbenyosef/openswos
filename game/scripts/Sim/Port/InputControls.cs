namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Per-tick input + team-controls layer ported from
// external/swos-port/src/controls/gameControls.cpp (whole file, 332 LOC).
//
// What's here:
//   - GameControlEvents enum (gameControlEvents.h:5-20).
//   - Direction enum (swos.h:136-147) — convenience re-export so callers can
//     stay inside InputControls without pulling the wider Sprite header.
//   - PlayerNumber enum (controls.h:3-7).
//   - All 11 exported functions (resetGameControls, updateFireBlocked,
//     selectTeamForUpdate, updateTeamControls, postUpdateTeamControls,
//     getPlayerEvents, isPlayerFiring, getFireStartedAndBumpFireCounter,
//     eventsToDirection, directionToEvents, isAnyPlayerFiring).
//   - All four file-static helpers (filterOverlappedEvents, updateGameControls,
//     the overloaded updateTeamControls(team, player, events), updatePlayerFire).
//   - Static state moved into Memory.Addr.ic_* (so the port is byte-identical
//     to the original module-static state from gameControls.cpp:15-26).
//
// What's stubbed (with a single, easy-to-find entry point):
//   - The keyboard/mouse/joypad dispatch in getPlayerEvents (gameControls.cpp:
//     107-124). Godot owns physical input; Main.cs calls SetJoystickState()
//     once per tick to write the resulting GameControlEvents bitmask into
//     Memory.Addr.ic_pl{1,2}Events, and getPlayerEvents reads it from there.
//   - UpdateControlledPlayer + UpdatePlayerBeingPassedTo (called by
//     updateTeamControls at gameControls.cpp:70-71). Both are separate
//     updatePlayers.cpp ports; here they're no-op stubs.
//   - inBench() (gameControls.cpp:81) — wired to a stub returning false; bench
//     mode isn't supported yet so the in-bench branch is unreachable.
//   - Replay / fade / zoom calls in updateGameControls (gameControls.cpp:266-276):
//     replay events are not yet ported; stubbed.
//   - SWOS::Player{1,2}StatusProc (gameControls.cpp:279-289) — render-side
//     callbacks. Not part of the deterministic per-tick path; omitted.
public static class InputControls
{
    // -------------------------------------------------------------------
    // Diagnostic counters — bumped by UpdateControlledPlayer's faithful
    // open-play reassignment (swos.asm:100973-101002, gated on
    // team.ballOutOfPlay != 0) whenever the controlledPlayer pointer actually
    // changes. Reported by the --swos-smoke harness. Port-only telemetry; the
    // original keeps no such counters.
    // See ResetCtrlSwapCounters() / CtrlSwapHumanTop / CtrlSwapHumanBot
    // / CtrlSwapAiTop / CtrlSwapAiBot below.
    // -------------------------------------------------------------------
    private static int s_ctrlSwapHumanTop;
    private static int s_ctrlSwapHumanBot;
    private static int s_ctrlSwapAiTop;
    private static int s_ctrlSwapAiBot;

    public static int CtrlSwapHumanTop => s_ctrlSwapHumanTop;
    public static int CtrlSwapHumanBot => s_ctrlSwapHumanBot;
    public static int CtrlSwapAiTop    => s_ctrlSwapAiTop;
    public static int CtrlSwapAiBot    => s_ctrlSwapAiBot;

    public static void ResetCtrlSwapCounters()
    {
        s_ctrlSwapHumanTop = 0;
        s_ctrlSwapHumanBot = 0;
        s_ctrlSwapAiTop    = 0;
        s_ctrlSwapAiBot    = 0;
    }

    // gameControlEvents.h:5-20. Layout exactly mirrored; values fit in int.
    [System.Flags]
    public enum GameControlEvents : int
    {
        kNoGameEvents       = 0,
        kGameEventUp        = 1,
        kGameEventDown      = 2,
        kGameEventLeft      = 4,
        kGameEventRight     = 8,
        kGameEventKick      = 16,
        kGameEventBench     = 32,
        kGameEventPause     = 64,
        kGameEventReplay    = 128,
        kGameEventSaveHighlight = 256,
        kGameEventZoomIn    = 512,
        kGameEventZoomOut   = 1024,
        kMaxGameEvent       = 2048,   // gameControlEvents.h:19 — synthesised sentinel after last bit.
    }

    // gameControlEvents.h:24 — movement-only mask.
    public const GameControlEvents kGameEventMovementMask =
        GameControlEvents.kGameEventUp | GameControlEvents.kGameEventDown |
        GameControlEvents.kGameEventLeft | GameControlEvents.kGameEventRight |
        GameControlEvents.kGameEventZoomIn | GameControlEvents.kGameEventZoomOut;

    // swos.h:136-147 — direction codes (8-way + sentinel).
    public const int kNoDirection         = -1;
    public const int kFacingTop           = 0;
    public const int kFacingTopRight      = 1;
    public const int kFacingRight         = 2;
    public const int kFacingBottomRight   = 3;
    public const int kFacingBottom        = 4;
    public const int kFacingBottomLeft    = 5;
    public const int kFacingLeft          = 6;
    public const int kFacingTopLeft       = 7;

    // Debug-only force-override slots used by Main.cs smoke tests to drive
    // P1's joystick input without simulating the keyboard handler. NULL =
    // no override; any other value short-circuits live input collection.
    public static int? DebugForceP1Direction = null;
    public static bool DebugForceP1Fire = false;

    // controls.h:3-7.
    public const int kNoPlayer = -1;
    public const int kPlayer1  = 0;
    public const int kPlayer2  = 1;

    // ----- Godot input bridge ----------------------------------------------
    // gameControls.cpp:107-124 — getPlayerEvents originally dispatches across
    // keyboard / mouse / joypad sources. We collapse all four into a single
    // memory-backed bitmask per player; Main.cs calls SetJoystickState() once
    // per tick after sampling Godot Input, before GameLoop.Tick() runs.
    //
    // `direction` is 0..7 (matching kFacing*) or kNoDirection (-1) for centred.
    // `fireDown` mirrors a held kick button; `fireTriggered` is ignored here
    // because the port's "fire just started" detection runs inside
    // GetFireStartedAndBumpFireCounter — fireDown is the only state needed.
    // (Parameter kept for API readability + future use, e.g. wiring secondary
    // fire / bench events.)
    public static void SetJoystickState(int teamIndex, int direction, bool fireDown, bool fireTriggered)
    {
        GameControlEvents events = DirectionToEvents((short)direction);
        if (fireDown)
            events |= GameControlEvents.kGameEventKick;

        int addr = teamIndex == kPlayer2 ? Memory.Addr.ic_pl2Events : Memory.Addr.ic_pl1Events;
        Memory.WriteDword(addr, (int)events);

        // `fireTriggered` is reserved — see method comment.
        _ = fireTriggered;
    }

    // ----- Ported functions, in source order -------------------------------

    // gameControls.cpp:33-46 — resetGameControls.
    // Re-zeros all module-static state. Called when entering a new match or
    // on demand (e.g. after match abort). Memory.Init() does this automatically
    // at boot; ResetGameControls is here so a mid-game restart can re-run it.
    public static void ResetGameControls()
    {
        Memory.WriteDword(Memory.Addr.teamSwitchCounter, 0);
        Memory.WriteByte(Memory.Addr.ic_pl1LastFired, 0);
        Memory.WriteByte(Memory.Addr.ic_pl2LastFired, 0);
        Memory.WriteDword(Memory.Addr.ic_pl1FireCounter, 0);
        Memory.WriteDword(Memory.Addr.ic_pl2FireCounter, 0);
        Memory.WriteDword(Memory.Addr.ic_oldPl1Events, 0);
        Memory.WriteDword(Memory.Addr.ic_oldPl2Events, 0);
        Memory.WriteDword(Memory.Addr.ic_pl1LastVertical, 0);
        Memory.WriteDword(Memory.Addr.ic_pl1LastHorizontal, 0);
        Memory.WriteDword(Memory.Addr.ic_pl2LastVertical, 0);
        Memory.WriteDword(Memory.Addr.ic_pl2LastHorizontal, 0);
    }

    // gameControls.cpp:48-56 — updateFireBlocked.
    // While `swos.fireBlocked` is set, the player-update branch is skipped
    // until both players have released the fire button.
    public static bool UpdateFireBlocked()
    {
        if (Memory.ReadWord(Memory.Addr.fireBlocked) != 0)
        {
            if (!IsAnyPlayerFiring())
                Memory.WriteWord(Memory.Addr.fireBlocked, 0);
            return true;
        }
        return false;
    }

    // gameControls.cpp:58-62 — selectTeamForUpdate.
    // `auto team = ++m_teamSwitchCounter & 1 ? &topTeamData : &bottomTeamData`.
    // Pre-increment; bit 0 picks top (odd) vs bottom (even) each tick.
    //
    // We surface `bool top` instead of returning a TeamGeneralInfo pointer
    // because the rest of the port already keys on `bool top` (see TeamData).
    public static bool SelectTeamForUpdate()
    {
        int counter = Memory.ReadSignedDword(Memory.Addr.teamSwitchCounter) + 1;
        Memory.WriteDword(Memory.Addr.teamSwitchCounter, counter);
        return (counter & 1) != 0;  // odd → top team
    }

    // gameControls.cpp:67-90 — public updateTeamControls(team).
    // Per-frame entry that handles ONE team. Refreshes controlledPlayer +
    // playerBeingPassedTo (stubs), then — if the team has a human controller —
    // pulls input events and pushes them through updateGameControls +
    // updateTeamControls(team, player, events). Finally, if !resetControls AND
    // the team is in the bench menu, zeroes all team-control fields so a
    // sub-mid-game can't accidentally drive the pitch.
    public static void UpdateTeamControls(bool top)
    {
        // gameControls.cpp:69 — A6 = team. The asm-style mov is just "current
        // team pointer" stash; in our port we pass `top` everywhere directly.
        // gameControls.cpp:70-71 — UpdateControlledPlayer + UpdatePlayerBeingPassedTo
        // (ported below — swos.asm:100851 / 101045).
        UpdateControlledPlayer(top);
        UpdatePlayerBeingPassedTo(top);

        int teamBase = TeamData.Base(top);
        short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);

        if (playerNumber != 0)
        {
            // gameControls.cpp:74 — player = playerNumber == 2 ? kPlayer2 : kPlayer1.
            int player = playerNumber == 2 ? kPlayer2 : kPlayer1;
            GameControlEvents events = GetPlayerEvents(player);
            UpdateGameControls(player, events);
            UpdateTeamControlsInternal(top, player, events);
        }

        // gameControls.cpp:80-89 — if (!team->resetControls) { if (inBench()) {…} }
        short resetControls = Memory.ReadSignedWord(teamBase + TeamData.OffResetControls);
        if (resetControls == 0)
        {
            if (Bench.InBench())
            {
                Memory.WriteWord(teamBase + TeamData.OffCurrentAllowedDirection, kNoDirection);
                Memory.WriteByte(teamBase + TeamData.OffQuickFire, 0);
                Memory.WriteByte(teamBase + TeamData.OffNormalFire, 0);
                Memory.WriteByte(teamBase + TeamData.OffFirePressed, 0);
                Memory.WriteByte(teamBase + TeamData.OffFireThisFrame, 0);
                Memory.WriteWord(teamBase + TeamData.OffFireCounter, 0);
            }
        }
    }

    // gameControls.cpp:92-99 — postUpdateTeamControls.
    // After updatePlayers runs, if the team raised headerOrTackle, clear it
    // and zero that player's fire counter (prevents header twice in a row).
    public static void PostUpdateTeamControls(bool top)
    {
        int teamBase = TeamData.Base(top);
        short headerOrTackle = Memory.ReadSignedWord(teamBase + TeamData.OffHeaderOrTackle);
        if (headerOrTackle != 0)
        {
            Memory.WriteWord(teamBase + TeamData.OffHeaderOrTackle, 0);
            short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
            int fireCounterAddr = playerNumber == 2 ? Memory.Addr.ic_pl2FireCounter : Memory.Addr.ic_pl1FireCounter;
            Memory.WriteDword(fireCounterAddr, 0);
        }
    }

    // gameControls.cpp:101-127 — getPlayerEvents.
    // Original dispatches across keyboard1Events / keyboard2Events / mouseEvents
    // / pl{1,2}JoypadEvents based on getPl{1,2}Controls(). We collapse all four
    // into the per-team memory slot that Main.cs writes via SetJoystickState.
    // The filterOverlappedEvents pass is preserved 1:1.
    public static GameControlEvents GetPlayerEvents(int player)
    {
        System.Diagnostics.Debug.Assert(player == kPlayer1 || player == kPlayer2);
        int addr = player == kPlayer2 ? Memory.Addr.ic_pl2Events : Memory.Addr.ic_pl1Events;
        GameControlEvents events = (GameControlEvents)Memory.ReadSignedDword(addr);
        return FilterOverlappedEvents(player, events);
    }

    // gameControls.cpp:129-133 — isPlayerFiring.
    public static bool IsPlayerFiring(int player)
    {
        GameControlEvents events = GetPlayerEvents(player);
        return (events & GameControlEvents.kGameEventKick) != 0;
    }

    // gameControls.cpp:135-161 — getFireStartedAndBumpFireCounter.
    // The "fire counter" state machine drives kick power:
    //   - Counter starts at -1 on press; decrements each frame while held.
    //   - On release the counter is negated (becomes positive).
    //   - 0 means "untouched".
    //   - quickFire fires when |counter| ∈ [1..4]; normalFire fires when |counter| > 4.
    public static bool GetFireStartedAndBumpFireCounter(bool currentFire, int player = kPlayer1)
    {
        int fireCounterAddr = player == kPlayer1 ? Memory.Addr.ic_pl1FireCounter : Memory.Addr.ic_pl2FireCounter;
        int lastFiredAddr   = player == kPlayer1 ? Memory.Addr.ic_pl1LastFired   : Memory.Addr.ic_pl2LastFired;

        int fireCounter = Memory.ReadSignedDword(fireCounterAddr);
        bool lastFired = Memory.ReadByte(lastFiredAddr) != 0;
        bool fireStartedThisFrame = false;

        if (lastFired)
        {
            if (currentFire)
            {
                if (fireCounter != 0)
                    fireCounter--;
            }
            else
            {
                lastFired = false;
                fireCounter = -fireCounter;
            }
        }
        else
        {
            if (currentFire)
            {
                fireStartedThisFrame = true;
                lastFired = true;
                fireCounter = -1;
            }
            else
            {
                lastFired = false;
            }
        }

        Memory.WriteDword(fireCounterAddr, fireCounter);
        Memory.WriteByte(lastFiredAddr, lastFired ? 1 : 0);
        return fireStartedThisFrame;
    }

    // gameControls.cpp:163-190 — eventsToDirection.
    public static short EventsToDirection(GameControlEvents events)
    {
        short direction = kNoDirection;

        bool left  = (events & GameControlEvents.kGameEventLeft)  != 0;
        bool right = (events & GameControlEvents.kGameEventRight) != 0;
        bool up    = (events & GameControlEvents.kGameEventUp)    != 0;
        bool down  = (events & GameControlEvents.kGameEventDown)  != 0;

        if      (up   && right) direction = kFacingTopRight;
        else if (down && right) direction = kFacingBottomRight;
        else if (down && left)  direction = kFacingBottomLeft;
        else if (up   && left)  direction = kFacingTopLeft;
        else if (up)            direction = kFacingTop;
        else if (right)         direction = kFacingRight;
        else if (down)          direction = kFacingBottom;
        else if (left)          direction = kFacingLeft;

        return direction;
    }

    // gameControls.cpp:192-216 — directionToEvents.
    public static GameControlEvents DirectionToEvents(short direction)
    {
        switch (direction)
        {
            case kFacingTop:         return GameControlEvents.kGameEventUp;
            case kFacingTopRight:    return GameControlEvents.kGameEventUp | GameControlEvents.kGameEventRight;
            case kFacingRight:       return GameControlEvents.kGameEventRight;
            case kFacingBottomRight: return GameControlEvents.kGameEventDown | GameControlEvents.kGameEventRight;
            case kFacingBottom:      return GameControlEvents.kGameEventDown;
            case kFacingBottomLeft:  return GameControlEvents.kGameEventDown | GameControlEvents.kGameEventLeft;
            case kFacingLeft:        return GameControlEvents.kGameEventLeft;
            case kFacingTopLeft:     return GameControlEvents.kGameEventUp | GameControlEvents.kGameEventLeft;
            default:
                // Original asserts; in production it falls through to kNoDirection.
                System.Diagnostics.Debug.Assert(direction == kNoDirection);
                return GameControlEvents.kNoGameEvents;
        }
    }

    // gameControls.cpp:218-221 — isAnyPlayerFiring.
    public static bool IsAnyPlayerFiring()
    {
        return ((GetPlayerEvents(kPlayer1) | GetPlayerEvents(kPlayer2)) & GameControlEvents.kGameEventKick) != 0;
    }

    // ----- File-static helpers (gameControls.cpp:225-332) ------------------

    // gameControls.cpp:225-258 — filterOverlappedEvents.
    // Resolves up+down / left+right conflicts. While both opposing keys remain
    // pressed, the axis sticks to whichever side arrived FIRST (so taps that
    // happen to overlap don't accidentally cancel the original direction).
    private static GameControlEvents FilterOverlappedEvents(int player, GameControlEvents events)
    {
        int oldEventsAddr     = player == kPlayer1 ? Memory.Addr.ic_oldPl1Events     : Memory.Addr.ic_oldPl2Events;
        int forceVerticalAddr = player == kPlayer1 ? Memory.Addr.ic_pl1LastVertical  : Memory.Addr.ic_pl2LastVertical;
        int forceHorizAddr    = player == kPlayer1 ? Memory.Addr.ic_pl1LastHorizontal : Memory.Addr.ic_pl2LastHorizontal;

        GameControlEvents oldEvents      = (GameControlEvents)Memory.ReadSignedDword(oldEventsAddr);
        GameControlEvents forceVertical  = (GameControlEvents)Memory.ReadSignedDword(forceVerticalAddr);
        GameControlEvents forceHorizontal = (GameControlEvents)Memory.ReadSignedDword(forceHorizAddr);

        // Vertical axis.
        if ((events & GameControlEvents.kGameEventUp) != 0 && (events & GameControlEvents.kGameEventDown) != 0)
        {
            events &= ~(GameControlEvents.kGameEventUp | GameControlEvents.kGameEventDown);
            if (forceVertical == GameControlEvents.kNoGameEvents)
            {
                forceVertical = (oldEvents & GameControlEvents.kGameEventUp) != 0
                    ? GameControlEvents.kGameEventDown
                    : GameControlEvents.kGameEventUp;
            }
            events |= forceVertical;
        }
        else
        {
            forceVertical = GameControlEvents.kNoGameEvents;
        }

        // Horizontal axis.
        if ((events & GameControlEvents.kGameEventLeft) != 0 && (events & GameControlEvents.kGameEventRight) != 0)
        {
            events &= ~(GameControlEvents.kGameEventLeft | GameControlEvents.kGameEventRight);
            if (forceHorizontal == GameControlEvents.kNoGameEvents)
            {
                forceHorizontal = (oldEvents & GameControlEvents.kGameEventLeft) != 0
                    ? GameControlEvents.kGameEventRight
                    : GameControlEvents.kGameEventLeft;
            }
            events |= forceHorizontal;
        }
        else
        {
            forceHorizontal = GameControlEvents.kNoGameEvents;
        }

        // gameControls.cpp:257 — `return oldEvents = events;` (assign + return).
        Memory.WriteDword(oldEventsAddr, (int)events);
        Memory.WriteDword(forceVerticalAddr, (int)forceVertical);
        Memory.WriteDword(forceHorizAddr, (int)forceHorizontal);
        return events;
    }

    // gameControls.cpp:260-277 — updateGameControls (file-static).
    // Wires non-movement events (replay, save-highlight, zoom) to engine ops.
    // We only port the call shape; the actual replay/zoom callees are stubs
    // until those modules are ported.
    private static void UpdateGameControls(int player, GameControlEvents events)
    {
        UpdatePlayerFire(player, events);

        if ((events & GameControlEvents.kGameEventReplay) != 0)
            StubRequestFadeAndInstantReplay();

        if ((events & GameControlEvents.kGameEventSaveHighlight) != 0)
            StubRequestFadeAndSaveReplay();

        if ((events & GameControlEvents.kGameEventZoomIn) != 0)
            StubZoomIn();

        if ((events & GameControlEvents.kGameEventZoomOut) != 0)
            StubZoomOut();
    }

    // gameControls.cpp:291-326 — updateTeamControls(team, player, events) (file-static).
    // Sets all per-team control fields from the events + player counter.
    //
    // C++ semantics translated:
    //   - team->fireThisFrame = GetFireStartedAndBumpFireCounter(fire, player).
    //     (bool stored to byte; SWOS uses 0/1 in fireThisFrame.)
    //   - `team->firePressed = (fire ? -1 : 0)` — note the assignment-as-condition:
    //     C++ assigns then checks the resulting non-zero. We replicate that with
    //     an explicit write + an `if (fire)` test.
    private static void UpdateTeamControlsInternal(bool top, int player, GameControlEvents events)
    {
        int teamBase = TeamData.Base(top);
        System.Diagnostics.Debug.Assert(Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber) != 0);

        short direction = EventsToDirection(events);
        bool fire = (events & GameControlEvents.kGameEventKick) != 0;

        Memory.WriteWord(teamBase + TeamData.OffCurrentAllowedDirection, direction);
        Memory.WriteWord(teamBase + TeamData.OffDirection,                direction);
        Memory.WriteByte(teamBase + TeamData.OffFireThisFrame,
            GetFireStartedAndBumpFireCounter(fire, player) ? 1 : 0);
        Memory.WriteByte(teamBase + TeamData.OffSecondaryFire,
            (events & GameControlEvents.kGameEventBench) != 0 ? 1 : 0);

        // gameControls.cpp:303-306 — firePressed = (fire ? -1 : 0). If fire pressed,
        // bump fireCounter; otherwise reset it.
        int firePressed = fire ? -1 : 0;
        Memory.WriteByte(teamBase + TeamData.OffFirePressed, firePressed);
        if (firePressed != 0)
        {
            short fc = Memory.ReadSignedWord(teamBase + TeamData.OffFireCounter);
            Memory.WriteWord(teamBase + TeamData.OffFireCounter, (short)(fc + 1));
        }
        else
        {
            Memory.WriteWord(teamBase + TeamData.OffFireCounter, 0);
        }

        // gameControls.cpp:308-309 — clear quick/normal fire flags before redecide.
        Memory.WriteByte(teamBase + TeamData.OffQuickFire, 0);
        Memory.WriteByte(teamBase + TeamData.OffNormalFire, 0);

        int fireCounterAddr = player == kPlayer1 ? Memory.Addr.ic_pl1FireCounter : Memory.Addr.ic_pl2FireCounter;
        int fireCounter = Memory.ReadSignedDword(fireCounterAddr);

        // gameControls.cpp:313-325 — quick / normal fire latching.
        if (fireCounter < 0)
        {
            if (fireCounter < -4)
            {
                Memory.WriteByte(teamBase + TeamData.OffNormalFire, -1);
                fireCounter = 0;
            }
        }
        else if (fireCounter > 0)
        {
            if (fireCounter > 4)
                Memory.WriteByte(teamBase + TeamData.OffNormalFire, -1);
            else
                Memory.WriteByte(teamBase + TeamData.OffQuickFire, -1);
            fireCounter = 0;
        }
        Memory.WriteDword(fireCounterAddr, fireCounter);
    }

    // gameControls.cpp:328-332 — updatePlayerFire (file-static).
    // Mirrors `swos.pl{1,2}Fire = -((events & kGameEventKick) != 0)`. The unary
    // minus turns the bool→int (0 or 1) into 0 or -1.
    private static void UpdatePlayerFire(int player, GameControlEvents events)
    {
        int fireAddr = player == kPlayer1 ? Memory.Addr.ic_pl1Fire : Memory.Addr.ic_pl2Fire;
        int value = (events & GameControlEvents.kGameEventKick) != 0 ? -1 : 0;
        Memory.WriteWord(fireAddr, value);
    }

    // ===================================================================
    // UpdateControlledPlayer — swos.asm:100851-101034
    // ===================================================================
    //
    // Picks the closest active outfield player to the ball as the team's
    // "controlled" sprite. Per-tick the loop scans the 11 spritesTable slots
    // and tracks the smallest ballDistance among candidates that pass a
    // chain of disqualifiers (off-screen during play, sent-off, keeper when
    // not playing-or-out, passingKickingPlayer, mid-tackle / mid-header /
    // mid-fall, passToPlayerPtr). ballDistance is refreshed for every player
    // on every call regardless (swos.asm:100884-100898).
    //
    // The promotion is gated on team.ballOutOfPlay != 0 — which means OPEN
    // PLAY: the flag is set to 1 on every kick/pass (swos.asm:114979/115088)
    // and cleared at stoppages. During open play control snaps to the
    // strictly-closest eligible player every time the function runs, with no
    // distance threshold or debounce (ties keep the incumbent —
    // swos.asm:100948-100950). The OLD controlled player and the (eventual)
    // passToPlayerPtr are STOPPED — destX/Y snapped to current X/Y — so a
    // pass can be received cleanly. When ballOutOfPlay == 0 (dead ball) the
    // function does nothing (swos.asm:100969-100972); set-piece code assigns
    // the taker explicitly. During penalties the promotion is also skipped
    // while gameStatePl == ST_GAME_IN_PROGRESS (swos.asm:100960-100964).
    //
    // Critical for "who has the ball" gating — all of AiBrain, Camera,
    // PlayerControlled, PlayerActions read team.controlledPlayer.
    //
    // from swos.asm:100851
    private static void UpdateControlledPlayer(bool top)
    {
        int a6 = TeamData.Base(top);

        // 100853-100859 — A2 = ballSprite; D3 = ball.x.whole; D4 = ball.y.whole.
        short d3BallX = BallSprite.XPixels;
        short d4BallY = BallSprite.YPixels;

        // 100860-100867 — A1 = team.spritesTable; A3=0 (closest), D5=-1 (closest dist), D6=10 (loop count).
        int a1Table = Memory.ReadSignedDword(a6 + TeamData.OffPlayers);
        int a3Closest = 0;
        uint d5BestDist = 0xFFFFFFFFu;  // unsigned max; jnb in asm => unsigned cmp.
        int d6 = 10;

        // 100869 — @@players_loop.
        while (true)
        {
            // 100870-100873 — A2 = [A1++]; current player sprite.
            int a2Sprite = Memory.ReadSignedDword(a1Table);
            a1Table += 4;

            // 100874-100883 — D1 = player.x.whole - ball.x.whole;
            //                  D2 = player.y.whole - ball.y.whole.
            short pX = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffX + 2);
            short pY = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffY + 2);
            int d1 = (short)(pX - d3BallX);
            int d2 = (short)(pY - d4BallY);

            // 100884-100895 — D1 = D1*D1 (32-bit signed); D2 = D2*D2; D1 += D2.
            // The asm uses `imul bx`-style 16x16→32 multiplies; the result fits in 32-bit
            // since max coord is ~672 → 672² + 848² ≈ 1.17M which is well below 2^31.
            int d1sq = d1 * d1;
            int d2sq = d2 * d2;
            int d1Dist = d1sq + d2sq;

            // 100896-100898 — player.ballDistance = D1.
            Memory.WriteDword(a2Sprite + PlayerSprite.OffBallDistance, d1Dist);

            // 100899-100904 — if (gameStatePl == ST_GAME_IN_PROGRESS && !player.onScreen) → @@next.
            short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gameStatePl == 100)
            {
                short onScreen = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffOnScreen);
                if (onScreen == 0)
                    goto l_next;
            }

            // 100906-100910 — @@check_is_player_sent_away: if (player.sentAway) → @@next.
            short sentAway = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffSentAway);
            if (sentAway != 0)
                goto l_next;

            // 100911-100917 — if (!team.goaliePlayingOrOut && player.playerOrdinal == 1) → @@next.
            short goaliePlayingOrOut = Memory.ReadSignedWord(a6 + TeamData.OffGoaliePlayingOrOut);
            if (goaliePlayingOrOut == 0)
            {
                short ordinal = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffPlayerOrdinal);
                if (ordinal == 1)
                    goto l_next;
            }

            // 100920-100924 — @@check_is_player_getting_pass: if (A2 == team.passingKickingPlayer) → @@next.
            int passingKickingPlayer = Memory.ReadSignedDword(a6 + TeamData.OffPassingKickingPlayer);
            if (a2Sprite == passingKickingPlayer)
                goto l_next;

            // 100929-100943 — disqualify mid-action playerStates: TACKLING (1), TACKLED (3),
            // JUMP_HEADING (9), STATIC_HEADING (8), ROLLING_INJURED (13).
            byte plState = Memory.ReadByte(a2Sprite + PlayerSprite.OffPlayerState);
            if (plState == 1) goto l_next;   // PL_TACKLING
            if (plState == 3) goto l_next;   // PL_TACKLED
            if (plState == 9) goto l_next;   // PL_JUMP_HEADING
            if (plState == 8) goto l_next;   // PL_STATIC_HEADING
            if (plState == 13) goto l_next;  // PL_ROLLING_INJURED

            // 100944-100947 — if (A2 == team.passToPlayerPtr) → @@next.
            int passToPlayerPtr = Memory.ReadSignedDword(a6 + TeamData.OffPassToPlayerPtr);
            if (a2Sprite == passToPlayerPtr)
                goto l_next;

            // 100948-100954 — `cmp D1, D5; jnb @@next` — unsigned. Only take when
            // d1Dist < d5BestDist; on tie (d1Dist >= d5BestDist) skip.
            if ((uint)d1Dist >= d5BestDist)
                goto l_next;

            d5BestDist = (uint)d1Dist;
            a3Closest = a2Sprite;

        l_next:
            // 100958-100959 — dec D6; jns @@players_loop. Loop while D6 >= 0
            // (11 iterations 10..0 inclusive).
            d6--;
            if (d6 < 0)
                break;
        }

        // 100960-100964 — if (playingPenalties && gameStatePl == ST_GAME_IN_PROGRESS) → @@out.
        short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        if (playingPenalties != 0)
        {
            short gameStatePl2 = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gameStatePl2 == 100)
                return;
        }

        // 100966-100968 — @@check_closest_active_player: if (A3 == 0) → @@out.
        if (a3Closest == 0)
            return;

        // 100969-100972 — if (team.ballOutOfPlay == 0) → @@out.
        //
        // ballOutOfPlay SEMANTICS (verified 2026-07-02): the flag is SET to 1
        // on every kick/pass (swos.asm:114979 / 115088) and cleared only at
        // stoppages — i.e. ballOutOfPlay != 0 means OPEN PLAY. So during open
        // play the reassignment below runs on EVERY call (each team every
        // other frame): control snaps to the strictly-closest eligible player
        // with NO distance threshold and NO debounce (`cmp D1, D5; jnb` at
        // swos.asm:100948-100950 — ties keep the incumbent). When the ball is
        // DEAD (ballOutOfPlay == 0) the original does NOTHING beyond the
        // per-player ballDistance stats writes above — set-piece code assigns
        // the taker explicitly. playerSwitchTimer gates only
        // UpdatePlayerBeingPassedTo (swos.asm:101063-101065), never this.
        //
        // A port-only "open-play auto-switch" heuristic (25-tick debounce +
        // 1444 sq-px gate) used to live in this branch — it inverted the
        // flag's meaning and could yank control off a set-piece taker. It was
        // removed 2026-07-02; the block below is the faithful open-play path.
        short ballOutOfPlay = Memory.ReadSignedWord(a6 + TeamData.OffBallOutOfPlay);
        if (ballOutOfPlay == 0)
            return;

        // 100973-100994 — if A3 != team.controlledPlayer, stop the OLD controlled
        // player (only if they exist and are PL_NORMAL).
        int currentControlled = Memory.ReadSignedDword(a6 + TeamData.OffControlledPlayer);
        if (a3Closest != currentControlled)
        {
            int oldCtrl = currentControlled;
            if (oldCtrl != 0)
            {
                byte oldState = Memory.ReadByte(oldCtrl + PlayerSprite.OffPlayerState);
                if (oldState == 0)
                {
                    // 100986-100993 — destX = x.whole; destY = y.whole.
                    short oldX = Memory.ReadSignedWord(oldCtrl + PlayerSprite.OffX + 2);
                    short oldY = Memory.ReadSignedWord(oldCtrl + PlayerSprite.OffY + 2);
                    Memory.WriteWord(oldCtrl + PlayerSprite.OffDestX, oldX);
                    Memory.WriteWord(oldCtrl + PlayerSprite.OffDestY, oldY);
                }
            }
            // Diagnostic — count the ballOutOfPlay-gated swap (the original
            // asm path at swos.asm:100975-101002). Bumped only when the swap
            // actually changes the pointer, so this measures real
            // reassignments, not "same sprite re-confirmed".
            short pnOop = Memory.ReadSignedWord(a6 + TeamData.OffPlayerNumber);
            if (pnOop == 0)
            {
                if (top) s_ctrlSwapAiTop++;
                else     s_ctrlSwapAiBot++;
            }
            else
            {
                if (top) s_ctrlSwapHumanTop++;
                else     s_ctrlSwapHumanBot++;
            }
        }

        // 100999-101002 — @@its_controlled_player: team.controlledPlayer = A3.
        Memory.WriteDword(a6 + TeamData.OffControlledPlayer, a3Closest);

        // 101003-101006 — if (A3 != team.passToPlayerPtr) → @@out.
        int passToPlayer = Memory.ReadSignedDword(a6 + TeamData.OffPassToPlayerPtr);
        if (a3Closest != passToPlayer)
            return;

        // 101007-101023 — new controlled player == pass target. Stop the pass target
        // (only if they exist and are PL_NORMAL) so they receive the pass cleanly.
        int passTarget = passToPlayer;
        if (passTarget != 0)
        {
            byte tgtState = Memory.ReadByte(passTarget + PlayerSprite.OffPlayerState);
            if (tgtState == 0)
            {
                short tgtX = Memory.ReadSignedWord(passTarget + PlayerSprite.OffX + 2);
                short tgtY = Memory.ReadSignedWord(passTarget + PlayerSprite.OffY + 2);
                Memory.WriteWord(passTarget + PlayerSprite.OffDestX, tgtX);
                Memory.WriteWord(passTarget + PlayerSprite.OffDestY, tgtY);
            }
        }

        // 101028-101029 — team.passToPlayerPtr = 0 (pass consumed).
        Memory.WriteDword(a6 + TeamData.OffPassToPlayerPtr, 0);
    }

    // ===================================================================
    // UpdatePlayerBeingPassedTo — swos.asm:101045-101321
    // ===================================================================
    //
    // Finds the closest non-controlled player to the ball and stores it in
    // team.passToPlayerPtr — i.e. the AI's "incoming pass" candidate. The
    // structure has two near-identical halves driven by gameStatePl:
    //   - GAME_IN_PROGRESS: must respect passingToPlayer gating, must skip
    //     non-onScreen sprites. If the picked player is NEW (differs from
    //     existing passToPlayerPtr) AND PL_NORMAL, STOP the previous one so
    //     it can receive the pass cleanly, then commit the new pointer.
    //   - !GAME_IN_PROGRESS (@@game_stopped): same loop minus the onScreen
    //     gate (set-piece prep can put sprites off-camera momentarily),
    //     never stops the picked player; just commits.
    // Both halves: track ball.x/y into team.ballX/ballY; require ballInPlay
    // and playerSwitchTimer == 0. Penalty path replaces the loop with the
    // penalty shooter sprite if any.
    //
    // from swos.asm:101045
    private static void UpdatePlayerBeingPassedTo(bool top)
    {
        int a6 = TeamData.Base(top);

        // 101047 — gameStatePl branch.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl != 100)  // ST_GAME_IN_PROGRESS
        {
            UpdatePlayerBeingPassedToStopped(a6);
            return;
        }

        // ---- GAME_IN_PROGRESS path (101049-101201) -----------------------------

        // 101049-101057 — team.ballX = ball.x.whole; team.ballY = ball.y.whole.
        short ballX = BallSprite.XPixels;
        short ballY = BallSprite.YPixels;
        Memory.WriteWord(a6 + TeamData.OffBallX, ballX);
        Memory.WriteWord(a6 + TeamData.OffBallY, ballY);

        // 101058-101061 — if (!team.ballInPlay) → @@out.
        short ballInPlay = Memory.ReadSignedWord(a6 + TeamData.OffBallInPlay);
        if (ballInPlay == 0)
            return;

        // 101062-101065 — if (team.playerSwitchTimer) → @@out.
        short playerSwitchTimer = Memory.ReadSignedWord(a6 + TeamData.OffPlayerSwitchTimer);
        if (playerSwitchTimer != 0)
            return;

        // 101066-101073 — if (team.passingToPlayer && team.passToPlayerPtr != 0) → @@out.
        short passingToPlayer = Memory.ReadSignedWord(a6 + TeamData.OffPassingToPlayer);
        if (passingToPlayer != 0)
        {
            int existing = Memory.ReadSignedDword(a6 + TeamData.OffPassToPlayerPtr);
            if (existing != 0)
                return;
        }

        // 101075-101085 — penalties branch. If playingPenalties && gameStatePl is
        // ST_PENALTIES (105), use penaltyShooterSprite and clear ballInPlay;
        // otherwise drop out. Penalty shooter sprite isn't wired in our port yet —
        // treat as 0 (no candidate) and exit through @@out2.
        short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        int a3Closest;
        if (playingPenalties != 0)
        {
            // ST_PENALTIES (penalty shootout state) — the regular game-in-progress
            // path never enters penalties (different gameStatePl); we already
            // checked gameStatePl==100 above, so reaching here means the asm
            // would have jumped to @@out (cmp gameStatePl, ST_PENALTIES → jnz).
            return;
        }

        // 101088-101103 — A1 = team.spritesTable; closest=0, best=0xFFFFFFFF, D6=10.
        int a1Table = Memory.ReadSignedDword(a6 + TeamData.OffPlayers);
        a3Closest = 0;
        uint d5BestDist = 0xFFFFFFFFu;
        int d6 = 10;

        // 101105 — @@find_closest_player_loop.
        while (true)
        {
            int a2Sprite = Memory.ReadSignedDword(a1Table);
            a1Table += 4;

            // 101110-101113 — if (!player.onScreen) → @@next.
            short onScreen = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffOnScreen);
            if (onScreen == 0)
                goto l_next;

            // 101114-101117 — if (player.sentAway) → @@next.
            short sentAway = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffSentAway);
            if (sentAway != 0)
                goto l_next;

            // 101122-101128 — if (!team.ballOutOfPlayOrKeeper && player.ordinal == 1) → @@next.
            short ballOutOfPlayOrKeeper = Memory.ReadSignedWord(a6 + TeamData.OffBallOutOfPlayOrKeeper);
            if (ballOutOfPlayOrKeeper == 0)
            {
                short ordinal = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffPlayerOrdinal);
                if (ordinal == 1)
                    goto l_next;
            }

            // 101130-101134 — if (A2 == team.controlledPlayer) → @@next.
            int controlledPlayer = Memory.ReadSignedDword(a6 + TeamData.OffControlledPlayer);
            if (a2Sprite == controlledPlayer)
                goto l_next;

            // 101135-101138 — if (A2 == team.passingKickingPlayer) → @@next.
            int passingKickingPlayer = Memory.ReadSignedDword(a6 + TeamData.OffPassingKickingPlayer);
            if (a2Sprite == passingKickingPlayer)
                goto l_next;

            // 101139-101141 — if (player.playerState != PL_NORMAL) → @@next.
            byte plState = Memory.ReadByte(a2Sprite + PlayerSprite.OffPlayerState);
            if (plState != 0)
                goto l_next;

            // 101142-101147 — D1 = player.ballDistance; if D1 >= D5 → @@next.
            int d1Dist = Memory.ReadSignedDword(a2Sprite + PlayerSprite.OffBallDistance);
            if ((uint)d1Dist >= d5BestDist)
                goto l_next;

            d5BestDist = (uint)d1Dist;
            a3Closest = a2Sprite;

        l_next:
            d6--;
            if (d6 < 0)
                break;
        }

        // 101157-101158 — if (A3 == 0) → @@out.
        if (a3Closest == 0)
            return;

        // 101164-101197 — @@skip_searching_for_closest_player.
        // If A3 differs from current passToPlayerPtr, STOP the previous one if
        // it's still PL_NORMAL, then commit A3.
        int currentPassTo = Memory.ReadSignedDword(a6 + TeamData.OffPassToPlayerPtr);
        if (a3Closest != currentPassTo)
        {
            int prev = currentPassTo;
            if (prev != 0)
            {
                byte prevState = Memory.ReadByte(prev + PlayerSprite.OffPlayerState);
                if (prevState == 0)
                {
                    short prevX = Memory.ReadSignedWord(prev + PlayerSprite.OffX + 2);
                    short prevY = Memory.ReadSignedWord(prev + PlayerSprite.OffY + 2);
                    Memory.WriteWord(prev + PlayerSprite.OffDestX, prevX);
                    Memory.WriteWord(prev + PlayerSprite.OffDestY, prevY);
                }
            }
        }

        // 101193-101197 — @@set_pass_to_player: team.passToPlayerPtr = A3.
        Memory.WriteDword(a6 + TeamData.OffPassToPlayerPtr, a3Closest);
    }

    // swos.asm:101204-101321 — @@game_stopped path. Same structure as the
    // GAME_IN_PROGRESS path but the inner candidate loop omits the onScreen
    // check (set-piece prep can park sprites off-camera) and the commit path
    // (cseg_735CE) doesn't STOP the picked player.
    private static void UpdatePlayerBeingPassedToStopped(int a6)
    {
        // 101206-101213 — team.ballX/ballY mirror.
        short ballX = BallSprite.XPixels;
        short ballY = BallSprite.YPixels;
        Memory.WriteWord(a6 + TeamData.OffBallX, ballX);
        Memory.WriteWord(a6 + TeamData.OffBallY, ballY);

        // 101214-101217 — if (!team.ballInPlay) → @@out2.
        short ballInPlay = Memory.ReadSignedWord(a6 + TeamData.OffBallInPlay);
        if (ballInPlay == 0)
            return;

        // 101218-101221 — if (team.playerSwitchTimer) → @@out2.
        short pst = Memory.ReadSignedWord(a6 + TeamData.OffPlayerSwitchTimer);
        if (pst != 0)
            return;

        // 101222-101229 — if (team.passingToPlayer && team.passToPlayerPtr) → @@out2.
        short passingToPlayer = Memory.ReadSignedWord(a6 + TeamData.OffPassingToPlayer);
        if (passingToPlayer != 0)
        {
            int existing = Memory.ReadSignedDword(a6 + TeamData.OffPassToPlayerPtr);
            if (existing != 0)
                return;
        }

        // 101231-101241 — @@check_if_penalties. Penalty shooter not wired
        // (see note in GAME_IN_PROGRESS branch); skip to @@no_penalties or @@out2.
        short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        if (playingPenalties != 0)
        {
            // Same disposition as above — we don't have the penalty shooter
            // wired so the equivalent code path returns without committing.
            return;
        }

        // 101244-101259 — A1 = team.spritesTable; loop init.
        int a1Table = Memory.ReadSignedDword(a6 + TeamData.OffPlayers);
        int a3Closest = 0;
        uint d5BestDist = 0xFFFFFFFFu;
        int d6 = 10;

        // 101261 — @@find_closest_player_loop2. Same shape minus onScreen.
        while (true)
        {
            int a2Sprite = Memory.ReadSignedDword(a1Table);
            a1Table += 4;

            // 101267-101270 — if (player.sentAway) → @@next_player.
            short sentAway = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffSentAway);
            if (sentAway != 0)
                goto l_next;

            // 101275-101281 — if (!team.ballOutOfPlayOrKeeper && ordinal == 1) → @@next_player.
            short ballOutOfPlayOrKeeper = Memory.ReadSignedWord(a6 + TeamData.OffBallOutOfPlayOrKeeper);
            if (ballOutOfPlayOrKeeper == 0)
            {
                short ordinal = Memory.ReadSignedWord(a2Sprite + PlayerSprite.OffPlayerOrdinal);
                if (ordinal == 1)
                    goto l_next;
            }

            // 101283-101291 — skip controlledPlayer / passingKickingPlayer.
            int controlledPlayer = Memory.ReadSignedDword(a6 + TeamData.OffControlledPlayer);
            if (a2Sprite == controlledPlayer)
                goto l_next;
            int passingKickingPlayer = Memory.ReadSignedDword(a6 + TeamData.OffPassingKickingPlayer);
            if (a2Sprite == passingKickingPlayer)
                goto l_next;

            // 101292-101294 — skip non-PL_NORMAL.
            byte plState = Memory.ReadByte(a2Sprite + PlayerSprite.OffPlayerState);
            if (plState != 0)
                goto l_next;

            // 101295-101304 — keep min ballDistance.
            int d1Dist = Memory.ReadSignedDword(a2Sprite + PlayerSprite.OffBallDistance);
            if ((uint)d1Dist >= d5BestDist)
                goto l_next;
            d5BestDist = (uint)d1Dist;
            a3Closest = a2Sprite;

        l_next:
            d6--;
            if (d6 < 0)
                break;
        }

        // 101310-101311 — if (A3 == 0) → @@out2.
        if (a3Closest == 0)
            return;

        // cseg_735CE (101313-101316) — commit without stopping previous.
        Memory.WriteDword(a6 + TeamData.OffPassToPlayerPtr, a3Closest);
    }

    // (Was: StubInBench(). Removed 2026-06-01 — replaced inline with
    //  Bench.InBench() which is the ported equivalent of bench.cpp:42-45.
    //  The previous stub duplicated Bench.InBench()'s identical Memory read of
    //  g_inSubstitutesMenu.
    //  Source: external/swos-port/src/game/bench/bench.cpp:42-45)

    // Mechanical port of gameLoop.cpp:155-158 — requestFadeAndInstantReplay.
    // Original:
    //   void requestFadeAndInstantReplay() {
    //       m_fadeAndInstantReplay = true;
    //   }
    // The flag is consumed by handleHighlightsAndReplays
    // (gameLoop.cpp:335-350) which fades the screen, calls playInstantReplay,
    // and clears the flag. The consumer (replay system) isn't ported yet —
    // it's Godot-render-bound — so this is currently a write-only flag.
    // Wiring the producer now lets the trigger pipeline work the moment the
    // replay consumer lands.
    // Source: external/swos-port/src/game/gameLoop.cpp:155.
    private static void StubRequestFadeAndInstantReplay()
    {
        Memory.WriteWord(Memory.Addr.m_fadeAndInstantReplay, 1);
    }

    // Mechanical port of gameLoop.cpp:150-153 — requestFadeAndSaveReplay.
    // Original:
    //   void requestFadeAndSaveReplay() {
    //       m_fadeAndSaveReplay = true;
    //   }
    // Consumed by handleHighlightsAndReplays (gameLoop.cpp:327-332) which
    // fades the screen and calls saveHighlightScene. Same situation as
    // requestFadeAndInstantReplay above: producer is wired, consumer
    // (replay-save) will land with the replay port.
    // Source: external/swos-port/src/game/gameLoop.cpp:150.
    private static void StubRequestFadeAndSaveReplay()
    {
        Memory.WriteWord(Memory.Addr.m_fadeAndSaveReplay, 1);
    }

    // TODO from camera.cpp — zoomIn / zoomOut. Camera owned by Godot.
    private static void StubZoomIn()  { /* TODO */ }
    private static void StubZoomOut() { /* TODO */ }
}
