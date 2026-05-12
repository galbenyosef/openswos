using System.Buffers.Binary;

namespace OpenSwos.Tools.RncDecode;

// Rob Northen ProPack v1 decoder.
//
// Header: 18 bytes big-endian:
//   [0..3]  'RNC\x01' magic
//   [4..7]  unpacked size (uint32 BE)
//   [8..11] packed size   (uint32 BE)  -- bytes AFTER the 18-byte header
//   [12..13] unpacked CRC (CRC-16/ARC over decoded output)
//   [14..15] packed CRC   (CRC-16/ARC over packed payload)
//   [16]    leeway byte (overrun allowance for in-place decode)
//   [17]    chunk count  (informational; loop runs until output is full)
//
// Bitstream model:
//   Compressed payload is a sequence of 16-bit little-endian words.
//   Within each word, bits are consumed LSB-first.
//   The decoder keeps a 32-bit register holding the current word in low 16 bits
//   and the next word pre-loaded in high 16 bits, so any read up to 16 bits is
//   one cheap mask-and-shift operation.
//
// After each literal byte run, the high half (the prefetched-but-not-yet-bit-read
// word) is rebuilt from the new byte position. The two streams (bit and byte)
// share the same byte cursor.
public sealed class RncV1Exception : Exception
{
    public RncV1Exception(string message) : base(message) { }
}

public static class RncV1
{
    private const uint MagicBE = 0x524E4301;
    private const int HeaderSize = 18;

    public static byte[] Decode(byte[] input)
    {
        if (input.Length < HeaderSize)
            throw new RncV1Exception($"input too short ({input.Length} < {HeaderSize})");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(0));
        if (magic != MagicBE)
            throw new RncV1Exception($"not RNC1 (magic = 0x{magic:X8}, expected 0x{MagicBE:X8})");

