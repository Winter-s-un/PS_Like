using Composa.Model;
using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Editing;

/// <summary>How Image Size resamples raster layers.</summary>
public enum ResampleMode
{
    /// <summary>Smooth when shrinking, Catmull-Rom when enlarging.</summary>
    Automatic,
    /// <summary>Hard pixel edges, for pixel art.</summary>
    Nearest,
    /// <summary>A model that invents detail while enlarging. Falls back to Automatic where it cannot run or when shrinking.</summary>
    Enhance
}

public sealed partial class EditorSession
{
    /// <summary>
    /// The slow half of Image Size with Enhance: every raster layer that would be resampled is enlarged by the model
    /// off the UI thread, from its committed pixels, which are immutable. The results go to <see cref="ResizeImage"/>,
    /// which uses one only while the layer still has the pixels it was made from, so an edit in between costs the
    /// enhancement and nothing else. Progress counts tiles across all layers. Call from the UI thread.
    /// </summary>
    public async Task<EnhancedLayers> PrepareEnhancedAsync(int width, int height, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default)
    {
        width = Math.Clamp(width, 1, Document.MaxSide);
        height = Math.Clamp(height, 1, Document.MaxSide);
        double sx = (double)width / document.Width, sy = (double)height / document.Height;
        var model = UpscaleModels.General;
        var jobs = new List<(Layer Layer, SKBitmap Source, int Width, int Height)>();
        foreach (var layer in document.AllLayers())
        {
            if (layer.Pixels is not { } pixels || layer.Text != null || layer.Shape != null || !layer.Transform.IsPureTranslation(pixels.Width, pixels.Height)) continue;
            int w = Math.Max(1, (int)Math.Round(pixels.Width * sx)), h = Math.Max(1, (int)Math.Round(pixels.Height * sy));
            // Only an enlargement gains anything; the model's multiple must also fit a layer.
            if ((w <= pixels.Width && h <= pixels.Height) || !DocumentLimits.FitsSurface((long)pixels.Width * model.Scale, (long)pixels.Height * model.Scale)) continue;
            jobs.Add((layer, pixels, w, h));
        }
        var results = new Dictionary<Guid, (SKBitmap Source, SKBitmap Result)>();
        if (jobs.Count == 0) return new EnhancedLayers(results);
        var totals = jobs.Sum(j => Upscaling.PlanTiles(j.Source.Width, j.Source.Height).Count);
        var finished = 0;
        try
        {
            foreach (var (layer, source, w, h) in jobs)
            {
                var before = finished;
                var perLayer = new Progress<(int Done, int Total)>(p => progress?.Report((before + p.Done, totals)));
                var enlarged = await Task.Run(() =>
                {
                    using var big = Upscaler.Enlarge(source, model, perLayer, cancellation);
                    // The model always makes its multiple; the rest of the way to the target is plain resampling.
                    return big.Width == w && big.Height == h ? Pixels.Clone(big) : Resample(big, w, h);
                }, cancellation);
                finished += Upscaling.PlanTiles(source.Width, source.Height).Count;
                progress?.Report((finished, totals));
                results[layer.Id] = (source, enlarged);
            }
        }
        catch
        {
            foreach (var (_, result) in results.Values) result.Dispose();
            throw;
        }
        return new EnhancedLayers(results);
    }
}

/// <summary>Enlarged pixels per layer id, with the committed bitmap each was made from.</summary>
public sealed class EnhancedLayers(Dictionary<Guid, (SKBitmap Source, SKBitmap Result)> results)
{
    public int Count => results.Count;

    /// <summary>The prepared pixels for a layer, when the layer still shows the pixels they were made from.</summary>
    public SKBitmap? For(Layer layer) =>
        results.TryGetValue(layer.Id, out var entry) && ReferenceEquals(entry.Source, layer.Pixels) ? entry.Result : null;

    /// <summary>Disposes the results nothing took, after <c>ResizeImage</c> has run or when it will not.</summary>
    public void DisposeUnused(Document document)
    {
        var inUse = document.AllLayers().Select(l => l.Pixels).ToHashSet();
        foreach (var (_, result) in results.Values) if (!inUse.Contains(result)) result.Dispose();
        results.Clear();
    }
}
