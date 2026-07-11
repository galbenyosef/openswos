namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Referee movement + card-handing FSM ported from external/swos-port/src/game/referee.cpp.
//
// The referee is a separate Sprite (not in the 22-player pool). After a foul,
// it walks onto the pitch toward foul position, waits, then displays a yellow
// or red card (with player-number sprite blinking next to the booked player),
// finally walks back off-screen.
//
// State machine (kRefereeState enum):
//   kRefOffScreen     (0) — hidden, at hiding position. Default state.
//   kRefIncoming      (1) — walking onto pitch toward foul.
//   kRefWaitingPlayer (2) — arrived at foul, idle, waiting for player.
//   kRefAboutToGiveCard (3) — transient; next tick starts the booking animation.
//   kRefBooking       (4) — running card animation + blinking player-number sprite.
//   kRefLeaving       (5) — walking back off-screen after card.
//
// Card types (kCardHanding enum):
//   kNoCard          (0) — idle.
//   kYellowCard      (1)
//   kRedCard         (2) — also kicks off sendPlayerAway.
//   kSecondYellowCard (3) — same as red (player off), different animation table.
//
// Constants (referee.cpp:10-21):
//   kRefereeSpeed                 = 1024 (Q8.8 — 4 px/tick)
//   kRefereeHidingPlace{X,Y}      = (276, 439) — off-screen parking position
//   kRefereeLeavingTopDestY       = 129
//   kRefereeLeavingBottomDestY    = 770
//   kSentOffPlayerX               = -20 — off-pitch destination for red-card'd player
//   kSentOffPlayerY               = 449
//   kPlayerNumberOffset           = 20  — z-offset for player-number sprite above head
//   kPitchCenterX / kPitchCenterY = 336 / 449 (from pitchConstants.h)
//
// Animation table pointers live in Memory.Addr.refXxxAnimTable (one per phase).
// updateBookedPlayerNumberSprite uses a 30-entry int8 table to blink the
// player-number digit on/off — entry indices >= 15 are zero (sprite hidden),
// final entry is -1 sentinel triggering "leave" transition.
public static class Referee
{
    // referee.cpp:10
    public const int kRefereeSpeed = 1024;

    // referee.cpp:12-13
    public const int kRefereeHidingPlaceX = 276;
    public const int kRefereeHidingPlaceY = 439;

    // referee.cpp:15-16
    public const int kRefereeLeavingTopDestY = 129;
    public const int kRefereeLeavingBottomDestY = 770;

    // referee.cpp:18-19
    public const int kSentOffPlayerX = -20;
    public const int kSentOffPlayerY = 449;

    // referee.cpp:21
    public const int kPlayerNumberOffset = 20;

    // pitchConstants.h:3-4
    public const int kPitchCenterX = 336;
    public const int kPitchCenterY = 449;

    // referee.cpp:24-31 — RefereeState enum.
    public const int kRefOffScreen        = 0;
    public const int kRefIncoming         = 1;
    public const int kRefWaitingPlayer    = 2;
    public const int kRefAboutToGiveCard  = 3;
    public const int kRefBooking          = 4;
    public const int kRefLeaving          = 5;

    // referee.cpp:33-39 — CardHanding enum.
    public const int kNoCard             = 0;
    public const int kYellowCard         = 1;
    public const int kRedCard            = 2;
    public const int kSecondYellowCard   = 3;

    // swos.h:139 — Direction enum (kFacingTop=0, kFacingLeft=6).
    private const int kFacingTop  = 0;
    private const int kFacingLeft = 6;

    // Sprite.h:29 — PlayerState::kBooked.
    private const int kPlayerStateBooked = 12;

    // GameState::kInProgress (swos.h:592).
    private const short kGameStateInProgress = 100;

    // updateBookedPlayerNumberSprite uses kSmallDigit1 (sprites.h:64) as the
    // base sprite index for player-number 1.
    private const int kSmallDigit1 = 1188;

    // referee.cpp:107-110 — player-number blink table (30 bytes).
    // Indices 0-11 alternate 0/9 (sprite frame off/on); 12-28 are 0 (hidden);
    // entry 29 is -1 (sentinel = leave).
    private static readonly sbyte[] kPlayerNumberBlinkTable = new sbyte[30]
    {
        0, 9, 0, 9, 0, 9, 0, 9, 0, 9, 0, 9, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1,
    };

