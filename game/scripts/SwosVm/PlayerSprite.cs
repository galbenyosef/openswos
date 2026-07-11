namespace OpenSwos.SwosVm;

// Player sprite view over Memory. 22 sprites in a contiguous pool starting at
// SpritePoolBase. Slot layout:
//
//   slot 0..10  → team1 (slot 0 = goalie1, slots 1..10 = team1 outfielders)
//   slot 11..21 → team2 (slot 11 = goalie2, slots 12..21 = team2 outfielders)
//
// Each slot is `SlotStride` bytes (128 — actual Sprite struct is 110 bytes per
// `external/swos-port/src/sprites/Sprite.h` with `static_assert` confirmed by
// `swos.asm:93105` "mov word ptr D1, 109 ; sizeof(Sprite) - 1"; 128 stride
// adds 18 bytes padding for alignment ease).
//
// **Unlike BallSprite, PlayerSprite is parameterised by slot index** — callers
// pass an `int slot` to read/write a specific player. This matches the asm
// idiom `mov esi, [team1SpritesTable + i*4]` which reads a pointer-table entry
// and dereferences. Our pool layout is contiguous (not pointer-table) but the
// per-slot access pattern is identical.
//
// Static helpers `Base(slot)` and `AddrInPool(slot, fieldOffset)` give the
// absolute byte address inside Memory. Field offsets are constants matching
// swos-port's Sprite struct layout (also see BallSprite which shares the same
// struct layout).
public static class PlayerSprite
{
    // Pool sits AFTER TeamData (which ends at 0x4FCFF). Aligned to 0x50000.
    public const int SpritePoolBase = 0x50000;
    public const int SlotStride     = 128;          // padded from 110 for alignment
    public const int SpriteSize     = 110;          // actual Sprite struct size

    public const int TeamSize       = 11;           // 11 sprites per team (incl. keeper)
    public const int TotalSlots     = TeamSize * 2; // 22 sprites total

    // Slot indices — keepers and team boundaries.
    public const int SlotGoalie1    = 0;
    public const int SlotTeam1First = 1;            // team1 outfielders 1..10 → slots 1..10
    public const int SlotGoalie2    = 11;
    public const int SlotTeam2First = 12;           // team2 outfielders 1..10 → slots 12..21

    // ---- Per-team sprite tables (pointer arrays) -----------------------------
    // Match swos-port's `team1SpritesTable @ g_memByte[330096]` and
    // `team2SpritesTable @ g_memByte[330140]` shape — each is 11 × 4-byte
    // pointers to sprite slots. swos-port stores absolute byte addresses;
    // our equivalent stores absolute Memory addresses to the slot.
    //
    // `TeamGeneralInfo.players` field at +20 should point to one of these
    // tables — Init() wires it.
    //
    // Relocated to 0x4FE00 (after Referee region 0x4FD00..0x4FDFF; below
    // sprite pool 0x50000) to avoid the long-standing overlap with TopBase
    // (0x4F900..) that corrupted slots 5..10 of Team2 once non-stub code
    // (AI walk) started reading the tables. 256 bytes of slack here easily
    // fits 2 × 44 = 88 bytes of pointers with 168 bytes spare.
    public const int Team1TableBase = 0x4FE00;      // 11 × 4 = 44 bytes
    public const int Team2TableBase = 0x4FE2C;      // 11 × 4 = 44 bytes

    // ---- Slot addressing -----------------------------------------------------

    public static int Base(int slot)
    {
        // Caller should pass slot in [0, 22). No bounds check in production —
        // matches asm where `team1SpritesTable[12]` would just trash memory.
        // For safety in tests, callers can guard themselves.
        return SpritePoolBase + slot * SlotStride;
    }

    // Determines team (top=true → slots 0..10; bottom=false → slots 11..21).
    public static int FirstSlotForTeam(bool top) => top ? 0 : TeamSize;

    public static int GoalieSlot(bool top) => top ? SlotGoalie1 : SlotGoalie2;

    public static bool IsGoalie(int slot) => slot == SlotGoalie1 || slot == SlotGoalie2;

