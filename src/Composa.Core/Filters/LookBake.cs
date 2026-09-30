using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

/// <summary>
/// Export Look: the document's adjustment layers baked into one lookup table. An identity lattice is laid out as a
/// bitmap, run through each adjustment layer's operation and opacity from the bottom up exactly as the renderer runs
/// the picture through them, and read back as a lattice. Only what is a pure function of a pixel's color can be
/// baked: a layer with a mask, a clipped layer, a layer inside a group (it changes the group, not the picture), and
/// an adjustment that reads neighbouring pixels or its position are left out, and <see cref="Survey"/> says which.
/// Opacity mixes the result back as the renderer does; the renderer applies no blend mode to an adjustment layer, so
/// neither does the bake.
/// </summary>
public static class LookBake
{
    /// <summary>The sizes offered: 17 points for a rough look, 33 as grading software exchanges, 65 for a fine one.</summary>
    public static readonly int[] Sizes = [17, 33, 65];

    /// <summary>A layer that could not be baked, and why, in words that follow its name.</summary>
    public readonly record struct LeftOut(Layer Layer, string Why);

    /// <summary>The visible top-level adjustment layers, bottom to top, split into those baked and those left out.</summary>
    public static (List<Layer> Baked, List<LeftOut> LeftOut) Survey(Document document)
    {
        var baked = new List<Layer>();
        var leftOut = new List<LeftOut>();
        foreach (var layer in document.AllLayers())
        {
            if (!layer.IsAdjustment || layer.Adjustment is not { } adjustment || !layer.Visible || layer.Opacity <= 0 || adjustment.IsIdentity) continue;
            if (!document.IsEffectivelyVisible(layer)) continue;
            var why = layer.Mask != null && layer.MaskEnabled ? "has a layer mask"
                : layer.Clipped ? "is clipped to the layer below"
                : document.ParentOf(layer.Id) != null ? "is inside a group"
                : adjustment.SamplingMargin > 0 ? "reads the pixels around each pixel"
                : adjustment.DependsOnPosition ? "changes from place to place"
                : null;
            if (why == null) baked.Add(layer); else leftOut.Add(new LeftOut(layer, why));
        }
        return (baked, leftOut);
    }

    /// <summary>Bakes the layers <see cref="Survey"/> found bakeable into a lattice of <paramref name="size"/> points per axis.</summary>
    public static ColorLattice Bake(Document document, int size, string title = "") => Bake(Survey(document).Baked, size, title);

    /// <summary>Bakes <paramref name="layers"/>, bottom to top, each applied at its opacity.</summary>
    public static unsafe ColorLattice Bake(IEnumerable<Layer> layers, int size, string title = "")
    {
        if (size < 2 || size > DocumentLimits.MaxLookupSize) throw new ArgumentOutOfRangeException(nameof(size));
        var last = size - 1;
        // Every color of the lattice as one pixel: red along x within a green column, blue down the rows.
        using var grid = Pixels.NewColor(size * size, size);
        var pixels = (byte*)grid.GetPixels();
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                {
                    var p = pixels + (long)b * grid.RowBytes + (g * size + r) * 4;
                    p[0] = (byte)Math.Round(r * 255.0 / last);
                    p[1] = (byte)Math.Round(g * 255.0 / last);
                    p[2] = (byte)Math.Round(b * 255.0 / last);
                    p[3] = 255;
                }
        foreach (var layer in layers)
        {
            if (layer.Adjustment is not { } adjustment || adjustment.IsIdentity || layer.Opacity <= 0) continue;
            using var adjusted = Pixels.Clone(grid);
            adjustment.Apply(adjusted);
            var opacity = Math.Clamp(layer.Opacity, 0, 1);
            var changed = (byte*)adjusted.GetPixels();
            for (var y = 0; y < size; y++)
            {
                var below = pixels + (long)y * grid.RowBytes;
                var above = changed + (long)y * adjusted.RowBytes;
                for (var i = 0; i < size * size * 4; i++)
                    below[i] = opacity >= 1 ? above[i] : (byte)Math.Round(below[i] + (above[i] - below[i]) * opacity);
            }
        }
        var data = new float[size * size * size * 3];
        var k = 0;
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                {
                    var p = pixels + (long)b * grid.RowBytes + (g * size + r) * 4;
                    data[k++] = p[0] / 255f;
                    data[k++] = p[1] / 255f;
                    data[k++] = p[2] / 255f;
                }
        return ColorLattice.FromCube(size, data, title);
    }
}
