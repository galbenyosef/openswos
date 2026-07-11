namespace OpenSwos.SwosVm;

// 68k / x86 flag-register emulation. swos-port's ida2asm output sets these
// flags after each compare / arithmetic op so the next branch can read them:
//
//   flags.carry = false;
//   flags.overflow = false;
//   flags.sign = (ax & 0x8000) != 0;
//   flags.zero = ax == 0;
//   if (!flags.zero)
//       goto l_check_z_coordinate;
//
// We mirror swos-port's `SwosVM::flags` namespace (external/swos-port/src/swos/
// SwosVM.h) but as a static class — one set of flags is enough since SWOS is
// single-threaded inside the VM.
//
// Setters below match the most common 68k instructions (sub.w, add.w, cmp.w,
// or.w, and.w, tst.w) at 16-bit width — which is what `word ptr` reads in
// swos-port use.
public static class Flags
{
    public static bool Zero;
    public static bool Carry;
    public static bool Sign;
    public static bool Overflow;

    // Reset all flags. Called rarely — at sub-system boundaries.
    public static void Clear()
    {
        Zero = false;
        Carry = false;
        Sign = false;
        Overflow = false;
    }

    // tst.w — sets Zero + Sign from a value, clears Carry + Overflow.
    // Mirrors:  flags.zero = (val == 0); flags.sign = (val & 0x8000) != 0;
    //           flags.carry = false; flags.overflow = false;
    public static void SetFromTest16(int val)
    {
        short s = (short)val;
        Zero = s == 0;
        Sign = s < 0;
        Carry = false;
        Overflow = false;
    }

    // sub.w / cmp.w — flags after `dst - src` at 16-bit width. The RESULT is
    // returned so the caller can write it back to dst (or discard for cmp).
    public static short SetFromSub16(int dst, int src)
    {
        short dstSigned = (short)dst;
        short srcSigned = (short)src;
        int   res32 = dstSigned - srcSigned;
        short res = (short)res32;
        Zero  = res == 0;
        Sign  = res < 0;
        // Overflow when dst and src have different signs AND result sign != dst sign
        // (classic 2-operand subtraction overflow rule).
        Overflow = ((dstSigned ^ srcSigned) & (dstSigned ^ res)) < 0;
        // Carry (= borrow) when unsigned dst < unsigned src.
        Carry = (ushort)dstSigned < (ushort)srcSigned;
        return res;
    }

    // add.w — flags after `dst + src` at 16-bit width.
    public static short SetFromAdd16(int dst, int src)
    {
        short dstSigned = (short)dst;
        short srcSigned = (short)src;
        int   res32 = dstSigned + srcSigned;
        short res = (short)res32;
        Zero  = res == 0;
        Sign  = res < 0;
        // Overflow when dst and src have same sign AND result sign differs.
        Overflow = ((~(dstSigned ^ srcSigned)) & (dstSigned ^ res)) < 0;
        // Unsigned carry-out.
        Carry = ((uint)(ushort)dstSigned + (uint)(ushort)srcSigned) > 0xFFFF;
        return res;
    }

    // and.w / or.w — Zero + Sign from result, Carry + Overflow cleared.
    public static short SetFromLogic16(int result)
    {
        short res = (short)result;
        Zero = res == 0;
        Sign = res < 0;
        Carry = false;
        Overflow = false;
        return res;
    }
}