    // ---- Debug/telemetry counters --------------------------------------
    // Mirrors the UpdatePlayers.s_fallbackChases* pattern so callers (smoke
    // test, Godot dev overlay) can verify the FSM is actually advancing.
    // Reset at process start; never written outside this file.
    public static int DbgActivations         { get; private set; }
    public static int DbgEnteredIncoming     { get; private set; }
    public static int DbgEnteredWaiting      { get; private set; }
    public static int DbgEnteredAboutToGive  { get; private set; }
    public static int DbgEnteredBooking      { get; private set; }
    public static int DbgEnteredLeaving      { get; private set; }
    public static int DbgEnteredOffScreen    { get; private set; }
    public static int DbgYellowCards         { get; private set; }
    public static int DbgRedCards            { get; private set; }
    public static int DbgSecondYellowCards   { get; private set; }
    public static int DbgPlayersSentAway     { get; private set; }

    public static void ResetDebugCounters()
    {
        DbgActivations         = 0;
        DbgEnteredIncoming     = 0;
        DbgEnteredWaiting      = 0;
        DbgEnteredAboutToGive  = 0;
        DbgEnteredBooking      = 0;
        DbgEnteredLeaving      = 0;
        DbgEnteredOffScreen    = 0;
        DbgYellowCards         = 0;
        DbgRedCards            = 0;
        DbgSecondYellowCards   = 0;
        DbgPlayersSentAway     = 0;
    }

    // Called by UpdatePlayers.CheckIfThisPlayerGettingBooked when the booked
    // player arrives at the foul spot and flips refState to kRefAboutToGiveCard
    // (updatePlayers.cpp:8997). Keeps the FSM-transition counter colocated with
    // the other Dbg* counters even though the write happens outside this file.
    public static void NotifyEnteredAboutToGiveCard()
    {
        DbgEnteredAboutToGive++;
    }

