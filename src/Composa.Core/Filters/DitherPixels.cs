using System.Text;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

/// <summary>
/// The Dither filter's pipeline, stage for stage as the macOS app's C code: tone (density as a gamma, contrast about
/// mid gray), then either error diffusion or an ordered threshold quantized to a number of tones, or marks (halftone
/// spots, old Mac patterns, glyphs) covering as much of each cell as the tone calls for. Straight color in,
/// premultiplied bytes out; alpha is kept and transparent pixels are left alone. Chunky pixels dither a copy averaged
/// down by the pixel size and blow it back up without smoothing.
/// </summary>
public static unsafe class DitherPixels
{
    public static SKBitmap Apply(SKBitmap source, DitherSettings settings)
    {
        var s = settings.Normalized();
        // ASCII draws its characters at full resolution: shrinking the image first would blur and break them up.
        var block = s.Style == DitherStyle.Ascii ? 1 : s.PixelSize;
        if (block <= 1) return Dither(source, s);
        using var small = Shrink(source, block);
        using var dithered = Dither(small, s);
        var full = Grow(dithered, block, source.Width, source.Height);
        // The gaps between round pixels are the dark color: black, or the one picked.
        if (s.PixelShape == DitherPixelShape.Dot) Dots(full, block, s.Colors == DitherColors.TwoColors ? DitherSettings.Bytes(s.Dark) : ((byte)0, (byte)0, (byte)0));
        Pixels.Invalidate(full);
        return full;
    }

