using System.Buffers.Binary;
using System.Text;

namespace OpenSwos.Tools.SpriteDescriptorExtract;

// Parses the SWOS Amiga sprite-descriptor opcode stream. The stream lives at file offset
// 0x18374 of the decompressed `SWOS.bin` (unk_118374 in starwindz's disassembly) and is
// processed at game boot by sub_103464 to build the runtime descriptor table.
//
// Each entry consists of one 16-bit big-endian opcode (high nibble = category, low 12
// bits = picture index for that entry) followed by a variable payload:
//
//   0x7000  + u32 ptr-to-filename                       LOAD atlas (sets current bitmap base)
//   0x8000  + u32 ptr-to-ptr-to-filename                LOAD atlas (indirect)
//   0xC000  + u8 cellX, u8 cellY, u8 cellW, u8 cellH    sprite with no anchor offset
//   0x1000  + u16 cellX, u16 cellY, u8 cellW, u8 cellH  sprite with 16-bit cell coords
//   0xD000  + u8 cellX, u8 cellY, u8 cellW, u8 cellH, i8 offsetX, i8 offsetY
//   0x2000  + u16 cellX, u16 cellY, u8 cellW, u8 cellH, i8 offsetX, i8 offsetY
//   0x5000  + u16 cellX, u16 cellY, u8 cellW, u8 cellH                       (mask variant of 0x1000)
//   0xE000  + u8 cellX, u8 cellY, u8 cellW, u8 cellH, i8 offsetX, i8 offsetY, u8 palette/flag
//   0x3000  + u16 cellX, u16 cellY, u8 cellW, u8 cellH, i8 offsetX, i8 offsetY, u16 palette
//   0xA000/0xB000/0xF000/0x9000  same payload sizes as their 8/16/D/2 counterparts but
//                                with the "mask copy" flag set in the descriptor
//   0xFFFF  end of stream
//
// Cell coords are PIXEL positions inside the 320×256 atlas. cellW and cellH are pixel
// counts; the descriptor's widthWords = (cellW + 15) / 16 = per-plane row stride in 16-bit
// words. offsetX/Y are signed anchor offsets used at draw time:
//   blit_at = (player_x - offsetX, player_y - offsetY)
// This is why aligning offsets per-frame eliminates the "head wobble" in running cycles.

public sealed class Descriptor
{
    public int PictureIndex;
    public int CellX;
    public int CellY;
    public int CellW;
    public int CellH;
    public int OffsetX;
    public int OffsetY;
    public string AtlasName = "";
    public bool HasMask;
    public int Palette;
    public int OpcodeCategory;
    public int StreamOffset;
}

public sealed class DescriptorStreamException : Exception
{
    public DescriptorStreamException(string msg) : base(msg) { }
}

public static class DescriptorStream
{
    // Amiga m68k load address — RAM addr 0x100000 maps to file offset 0.
    public const uint LoadAddress = 0x100000;

    public static List<Descriptor> Parse(byte[] swosBin, int startOffset, int maxEntries = 4096)
    {
        var result = new List<Descriptor>();
        int pos = startOffset;
        string atlas = "";

        while (pos + 2 <= swosBin.Length && result.Count < maxEntries)
        {
            int opPos = pos;
            ushort op = BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos, 2));
            pos += 2;
            if (op == 0xFFFF) break;

            int cat = op & 0xF000;
            int picIdx = op & 0x0FFF;