    // referee.cpp:50-75 — activateReferee.
    // Called when a foul is registered. Sets up referee sprite to walk in from
    // off-screen toward the foul position.
    public static void ActivateReferee()
    {
        DbgActivations++;
        short whichCardOnAct = Memory.ReadSignedWord(Memory.Addr.whichCard);
        if (whichCardOnAct == kYellowCard)        DbgYellowCards++;
        else if (whichCardOnAct == kRedCard)      DbgRedCards++;
        else if (whichCardOnAct == kSecondYellowCard) DbgSecondYellowCards++;
        // referee.cpp:52-53 — destination = foul position + small offsets.
        short foulX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
        short foulY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);

        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDestX, foulX + 28);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDestY, foulY + 5);

        // referee.cpp:55-58 — random horizontal offset for starting position.
        // SWOS::rand() returns 16-bit unsigned; dividing by 8 gives 0..8191.
        int xOffset = SwosRand() / 8;
        if (foulX >= kPitchCenterX)
            xOffset = -xOffset;

        // referee.cpp:60-64 — starting Y depends on which half of the pitch
        // the foul was on (foul in upper half → ref starts below; foul in
        // lower half → ref starts above).
        int cameraY = (int)GetCameraY();
        int refStartY = cameraY - 20;
        if (foulY <= kPitchCenterY)
            refStartY = cameraY + 215;

        // referee.cpp:66-68.
        int destX = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffDestX);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffX + 2, destX + xOffset);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffY + 2, refStartY);

        // referee.cpp:68 — speed.
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffSpeed, kRefereeSpeed);

        // referee.cpp:69 — show().
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffVisible, 1);

        // referee.cpp:71-72.
        MarkDisplaySpritesDirty();
        InitRefereeAnimationTable(Memory.Addr.refComingAnimTable);

        // referee.cpp:74.
        Memory.WriteWord(Memory.Addr.refState, kRefIncoming);
        DbgEnteredIncoming++;
    }

    // referee.cpp:77-80 — refereeActive.
    public static bool RefereeActive()
    {
        return Memory.ReadSignedWord(Memory.Addr.refState) != kRefOffScreen;
    }

    // ---- Read-only render accessors (task #181) -------------------------------
    // The host renderer (Main.cs) reads these each frame to draw the referee +
    // card sprites. Pure getters — no side effects, no FSM logic. The referee
    // Sprite fields live on RefereeSprite (a Memory-backed view outside the
    // 22-player pool); these surface the subset the renderer needs so Main.cs
    // doesn't have to reach into raw Sprite offsets.
    public static int   RefState        => Memory.ReadSignedWord(Memory.Addr.refState);
    public static int   RefWhichCard    => Memory.ReadSignedWord(Memory.Addr.whichCard);
    public static bool  RefVisible      => Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffVisible) != 0;
    public static int   RefImageIndex   => Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffImageIndex);
    // Whole-pixel SWOS-world position (Q16.16 integer part) + z lift.
    public static int   RefWorldX       => Memory.ReadSignedDword(RefereeSprite.Base + RefereeSprite.OffX) >> 16;
    public static int   RefWorldY       => Memory.ReadSignedDword(RefereeSprite.Base + RefereeSprite.OffY) >> 16;
    public static int   RefWorldZ       => Memory.ReadSignedDword(RefereeSprite.Base + RefereeSprite.OffZ) >> 16;
    // Sprite-ordinal band for the referee (sprites.h:72-73).
    public const int    kRefereeSpriteStart = 1273;
    public const int    kRefereeSpriteEnd   = 1283;
    public const int    kYellowCardPose     = 1280;
    public const int    kRedCardPose        = 1281;

    // referee.cpp:82-85 — cardHandingInProgress.
    public static bool CardHandingInProgress()
    {
        return Memory.ReadSignedWord(Memory.Addr.whichCard) != kNoCard;
    }

    // ====================================================================
    // Referee sprite onScreen maintenance — DrawSprites clip test
    // (swos.asm:100200-100317, DrawSprites @@sprites_loop body)
    // ====================================================================
    //
    // In the original, `Sprite.onScreen` is written every frame by the
    // DrawSprites render pass: a sprite whose camera-relative rectangle
    // intersects the 336×200 view window is marked drawn (onScreen = 1,
    // swos.asm:100260), otherwise clipped (onScreen = 0, swos.asm:100315).
    // Our port has no DrawSprites — Godot renders — so onScreen froze at
    // its init value of 1 for every sprite. That deadlocks the referee
    // state machine: kRefLeaving → kRefOffScreen (referee.cpp:215-221)
    // fires ONLY when onScreen == 0, and the breakCameraMode==3 arrival
    // wait (gameLoop.cpp:1506-1511) blocks the whole restart ladder while
    // refState != 0 — the task #170 "free kick never taken" freeze after
    // a booking (bookings need a human team, so AI-vs-AI smokes never saw
    // it).
    //
    // Mechanical port of the clip test, applied to the REFEREE sprite
    // (the one consumer whose state machine depends on the flag; player
    // sprites keep the port's always-on value — flipping them would also
    // change the Mode3 off-screen arrival exemption and is out of scope
    // here):
    //   D1 = sprite.x - graphics.centerX - g_cameraX
    //   D2 = sprite.y - graphics.centerY - g_cameraY - sprite.z
    //   clipped when D1 >= 336 || D2 >= 200 || D1 <= -pixWidth || D2 <= -nlines
    //
    // SpriteGraphics tables aren't ported; the referee frame is a standard
    // 16×32 player-sized cell (centerX 8 / centerY 16 nominal). The dims
    // only move the clip edge by a few pixels — the leaving walk exits the
    // 200-px window by hundreds of pixels, so nominal metrics are safe.
    private const short kRefSprCenterX  = 8;    // SpriteGraphics.centerX (nominal)
    private const short kRefSprCenterY  = 16;   // SpriteGraphics.centerY (nominal)
    private const short kRefSprPixWidth = 16;   // SpriteGraphics.pixWidth (nominal)
    private const short kRefSprNLines   = 32;   // SpriteGraphics.nlines (nominal)

    private static void UpdateRefereeOnScreenFlag()
    {
        short x = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffX + 2);
        short y = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffY + 2);
        short z = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffZ + 2);

        // swos.asm:100209-100221 — D1/D2 camera-relative top-left corner.
        int d1 = x - kRefSprCenterX - Camera.GetCameraXWhole();
        int d2 = y - kRefSprCenterY - Camera.GetCameraYWhole() - z;

        // swos.asm:100223-100241 — the four clip comparisons (jge/jle @@clipped).
        bool clipped = d1 >= 336 || d2 >= 200
                    || d1 <= -kRefSprPixWidth || d2 <= -kRefSprNLines;

        // swos.asm:100260 (drawn) / :100315 (clipped).
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffOnScreen,
                         clipped ? (short)0 : (short)1);
    }

    // referee.cpp:87-99 — updateReferee.
    // Main per-tick entry point. If referee is on-screen, advance animation +
    // state machine. If off-screen but game in progress, move sprite (walking
    // in/out). Otherwise (game stopped, ref off-screen) only state machine ticks.
    public static void UpdateReferee()
    {
        if (!(RefereeActive() &&
              Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffVisible) != 0))
            return;

        // Port-side stand-in for the DrawSprites per-frame onScreen update
        // (see UpdateRefereeOnScreenFlag above). Runs only while the referee
        // is active, mirroring the window in which the original's flag value
        // matters to this state machine.
        UpdateRefereeOnScreenFlag();

        short onScreen = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffOnScreen);
        if (onScreen != 0)
        {
            // referee.cpp:91-92 — already on screen: animate + state machine.
            SpriteUpdate.UpdateSpriteAnimation(RefereeSprite.Base);
            UpdateRefereeState();
        }
        else
        {
            short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gameStatePl == kGameStateInProgress)
            {
                // referee.cpp:94 — game in progress, walking on/off — move sprite.
                // Real port: SpriteUpdate.MoveSprite (updateSprite.cpp:157-186).
                // RefereeSprite offsets alias PlayerSprite offsets (see class
                // alias below: OffX, OffY, OffDeltaX/Y, OffDestX/Y all forward
                // to PlayerSprite.*) so the same code applies to the referee.
                SpriteUpdate.MoveSprite(RefereeSprite.Base);
            }
            else
            {
                // referee.cpp:96 — game stopped, advance state machine only.
                UpdateRefereeState();
            }
        }
    }

    // referee.cpp:101-151 — updateBookedPlayerNumberSprite.
    // Renders + blinks the player-number sprite (small digit) over the booked
    // player's head during the kRefBooking phase. Uses the 30-entry blink
    // table to alternate the digit on/off, ending with sentinel -1 = leave.
    public static void UpdateBookedPlayerNumberSprite()
    {
        // referee.cpp:103 — clear the previous frame's image.
        Memory.WriteWord(BookedPlayerNumberSprite.Base + BookedPlayerNumberSprite.OffImageIndex, -1);

        short whichCard = Memory.ReadSignedWord(Memory.Addr.whichCard);
        short refState  = Memory.ReadSignedWord(Memory.Addr.refState);
        if (whichCard == 0 || refState != kRefBooking) return;

        int bookedPlayer = Memory.ReadSignedDword(Memory.Addr.bookedPlayer);
        // referee.cpp:105 — assert(bookedPlayer)
        if (bookedPlayer == 0) return;

        // referee.cpp:106 — only blink while booked-state animation is running.
        byte plState = Memory.ReadByte(bookedPlayer + PlayerSprite.OffPlayerState);
        if (plState != kPlayerStateBooked) return;

        // referee.cpp:111 — refTimer += lastFrameTicks.
        short timer = (short)(Memory.ReadSignedWord(Memory.Addr.refTimer)
                              + Memory.ReadSignedWord(Memory.Addr.lastFrameTicks));
        Memory.WriteWord(Memory.Addr.refTimer, timer);

        // referee.cpp:113-115 — index = refTimer >> 3 (one frame per 8 ticks).
        int index = timer >> 3;
        if (index < 0 || index >= kPlayerNumberBlinkTable.Length) return;
        int action = kPlayerNumberBlinkTable[index];

        if (action > 0)
        {
            // referee.cpp:127-139 — paint the player-number digit over the
            // player's head. action value (9) is just "non-zero" — actual
            // sprite is kSmallDigit1 + (shirtNumber - 1).
            int lastTeamBooked = Memory.ReadSignedDword(Memory.Addr.lastTeamBooked);
            if (lastTeamBooked == 0) return;

            int teamGame = Memory.ReadSignedDword(lastTeamBooked + TeamData.OffInGameTeamPtr);
            // referee.cpp:130 — playerOrdinal - 1 gives the array index into
            // teamGame.players (which is a per-team player info array).
            short playerOrdinal = Memory.ReadSignedWord(bookedPlayer + PlayerSprite.OffPlayerOrdinal);
            int shirtNumber = GetTeamGamePlayerShirtNumber(teamGame, playerOrdinal - 1);
            int imageIndex = kSmallDigit1 + shirtNumber - 1;

            Memory.WriteWord(BookedPlayerNumberSprite.Base + BookedPlayerNumberSprite.OffImageIndex, imageIndex);

            // referee.cpp:135-138 — copy booked player position to the digit sprite.
            // Production path: copy full Q16.16 X/Y (the SWOS_TEST path uses whole-pixel).
            int bpX = Memory.ReadSignedDword(bookedPlayer + PlayerSprite.OffX);
            int bpY = Memory.ReadSignedDword(bookedPlayer + PlayerSprite.OffY);
            Memory.WriteDword(BookedPlayerNumberSprite.Base + BookedPlayerNumberSprite.OffX, bpX);
            Memory.WriteDword(BookedPlayerNumberSprite.Base + BookedPlayerNumberSprite.OffY, bpY);

            // referee.cpp:139 — z = kPlayerNumberOffset (digit floats above head).
            Memory.WriteDword(BookedPlayerNumberSprite.Base + BookedPlayerNumberSprite.OffZ,
                              kPlayerNumberOffset << 16);
        }
        else if (action < 0)
        {
            // referee.cpp:140-148 — sentinel: transition to leaving + red-card off.
            PutRefereeToLeavingState();

            // referee.cpp:143-144 — kRedCard bit (also matches kSecondYellowCard which is 3).
            if ((whichCard & kRedCard) != 0)
                SendPlayerAway();

            Memory.WriteWord(Memory.Addr.whichCard, 0);
            Memory.WriteDword(Memory.Addr.bookedPlayer, 0);
        }
    }

    // referee.cpp:163-185 — removeReferee.
    // Hides referee + resets to hiding position. Called when leaving anim
    // completes (kRefLeaving + offscreen).
    public static void RemoveReferee()
    {
        Memory.WriteWord(Memory.Addr.refState, kRefOffScreen);
        // hide().
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffVisible, 0);

        // Production path: write whole Q16.16 = hidingPlace << 16.
        Memory.WriteDword(RefereeSprite.Base + RefereeSprite.OffX, kRefereeHidingPlaceX << 16);
        Memory.WriteDword(RefereeSprite.Base + RefereeSprite.OffY, kRefereeHidingPlaceY << 16);

        Memory.WriteDword(RefereeSprite.Base + RefereeSprite.OffZ, 0);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDestX, kRefereeHidingPlaceX);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDestY, kRefereeHidingPlaceY);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffSpeed, 0);
        Memory.WriteByte(RefereeSprite.Base + RefereeSprite.OffPlayerDownTimer, 0);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffFrameIndex, -1);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffCycleFramesTimer, 1);
        // clearImage().
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffImageIndex, -1);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDirection, kFacingTop);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffOnScreen, 1);

        InitRefereeAnimationTable(Memory.Addr.refWaitingAnimTable);
    }

    // referee.cpp:192-245 — updateRefereeState.
    // The state-machine switch:
    //   kRefAboutToGiveCard → kRefBooking + animation table by card type.
    //   kRefLeaving         → kRefOffScreen when off-screen (fall through to
    //                         shared Incoming/Leaving movement path otherwise).
    //   kRefIncoming        → recompute direction, move, switch to Waiting on arrival.
    //
    // The fall-through from kRefLeaving to kRefIncoming below the `break`-less
    // case is intentional in the original; we preserve that.
    private static void UpdateRefereeState()
    {
        short refState = Memory.ReadSignedWord(Memory.Addr.refState);
        short whichCard = Memory.ReadSignedWord(Memory.Addr.whichCard);

        switch (refState)
        {
            case kRefAboutToGiveCard:
                // referee.cpp:195-213.
                // assert(whichCard != kNoCard).
                Memory.WriteWord(Memory.Addr.refState, kRefBooking);
                DbgEnteredBooking++;
                switch (whichCard)
                {
                    case kRedCard:
                        InitRefereeAnimationTable(Memory.Addr.refRedCardAnimTable);
                        StubEnqueueRedCardSample();
                        break;
                    case kYellowCard:
                        InitRefereeAnimationTable(Memory.Addr.refYellowCardAnimTable);
                        StubEnqueueYellowCardSample();
                        break;
                    case kSecondYellowCard:
                        InitRefereeAnimationTable(Memory.Addr.refSecondYellowAnimTable);
                        StubEnqueueRedCardSample();
                        break;
                }
                break;

            case kRefLeaving:
                // referee.cpp:215-221 — off-screen check; either transition off or
                // fall through to the shared Incoming/Leaving direction+move path.
                if (Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffOnScreen) == 0)
                {
                    Memory.WriteWord(Memory.Addr.refState, kRefOffScreen);
                    DbgEnteredOffScreen++;
                    // hide().
                    Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffVisible, 0);
                    MarkDisplaySpritesDirty();
                    break;
                }
                // referee.cpp:222 — fall-through.
                goto case kRefIncoming;

            case kRefIncoming:
                // referee.cpp:224-242 — recompute direction, move, transition on arrival.
                Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffSpeed, kRefereeSpeed);
                short oldDirection = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffDirection);

                SpriteUpdate.UpdateSpriteDirectionAndDeltas(RefereeSprite.Base);

                // referee.cpp:230 SWOS_TEST: fullDirection = 0. Production path leaves it.

                short newDirection = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffDirection);
                if (oldDirection != newDirection)
                    InitRefereeAnimationTable(Memory.Addr.refComingAnimTable);

                // referee.cpp:236 — move toward player and stop on arrival.
                // Real port — SpriteUpdate.MoveSprite (updateSprite.cpp:157-186).
                SpriteUpdate.MoveSprite(RefereeSprite.Base);

                // referee.cpp:238 — stationary() check (deltaX == 0 && deltaY == 0).
                int dx = Memory.ReadSignedDword(RefereeSprite.Base + RefereeSprite.OffDeltaX);
                int dy = Memory.ReadSignedDword(RefereeSprite.Base + RefereeSprite.OffDeltaY);
                if (dx == 0 && dy == 0)
                {
                    Memory.WriteWord(Memory.Addr.refState, kRefWaitingPlayer);
                    DbgEnteredWaiting++;
                    Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDirection, kFacingLeft);
                    InitRefereeAnimationTable(Memory.Addr.refWaitingAnimTable);
                }
                break;
        }
    }

    // referee.cpp:247-258 — putRefereeToLeavingState.
    // Sets destination to a random X offset + Y near top/bottom border depending
    // on which half the foul was in. Triggers walking-out path.
    private static void PutRefereeToLeavingState()
    {
        // referee.cpp:249 — rand() / 4 - 32 gives a value in [-32, 31].
        // Original assumes rand() returns 0..255 (after div-4 → 0..63, minus 32).
        int xOffset = (SwosRand() / 4) - 32;

        short refX = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffX + 2);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDestX, refX + xOffset);

        short foulY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);
        int destY = foulY > kPitchCenterY ? kRefereeLeavingTopDestY : kRefereeLeavingBottomDestY;
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffDestY, destY);

        InitRefereeAnimationTable(Memory.Addr.refComingAnimTable);

        Memory.WriteWord(Memory.Addr.refState, kRefLeaving);
        DbgEnteredLeaving++;
    }

    // referee.cpp:260-273 — sendPlayerAway.
    // Marks the booked player as sent off (cards = -1, sentAway = 1) and
    // sets destination off-pitch. If they were the marked player for their
    // team, clear the marker.
    private static void SendPlayerAway()
    {
        int bookedPlayer = Memory.ReadSignedDword(Memory.Addr.bookedPlayer);
        int lastTeamBooked = Memory.ReadSignedDword(Memory.Addr.lastTeamBooked);
        if (bookedPlayer == 0 || lastTeamBooked == 0) return;

        int teamGame = Memory.ReadSignedDword(lastTeamBooked + TeamData.OffInGameTeamPtr);
        short playerOrdinal = Memory.ReadSignedWord(bookedPlayer + PlayerSprite.OffPlayerOrdinal);

        // referee.cpp:266-267 — clear markedPlayer if it was this player.
        int markedPlayer = GetTeamGameMarkedPlayer(teamGame);
        if (markedPlayer == playerOrdinal - 1)
            SetTeamGameMarkedPlayer(teamGame, -1);

        // referee.cpp:269-272.
        Memory.WriteWord(bookedPlayer + PlayerSprite.OffCards, -1);
        Memory.WriteWord(bookedPlayer + PlayerSprite.OffSentAway, 1);
        Memory.WriteWord(bookedPlayer + PlayerSprite.OffDestX, kSentOffPlayerX);
        Memory.WriteWord(bookedPlayer + PlayerSprite.OffDestY, kSentOffPlayerY);
        DbgPlayersSentAway++;
    }

    // referee.cpp:275-287 — initRefereeAnimationTable.
    // Reads a RefereeAnimationTable struct from the given address and configures
    // the referee sprite's frame-delay + frameIndicesTable pointer for the
    // current direction.
    //
    // RefereeAnimationTable layout (inferred from the asm and sprite struct):
    //   word numCycles                       (used as frameDelay)
    //   dword indicesTable[8]                 (per-direction pointer)
    private static void InitRefereeAnimationTable(int animTableAddr)
    {
        // referee.cpp:277-278 — read numCycles + indices pointer.
        short delay = Memory.ReadSignedWord(animTableAddr);
        // referee.cpp:281 — frameTable[direction] — direction is referee's current direction.
        short direction = Memory.ReadSignedWord(RefereeSprite.Base + RefereeSprite.OffDirection);

        // Each per-direction entry is a 4-byte pointer; numCycles is 2 bytes,
        // so indicesTable starts at offset +2 within the struct.
        int frameTablePtr = Memory.ReadSignedDword(animTableAddr + 2 + direction * 4);

        // referee.cpp:280-281.
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffFrameDelay, delay);
        Memory.WriteDword(RefereeSprite.Base + RefereeSprite.OffFrameIndicesTable, frameTablePtr);

        // referee.cpp:284-286.
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffFrameSwitchCounter, -1);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffFrameIndex, -1);
        Memory.WriteWord(RefereeSprite.Base + RefereeSprite.OffCycleFramesTimer, 1);
    }

    // ---- Stubs ---------------------------------------------------------------
    // These call into not-yet-ported helpers from external/swos-port/src/.

    // SWOS::rand — table-driven xor-stream PRNG. Returns a byte (0..255).
    // referee.cpp:57 uses `Rand() / 8` for the ref's spawn X jitter (~0..31
    // px offset); referee.cpp:249 uses `Rand() / 4 - 32` for the leaving-state
    // X jitter (signed -32..31). Wired through SwosVm.Rng — canonical
    // deterministic-lockstep stream-1 source.
    // Source: external/swos-port/src/util/random.cpp
    private static int SwosRand() => Rng.NextByte();

    // camera.cpp:82-85 — getCameraY(). Original returns Q16.16 FixedPoint;
    // referee.cpp:60 casts via `static_cast<int>` to whole pixels
    // (FixedPoint's `int()` returns the integer part). Camera.cs exposes
    // that directly via GetCameraYWhole.
    // Source: external/swos-port/src/game/camera.cpp:82,
    //         external/swos-port/src/game/referee.cpp:60.
    private static int GetCameraY() => Camera.GetCameraYWhole();

    // Mechanical port of gameSprites.cpp:101-111 — initDisplaySprites.
    // Original:
    //   void initDisplaySprites() {
    //       m_numSpritesToRender = 0;
    //       for (auto sprite : kAllSprites)
    //           if (sprite->visible)
    //               m_sortedSprites[m_numSpritesToRender++] = sprite;
    //       sortDisplaySprites();
    //   }
    //
    // m_sortedSprites + m_numSpritesToRender are owned by the host renderer
    // (Godot iterates the sprite pool directly each frame). The only
    // cross-layer effect we need is "tell the renderer the sprite set has
    // changed" so cached per-frame draw data invalidates. Mirrors the dirty-
    // flag pattern used by Result.MarkScorersTextDirty.
    //
    // Call sites (Referee.cs:139, 341) fire on the same boundary the original
    // does — after pushing the referee / booked-player digits into the live
    // sprite list. Without the flag, a Godot consumer that caches per-frame
    // sort orders would miss the referee sprite the first frame it appears.
    //
    // Source: external/swos-port/src/sprites/gameSprites.cpp:101-111.
    private static void MarkDisplaySpritesDirty()
    {
        Memory.WriteWord(Memory.Addr.displaySpritesDirtyFlag, 1);
    }

    // updateSprite.cpp:moveSprite is ported to SpriteUpdate.MoveSprite. Call
    // sites above (referee.cpp:94, referee.cpp:236) go directly there.
    //
    // updateSprite.cpp:updateSpriteAnimation is now ported to
    // SpriteUpdate.UpdateSpriteAnimation. Call site above goes directly there.

    // External: comments.cpp enqueueRedCardSample / enqueueYellowCardSample.
    // Plays the referee whistle + voice sample.
    // TODO from external/swos-port/src/audio/comments.cpp
    private static void StubEnqueueRedCardSample()    { /* TODO */ }
    private static void StubEnqueueYellowCardSample() { /* TODO */ }

    // Reads players[index].shirtNumber from a TeamGame (in-game team) struct.
    // PlayerInfo struct (swos.h:162) has shirtNumber at offset +3.
    //
    // TeamGame struct layout (swos.h:296-315):
    //   prShirtType..prSocksCol   (5×word) = 10 bytes  (+0..+9)
    //   secShirtType..secSocksCol (5×word) = 10 bytes  (+10..+19)
    //   markedPlayer              (int16)  = 2 bytes   (+20,+21)
    //   teamName[17]              (chars)  = 17 bytes  (+22..+38)
    //   unk_1                     (byte)   = 1 byte    (+39)
    //   numOwnGoals               (byte)   = 1 byte    (+40)
    //   unk_2                     (byte)   = 1 byte    (+41)
    //   players[kNumPlayersInTeam] starts at +42, 61 bytes per PlayerInfo
    //
    // Source: external/swos-port/src/swos/swos.h:296-315.
    private const int kTeamGameOffPlayers     = 42;
    private const int kPlayerInfoSize         = 61;
    private const int kPlayerInfoOffShirtNum  = 3;
    private static int GetTeamGamePlayerShirtNumber(int teamGameAddr, int slotInTeam)
    {
        if (teamGameAddr == 0) return 1;
        return Memory.ReadByte(teamGameAddr
                               + kTeamGameOffPlayers
                               + slotInTeam * kPlayerInfoSize
                               + kPlayerInfoOffShirtNum);
    }

    // Reads / writes TeamGame.markedPlayer (signed word at offset +20).
    // Layout from external/swos-port/src/swos/swos.h:296-315 — 10 word fields
    // (prShirtType .. secSocksCol) precede markedPlayer at byte offset 20.
    // Mechanical port: direct field access — sentinel -1 when teamGameAddr=0
    // mirrors the original "no marker" state.
    private const int kOffTeamGameMarkedPlayer = 20;
    private static int  GetTeamGameMarkedPlayer(int teamGameAddr)
        => teamGameAddr == 0 ? -1 : Memory.ReadSignedWord(teamGameAddr + kOffTeamGameMarkedPlayer);
    private static void SetTeamGameMarkedPlayer(int teamGameAddr, int v)
    {
        if (teamGameAddr != 0)
            Memory.WriteWord(teamGameAddr + kOffTeamGameMarkedPlayer, v);
    }
}