        int unpackedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(4));
        int packedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(8));
        ushort declaredUnpackedCrc = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(12));
        ushort declaredPackedCrc = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(14));
        byte leeway = input[16];
        byte chunkCount = input[17];
        _ = leeway;
        _ = chunkCount;

        if (HeaderSize + packedSize > input.Length)
            throw new RncV1Exception(
                $"declared packed size {packedSize} overflows file ({input.Length - HeaderSize} available)");

        ushort actualPackedCrc = Crc16Arc(input, HeaderSize, packedSize);
        if (actualPackedCrc != declaredPackedCrc)
            throw new RncV1Exception(
                $"packed CRC mismatch: declared 0x{declaredPackedCrc:X4}, got 0x{actualPackedCrc:X4}");

        var output = new byte[unpackedSize];
        var decoder = new Decoder(input, HeaderSize, packedSize, output);
        decoder.Run();

        ushort actualUnpackedCrc = Crc16Arc(output, 0, unpackedSize);
        if (actualUnpackedCrc != declaredUnpackedCrc)
            throw new RncV1Exception(
                $"unpacked CRC mismatch: declared 0x{declaredUnpackedCrc:X4}, got 0x{actualUnpackedCrc:X4}");

        return output;
    }

    private sealed class Decoder
    {
        private readonly byte[] _src;
        private readonly int _srcBase;
        private readonly int _srcLen;
        private readonly byte[] _dst;
        private int _dstPos;

        private int _bytePos;
        private uint _bitBuf;
        private int _bitsLeft;

        public Decoder(byte[] src, int srcBase, int srcLen, byte[] dst)
        {
            _src = src;
            _srcBase = srcBase;
            _srcLen = srcLen;
            _dst = dst;
        }

        public void Run()
        {
            InitBitBuffer();

            _ = ReadBits(1); // lock flag — informational
            uint keyFlag = ReadBits(1);
            if (keyFlag != 0)
                throw new RncV1Exception("encrypted (key-flag set) RNC1 streams are not supported");

            while (_dstPos < _dst.Length)
            {
                DecodeChunk();
            }
        }

        // Model (per aybe/RNCUnpacker):
        //   _bytePos = byte offset of the word CURRENTLY sitting in the buffer's high half
        //              (= byte position that literal mode reads from next).
        //   _bitsLeft = number of valid bits in _bitBuf starting at bit 0. Range [16..31]
        //               outside of EOF cases.
        //   Init loads 2 bytes (one 16-bit LE word) into the LOW half, _bitsLeft = 16, _bytePos = 0.
        //   Refill triggers eagerly when _bitsLeft < 16 after any consume — advances _bytePos by 2
        //   FIRST, then loads the new word into the high half above _bitsLeft.
        private void InitBitBuffer()
        {
            if (_srcLen < 2)
                throw new RncV1Exception("compressed payload too short to initialise bit buffer");
            _bitBuf = (uint)_src[_srcBase + 0] | ((uint)_src[_srcBase + 1] << 8);
            _bitsLeft = 16;
            _bytePos = 0;
        }

        private uint ReadBits(int n)
        {
            uint result = _bitBuf & ((1u << n) - 1);
            _bitBuf >>= n;
            _bitsLeft -= n;
            if (_bitsLeft < 16) Refill();
            return result;
        }

        private void ConsumeBits(int n)
        {
            _bitBuf >>= n;
            _bitsLeft -= n;
            if (_bitsLeft < 16) Refill();
        }

        private void Refill()
        {
            _bytePos += 2;
            int absPos = _srcBase + _bytePos;
            if (_bytePos + 1 < _srcLen)
            {
                uint nextWord = (uint)_src[absPos] | ((uint)_src[absPos + 1] << 8);
                _bitBuf |= nextWord << _bitsLeft;
                _bitsLeft += 16;
            }
            else if (_bytePos < _srcLen)
            {
                _bitBuf |= (uint)_src[absPos] << _bitsLeft;
                _bitsLeft += 8;
            }
            // else: past EOF, leave buffer as-is and let consumers deplete what's left.
        }

        private void ResyncAfterLiteralRun()
        {
            // Discard the top 16 bits (stale prefetch the literal loop consumed via _bytePos),
            // then reload from the now-updated _bytePos. _bytePos is NOT advanced by the fix-up.
            _bitsLeft -= 16;
            if (_bitsLeft < 0) _bitsLeft = 0;
            _bitBuf &= (_bitsLeft == 0) ? 0u : ((1u << _bitsLeft) - 1);
            int absPos = _srcBase + _bytePos;
            if (_bytePos + 1 < _srcLen)
            {
                uint nextWord = (uint)_src[absPos] | ((uint)_src[absPos + 1] << 8);
                _bitBuf |= nextWord << _bitsLeft;
                _bitsLeft += 16;
            }
            else if (_bytePos < _srcLen)
            {
                _bitBuf |= (uint)_src[absPos] << _bitsLeft;
                _bitsLeft += 8;
            }
        }

        private void DecodeChunk()
        {
            var rawTable = ReadHuffmanTable();
            var distTable = ReadHuffmanTable();
            var lenTable = ReadHuffmanTable();

            uint numPairs = ReadBits(16);
            if (numPairs == 0)
                throw new RncV1Exception("chunk with zero sub-chunks");

            for (uint p = 0; p < numPairs; p++)
            {
                // (a) literal run
                int runLen = (int)DecodeValue(rawTable);
                if (runLen < 0 || _dstPos + runLen > _dst.Length)
                    throw new RncV1Exception(
                        $"literal run overflow ({runLen} bytes, {_dst.Length - _dstPos} remaining)");
                for (int i = 0; i < runLen; i++)
                {
                    if (_srcBase + _bytePos >= _srcBase + _srcLen)
                        throw new RncV1Exception("literal read past end of compressed payload");
                    _dst[_dstPos++] = _src[_srcBase + _bytePos++];
                }
                if (runLen > 0)
                    ResyncAfterLiteralRun();

                // (b) match — skipped on the very last pair of the chunk
                if (p + 1 < numPairs)
                {
                    int distance = (int)DecodeValue(distTable) + 1;
                    int length = (int)DecodeValue(lenTable) + 2;
                    if (distance > _dstPos)
                        throw new RncV1Exception(
                            $"match distance {distance} exceeds current output position {_dstPos}");
                    if (_dstPos + length > _dst.Length)
                        throw new RncV1Exception(
                            $"match length {length} overflows output ({_dst.Length - _dstPos} remaining)");
                    // byte-by-byte copy to support self-overlapping references (distance=1 byte-fill etc.)
                    for (int i = 0; i < length; i++)
                    {
                        _dst[_dstPos] = _dst[_dstPos - distance];
                        _dstPos++;
                    }
                }
            }
        }

        private HuffmanTable ReadHuffmanTable()
        {
            int numLeaves = (int)ReadBits(5);
            if (numLeaves > 16)
                throw new RncV1Exception($"huffman numLeaves={numLeaves} > 16");
            var table = new HuffmanTable(numLeaves);
            for (int i = 0; i < numLeaves; i++)
                table.CodeLen[i] = (int)ReadBits(4);
            table.BuildCodes();
            return table;
        }

        private uint DecodeValue(HuffmanTable table)
        {
            if (table.NumLeaves == 0)
                throw new RncV1Exception("attempt to decode from empty huffman table");

            int symbol = -1;
            for (int i = 0; i < table.NumLeaves; i++)
            {
                int L = table.CodeLen[i];
                if (L == 0) continue;
                uint mask = (1u << L) - 1;
                if ((_bitBuf & mask) == table.Code[i])
                {
                    ConsumeBits(L);
                    symbol = i;
                    break;
                }
            }
            if (symbol < 0)
                throw new RncV1Exception(
                    $"no huffman code matched (bitBuf=0x{_bitBuf:X8}, table has {table.NumLeaves} leaves)");

            if (symbol < 2) return (uint)symbol;
            int extra = symbol - 1;
            uint extraBits = ReadBits(extra);
            return extraBits | (1u << (symbol - 1));
        }
    }

    private sealed class HuffmanTable
    {
        public readonly int NumLeaves;
        public readonly int[] CodeLen;
        public readonly uint[] Code;

        public HuffmanTable(int numLeaves)
        {
            NumLeaves = numLeaves;
            CodeLen = new int[numLeaves];
            Code = new uint[numLeaves];
        }

        public void BuildCodes()
        {
            uint val = 0;
            for (int L = 1; L <= 16; L++)
            {
                uint step = 1u << (16 - L);
                for (int i = 0; i < NumLeaves; i++)
                {
                    if (CodeLen[i] == L)
                    {
                        Code[i] = ReverseBits(val >> (16 - L), L);
                        val += step;
                    }
                }
            }
        }
    }

    private static uint ReverseBits(uint value, int bits)
    {
        uint result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1u);
            value >>= 1;
        }
        return result;
    }

    // CRC-16/ARC: poly 0x8005 reflected (=0xA001), init 0, reflect in/out, no final xor
    private static readonly ushort[] CrcTable = BuildCrcTable();

    private static ushort[] BuildCrcTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int b = 0; b < 8; b++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            t[i] = crc;
        }
        return t;
    }

    public static ushort Crc16Arc(byte[] data, int offset, int length)
    {
        ushort crc = 0;
        for (int i = 0; i < length; i++)
            crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ data[offset + i]) & 0xFF]);
        return crc;
    }
}
