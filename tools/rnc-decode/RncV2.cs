namespace OpenSwos.Tools.RncDecode;

// Rob Northen ProPack v2 decoder (RNC\x02 magic).
//
// Shares the 18-byte header layout and CRC-16/ARC with v1 — only the body algorithm
// and bit reader differ.
//
// Bit reader (v2):
//   MSB-first within a byte, byte-at-a-time, lazy 1-byte refill.
//   Single shared source-byte cursor — both the bit register and "read raw byte"
//   pulls from the SAME position counter. Bit register holds residual bits; raw
//   byte reads advance the cursor without disturbing the register.
//
// Body algorithm (no Huffman tables; flat prefix opcodes):
//   bit 0          -> literal: emit one raw source byte
//   bits 10        -> 2-byte match, distance = source_byte + 1
//   bits 110       -> variable-count match (count 4..9 via DecodeMatchCount):
//                       if count != 9: distance = DecodeMatchOffset, copy count bytes
//                       if count == 9: long literal run, length = (ReadBits(4)<<2) + 12
//   bits 1110      -> 3-byte match, distance = DecodeMatchOffset
//   bits 1111      -> read source byte n:
//                       n == 0  -> END OF STREAM (then consume one padding bit)
//                       n >= 1  -> match count = n + 8, distance = DecodeMatchOffset
public sealed class RncV2Exception : Exception
{
    public RncV2Exception(string message) : base(message) { }
}

public static class RncV2
{
    public static byte[] Decode(byte[] input)
    {
        var hdr = RncHeader.Parse(input, expectedMethod: 2,
            err => new RncV2Exception(err));

        ushort actualPackedCrc = RncV1.Crc16Arc(input, RncHeader.Size, hdr.PackedSize);
        if (actualPackedCrc != hdr.PackedCrc)
            throw new RncV2Exception(
                $"packed CRC mismatch: declared 0x{hdr.PackedCrc:X4}, got 0x{actualPackedCrc:X4}");

        var output = new byte[hdr.UnpackedSize];
        var decoder = new Decoder(input, RncHeader.Size, hdr.PackedSize, output);
        decoder.Run();

        ushort actualUnpackedCrc = RncV1.Crc16Arc(output, 0, hdr.UnpackedSize);
        if (actualUnpackedCrc != hdr.UnpackedCrc)
            throw new RncV2Exception(
                $"unpacked CRC mismatch: declared 0x{hdr.UnpackedCrc:X4}, got 0x{actualUnpackedCrc:X4}");

        return output;
    }

    private sealed class Decoder
    {
        private readonly byte[] _src;
        private readonly int _srcBase;
        private readonly int _srcLen;
        private readonly byte[] _dst;
        private int _srcPos;
        private int _dstPos;
        private int _bitBuf;   // 8-bit shift register, MSB is next bit
        private int _bitCount; // bits remaining in _bitBuf

        public Decoder(byte[] src, int srcBase, int srcLen, byte[] dst)
        {
            _src = src;
            _srcBase = srcBase;
            _srcLen = srcLen;
            _dst = dst;
        }

        public void Run()
        {
            _srcPos = 0;
            _bitBuf = 0;
            _bitCount = 0;
            _dstPos = 0;

            _ = ReadBit();           // pack-mode flag — informational
            int keyFlag = ReadBit(); // encryption flag
            if (keyFlag != 0)
                throw new RncV2Exception("encrypted (key-flag set) RNC2 streams are not supported");

            // Outer loop: keeps decoding sections until the output is full.
            // Inner loop: decodes one section, terminating on the `1111 0x00 X` EOS marker.
            // Files can contain multiple sections — chunk_count in the header indicates how many.
            while (_dstPos < _dst.Length)
            {
                if (!DecodeSection()) break;
            }
        }