    // ---- Sprite struct field offsets (110-byte struct, pack=1) -------------
    // Source of truth: external/swos-port/src/sprites/Sprite.h + audit map
    // produced 2026-05-23. Fields shared with BallSprite use the same offsets;
    // ones absent from BallSprite are marked as PLAYER-SPECIFIC.
    public const int OffTeamNumber          =   0;  // word  — 1/2 for player teams, 0=AI, 3=corner flag
    public const int OffPlayerOrdinal       =   2;  // word  — 1=goalkeeper, 2..11=outfielders
    public const int OffFrameOffset         =   4;  // word
    public const int OffAnimTablePtr        =   6;  // dword
    public const int OffStartingDirection   =  10;  // word
    public const int OffPlayerState         =  12;  // byte  — PlayerState enum (Normal=0, GoalieCatchingBall=4, DivingHigh=6, DivingLow=7, Down=10, Claimed=11)
    public const int OffPlayerDownTimer     =  13;  // byte  — counts down post-dive / catch animation
    public const int OffFrameIndicesTable   =  18;  // dword — animation table (matches BallSprite +18)
    public const int OffFrameIndex          =  22;  // word
    public const int OffFrameDelay          =  24;  // word
    public const int OffCycleFramesTimer    =  26;  // word
    public const int OffFrameSwitchCounter  =  28;  // word
    public const int OffX                   =  30;  // FixedPoint Q16.16
    public const int OffY                   =  34;
    public const int OffZ                   =  38;
    public const int OffDirection           =  42;  // word — 0..7 quantised
    public const int OffSpeed               =  44;  // word Q8.8
    public const int OffDeltaX              =  46;
    public const int OffDeltaY              =  50;
    public const int OffDeltaZ              =  54;
    public const int OffDestX               =  58;  // word whole pixel
    public const int OffDestY               =  60;
    public const int OffVisible             =  68;  // word — hide/show
    public const int OffImageIndex          =  70;  // word — <0 = no image
    public const int OffSaveSprite          =  72;  // word
    public const int OffBallDistance        =  74;  // dword — PLAYER-SPECIFIC, squared dist to ball
    public const int OffFullDirection       =  82;  // word — 0..255
    public const int OffOnScreen            =  84;  // word
    public const int OffPlayerDirection     =  92;  // word — -1 for non-players
    public const int OffIsMoving            =  94;  // word
    public const int OffTackleState         =  96;  // word
    public const int OffDestReachedState    = 100;  // word — 0..3
    public const int OffCards               = 102;  // word
    public const int OffInjuryLevel         = 104;  // word
    public const int OffTacklingTimer       = 106;  // word
    public const int OffSentAway            = 108;  // word

    // ---- Per-slot accessors --------------------------------------------------
    // Each accessor takes the slot index and reads/writes the appropriate
    // Memory address. All reads are bounds-unchecked for performance — caller's
    // responsibility to pass valid slot indices.

    public static int    X(int slot)                { return Memory.ReadSignedDword(Base(slot) + OffX); }
    public static void   SetX(int slot, int v)      { Memory.WriteDword(Base(slot) + OffX, v); }
    public static int    Y(int slot)                { return Memory.ReadSignedDword(Base(slot) + OffY); }
    public static void   SetY(int slot, int v)      { Memory.WriteDword(Base(slot) + OffY, v); }
    public static int    Z(int slot)                { return Memory.ReadSignedDword(Base(slot) + OffZ); }
    public static void   SetZ(int slot, int v)      { Memory.WriteDword(Base(slot) + OffZ, v); }

    public static short  XPixels(int slot)          { return Memory.ReadSignedWord(Base(slot) + OffX + 2); }
    public static void   SetXPixels(int slot, int v){ Memory.WriteWord(Base(slot) + OffX + 2, v); }
    public static short  YPixels(int slot)          { return Memory.ReadSignedWord(Base(slot) + OffY + 2); }
    public static void   SetYPixels(int slot, int v){ Memory.WriteWord(Base(slot) + OffY + 2, v); }
    public static short  ZPixels(int slot)          { return Memory.ReadSignedWord(Base(slot) + OffZ + 2); }
    public static void   SetZPixels(int slot, int v){ Memory.WriteWord(Base(slot) + OffZ + 2, v); }

    public static int    DeltaX(int slot)           { return Memory.ReadSignedDword(Base(slot) + OffDeltaX); }
    public static void   SetDeltaX(int slot, int v) { Memory.WriteDword(Base(slot) + OffDeltaX, v); }
    public static int    DeltaY(int slot)           { return Memory.ReadSignedDword(Base(slot) + OffDeltaY); }
    public static void   SetDeltaY(int slot, int v) { Memory.WriteDword(Base(slot) + OffDeltaY, v); }
    public static int    DeltaZ(int slot)           { return Memory.ReadSignedDword(Base(slot) + OffDeltaZ); }
    public static void   SetDeltaZ(int slot, int v) { Memory.WriteDword(Base(slot) + OffDeltaZ, v); }

    public static short  Direction(int slot)        { return Memory.ReadSignedWord(Base(slot) + OffDirection); }
    public static void   SetDirection(int slot, int v) { Memory.WriteWord(Base(slot) + OffDirection, v); }
    public static short  Speed(int slot)            { return Memory.ReadSignedWord(Base(slot) + OffSpeed); }
    public static void   SetSpeed(int slot, int v)  { Memory.WriteWord(Base(slot) + OffSpeed, v); }
    public static short  DestX(int slot)            { return Memory.ReadSignedWord(Base(slot) + OffDestX); }
    public static void   SetDestX(int slot, int v)  { Memory.WriteWord(Base(slot) + OffDestX, v); }
    public static short  DestY(int slot)            { return Memory.ReadSignedWord(Base(slot) + OffDestY); }
    public static void   SetDestY(int slot, int v)  { Memory.WriteWord(Base(slot) + OffDestY, v); }

