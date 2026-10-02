// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/upscale-math.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
namespace Composa.Vision;

/// <summary>
/// One tile of an enlargement: the core rectangle the result keeps, and the padded window fed to the model so the
/// core's edges see their neighbours. All in source pixels.
/// </summary>
public readonly record struct TilePlan(int CoreX, int CoreY, int CoreWidth, int CoreHeight, int PadX, int PadY, int PadWidth, int PadHeight);

/// <summary>The arithmetic of enlarging in tiles: the grid, and the model's planes back to bytes.</summary>
public static class Upscaling
{
    /// <summary>The source pixels each tile's window reaches past its core, cropped off again at the model's multiple.</summary>
    public const int Overlap = 16;

    /// <summary>The core size of a tile. Measured on a CPU: about 1.3 seconds a tile for the general model.</summary>
    public const int TileSize = 256;

    /// <summary>Every tile for a width by height source, row by row.</summary>
    public static List<TilePlan> PlanTiles(int width, int height, int tile = TileSize, int overlap = Overlap)
    {
        var tiles = new List<TilePlan>();
        int across = Math.Max(1, (width + tile - 1) / tile), down = Math.Max(1, (height + tile - 1) / tile);
        for (var ty = 0; ty < down; ty++)
        for (var tx = 0; tx < across; tx++)
        {
            int cx = tx * tile, cy = ty * tile;
            int cw = Math.Min(tile, width - cx), ch = Math.Min(tile, height - cy);
            int px0 = Math.Max(0, cx - overlap), py0 = Math.Max(0, cy - overlap);
            int px1 = Math.Min(width, cx + cw + overlap), py1 = Math.Min(height, cy + ch + overlap);
            tiles.Add(new TilePlan(cx, cy, cw, ch, px0, py0, px1 - px0, py1 - py0));
        }
        return tiles;
    }

    /// <summary>
    /// Copies a window of plane-major 0 to 1 floats (all red, then green, then blue, each <paramref name="planeWidth"/>
    /// by <paramref name="planeHeight"/>) into straight RGB bytes at <paramref name="destination"/>, a buffer with
    /// three bytes per pixel and <paramref name="destinationStride"/> bytes per row, at the given offset.
    /// </summary>
    public static void PlanesToRgb(ReadOnlySpan<float> planes, int planeWidth, int planeHeight, int cropX, int cropY, int cropWidth, int cropHeight,
        Span<byte> destination, int destinationStride, int destinationX, int destinationY)
    {
        var page = planeWidth * planeHeight;
        for (var y = 0; y < cropHeight; y++)
        {
            var sourceRow = (cropY + y) * planeWidth + cropX;
            var row = destination.Slice((destinationY + y) * destinationStride + destinationX * 3, cropWidth * 3);
            for (var x = 0; x < cropWidth; x++)
            {
                var at = sourceRow + x;
                row[x * 3] = ToByte(planes[at]);
                row[x * 3 + 1] = ToByte(planes[at + page]);
                row[x * 3 + 2] = ToByte(planes[at + 2 * page]);
            }
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
