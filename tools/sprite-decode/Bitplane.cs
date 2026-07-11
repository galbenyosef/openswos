namespace OpenSwos.Tools.SpriteDecode;

// Amiga uses planar bitmap storage. For N-plane art, each pixel's palette index
// is reconstructed by reading 1 bit from each of N planes at the same (x, y) and
// concatenating: index = bit_plane0 | (bit_plane1 << 1) | ... | (bit_plane{N-1} << (N-1)).
//
// Within a byte, Amiga is MSB-first: pixel 0 of a row is at bit 7 of byte 0, pixel 1 is at
// bit 6, ..., pixel 7 is at bit 0; pixel 8 is at bit 7 of byte 1; etc.
//
// Two common storage layouts:
//
//   Sequential:   all of plane 0 (full sprite), then all of plane 1, ..., plane N-1.
//                 byte_offset(plane p, row r) = p * (bytesPerRow * height) + r * bytesPerRow
//
//   Interleaved:  row 0 of plane 0, row 0 of plane 1, ..., row 0 of plane N-1, row 1 of plane 0, ...
//                 byte_offset(plane p, row r) = r * (planes * bytesPerRow) + p * bytesPerRow

public enum BitplaneLayout
{
    Sequential,
    Interleaved,
}

public static class Bitplane
{
    // Decode a single sprite at `spriteOffset` bytes into the data buffer.
    // Returns a [height, width] palette-index grid.
    public static byte[,] Decode(
        ReadOnlySpan<byte> data, int spriteOffset,
        int width, int height, int planes,
        BitplaneLayout layout)
    {
        int bytesPerRow = (width + 7) / 8;
        int planeBytes = bytesPerRow * height;
        int requiredBytes = planes * planeBytes;
        if (spriteOffset + requiredBytes > data.Length)
            throw new ArgumentException(
                $"sprite at offset {spriteOffset} needs {requiredBytes} bytes but only {data.Length - spriteOffset} available");

        var output = new byte[height, width];

        for (int y = 0; y < height; y++)
        {
            for (int p = 0; p < planes; p++)
            {
                int rowBase = layout == BitplaneLayout.Sequential
                    ? spriteOffset + p * planeBytes + y * bytesPerRow
                    : spriteOffset + y * planes * bytesPerRow + p * bytesPerRow;

                for (int x = 0; x < width; x++)
                {
                    int b = data[rowBase + (x >> 3)];
                    int bit = (b >> (7 - (x & 7))) & 1;
                    if (bit != 0) output[y, x] |= (byte)(1 << p);
                }
            }
        }

        return output;
    }

    public static int BytesPerSprite(int width, int height, int planes) =>
        ((width + 7) / 8) * height * planes;
}