        // Returns true if the section ended with an EOS marker (continue with next section);
        // false if we ran out of output room mid-section (terminate the outer loop too).
        private bool DecodeSection()
        {
            while (true)
            {
                if (_dstPos >= _dst.Length) return false;

                int op = ReadBit();
                if (op == 0)
                {
                    _dst[_dstPos++] = ReadSourceByte();
                    continue;
                }

                if (ReadBit() == 1)
                {
                    int matchCount;
                    int matchOffset;
                    if (ReadBit() == 1)
                    {
                        if (ReadBit() == 1)
                        {
                            // prefix 1111
                            matchCount = ReadSourceByte() + 8;
                            if (matchCount == 8)
                            {
                                // EOS marker: consume the padding bit, then back to outer loop
                                ReadBit();
                                return true;
                            }
                        }
                        else
                        {
                            // prefix 1110
                            matchCount = 3;
                        }
                        matchOffset = DecodeMatchOffset();
                    }
                    else
                    {
                        // prefix 110
                        matchCount = 2;
                        matchOffset = ReadSourceByte() + 1;
                    }
                    Copy(matchOffset, matchCount);
                }
                else
                {
                    // prefix 10
                    int matchCount = DecodeMatchCount();
                    if (matchCount != 9)
                    {
                        int matchOffset = DecodeMatchOffset();
                        Copy(matchOffset, matchCount);
                    }
                    else
                    {
                        int runLen = (ReadBits(4) << 2) + 12;
                        for (int i = 0; i < runLen; i++)
                        {
                            if (_dstPos >= _dst.Length) return false;
                            _dst[_dstPos++] = ReadSourceByte();
                        }
                    }
                }
            }
        }

        private void Copy(int distance, int count)
        {
            if (distance > _dstPos)
                throw new RncV2Exception(
                    $"match distance {distance} exceeds current output position {_dstPos}");
            if (_dstPos + count > _dst.Length)
                throw new RncV2Exception(
                    $"match length {count} overflows output ({_dst.Length - _dstPos} remaining)");
            for (int i = 0; i < count; i++)
            {
                _dst[_dstPos] = _dst[_dstPos - distance];
                _dstPos++;
            }
        }

        private int ReadBit()
        {
            if (_bitCount == 0)
            {
                if (_srcPos >= _srcLen)
                    throw new RncV2Exception("bit-stream read past end of input");
                _bitBuf = _src[_srcBase + _srcPos++];
                _bitCount = 8;
            }
            int bit = (_bitBuf >> 7) & 1;
            _bitBuf = (_bitBuf << 1) & 0xFF;
            _bitCount--;
            return bit;
        }

        private int ReadBits(int n)
        {
            int result = 0;
            for (int i = 0; i < n; i++)
                result = (result << 1) | ReadBit();
            return result;
        }

        private byte ReadSourceByte()
        {
            if (_srcPos >= _srcLen)
                throw new RncV2Exception("source-byte read past end of input");
            return _src[_srcBase + _srcPos++];
        }

        // Count in 4..9, encoded as:
        //   0 0     -> 4
        //   1 0     -> 5
        //   0 1 X   -> 6 + X  (6 or 7)
        //   1 1 X   -> 8 + X  (8 or 9)
        private int DecodeMatchCount()
        {
            int count = ReadBit() + 4;
            if (ReadBit() == 1)
                count = ((count - 1) << 1) + ReadBit();
            return count;
        }

        // High byte of distance via small prefix tree, low byte raw, result = (high<<8 | low) + 1.
        private int DecodeMatchOffset()
        {
            int offset = 0;
            if (ReadBit() == 1)
            {
                offset = ReadBit();
                if (ReadBit() == 1)
                {
                    offset = ((offset << 1) | ReadBit()) | 4;
                    if (ReadBit() == 0)
                        offset = (offset << 1) | ReadBit();
                }
                else if (offset == 0)
                {
                    offset = ReadBit() + 2;
                }
            }
            return ((offset << 8) | ReadSourceByte()) + 1;
        }
    }
}