    /// <summary>Each block of pixels averaged into one (premultiplied, so transparent pixels weigh nothing); the last row and column may be partial.</summary>
    private static SKBitmap Shrink(SKBitmap source, int block)
    {
        int width = (source.Width + block - 1) / block, height = (source.Height + block - 1) / block;
        var small = Pixels.NewColor(width, height);
        byte* src = (byte*)source.GetPixels(); byte* dst = (byte*)small.GetPixels();
        int srcStride = source.RowBytes, dstStride = small.RowBytes, sw = source.Width, sh = source.Height;
        Parallel.For(0, height, y =>
        {
            var row = dst + y * dstStride;
            for (var x = 0; x < width; x++)
            {
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int yy = y * block, yEnd = Math.Min(sh, yy + block); yy < yEnd; yy++)
                {
                    var p = src + yy * srcStride + x * block * 4;
                    for (int xx = x * block, xEnd = Math.Min(sw, xx + block); xx < xEnd; xx++, p += 4) { r += p[0]; g += p[1]; b += p[2]; a += p[3]; n++; }
                }
                var o = row + x * 4;
                o[0] = (byte)((r + n / 2) / n); o[1] = (byte)((g + n / 2) / n); o[2] = (byte)((b + n / 2) / n); o[3] = (byte)((a + n / 2) / n);
            }
        });
        Pixels.Invalidate(small);
        return small;
    }

    /// <summary>The small picture blown up by the block size without smoothing.</summary>
    private static SKBitmap Grow(SKBitmap small, int block, int width, int height)
    {
        var full = Pixels.NewColor(width, height);
        byte* src = (byte*)small.GetPixels(); byte* dst = (byte*)full.GetPixels();
        int srcStride = small.RowBytes, dstStride = full.RowBytes;
        Parallel.For(0, height, y =>
        {
            var from = src + (y / block) * srcStride;
            var to = dst + y * dstStride;
            for (var x = 0; x < width; x++) *(uint*)(to + x * 4) = *(uint*)(from + (x / block) * 4);
        });
        return full;
    }

    /// <summary>
    /// Turns each block of premultiplied pixels into a round dot in its own color on the gap color, like the lit
    /// pixels of a dot-matrix screen. The dot's edge is smoothed and alpha is kept.
    /// </summary>
    internal static void Dots(SKBitmap bitmap, int block, (byte R, byte G, byte B) gap)
    {
        if (block < 2) return;
        float radius = block * 0.42f, middle = block / 2f;
        byte* pixels = (byte*)bitmap.GetPixels();
        int stride = bitmap.RowBytes, width = bitmap.Width;
        Parallel.For(0, bitmap.Height, y =>
        {
            var row = pixels + y * stride;
            var dy = y % block + 0.5f - middle;
            for (var x = 0; x < width; x++)
            {
                var px = row + x * 4;
                if (px[3] == 0) continue;
                var dx = x % block + 0.5f - middle;
                var cover = Clamp01(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f);
                if (cover >= 1) continue;
                var alpha = px[3] / 255f;
                px[0] = (byte)MathF.Round(px[0] * cover + gap.R * alpha * (1 - cover));
                px[1] = (byte)MathF.Round(px[1] * cover + gap.G * alpha * (1 - cover));
                px[2] = (byte)MathF.Round(px[2] * cover + gap.B * alpha * (1 - cover));
            }
        });
    }

    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>Density darkens (positive) or lightens as a gamma, so black and white stay put; contrast pivots on mid gray.</summary>
    private static float AdjustTone(float v, float gamma, float contrast)
    {
        v = MathF.Pow(Clamp01(v), gamma);
        return Clamp01((v - 0.5f) * contrast + 0.5f);
    }

    private readonly record struct Tap(int Dx, int Dy, int Weight);
    private static readonly Tap[] AtkinsonTaps = [new(1, 0, 1), new(2, 0, 1), new(-1, 1, 1), new(0, 1, 1), new(1, 1, 1), new(0, 2, 1)];
    private static readonly Tap[] FloydTaps = [new(1, 0, 7), new(-1, 1, 3), new(0, 1, 5), new(1, 1, 1)];

    private static float Quantize(float v, int levels)
    {
        float steps = levels - 1;
        return MathF.Round(Clamp01(v) * steps) / steps;
    }

    /// <summary>
    /// Diffuses one plane in serpentine order, so the error's drift does not streak to one side. Atkinson passes on
    /// only six eighths of the error, which is what gives the Mac's crisp, contrasty look.
    /// </summary>
    private static void Diffuse(float[] plane, long offset, byte[] alpha, int width, int height, DitherStyle style, int levels, float diffusion)
    {
        var taps = style == DitherStyle.Atkinson ? AtkinsonTaps : FloydTaps;
        var divisor = style == DitherStyle.Atkinson ? 8f : 16f;
        for (var y = 0; y < height; y++)
        {
            var reverse = (y & 1) == 1;
            for (var i = 0; i < width; i++)
            {
                var x = reverse ? width - 1 - i : i;
                var at = (long)y * width + x;
                if (alpha[at] == 0) continue;
                float old = plane[offset + at], q = Quantize(old, levels);
                plane[offset + at] = q;
                var error = (old - q) * diffusion / divisor;
                foreach (var tap in taps)
                {
                    int nx = x + (reverse ? -tap.Dx : tap.Dx), ny = y + tap.Dy;
                    if (nx < 0 || nx >= width || ny >= height) continue;
                    plane[offset + (long)ny * width + nx] += error * tap.Weight;
                }
            }
        }
    }

    private static readonly byte[] Bayer8 =
    [
         0, 32,  8, 40,  2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37, 63, 31, 55, 23, 61, 29, 53, 21
    ];
    private static readonly byte[] Bayer2 = [0, 2, 3, 1];
    private static readonly byte[] Bayer4 = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <summary>The ordered threshold for a pixel, in [0, 1). The smaller matrices nest in the 8 × 8 one, rescaled.</summary>
    private static float OrderedThreshold(DitherStyle style, int x, int y) => style switch
    {
        DitherStyle.Bayer2 => (Bayer2[(y & 1) * 2 + (x & 1)] + 0.5f) / 4,
        DitherStyle.Bayer4 => (Bayer4[(y & 3) * 4 + (x & 3)] + 0.5f) / 16,
        _ => (Bayer8[(y & 7) * 8 + (x & 7)] + 0.5f) / 64
    };

    private static float Ordered(float v, float threshold, int levels)
    {
        float steps = levels - 1;
        var q = MathF.Floor(Clamp01(v) * steps + threshold);
        return (q > steps ? steps : q) / steps;
    }

    /// <summary>
    /// How much of a halftone cell a point must be covered by before it is marked, for each screen shape. u and v run
    /// from -0.5 to 0.5 across the cell; the shapes grow from its middle as coverage rises.
    /// </summary>
    private static float Spot(DitherStyle style, float u, float v) => style switch
    {
        DitherStyle.HalftoneDots => MathF.PI * (u * u + v * v),
        DitherStyle.HalftoneLines => MathF.Abs(v) * 2,
        _ => MathF.Abs(u) + MathF.Abs(v)
    };

    /// <summary>Old Mac fill patterns, 8 × 8, one byte per row with the leftmost pixel in the top bit, from sparsest to fullest.</summary>
    private static readonly byte[][] Patterns =
    [
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x80, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00],
        [0x88, 0x00, 0x22, 0x00, 0x88, 0x00, 0x22, 0x00],
        [0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01],
        [0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22],
        [0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00],
        [0x11, 0x22, 0x44, 0x88, 0x11, 0x22, 0x44, 0x88],
        [0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00],
        [0x88, 0x55, 0x22, 0x55, 0x88, 0x55, 0x22, 0x55],
        [0xFF, 0x80, 0x80, 0x80, 0xFF, 0x08, 0x08, 0x08],
        [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55],
        [0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81],
        [0x77, 0xAA, 0xDD, 0xAA, 0x77, 0xAA, 0xDD, 0xAA],
        [0xEE, 0xDD, 0xBB, 0x77, 0xEE, 0xDD, 0xBB, 0x77],
        [0x77, 0xFF, 0xDD, 0xFF, 0x77, 0xFF, 0xDD, 0xFF],
        [0x7F, 0xFF, 0xFF, 0xFF, 0xF7, 0xFF, 0xFF, 0xFF],
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]
    ];

    private static void WritePixel(byte* px, float r, float g, float b)
    {
        var a = px[3] / 255f;
        px[0] = (byte)MathF.Round(Clamp01(r) * a * 255);
        px[1] = (byte)MathF.Round(Clamp01(g) * a * 255);
        px[2] = (byte)MathF.Round(Clamp01(b) * a * 255);
    }

    /// <summary>Dithers a copy of the picture at its own size.</summary>
    private static SKBitmap Dither(SKBitmap image, DitherSettings s)
    {
        var result = Pixels.Clone(image);
        int width = result.Width, height = result.Height, stride = result.RowBytes;
        var count = (long)width * height;
        if (count == 0) return result;
        byte* rgba = (byte*)result.GetPixels();
        var original = s.Colors == DitherColors.Original;
        var planes = original ? 3 : 1;
        var tone = new float[count * planes];
        var alpha = new byte[count];
        // The image's own colors, unadjusted: halftone dots and glyphs take them in Original mode.
        var source = original ? new float[count * 3] : null;

        var gamma = MathF.Pow(2, (float)(s.Density / 100) * 1.5f);
        var contrastAmount = (float)(s.Contrast / 100);
        var contrast = contrastAmount >= 0 ? 1 / (1 - 0.95f * contrastAmount) : 1 + contrastAmount;
        Parallel.For(0, height, y =>
        {
            var row = rgba + y * stride;
            for (var x = 0; x < width; x++)
            {
                var px = row + x * 4;
                var at = (long)y * width + x;
                alpha[at] = px[3];
                float r = 0, g = 0, b = 0;
                if (px[3] != 0)
                {
                    var scale = 1f / px[3];
                    r = px[0] * scale; g = px[1] * scale; b = px[2] * scale;
                }
                if (original)
                {
                    tone[at] = AdjustTone(r, gamma, contrast);
                    tone[count + at] = AdjustTone(g, gamma, contrast);
                    tone[2 * count + at] = AdjustTone(b, gamma, contrast);
                    source![at * 3] = r; source[at * 3 + 1] = g; source[at * 3 + 2] = b;
                }
                else tone[at] = AdjustTone(0.2126f * r + 0.7152f * g + 0.0722f * b, gamma, contrast);
            }
        });

        (byte R, byte G, byte B) darkColor = s.Colors == DitherColors.TwoColors ? DitherSettings.Bytes(s.Dark) : ((byte)0, (byte)0, (byte)0);
        (byte R, byte G, byte B) lightColor = s.Colors == DitherColors.TwoColors ? DitherSettings.Bytes(s.Light) : ((byte)255, (byte)255, (byte)255);
        float[] dark = [darkColor.R / 255f, darkColor.G / 255f, darkColor.B / 255f], light = [lightColor.R / 255f, lightColor.G / 255f, lightColor.B / 255f];
        var style = s.Style;
        var levels = Math.Clamp(s.Levels, 2, 16);

        if (s.HasTones)
        {
            // Diffusion and ordered dithering: each plane is quantized to the tones, then mapped to colors.
            if (s.Diffuses)
            {
                var diffusion = (float)(s.Diffusion / 100);
                for (var c = 0; c < planes; c++) Diffuse(tone, c * count, alpha, width, height, style, levels, diffusion);
            }
            else
            {
                for (var c = 0; c < planes; c++)
                {
                    var offset = c * count;
                    Parallel.For(0, height, y =>
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var at = (long)y * width + x;
                            if (alpha[at] != 0) tone[offset + at] = Ordered(tone[offset + at], OrderedThreshold(style, x, y), levels);
                        }
                    });
                }
            }
            Parallel.For(0, height, y =>
            {
                var row = rgba + y * stride;
                for (var x = 0; x < width; x++)
                {
                    var at = (long)y * width + x;
                    if (alpha[at] == 0) continue;
                    if (original) WritePixel(row + x * 4, tone[at], tone[count + at], tone[2 * count + at]);
                    else
                    {
                        var t = tone[at];
                        WritePixel(row + x * 4, dark[0] + (light[0] - dark[0]) * t, dark[1] + (light[1] - dark[1]) * t, dark[2] + (light[2] - dark[2]) * t);
                    }
                }
            });
        }
        else
        {
            // Marks (halftone shapes, patterns, glyphs) cover as much of each spot as the tone calls for. On light,
            // they stand for darkness and are drawn in the dark color; light on dark, the reverse.
            var marks = tone;
            if (original)
            {
                marks = new float[count];
                Parallel.For(0, height, y =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var i = (long)y * width + x;
                        marks[i] = 0.2126f * tone[i] + 0.7152f * tone[count + i] + 0.0722f * tone[2 * count + i];
                    }
                });
            }
            var cell = Math.Max(2, s.CellSize);
            var angle = (float)(s.Angle * Math.PI / 180);
            float cosA = MathF.Cos(angle), sinA = MathF.Sin(angle);
            var lightOnDark = s.LightOnDark;
            float[] ink = lightOnDark ? light : dark, paper = lightOnDark ? dark : light;
            // Glyphs: each cell shares one, picked from the cell's average tone, worked out once per cell.
            var glyphs = style == DitherStyle.Ascii ? Glyphs(s.Characters.Length == 0 ? DitherSettings.DefaultCharacters : s.Characters, s.TextSize) : null;
            int gw = glyphs?.Width ?? 1, gh = glyphs?.Height ?? 1;
            int columns = (width + gw - 1) / gw, cellRows = (height + gh - 1) / gh;
            int[]? picked = null;
            if (glyphs is { Coverage.Length: > 0 })
            {
                picked = new int[columns * cellRows];
                var coverage = glyphs.Coverage;
                Parallel.For(0, cellRows, row =>
                {
                    for (var column = 0; column < columns; column++)
                    {
                        float sum = 0; var n = 0;
                        for (int yy = row * gh, yEnd = Math.Min(height, yy + gh); yy < yEnd; yy++)
                            for (int xx = column * gw, xEnd = Math.Min(width, xx + gw); xx < xEnd; xx++)
                            {
                                var i = (long)yy * width + xx;
                                if (alpha[i] != 0) { sum += marks[i]; n++; }
                            }
                        var t = n > 0 ? sum / n : 1;
                        var wanted = (lightOnDark ? t : 1 - t) * coverage[^1];
                        var best = 0;
                        var bestDistance = 2f;
                        for (var g = 0; g < coverage.Length; g++)
                        {
                            var d = MathF.Abs(coverage[g] - wanted);
                            if (d < bestDistance) { bestDistance = d; best = g; }
                        }
                        picked[row * columns + column] = best;
                    }
                });
            }
            // Original colors: marks take the pixel's own color, on black (light on dark) or white.
            var paperOriginal = lightOnDark ? 0f : 1f;
            Parallel.For(0, height, y =>
            {
                var row = rgba + y * stride;
                for (var x = 0; x < width; x++)
                {
                    var at = (long)y * width + x;
                    if (alpha[at] == 0) continue;
                    float amount;
                    if (picked != null)
                    {
                        var glyph = picked[(y / gh) * columns + x / gw];
                        amount = glyphs!.Maps[(long)glyph * gw * gh + (y % gh) * gw + x % gw] / 255f;
                    }
                    else if (style == DitherStyle.MacPatterns)
                    {
                        var t = marks[at];
                        var coverage = lightOnDark ? t : 1 - t;
                        var index = (int)MathF.Round(coverage * (Patterns.Length - 1));
                        amount = (Patterns[index][y & 7] >> (7 - (x & 7))) & 1;
                    }
                    else
                    {
                        float fx = x + 0.5f, fy = y + 0.5f;
                        float u = (fx * cosA + fy * sinA) / cell, v = (-fx * sinA + fy * cosA) / cell;
                        u -= MathF.Floor(u) + 0.5f; v -= MathF.Floor(v) + 0.5f;
                        var t = marks[at];
                        amount = (lightOnDark ? t : 1 - t) > Spot(style, u, v) ? 1 : 0;
                    }
                    if (original)
                    {
                        var i = at * 3;
                        WritePixel(row + x * 4, paperOriginal + (source![i] - paperOriginal) * amount, paperOriginal + (source[i + 1] - paperOriginal) * amount, paperOriginal + (source[i + 2] - paperOriginal) * amount);
                    }
                    else WritePixel(row + x * 4, paper[0] + (ink[0] - paper[0]) * amount, paper[1] + (ink[1] - paper[1]) * amount, paper[2] + (ink[2] - paper[2]) * amount);
                }
            });
        }
        Pixels.Invalidate(result);
        return result;
    }

    /// <summary>Glyph coverage maps of one size each, from least ink to most, with each map's mean coverage (0 to 1).</summary>
    internal sealed record GlyphSet(byte[] Maps, float[] Coverage, int Width, int Height);

    /// <summary>
    /// Each distinct character drawn into a cell of monospaced text, the line height tall and one character wide, on
    /// a shared baseline as a terminal lays them out, sorted from least ink to most.
    /// </summary>
    internal static GlyphSet Glyphs(string characters, int lineHeight)
    {
        var typeface = Monospace();
        using var font = new SKFont(typeface, lineHeight / 1.2f) { Edging = SKFontEdging.Antialias, Subpixel = false };
        var height = Math.Max(1, lineHeight);
        var width = Math.Max(1, (int)MathF.Round(font.MeasureText("M")));
        var metrics = font.Metrics;
        var baseline = MathF.Round((height - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent);
        var drawn = new List<(byte[] Map, float Coverage)>();
        var seen = new HashSet<Rune>();
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        foreach (var rune in characters.EnumerateRunes())
        {
            if (!seen.Add(rune)) continue;
            using var cell = Pixels.NewMask(width, height);
            var text = rune.ToString();
            using (var canvas = new SKCanvas(cell))
                canvas.DrawText(text, MathF.Round((width - font.MeasureText(text)) / 2), baseline, SKTextAlign.Left, font, paint);
            var map = new byte[width * height];
            byte* pixels = (byte*)cell.GetPixels();
            long sum = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var value = pixels[y * cell.RowBytes + x];
                    map[y * width + x] = value;
                    sum += value;
                }
            drawn.Add((map, sum / (255f * width * height)));
        }
        drawn.Sort((a, b) => a.Coverage.CompareTo(b.Coverage));
        var maps = new byte[drawn.Count * width * height];
        for (var i = 0; i < drawn.Count; i++) drawn[i].Map.CopyTo(maps, i * width * height);
        return new GlyphSet(maps, drawn.Select(d => d.Coverage).ToArray(), width, height);
    }

    /// <summary>A bold monospaced face: the platform's alias where it has one, a common family otherwise, the default face failing all.</summary>
    private static SKTypeface Monospace()
    {
        var bold = new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        foreach (var family in new[] { "monospace", "DejaVu Sans Mono", "Liberation Mono", "Consolas", "Cascadia Mono", "Courier New", "Menlo", "Noto Sans Mono" })
        {
            if (SKFontManager.Default.MatchFamily(family, bold) is { } face) return face;
        }
        return SKTypeface.Default;
    }
}