    public static short  TeamNumber(int slot)       { return Memory.ReadSignedWord(Base(slot) + OffTeamNumber); }
    public static void   SetTeamNumber(int slot, int v) { Memory.WriteWord(Base(slot) + OffTeamNumber, v); }
    public static short  PlayerOrdinal(int slot)    { return Memory.ReadSignedWord(Base(slot) + OffPlayerOrdinal); }
    public static void   SetPlayerOrdinal(int slot, int v) { Memory.WriteWord(Base(slot) + OffPlayerOrdinal, v); }

    public static byte   PlayerState(int slot)      { return Memory.ReadByte(Base(slot) + OffPlayerState); }
    public static void   SetPlayerState(int slot, int v) { Memory.WriteByte(Base(slot) + OffPlayerState, v); }
    public static sbyte  PlayerDownTimer(int slot)  { return (sbyte)Memory.ReadByte(Base(slot) + OffPlayerDownTimer); }
    public static void   SetPlayerDownTimer(int slot, int v) { Memory.WriteByte(Base(slot) + OffPlayerDownTimer, v); }

    public static short  ImageIndex(int slot)       { return Memory.ReadSignedWord(Base(slot) + OffImageIndex); }
    public static void   SetImageIndex(int slot, int v) { Memory.WriteWord(Base(slot) + OffImageIndex, v); }

    public static int    BallDistance(int slot)     { return Memory.ReadSignedDword(Base(slot) + OffBallDistance); }
    public static void   SetBallDistance(int slot, int v) { Memory.WriteDword(Base(slot) + OffBallDistance, v); }

    public static short  FullDirection(int slot)    { return Memory.ReadSignedWord(Base(slot) + OffFullDirection); }
    public static void   SetFullDirection(int slot, int v) { Memory.WriteWord(Base(slot) + OffFullDirection, v); }

    public static short  TackleState(int slot)      { return Memory.ReadSignedWord(Base(slot) + OffTackleState); }
    public static void   SetTackleState(int slot, int v) { Memory.WriteWord(Base(slot) + OffTackleState, v); }
    public static short  TacklingTimer(int slot)    { return Memory.ReadSignedWord(Base(slot) + OffTacklingTimer); }
    public static void   SetTacklingTimer(int slot, int v) { Memory.WriteWord(Base(slot) + OffTacklingTimer, v); }

    // ---- Initialisation ------------------------------------------------------
    // Populates the per-team pointer tables and clears all sprite slots.
    // Called from Memory.Init().
    public static void Init()
    {
        // Clear all 22 slot regions (in case Memory.Init didn't already).
        for (int slot = 0; slot < TotalSlots; slot++)
        {
            for (int i = 0; i < SpriteSize; i++)
                Memory.WriteByte(Base(slot) + i, 0);

            // Default PlayerDirection = -1 (matches swos-port init for non-players).
            Memory.WriteWord(Base(slot) + OffPlayerDirection, -1);

            // Sprite::init() — Sprite.h:96-107 — set onScreen=1 and a sane
            // animation-state default so the per-tick updateSpriteAnimation
            // (SpriteUpdate.UpdateSpriteAnimation) can advance frames. Without
            // onScreen the C++ short-circuit ` !--cycleFramesTimer` is never
            // tested and the sprite freezes. frameIndex=-1, frameDelay=5,
            // cycleFramesTimer=1, frameSwitchCounter=-1 mirror Sprite.h:98-103.
            Memory.WriteWord(Base(slot) + OffOnScreen, 1);
            Memory.WriteWord(Base(slot) + OffFrameIndex, -1);
            Memory.WriteWord(Base(slot) + OffFrameDelay, 5);
            Memory.WriteWord(Base(slot) + OffCycleFramesTimer, 1);
            Memory.WriteWord(Base(slot) + OffFrameSwitchCounter, -1);
            Memory.WriteWord(Base(slot) + OffImageIndex, -1);  // hasNoImage() → true
        }

        // Populate per-team sprite tables: each entry holds the absolute
        // Memory address of its corresponding slot.
        for (int i = 0; i < TeamSize; i++)
        {
            Memory.WriteDword(Team1TableBase + i * 4, Base(i));                  // slots 0..10
            Memory.WriteDword(Team2TableBase + i * 4, Base(TeamSize + i));       // slots 11..21
        }

        // Set keeper ordinal (1) and outfielder ordinals (2..11) for each team.
        // swos-port uses ordinal=1 to mark the goalkeeper.
        for (int t = 0; t < 2; t++)
        {
            int firstSlot = t == 0 ? 0 : TeamSize;
            Memory.WriteWord(Base(firstSlot) + OffPlayerOrdinal, 1);             // keeper
            Memory.WriteWord(Base(firstSlot) + OffTeamNumber, t + 1);
            for (int o = 1; o < TeamSize; o++)
            {
                Memory.WriteWord(Base(firstSlot + o) + OffPlayerOrdinal, o + 1); // 2..11
                Memory.WriteWord(Base(firstSlot + o) + OffTeamNumber, t + 1);
            }
        }
    }
}