            switch (cat)
            {
                case 0x7000:
                {
                    uint ptr = BinaryPrimitives.ReadUInt32BigEndian(swosBin.AsSpan(pos, 4));
                    pos += 4;
                    atlas = ReadCString(swosBin, (int)(ptr - LoadAddress));
                    break;
                }
                case 0x8000:
                {
                    uint pp = BinaryPrimitives.ReadUInt32BigEndian(swosBin.AsSpan(pos, 4));
                    pos += 4;
                    uint ptr = BinaryPrimitives.ReadUInt32BigEndian(swosBin.AsSpan((int)(pp - LoadAddress), 4));
                    atlas = ReadCString(swosBin, (int)(ptr - LoadAddress));
                    break;
                }
                case 0xC000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        swosBin[pos], swosBin[pos + 1], swosBin[pos + 2], swosBin[pos + 3], 0, 0, false));
                    pos += 4;
                    break;
                case 0x1000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 2, 2)),
                        swosBin[pos + 4], swosBin[pos + 5], 0, 0, false));
                    pos += 6;
                    break;
                case 0x5000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 2, 2)),
                        swosBin[pos + 4], swosBin[pos + 5], 0, 0, true));
                    pos += 6;
                    break;
                case 0xD000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        swosBin[pos], swosBin[pos + 1], swosBin[pos + 2], swosBin[pos + 3],
                        (sbyte)swosBin[pos + 4], (sbyte)swosBin[pos + 5], false));
                    pos += 6;
                    break;
                case 0x2000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 2, 2)),
                        swosBin[pos + 4], swosBin[pos + 5],
                        (sbyte)swosBin[pos + 6], (sbyte)swosBin[pos + 7], false));
                    pos += 8;
                    break;
                case 0xE000:
                {
                    var d = NewDesc(picIdx, cat, opPos, atlas,
                        swosBin[pos], swosBin[pos + 1], swosBin[pos + 2], swosBin[pos + 3],
                        (sbyte)swosBin[pos + 4], (sbyte)swosBin[pos + 5], false);
                    d.Palette = swosBin[pos + 6];
                    result.Add(d);
                    pos += 7;
                    break;
                }
                case 0x3000:
                {
                    var d = NewDesc(picIdx, cat, opPos, atlas,
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 2, 2)),
                        swosBin[pos + 4], swosBin[pos + 5],
                        (sbyte)swosBin[pos + 6], (sbyte)swosBin[pos + 7], false);
                    d.Palette = BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 8, 2));
                    result.Add(d);
                    pos += 10;
                    break;
                }
                case 0xA000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        swosBin[pos], swosBin[pos + 1], swosBin[pos + 2], swosBin[pos + 3], 0, 0, true));
                    pos += 4;
                    break;
                case 0xB000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 2, 2)),
                        swosBin[pos + 4], swosBin[pos + 5], 0, 0, true));
                    pos += 6;
                    break;
                case 0xF000:
                {
                    // Empirically 8 bytes for player descriptors: same as D000 (8-bit cell
                    // + i8 offsetX + i8 offsetY) plus a u16 palette/flag word.
                    var d = NewDesc(picIdx, cat, opPos, atlas,
                        swosBin[pos], swosBin[pos + 1], swosBin[pos + 2], swosBin[pos + 3],
                        (sbyte)swosBin[pos + 4], (sbyte)swosBin[pos + 5], true);
                    d.Palette = BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 6, 2));
                    result.Add(d);
                    pos += 8;
                    break;
                }
                case 0x9000:
                    result.Add(NewDesc(picIdx, cat, opPos, atlas,
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(swosBin.AsSpan(pos + 2, 2)),
                        swosBin[pos + 4], swosBin[pos + 5],
                        (sbyte)swosBin[pos + 6], (sbyte)swosBin[pos + 7], true));
                    pos += 8;
                    break;
                default:
                    Console.Error.WriteLine(
                        $"  unknown opcode 0x{op:X4} at file offset 0x{opPos:X} after {result.Count} entries — stopping");
                    return result;
            }
        }

        return result;
    }

    private static Descriptor NewDesc(int picIdx, int cat, int streamOff, string atlas,
        int cx, int cy, int cw, int ch, int ox, int oy, bool mask) =>
        new()
        {
            PictureIndex = picIdx,
            OpcodeCategory = cat,
            StreamOffset = streamOff,
            AtlasName = atlas,
            CellX = cx, CellY = cy, CellW = cw, CellH = ch,
            OffsetX = ox, OffsetY = oy,
            HasMask = mask,
        };

    private static string ReadCString(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length) return "<invalid ptr>";
        int end = offset;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.Latin1.GetString(data.AsSpan(offset, end - offset));
    }
}
