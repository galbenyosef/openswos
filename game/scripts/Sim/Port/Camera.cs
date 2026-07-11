namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Camera position update ported from external/swos-port/src/game/camera.cpp.
//
// SCOPE: This port owns the per-tick camera STATE update (m_cameraX / m_cameraY).
// Godot renders the actual screen camera; gameplay code (referee, AI, off-pitch
// player clamping) reads m_cameraX/Y to anticipate "what's currently visible".
// Therefore the math here must port — Godot just consumes the result.
//
// We DO NOT call:
//   - actual Godot camera transforms (handled host-side)
//   - bench/penalty-shootout/booking-mode UI side effects beyond writing state
//
// Mode selection (camera.cpp:97-119, moveCamera):
//   - cardHandingInProgress         → bookingPlayerMode (focus on booked player)
//   - playingPenalties              → penaltyShootoutMode (fixed coord)
//   - g_waitForPlayerToGoInTimer    → benchMode(substituting=true)
//   - m_leavingBenchMode            → leavingBenchMode (walk back to pitch)
//   - inBench()                     → benchMode(substituting=false)
//   - else                          → standardMode (follow ball / show result / etc.)
//
// updateCameraCoordinates (camera.cpp:219-249) is the per-tick numerical core:
//   1. Subtract VGA half-width/height to convert "target focus" → "top-left dest".
//   2. Add accumulated velocity (for smoother panning during play).
//   3. Clip to pitch limits (clipCameraDestination).
//   4. Compute delta = (dest - current) / 16 (slew rate).
//   5. Clamp |delta| <= 5 (clipCameraMovement).
//   6. Apply delta and constrain to absolute pitch bounds.
//
// Constants — camera.cpp:8-39.
public static class Camera
{
    // camera.cpp:8-9 — training-game start (off-pitch, used at init only).
    public const int kTrainingGameStartX = 168;
    public const int kTrainingGameStartY = 313;

    // camera.cpp:11-13 — non-training start positions (picked randomly between
    // top and bottom; both share the same X).
    public const int kTopStartLocationY    = 16;
    public const int kBottomStartLocationY = 664;
    public const int kCenterX              = 176;

    // camera.cpp:15-17.
    public const int kPenaltyShootoutCameraX  = 336;
    public const int kPenaltyShootoutCameraY  = 107;
    public const int kLeavingBenchCameraDestX = 211;

    // camera.cpp:20-21.
    public const int kBenchSlideAreaStartY = 339;
    public const int kBenchSlideAreaEndY   = 359;

    // camera.cpp:23-24.
    public const int kPlayersOutsidePitchX = 590;
    public const int kTopGoalLine          = 129;

    // camera.cpp:26-30.
    public const int kPitchMaxX          = 352;
    public const int kPitchMinY          = 16;
    public const int kPitchMaxY          = 664;
    public const int kTrainingPitchMinY  = 80;
    public const int kTrainingPitchMaxY  = 616;

    // camera.cpp:32-34.
    public const int kPitchSideCameraLimitDuringBreak = 37;
    public const int kPitchSideCameraLimitDuringGame  = 63;
    public const int kSubstituteCameraLimit           = 51;

    // camera.cpp:36-39.
    public const int kCameraMinX = 0;
    public const int kCameraMaxX = kPitchMaxX;
    public const int kCameraMinY = kPitchMinY;
    public const int kCameraMaxY = 680;

    // pitchConstants.h:3-4.
    public const int kPitchCenterX = 336;
    public const int kPitchCenterY = 449;

    // render.h:3-4.
    public const int kVgaWidth  = 320;
    public const int kVgaHeight = 200;

    // camera.cpp:211 — kCameraLeavingBenchXLimit (local constant).
    public const int kCameraLeavingBenchXLimit = 35;

    // GameState enum (swos.h:568-595) — only the values referenced here.
    public const short kGameStateInProgress         = 100;
    public const short kGameStateCornerLeft         = 4;
    public const short kGameStateCornerRight        = 5;
    public const short kGameStateThrowInForwardRight = 15;
    public const short kGameStateThrowInBackLeft     = 20;
    public const short kGameStateStartingGame        = 21;
    public const short kGameStateCameraGoingToShowers = 22;
    public const short kGameStateGoingToHalftime     = 23;
    public const short kGameStatePlayersGoingToShower = 24;
    public const short kGameStateResultOnHalftime    = 25;
    public const short kGameStateResultAfterTheGame  = 26;
    public const short kGameStateFirstHalfEnded      = 29;
    public const short kGameStateGameEnded           = 30;

