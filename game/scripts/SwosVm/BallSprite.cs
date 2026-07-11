namespace OpenSwos.SwosVm;

// Ball sprite view nad Memory layer. Mirrors swos-port's `swos.ballSprite`
// struct fields by name, but each property reads/writes through Memory at
// fixed byte offsets — so asm-translated code (`readMemory(esi+44, 2)`) and
// hand-written code (`swos.ballSprite.speed`) port to the SAME backing.
//
// Field offsets verified against swos-port src/game/ball/ball.cpp asm-line
// comments (e.g. `mov ax, [esi+Sprite.speed]` at +44). Matches the Sprite
// struct in src/sprites/Sprite.h.
//
// FixedPoint values are SWOS-native Q16.16 (32-bit). Q24.8 conversion happens
// at the BallState boundary in Main.cs (we convert to our existing Fixed only
// when handing off to the renderer).
public static class BallSprite
{
    // Base address inside Memory for the ball sprite. Picked in the sprite
    // memory pool region (Memory.kMemSize = 0x50000, pool starts at 0x4F800).
    public const int Base = 0x4F800;

    // Field offsets within the Sprite struct (src/sprites/Sprite.h:44+).
    public const int OffFrameIndicesTable = 18;  // dword — pointer to frame-index lookup table
    public const int OffFrameIndex       = 22;   // int16 — current index INTO that table
    public const int OffFrameDelay       = 24;   // int16 — animation pacing delay
    public const int OffCycleFramesTimer = 26;   // int16 — countdown to next frame switch
    public const int OffX        = 30;   // FixedPoint Q16.16
    public const int OffY        = 34;   // FixedPoint Q16.16
    public const int OffZ        = 38;   // FixedPoint Q16.16
    public const int OffDirection = 42;  // int16
    public const int OffSpeed    = 44;   // int16, Q8.8
    public const int OffDeltaX   = 46;   // FixedPoint Q16.16
    public const int OffDeltaY   = 50;   // FixedPoint Q16.16
    public const int OffDeltaZ   = 54;   // FixedPoint Q16.16
    public const int OffDestX    = 58;   // int16
    public const int OffDestY    = 60;   // int16
    public const int OffImageIndex      = 70;   // int16 — current sprite image (-1 = hidden)
    public const int OffFullDirection   = 82;   // int16 — 0..255 angle (vs +42 which is 0..7 quantised)

    // ---- Q16.16 fixed-point (matches swos-port FixedPoint) -----------------
    // Whole-pixel reads: SWOS asm often does `word ptr [esi + Sprite.x + 2]`
    // which fetches the HIGH word of the Q16.16 — i.e. integer pixels. Helpers
    // below expose both "raw 32-bit" and "whole pixel" views.

    public static int X        { get => Memory.ReadSignedDword(Base + OffX);        set => Memory.WriteDword(Base + OffX, value); }
    public static int Y        { get => Memory.ReadSignedDword(Base + OffY);        set => Memory.WriteDword(Base + OffY, value); }
    public static int Z        { get => Memory.ReadSignedDword(Base + OffZ);        set => Memory.WriteDword(Base + OffZ, value); }
    public static int DeltaX   { get => Memory.ReadSignedDword(Base + OffDeltaX);   set => Memory.WriteDword(Base + OffDeltaX, value); }
    public static int DeltaY   { get => Memory.ReadSignedDword(Base + OffDeltaY);   set => Memory.WriteDword(Base + OffDeltaY, value); }
    public static int DeltaZ   { get => Memory.ReadSignedDword(Base + OffDeltaZ);   set => Memory.WriteDword(Base + OffDeltaZ, value); }

    // Whole-pixel accessors — high word of Q16.16. SWOS reads these via
    // `word ptr [esi + Sprite.x + 2]` (offset +2 inside the 4-byte field).
    public static short XPixels { get => Memory.ReadSignedWord(Base + OffX + 2);     set => Memory.WriteWord(Base + OffX + 2, value); }
    public static short YPixels { get => Memory.ReadSignedWord(Base + OffY + 2);     set => Memory.WriteWord(Base + OffY + 2, value); }
    public static short ZPixels { get => Memory.ReadSignedWord(Base + OffZ + 2);     set => Memory.WriteWord(Base + OffZ + 2, value); }

    public static short Direction { get => Memory.ReadSignedWord(Base + OffDirection); set => Memory.WriteWord(Base + OffDirection, value); }
    public static short Speed     { get => Memory.ReadSignedWord(Base + OffSpeed);     set => Memory.WriteWord(Base + OffSpeed, value); }
    public static short DestX     { get => Memory.ReadSignedWord(Base + OffDestX);     set => Memory.WriteWord(Base + OffDestX, value); }
    public static short DestY     { get => Memory.ReadSignedWord(Base + OffDestY);     set => Memory.WriteWord(Base + OffDestY, value); }

    // Animation fields — used by updateBall section 1 (frame index switching).
    public static int   FrameIndicesTable { get => Memory.ReadSignedDword(Base + OffFrameIndicesTable); set => Memory.WriteDword(Base + OffFrameIndicesTable, value); }
    public static short FrameIndex        { get => Memory.ReadSignedWord(Base + OffFrameIndex);       set => Memory.WriteWord(Base + OffFrameIndex, value); }
    public static short FrameDelay        { get => Memory.ReadSignedWord(Base + OffFrameDelay);       set => Memory.WriteWord(Base + OffFrameDelay, value); }
    public static short CycleFramesTimer  { get => Memory.ReadSignedWord(Base + OffCycleFramesTimer); set => Memory.WriteWord(Base + OffCycleFramesTimer, value); }
    public static short ImageIndex        { get => Memory.ReadSignedWord(Base + OffImageIndex);       set => Memory.WriteWord(Base + OffImageIndex, value); }
    public static short FullDirection     { get => Memory.ReadSignedWord(Base + OffFullDirection);    set => Memory.WriteWord(Base + OffFullDirection, value); }
}
