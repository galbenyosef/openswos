using System.Text;

namespace OpenSwos.Tools.SpriteDecode;

// Minimal PPM (P6, binary) writer. PPM is trivial — no compression, no dependencies,
// every image viewer on every platform reads it. Good for eyeballing decoded sprites
// before we add a proper PNG encoder later.
public static class Ppm
{
    public static void Write(string path, byte[,] indices, byte[] paletteRgb)
    {
        int height = indices.GetLength(0);
        int width = indices.GetLength(1);

        using var fs = File.Create(path);
        byte[] header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        fs.Write(header);

        var row = new byte[width * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte idx = indices[y, x];
                int p = idx * 3;
                row[x * 3 + 0] = paletteRgb[p + 0];
                row[x * 3 + 1] = paletteRgb[p + 1];
                row[x * 3 + 2] = paletteRgb[p + 2];
            }
            fs.Write(row);
        }
    }

    // Compose multiple [h, w] sprites into one big grid image (cols across, sprites top-left first).
    public static void WriteGrid(string path, byte[][,] sprites, byte[] paletteRgb, int cols, int padding = 1, byte padIndex = 0)
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

        Write(path, grid, paletteRgb);
    }
}