    // camera.cpp:351-353 — velocity step + cap (per-tick adjustments).
    private const int kVelocityIncrement = 2;
    private const int kMaxVelocity       = 40;

    // camera.cpp:273 — max per-tick camera delta (whole pixels; Q16.16 in code).
    private const int kMaxCameraMovement = 5;

    // Internal struct for the params bundle (camera.cpp:46-56).
    private struct CameraParams
    {
        public int XDest;        // whole pixels — we keep ints, not Q16.16 (no float)
        public int YDest;
        public int XLimit;
        public int XVelocity;
        public int YVelocity;
    }

    private static CameraParams MakeParams(int xDest, int yDest, int xLimit = 0, int xVel = 0, int yVel = 0)
        => new CameraParams { XDest = xDest, YDest = yDest, XLimit = xLimit, XVelocity = xVel, YVelocity = yVel };

    // camera.cpp:77-95 — getCameraX/Y + setCameraX/Y.
    // Read/write Q16.16 representation in Memory. Whole-pixel access via shr 16.
    public static int GetCameraX() => Memory.ReadSignedDword(Memory.Addr.cameraX);
    public static int GetCameraY() => Memory.ReadSignedDword(Memory.Addr.cameraY);
    public static int GetCameraXWhole() => GetCameraX() >> 16;
    public static int GetCameraYWhole() => GetCameraY() >> 16;
    public static void SetCameraX(int q16_16) => Memory.WriteDword(Memory.Addr.cameraX, q16_16);
    public static void SetCameraY(int q16_16) => Memory.WriteDword(Memory.Addr.cameraY, q16_16);

    // camera.cpp:97-119 — moveCamera.
    // Picks mode, then runs updateCameraCoordinates + updateCameraLeaving.
    public static void MoveCamera()
    {
        // pitch.cpp:144-145 — `swos.cameraCoordinatesValid = 1;` with the
        // upstream comment "unfortunately, this has to stay until
        // UpdateCameraBreakMode() is converted". In swos-port this is
        // re-asserted EVERY frame by drawPitch (called from drawFrame right
        // after coreGameUpdate, gameLoop.cpp:106); our renderer is Godot-side
        // so the per-frame assertion lives here instead — before the
        // showFansCounter early-out, because the original's drawPitch runs
        // during the fans intro too. GameLoop Mode2 (gameLoop.cpp:1365-1370)
        // gates the breakCameraMode 2 → 4 transition on this flag.
        // Source: external/swos-port/src/game/pitch/pitch.cpp:140-158.
        Memory.WriteWord(Memory.Addr.cameraCoordinatesValid, 1);

        // PORT-GLUE (match-start camera position, 2026-07-02) -----------------
        // The original seeds the camera BEFORE the first frame of a match:
        // initGameLoop (gameLoop.cpp:202-238) calls setCameraToInitialPosition()
        // at gameLoop.cpp:226, which puts the camera at (kCenterX=176,
        // kTopStartLocationY=16 | kBottomStartLocationY=664) — the crowd behind
        // one of the goals — for the showFansCounter intro (camera.cpp:121-132).
        // Our port had NO caller for SetCameraToInitialPosition, so cameraX/Y
        // stayed at Memory.Init()'s zeroes and the match opened staring at the
        // pitch's top-LEFT corner (SWOS (0,0)) instead of the fans view, then
        // panned in from the wrong corner ("gra zaczyna się w złym miejscu").
        //
        // The sanctioned call site is match init (Main.InitSwosVmFromMatchSetup,
        // mirroring gameLoop.cpp:226). This guard is a self-heal for boot paths
        // that miss it. It cannot misfire mid-match: every per-tick write goes
        // through ConstrainCameraToPitch (camera.cpp:285-295) which clamps
        // cameraY to [kCameraMinY=16, kCameraMaxY=680], SetCameraToInitialPosition
        // itself writes Y ∈ {16, 664, 313}, and the game-over snap writes
        // (176, 80) (gameLoop.cpp:40-41 kGameEndCameraX/Y) — so (0,0) exists
        // ONLY in the never-initialised state.
        if (GetCameraX() == 0 && GetCameraY() == 0)
            SetCameraToInitialPosition();

        // camera.cpp:99-100 — early-out while fans-counter is showing.
        if (Memory.ReadSignedWord(Memory.Addr.showFansCounter) != 0)
            return;

        CameraParams ps;

        if (Referee.CardHandingInProgress())
            ps = BookingPlayerMode();
        else if (Memory.ReadWord(Memory.Addr.playingPenalties) != 0)
            ps = PenaltyShootoutMode();
        else if (Memory.ReadSignedWord(Memory.Addr.g_waitForPlayerToGoInTimer) != 0)
            ps = BenchMode(true);
        else if (Memory.ReadSignedWord(Memory.Addr.leavingBenchMode) != 0)
            ps = LeavingBenchMode();
        else if (Bench.InBench())
            ps = BenchMode(false);
        else
            ps = StandardMode();

        UpdateCameraCoordinates(ps);
        UpdateCameraLeaving();
    }