// Memory-backed sprite view for the referee. Mirrors swos-port's
// `static Sprite m_refereeSprite{3}` — a Sprite outside the 22-player pool,
// allocated in the data section. teamNumber=3 is the "non-player" marker.
//
// Sprite struct layout matches PlayerSprite / BallSprite (110 bytes).
public static class RefereeSprite
{
    // Allocate at 0x4FD00 — after TeamData bottom (which ends at 0x4FCFF).
    // 128 bytes reserved (padded from 110 for alignment).
    public const int Base = 0x4FD00;

    // Field offsets — match PlayerSprite.Off* values (same 110-byte Sprite struct).
    public const int OffPlayerState         = PlayerSprite.OffPlayerState;
    public const int OffPlayerDownTimer     = PlayerSprite.OffPlayerDownTimer;
    public const int OffFrameIndicesTable   = PlayerSprite.OffFrameIndicesTable;
    public const int OffFrameIndex          = PlayerSprite.OffFrameIndex;
    public const int OffFrameDelay          = PlayerSprite.OffFrameDelay;
    public const int OffCycleFramesTimer    = PlayerSprite.OffCycleFramesTimer;
    public const int OffFrameSwitchCounter  = PlayerSprite.OffFrameSwitchCounter;
    public const int OffX                   = PlayerSprite.OffX;
    public const int OffY                   = PlayerSprite.OffY;
    public const int OffZ                   = PlayerSprite.OffZ;
    public const int OffDirection           = PlayerSprite.OffDirection;
    public const int OffSpeed               = PlayerSprite.OffSpeed;
    public const int OffDeltaX              = PlayerSprite.OffDeltaX;
    public const int OffDeltaY              = PlayerSprite.OffDeltaY;
    public const int OffDestX               = PlayerSprite.OffDestX;
    public const int OffDestY               = PlayerSprite.OffDestY;
    public const int OffVisible             = PlayerSprite.OffVisible;
    public const int OffImageIndex          = PlayerSprite.OffImageIndex;
    public const int OffOnScreen            = PlayerSprite.OffOnScreen;
}

// Memory-backed sprite view for the booked-player-number digit (a small
// floating sprite painted above the booked player during card animation).
// Mirrors swos-port's `static Sprite m_bookedPlayerNumberSprite{3}`.
public static class BookedPlayerNumberSprite
{
    // Allocate at 0x4FD80 — 128 bytes after RefereeSprite.
    public const int Base = 0x4FD80;

    public const int OffX          = PlayerSprite.OffX;
    public const int OffY          = PlayerSprite.OffY;
    public const int OffZ          = PlayerSprite.OffZ;
    public const int OffImageIndex = PlayerSprite.OffImageIndex;
}
