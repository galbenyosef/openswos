namespace OpenSwos.Tools.SpriteDecode;

// Minimal 24-bit uncompressed BMP writer. BMP renders out-of-the-box in every Windows
// image viewer (including Explorer thumbnails) so this is the format we hand to the user
// for eyeball checks. PNG with palette is the proper final format for Godot but writing
// it requires deflate + CRC — deferred until we wire sprites into the game.
public static class Bmp
{
    public static void Write24(string path, byte[,] indices, byte[] paletteRgb)
    {
        int height = indices.GetLength(0);
        int width = indices.GetLength(1);
        int rowSize = (width * 3 + 3) & ~3;  // pad rows to a multiple of 4 bytes
        int pixelDataSize = rowSize * height;
        int fileSize = 14 + 40 + pixelDataSize;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((ushort)0x4D42);   // 'BM'
        bw.Write(fileSize);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write(54);               // offset to pixel data

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((ushort)1);        // planes
        bw.Write((ushort)24);       // bits per pixel
        bw.Write(0);                // BI_RGB (uncompressed)
        bw.Write(pixelDataSize);
        bw.Write(2835);             // 72 dpi horizontal
        bw.Write(2835);             // 72 dpi vertical
        bw.Write(0);                // palette colours used (0 = 2^bpp)
        bw.Write(0);                // important colours

        // Pixel data — BMP is bottom-up: write last row first.
        var row = new byte[rowSize];
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                byte idx = indices[y, x];
                int p = idx * 3;
                if (p + 2 < paletteRgb.Length)
                {
                    row[x * 3 + 0] = paletteRgb[p + 2]; // B
                    row[x * 3 + 1] = paletteRgb[p + 1]; // G
                    row[x * 3 + 2] = paletteRgb[p + 0]; // R
                }
                else
                {
                    row[x * 3 + 0] = 0xFF; // out-of-palette → magenta
                    row[x * 3 + 1] = 0x00;
                    row[x * 3 + 2] = 0xFF;
                }
            }
            // Pad bytes at end of row stay zero.
            fs.Write(row);
        }
    }

    // Render a single image with a grid overlay every `cellSize` pixels in `gridIndex`
    // palette colour. Useful for eyeballing which atlas cells contain art.
    public static void WriteWithGrid(string path, byte[,] indices, byte[] paletteRgb,
        int cellSize, byte gridIndex)
    {
        int h = indices.GetLength(0);
        int w = indices.GetLength(1);
        var overlay = new byte[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                overlay[y, x] = (y % cellSize == 0 || x % cellSize == 0)
                    ? gridIndex
                    : indices[y, x];
        Write24(path, overlay, paletteRgb);
    }

    // Nearest-neighbour scale + grid overlay. Each source pixel becomes `scale × scale`
    // output pixels; a grid line is drawn every `cellSize` SOURCE pixels in `gridIndex`.
    public static void WriteScaledWithGrid(string path, byte[,] indices, byte[] paletteRgb,
        int scale, int cellSize, byte gridIndex)
    {
        int srcH = indices.GetLength(0);
        int srcW = indices.GetLength(1);
        int dstH = srcH * scale;
        int dstW = srcW * scale;
        var scaled = new byte[dstH, dstW];
        for (int y = 0; y < srcH; y++)
        {
            bool gridRow = y % cellSize == 0;
            for (int x = 0; x < srcW; x++)
            {
                bool gridCol = x % cellSize == 0;
                byte idx = (gridRow || gridCol) ? gridIndex : indices[y, x];
                for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                        scaled[y * scale + dy, x * scale + dx] = idx;
            }
        }
        Write24(path, scaled, paletteRgb);
    }

    public static void WriteGrid24(string path, byte[][,] sprites, byte[] paletteRgb,
        int cols, int padding = 1, byte padIndex = 0)
    {
        if (sprites.Length == 0) return;
        int spriteH = sprites[0].GetLength(0);
        int spriteW = sprites[0].GetLength(1);
        int rows = (sprites.Length + cols - 1) / cols;

        int gridW = cols * (spriteW + padding) + padding;
        int gridH = rows * (spriteH + padding) + padding;
        var grid = new byte[gridH, gridW];
        for (int y = 0; y < gridH; y++)
            for (int x = 0; x < gridW; x++)
                grid[y, x] = padIndex;

        for (int i = 0; i < sprites.Length; i++)
        {
            int r = i / cols, c = i % cols;
            int x0 = padding + c * (spriteW + padding);
            int y0 = padding + r * (spriteH + padding);
            for (int y = 0; y < spriteH; y++)
                for (int x = 0; x < spriteW; x++)
                    grid[y0 + y, x0 + x] = sprites[i][y, x];
        }

        Write24(path, grid, paletteRgb);
    }
}