    // camera.cpp:121-132 — setCameraToInitialPosition.
    public static void SetCameraToInitialPosition()
    {
        int startX, startY;
        if (Memory.ReadSignedWord(Memory.Addr.g_trainingGame) != 0)
        {
            startX = kTrainingGameStartX;
            startY = kTrainingGameStartY;
        }
        else
        {
            startX = kCenterX;
            // SWOS::rand() & 1 picks one of two start Ys (top vs bottom).
            // Source: external/swos-port/src/game/camera.cpp:166 — uses Rand()
            // for the same coin-flip between kBottomStartLocationY and
            // kTopStartLocationY constants.
            startY = (SwosRand() & 1) != 0 ? kBottomStartLocationY : kTopStartLocationY;
        }

        SetCameraX(startX << 16);
        SetCameraY(startY << 16);
    }

    // camera.cpp:134-137 — switchCameraToLeavingBenchMode.
    public static void SwitchCameraToLeavingBenchMode()
    {
        Memory.WriteWord(Memory.Addr.leavingBenchMode, 1);
    }

    // ---- Mode params -------------------------------------------------------

    // camera.cpp:139-142 — bookingPlayerMode.
    private static CameraParams BookingPlayerMode()
    {
        int bookedPlayer = Memory.ReadSignedDword(Memory.Addr.bookedPlayer);
        int x = bookedPlayer == 0 ? kPitchCenterX
                                  : Memory.ReadSignedWord(bookedPlayer + PlayerSprite.OffX + 2);
        int y = bookedPlayer == 0 ? kPitchCenterY
                                  : Memory.ReadSignedWord(bookedPlayer + PlayerSprite.OffY + 2);
        return MakeParams(x, y, kPitchSideCameraLimitDuringBreak);
    }

    // camera.cpp:144-147 — penaltyShootoutMode.
    private static CameraParams PenaltyShootoutMode()
        => MakeParams(kPenaltyShootoutCameraX, kPenaltyShootoutCameraY);

    // camera.cpp:149-153 — benchMode(substitutingPlayer).
    private static CameraParams BenchMode(bool substitutingPlayer)
    {
        int limit = substitutingPlayer ? kSubstituteCameraLimit : GetBenchCameraXLimit();
        int x = BenchCameraX();
        return MakeParams(x, kPitchCenterY, limit);
    }

    // camera.cpp:155-158 — leavingBenchMode.
    private static CameraParams LeavingBenchMode()
        => MakeParams(kLeavingBenchCameraDestX, kPitchCenterY, kPitchSideCameraLimitDuringBreak);

    // camera.cpp:160-207 — standardMode.
    private static CameraParams StandardMode()
    {
        int xDirection;
        int yDirection;
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);

        if (gameStatePl == kGameStateInProgress)
        {
            // camera.cpp:164-166 — follow ball deltas (Q16.16).
            xDirection = Memory.ReadSignedDword(BallSprite.Base + BallSprite.OffDeltaX);
            yDirection = Memory.ReadSignedDword(BallSprite.Base + BallSprite.OffDeltaY);
        }
        else
        {
            // camera.cpp:167-169 — getGameStoppedCameraDirections.
            (xDirection, yDirection) = GetGameStoppedCameraDirections();
        }

        // camera.cpp:171-172 — accumulate velocity.
        (int xVelocity, int yVelocity) = GetStandardModeCameraVelocity(xDirection, yDirection);

        int limit = kPitchSideCameraLimitDuringGame;

