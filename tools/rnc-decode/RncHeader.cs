using System.Buffers.Binary;

namespace OpenSwos.Tools.RncDecode;

// Shared 18-byte RNC header — identical layout for v1 and v2.
public readonly record struct RncHeader(
    int Method,
    int UnpackedSize,
    int PackedSize,
    ushort UnpackedCrc,
    ushort PackedCrc,
    byte Leeway,
    byte ChunkCount)
{
    public const int Size = 18;

    public static RncHeader Parse(byte[] input, int expectedMethod, Func<string, Exception> errorFactory)
    {
        if (input.Length < Size)
            throw errorFactory($"input too short ({input.Length} < {Size})");

        if (input[0] != (byte)'R' || input[1] != (byte)'N' || input[2] != (byte)'C')
            throw errorFactory(
                $"not an RNC stream: magic = {input[0]:X2} {input[1]:X2} {input[2]:X2} {input[3]:X2}");

        int method = input[3];
        if (method != expectedMethod)
            throw errorFactory($"RNC method mismatch: expected {expectedMethod}, got {method}");

        var hdr = new RncHeader(
            Method: method,
            UnpackedSize: (int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(4)),
            PackedSize: (int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(8)),
            UnpackedCrc: BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(12)),
            PackedCrc: BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(14)),
            Leeway: input[16],
            ChunkCount: input[17]);

        if (Size + hdr.PackedSize > input.Length)
            throw errorFactory(
                $"declared packed size {hdr.PackedSize} overflows file ({input.Length - Size} bytes available)");

        return hdr;
    }
}

// Top-level dispatcher: auto-detects v1 vs v2 from the method byte and calls the right decoder.
public static class Rnc
{
    public static byte[] Decode(byte[] input)
    {
        if (input.Length < 4)
            throw new InvalidDataException("input too short to contain an RNC header");
        if (input[0] != (byte)'R' || input[1] != (byte)'N' || input[2] != (byte)'C')
            throw new InvalidDataException($"not RNC (magic = {input[0]:X2} {input[1]:X2} {input[2]:X2})");
        return input[3] switch
        {
            0x01 => RncV1.Decode(input),
            0x02 => RncV2.Decode(input),
            _ => throw new InvalidDataException($"unsupported RNC method 0x{input[3]:X2}"),
        };
    }

    public static int DetectMethod(byte[] input) =>
        input.Length >= 4 && input[0] == 'R' && input[1] == 'N' && input[2] == 'C' ? input[3] : 0;
}
