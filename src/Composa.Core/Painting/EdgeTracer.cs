using Composa.Filters;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>
/// Finds the edges in a picture and hands them back as polylines, so whoever draws by hand knows where the lines
/// really are. It is Canny's detector: a light blur, Sobel gradients, only the ridge of each gradient kept, then a
/// high threshold that starts an edge and a lower one that lets it continue. The edge pixels are linked into chains,
/// each chain is simplified to the points that matter (Douglas-Peucker) and the short scraps are dropped.
/// </summary>
public static unsafe class EdgeTracer
{
    /// <summary>
    /// The edges of <paramref name="source"/> (or of <paramref name="region"/> within it) as polylines in its pixel
    /// coordinates, longest first. <paramref name="detail"/> 0 to 100 says how faint an edge may be, <paramref name="minLength"/>
    /// the shortest chain kept in pixels, <paramref name="simplify"/> how far a polyline may stray from the edge.
    /// </summary>
    public static List<List<SKPoint>> Trace(SKBitmap source, double detail = 50, double minLength = 20, double simplify = 2, int maxLines = int.MaxValue, SKRectI? region = null)
    {
        var whole = new SKRectI(0, 0, source.Width, source.Height);
        var area = region is { } r ? SKRectI.Intersect(r, whole) : whole;
        if (area.IsEmpty) return [];
        int w = area.Width, h = area.Height;
        using var crop = Pixels.NewColor(w, h);
        using (var canvas = new SKCanvas(crop)) canvas.DrawBitmap(source, -area.Left, -area.Top);
        ImageFilters.BlurInPlace(crop, 1.4f);
        var luminance = new float[w * h];
        var src = (byte*)crop.GetPixels();
        Parallel.For(0, h, y =>
        {
            var row = src + (long)y * crop.RowBytes;
            for (var x = 0; x < w; x++) { var p = row + x * 4; luminance[y * w + x] = 0.2126f * p[0] + 0.7152f * p[1] + 0.0722f * p[2]; }
        });

        // Sobel: the gradient's strength and its direction rounded to one of four, for the thinning step.
        var magnitude = new float[w * h];
        var direction = new byte[w * h];
        Parallel.For(1, Math.Max(1, h - 1), y =>
        {
            for (var x = 1; x < w - 1; x++)
            {
                float At(int px, int py) => luminance[py * w + px];
                var gx = At(x + 1, y - 1) + 2 * At(x + 1, y) + At(x + 1, y + 1) - At(x - 1, y - 1) - 2 * At(x - 1, y) - At(x - 1, y + 1);
                var gy = At(x - 1, y + 1) + 2 * At(x, y + 1) + At(x + 1, y + 1) - At(x - 1, y - 1) - 2 * At(x, y - 1) - At(x + 1, y - 1);
                var i = y * w + x;
                magnitude[i] = MathF.Sqrt(gx * gx + gy * gy);
                var angle = MathF.Atan2(gy, gx) * 180 / MathF.PI;
                if (angle < 0) angle += 180;
                direction[i] = (byte)(angle < 22.5f || angle >= 157.5f ? 0 : angle < 67.5f ? 1 : angle < 112.5f ? 2 : 3);
            }
        });
        // Only the ridge of each gradient stays: a pixel that is not at least as strong as its two neighbours along the gradient is dropped.
        var ridge = new float[w * h];
        Parallel.For(1, Math.Max(1, h - 1), y =>
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                var m = magnitude[i];
                if (m <= 0) continue;
                var (dx, dy) = direction[i] switch { 0 => (1, 0), 1 => (1, 1), 2 => (0, 1), _ => (-1, 1) };
                // A tie with the pixel before it loses and with the one after it wins, so a step that straddles two
                // equal pixels keeps one ridge, not two.
                if (m > magnitude[(y - dy) * w + x - dx] && m >= magnitude[(y + dy) * w + x + dx]) ridge[i] = m;
            }
        });
        // Sobel of a one-level step is 4, so the thresholds are in gray levels times four; after the blur, Detail 50
        // starts an edge at a step of about 50 levels, Detail 100 at a few and Detail 0 only at a strong one.
        var high = (float)((0.02 + (100 - Math.Clamp(detail, 0, 100)) / 100 * 0.28) * 1020);
        var low = high * 0.4f;
        var edge = new bool[w * h];
        var stack = new Stack<int>();
        for (var i = 0; i < ridge.Length; i++)
        {
            if (ridge[i] < high || edge[i]) continue;
            edge[i] = true;
            stack.Push(i);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                int ax = at % w, ay = at / w;
                for (var ny = Math.Max(0, ay - 1); ny <= Math.Min(h - 1, ay + 1); ny++)
                    for (var nx = Math.Max(0, ax - 1); nx <= Math.Min(w - 1, ax + 1); nx++)
                    {
                        var n = ny * w + nx;
                        if (edge[n] || ridge[n] < low) continue;
                        edge[n] = true;
                        stack.Push(n);
                    }
            }
        }

        // Chains: from every loose end first, so a line is followed from one end to the other, then whatever is left, which are loops.
        var visited = new bool[w * h];
        var chains = new List<(float Length, List<SKPoint> Points)>();
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < edge.Length; i++)
            {
                if (!edge[i] || visited[i]) continue;
                if (pass == 0 && Neighbours(edge, visited, w, h, i) != 1) continue;
                var chain = Follow(edge, visited, w, h, i);
                var length = 0f;
                for (var k = 1; k < chain.Count; k++) length += SKPoint.Distance(chain[k - 1], chain[k]);
                if (length < minLength) continue;
                var simplified = Simplify(chain, (float)Math.Max(0, simplify));
                for (var k = 0; k < simplified.Count; k++) simplified[k] = new SKPoint(simplified[k].X + area.Left, simplified[k].Y + area.Top);
                chains.Add((length, simplified));
            }
        return chains.OrderByDescending(c => c.Length).Take(Math.Max(0, maxLines)).Select(c => c.Points).ToList();
    }

    private static int Neighbours(bool[] edge, bool[] visited, int w, int h, int i)
    {
        int x = i % w, y = i / w, count = 0;
        for (var ny = Math.Max(0, y - 1); ny <= Math.Min(h - 1, y + 1); ny++)
            for (var nx = Math.Max(0, x - 1); nx <= Math.Min(w - 1, x + 1); nx++)
            {
                var n = ny * w + nx;
                if (n != i && edge[n] && !visited[n]) count++;
            }
        return count;
    }

    /// <summary>
    /// Walks from a pixel along unvisited edge pixels, straight on before turning, until the chain ends. A gap of one
    /// pixel is stepped over, because thinning drops the very pixel at a sharp corner and the outline would otherwise
    /// come back as four sides.
    /// </summary>
    private static List<SKPoint> Follow(bool[] edge, bool[] visited, int w, int h, int start)
    {
        var points = new List<SKPoint>();
        int at = start, dx = 0, dy = 0;
        while (true)
        {
            visited[at] = true;
            int x = at % w, y = at / w;
            points.Add(new SKPoint(x + 0.5f, y + 0.5f));
            var next = -1;
            for (var reach = 1; reach <= 2 && next < 0; reach++)
            {
                var best = int.MinValue;
                for (var ny = Math.Max(0, y - reach); ny <= Math.Min(h - 1, y + reach); ny++)
                    for (var nx = Math.Max(0, x - reach); nx <= Math.Min(w - 1, x + reach); nx++)
                    {
                        var n = ny * w + nx;
                        if (n == at || !edge[n] || visited[n]) continue;
                        // Straight on scores highest, a side step next, a step back last; a 4-neighbour beats a diagonal at equal turn.
                        var score = Math.Sign(nx - x) * dx + Math.Sign(ny - y) * dy;
                        score = score * 4 + (nx == x || ny == y ? 1 : 0);
                        if (score > best) { best = score; next = n; }
                    }
            }
            if (next < 0) return points;
            dx = Math.Sign(next % w - x); dy = Math.Sign(next / w - y);
            at = next;
        }
    }

    /// <summary>Douglas-Peucker: keeps the points that pull the line more than <paramref name="tolerance"/> away from the straight line between its neighbours.</summary>
    private static List<SKPoint> Simplify(List<SKPoint> points, float tolerance)
    {
        if (points.Count < 3 || tolerance <= 0) return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var ranges = new Stack<(int, int)>();
        ranges.Push((0, points.Count - 1));
        while (ranges.Count > 0)
        {
            var (first, last) = ranges.Pop();
            float farthest = 0;
            var index = -1;
            var a = points[first]; var b = points[last];
            float abx = b.X - a.X, aby = b.Y - a.Y, ab = MathF.Sqrt(abx * abx + aby * aby);
            for (var i = first + 1; i < last; i++)
            {
                var p = points[i];
                var distance = ab < 1e-6f ? SKPoint.Distance(a, p) : MathF.Abs(abx * (a.Y - p.Y) - (a.X - p.X) * aby) / ab;
                if (distance > farthest) { farthest = distance; index = i; }
            }
            if (index < 0 || farthest <= tolerance) continue;
            keep[index] = true;
            ranges.Push((first, index));
            ranges.Push((index, last));
        }
        var result = new List<SKPoint>();
        for (var i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
        return result;
    }
}