        // camera.cpp:176-181 — corner/throw-in tighter limit during break.
        if (gameStatePl != kGameStateInProgress)
        {
            short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
            bool cornerOrThrowIn =
                gameState == kGameStateCornerLeft  ||
                gameState == kGameStateCornerRight ||
                (gameState >= kGameStateThrowInForwardRight && gameState <= kGameStateThrowInBackLeft);
            if (cornerOrThrowIn)
                limit = kPitchSideCameraLimitDuringBreak;
        }

        // camera.cpp:183-204 — period-end and result screens.
        short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gs >= kGameStateStartingGame && gs <= kGameStateGameEnded)
        {
            switch (gs)
            {
                case kGameStateStartingGame:
                case kGameStateCameraGoingToShowers:
                case kGameStateGoingToHalftime:
                case kGameStatePlayersGoingToShower:
                    return WaitingForPlayersToLeaveCameraLocation(limit);

                case kGameStateResultAfterTheGame:
                    return ShowResultAtTop(limit);

                case kGameStateGameEnded:
                    // camera.cpp:194-199 — if penaltiesState < 0, fall through
                    // to ShowResultAtCenter via the default case. The original
                    // uses a labels-into-switch trick; we preserve semantics.
                    short penaltiesState = Memory.ReadSignedWord(Memory.Addr.penaltiesState);
                    if (penaltiesState < 0)
                        return ShowResultAtCenter(limit);
                    break;

                case kGameStateFirstHalfEnded:
                    // camera.cpp:201-202 — fall through to followTheBall.
                    break;

                default:
                    // camera.cpp:196-198 — labels-into-default = ShowResultAtCenter.
                    return ShowResultAtCenter(limit);
            }
        }

        // camera.cpp:206 — default path: follow ball + velocity.
        return FollowTheBall(limit, xVelocity, yVelocity);
    }

    // camera.cpp:209-217 — updateCameraLeaving.
    private static void UpdateCameraLeaving()
    {
        bool leaving = Memory.ReadSignedWord(Memory.Addr.leavingBenchMode) != 0;
        if (leaving)
        {
            int camXWhole = GetCameraXWhole();
            bool benchVisibleByX = camXWhole < kCameraLeavingBenchXLimit;
            Memory.WriteWord(Memory.Addr.leavingBenchMode, benchVisibleByX ? 1 : 0);
        }
    }

    // camera.cpp:219-249 — updateCameraCoordinates(const CameraParams&).
    // The per-tick numerical core. All arithmetic in Q16.16 (no float).
    private static void UpdateCameraCoordinates(CameraParams ps)
    {
        // camera.cpp:221-222 — store accumulated velocity.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, ps.XVelocity);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, ps.YVelocity);

        // camera.cpp:224-227 — dest = focus - vgaSize/2 + velocity.
        // Translate to Q16.16 — XDest/YDest are whole pixels.
        int xDest = (ps.XDest - kVgaWidth  / 2) << 16;
        int yDest = (ps.YDest - kVgaHeight / 2) << 16;
        xDest += ps.XVelocity << 16;
        yDest += ps.YVelocity << 16;

        // camera.cpp:229 — clipCameraDestination.
        (xDest, yDest) = ClipCameraDestination(xDest, yDest, ps.XLimit);

        int cameraX = GetCameraX();
        int cameraY = GetCameraY();

        // camera.cpp:237-239 — production delta = (dest - cur) / 16.
        // Integer division on Q16.16 is just signed shift right (>> 4) — same
        // numeric result as `FixedPoint / 16` in the C++.
        int deltaX = (xDest - cameraX) >> 4;
        int deltaY = (yDest - cameraY) >> 4;

        // camera.cpp:242 — clipCameraMovement.
        (deltaX, deltaY) = ClipCameraMovement(deltaX, deltaY);

        cameraX += deltaX;
        cameraY += deltaY;

        // camera.cpp:247-248 — constrain + writeback.
        (cameraX, cameraY) = ConstrainCameraToPitch(cameraX, cameraY);
        SetCameraX(cameraX);
        SetCameraY(cameraY);
    }

    // camera.cpp:251-269 — clipCameraDestination.
    private static (int, int) ClipCameraDestination(int xDest, int yDest, int xLimit)
    {
        // assert(xLimit >= 0)
        int xLimitQ = xLimit << 16;
        if (xDest < xLimitQ) xDest = xLimitQ;

        int maxXQ = (kPitchMaxX - xLimit) << 16;
        if (xDest > maxXQ) xDest = maxXQ;

        bool training = Memory.ReadSignedWord(Memory.Addr.g_trainingGame) != 0;
        int minY = (training ? kTrainingPitchMinY : kPitchMinY) << 16;
        int maxY = (training ? kTrainingPitchMaxY : kPitchMaxY) << 16;

        if (yDest < minY) yDest = minY;
        if (yDest > maxY) yDest = maxY;
        return (xDest, yDest);
    }

    // camera.cpp:271-283 — clipCameraMovement.
    // Clamps per-tick delta to ±5 pixels (Q16.16).
    private static (int, int) ClipCameraMovement(int deltaX, int deltaY)
    {
        int maxQ = kMaxCameraMovement << 16;
        int minQ = -maxQ;
        if (deltaX > maxQ) deltaX = maxQ;
        if (deltaX < minQ) deltaX = minQ;
        if (deltaY > maxQ) deltaY = maxQ;
        if (deltaY < minQ) deltaY = minQ;
        return (deltaX, deltaY);
    }

    // camera.cpp:285-295 — constrainCameraToPitch.
    private static (int, int) ConstrainCameraToPitch(int cameraX, int cameraY)
    {
        int minX = kCameraMinX << 16, maxX = kCameraMaxX << 16;
        int minY = kCameraMinY << 16, maxY = kCameraMaxY << 16;
        if (cameraX < minX) cameraX = minX;
        if (cameraX > maxX) cameraX = maxX;
        if (cameraY < minY) cameraY = minY;
        if (cameraY > maxY) cameraY = maxY;
        return (cameraX, cameraY);
    }

    // camera.cpp:303-324 — getBenchCameraXLimit.
    private static int GetBenchCameraXLimit()
    {
        int limit = kPitchSideCameraLimitDuringBreak;
        int camYWhole = GetCameraYWhole();
        bool cameraAtBenchLevel = camYWhole >= kBenchSlideAreaStartY && camYWhole <= kBenchSlideAreaEndY;

        // Mechanical equivalent of camera.cpp:316-320 — see GoalsNotVisible
        // for the derivation. We replace the goalNTopSprite.onScreen reads
        // with a static camera-band check.
        if (cameraAtBenchLevel && GoalsNotVisible())
        {
            short substitute = Memory.ReadSignedWord(Memory.Addr.g_substituteInProgress);
            limit = substitute != 0 ? kSubstituteCameraLimit : kCameraMinX;
        }

        return limit;
    }

    // camera.cpp:326-349 — getGameStoppedCameraDirections.
    // Picks per-tick camera direction from either the (now-stationary) last
    // controlled player, or a sticky cameraDirection global. Result is a unit
    // (-1/0/+1, -1/0/+1) cross direction.
    private static (int, int) GetGameStoppedCameraDirections()
    {
        int xDirection = 0, yDirection = 0;

        int direction;
        bool gotPlayerDirection = false;

        int lastTeam = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        int controlled = lastTeam == 0
            ? 0
            : Memory.ReadSignedDword(lastTeam + TeamData.OffControlledPlayer);

        if (lastTeam != 0 && controlled != 0)
        {
            direction = Memory.ReadSignedWord(controlled + PlayerSprite.OffDirection);
            gotPlayerDirection = true;
        }
        else
        {
            direction = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
        }

        if (gotPlayerDirection || direction != -1)
        {
            // camera.cpp:341-343 — kNextCameraDirections (8 dirs × dx,dy unit vectors).
            int dx = kNextCameraDirections[2 * direction];
            int dy = kNextCameraDirections[2 * direction + 1];
            xDirection = dx;
            yDirection = dy;
        }

        return (xDirection, yDirection);
    }

    // camera.cpp:341-343.
    private static readonly sbyte[] kNextCameraDirections = new sbyte[16]
    {
        0, -1, 1, -1, 1, 0, 1, 1, 0, 1, -1, 1, -1, 0, -1, -1,
    };

    // camera.cpp:351-370 — getStandardModeCameraVelocity.
    private static (int, int) GetStandardModeCameraVelocity(int xDirection, int yDirection)
    {
        int xVelocity = Memory.ReadSignedWord(Memory.Addr.cameraXVelocity);
        int yVelocity = Memory.ReadSignedWord(Memory.Addr.cameraYVelocity);

        if (xDirection < 0 && xVelocity != -kMaxVelocity)
            xVelocity -= kVelocityIncrement;
        else if (xDirection > 0 && xVelocity != kMaxVelocity)
            xVelocity += kVelocityIncrement;

        if (yDirection < 0 && yVelocity != -kMaxVelocity)
            yVelocity -= kVelocityIncrement;
        else if (yDirection > 0 && yVelocity != kMaxVelocity)
            yVelocity += kVelocityIncrement;

        return (xVelocity, yVelocity);
    }

    // camera.cpp:372-375.
    private static CameraParams WaitingForPlayersToLeaveCameraLocation(int limit)
        => MakeParams(kPlayersOutsidePitchX, kPitchCenterY, limit);

    // camera.cpp:377-380.
    private static CameraParams ShowResultAtCenter(int limit)
        => MakeParams(kPitchCenterX, kPitchCenterY, limit);

    // camera.cpp:382-385.
    private static CameraParams ShowResultAtTop(int limit)
        => MakeParams(kPitchCenterX, kTopGoalLine, limit);

    // camera.cpp:387-390.
    private static CameraParams FollowTheBall(int limit, int xVelocity, int yVelocity)
    {
        int bx = Memory.ReadSignedWord(BallSprite.Base + BallSprite.OffX + 2);
        int by = Memory.ReadSignedWord(BallSprite.Base + BallSprite.OffY + 2);
        return MakeParams(bx, by, limit, xVelocity, yVelocity);
    }

    // ---- Helpers / cross-module references ---------------------------------
    //
    // (Was: StubInBench(). Removed 2026-06-01 — replaced inline with
    //  Bench.InBench() which is the ported equivalent of bench.cpp:42-45.
    //  The previous stub duplicated Bench.InBench()'s identical Memory read of
    //  g_inSubstitutesMenu.
    //  Source: external/swos-port/src/game/bench/bench.cpp:42-45)

    // Mechanical port of bench.cpp:73-76 — benchCameraX(). Original simply
    // returns the file-scope constant kBenchX (line 9: `constexpr FixedPoint
    // kBenchX = 27`). Whole-pixel value; the FixedPoint type just promotes
    // for downstream Q16.16 arithmetic, no fractional part.
    //
    // Source: external/swos-port/src/game/bench/bench.cpp:73-76
    //         external/swos-port/src/game/bench/bench.cpp:9  (kBenchX = 27)
    private const int kBenchX = 27;
    private static int BenchCameraX() => kBenchX;

    // SWOS::rand — table-driven xor-stream PRNG. Returns a byte (0..255).
    // camera.cpp:130 uses `Rand() & 1` for top/bottom start selection — only
    // the low bit matters. Wired through SwosVm.Rng (canonical
    // deterministic-lockstep stream-1 source).
    // Source: external/swos-port/src/util/random.cpp
    private static int SwosRand() => Rng.NextByte();

    // Mechanical equivalent of camera.cpp:316-320 — goals-not-visible check.
    // The original tests
    //   (goal1TopSprite.hasNoImage() || !goal1TopSprite.onScreen) &&
    //   (goal2BottomSprite.hasNoImage() || !goal2BottomSprite.onScreen)
    // and the line above it asserts this is *guaranteed* when cameraAtBenchLevel
    // is true. We derive the same result statically: goals live at world
    // Y=kTopGoalY=129 / Y=kBottomGoalY=778 (gameSprites.cpp:81-83). When the
    // camera is in the bench Y band [kBenchSlideAreaStartY=339,
    // kBenchSlideAreaEndY=359] the visible vertical window
    // (camY ± kVgaHeight/2 = camY ± 100) spans roughly [239, 459], so both
    // goals fall well outside. Return true unconditionally — same correctness
    // bound as the original assert.
    //
    // Source: external/swos-port/src/game/camera.cpp:316-320
    //         external/swos-port/src/sprites/gameSprites.cpp:81-94
    private const int kTopGoalY    = 129;
    private const int kBottomGoalY = 778;
    private static bool GoalsNotVisible()
    {
        int camY = GetCameraYWhole();
        const int kHalfVgaHeight = 100;
        // Goal sprites are several pixels tall; bench-band camera positions
        // can't see either goal Y. Cheap static check covers exactly the
        // assert at camera.cpp:316: when camY is bench-band, both goals are
        // off-screen.
        return (camY - kHalfVgaHeight > kTopGoalY) && (camY + kHalfVgaHeight < kBottomGoalY);
    }
}
