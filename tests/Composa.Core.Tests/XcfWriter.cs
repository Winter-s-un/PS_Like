// Ported from Lolly (github.com/lolly-tools/lolly, tests/helpers/xcf-fixture.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>
/// Builds GIMP files for tests, following GIMP's description of the format: every version family (4-byte pointers
/// before XCF 11, 8-byte from it, the precision field from 4, the paths list from 18, the effect list from 20), every
/// compression and every precision. Layers are listed top first, as GIMP lists them. Composa never writes XCF; this
/// is the test project's own writer, shared into the app tests by source.
/// </summary>
public sealed class XcfWriter
{
    public int Version = 11;
    public int Width = 4, Height = 4;
    /// <summary>0 RGB, 1 grayscale, 2 indexed.</summary>
    public int BaseType;
    /// <summary>The precision code written for XCF 4 and later; 150 (8-bit gamma) unless set.</summary>
    public uint Precision = 150;
    /// <summary>0 none, 1 RLE, 2 zlib.</summary>
    public int Compression = 1;
    public byte[]? Colormap;
    public List<(int Position, bool Vertical)> Guides = [];
    public float? Resolution;
    public bool IccProfile;
    /// <summary>Image channels and paths to list; their structures are never read, so dummies are pointed at.</summary>
    public int Channels, Paths;
    public List<XcfWriterLayer> Layers = [];

    private const int Tile = 64;
    private const uint PropEnd = 0, PropColormap = 1, PropOpacity = 6, PropMode = 7, PropVisible = 8, PropApplyMask = 11, PropOffsets = 15,
        PropCompression = 17, PropGuides = 18, PropResolution = 19, PropParasites = 21, PropVectors = 25, PropGroupItem = 29, PropItemPath = 30,
        PropGroupItemFlags = 31, PropFloatOpacity = 33, PropCompositeMode = 35, PropCompositeSpace = 36, PropBlendSpace = 37, PropVectorLayer = 47, PropLinkLayer = 48;

    public bool Wide => Version >= 11;
    public int BytesPerSample => Version < 4 ? 1 : Precision switch { 0 or 100 or 150 => 1, 1 or 200 or 250 or 3 => 2, 400 or 450 when Version is 5 or 6 => 2, 500 or 550 => Version is 5 or 6 ? 4 : 2, 2 or 300 or 350 or 4 or 600 or 650 => 4, 700 or 750 => 8, _ => 1 };
    public bool FloatSamples => Version >= 4 && Precision switch { 3 or 4 => true, 400 or 450 => Version is 5 or 6, 500 or 550 or 600 or 650 or 700 or 750 => true, _ => false };
    public bool LinearSamples => Version >= 4 && Precision switch { 2 or 3 or 4 => true, 100 or 200 or 300 or 400 or 500 or 600 or 700 => true, _ => false };

    public byte[] Build()
    {
        var w = new Buffer();
        w.Ascii("gimp xcf ");
        w.Ascii(Version == 0 ? "file" : $"v{Version:000}");
        w.U8(0);
        w.U32((uint)Width);
        w.U32((uint)Height);
        w.U32((uint)BaseType);
        if (Version >= 4) w.U32(Precision);

        // Image properties.
        w.Property(PropCompression, p => p.U8((byte)Compression));
        if (Colormap != null) w.Property(PropColormap, p => { p.U32((uint)(Colormap.Length / 3)); p.Bytes(Colormap); });
        if (Guides.Count > 0) w.Property(PropGuides, p => { foreach (var (position, vertical) in Guides) { p.I32(position); p.U8((byte)(vertical ? 2 : 1)); } });
        if (Resolution is { } resolution) w.Property(PropResolution, p => { p.F32(resolution); p.F32(resolution); });
        if (IccProfile) w.Property(PropParasites, p => { p.Str("icc-profile"); p.U32(1); p.U32(16); p.Bytes(new byte[16]); });
        if (Paths > 0 && Version < 18) w.Property(PropVectors, p => { p.U32(1); p.U32(0); p.U32((uint)Paths); });
        w.U32(PropEnd); w.U32(0);

        // The three pointer lists: layers, channels, and from XCF 18 the paths. Channels and paths point at the header.
        var layerAt = Layers.Select(_ => new long[1]).ToList();
        foreach (var box in layerAt) w.Ptr(Wide, () => box[0]);
        w.Ptr(Wide, () => 0);
        for (var i = 0; i < Channels; i++) w.Ptr(Wide, () => 14);
        w.Ptr(Wide, () => 0);
        if (Version >= 18)
        {
            for (var i = 0; i < Paths; i++) w.Ptr(Wide, () => 14);
            w.Ptr(Wide, () => 0);
        }

        for (var li = 0; li < Layers.Count; li++)
        {
            var l = Layers[li];
            layerAt[li][0] = w.Length;
            var here = w.Length;
            w.U32((uint)l.Width);
            w.U32((uint)l.Height);
            w.U32((uint)l.Type);
            w.Str(l.Name);
            if (l.Opacity255 is { } opacity) w.Property(PropOpacity, p => p.U32((uint)opacity));
            if (l.FloatOpacity is { } floatOpacity) w.Property(PropFloatOpacity, p => p.F32(floatOpacity));
            w.Property(PropVisible, p => p.U32(l.Visible ? 1u : 0u));
            if (l.Mode is { } mode) w.Property(PropMode, p => p.U32((uint)mode));
            if (l.X != 0 || l.Y != 0 || l.WriteOffsets) w.Property(PropOffsets, p => { p.I32(l.X); p.I32(l.Y); });
            if (l.IsGroup) w.Property(PropGroupItem, _ => { });
            if (l.Expanded is { } expanded) w.Property(PropGroupItemFlags, p => p.U32(expanded ? 1u : 0u));
            if (l.ItemPath != null) w.Property(PropItemPath, p => { foreach (var index in l.ItemPath) p.U32((uint)index); });
            if (l.ApplyMask is { } applyMask) w.Property(PropApplyMask, p => p.U32(applyMask ? 1u : 0u));
            if (l.BlendSpace is { } blendSpace) w.Property(PropBlendSpace, p => p.I32(blendSpace));
            if (l.CompositeSpace is { } compositeSpace) w.Property(PropCompositeSpace, p => p.I32(compositeSpace));
            if (l.CompositeMode is { } compositeMode) w.Property(PropCompositeMode, p => p.I32(compositeMode));
            if (l.Text != null) w.Property(PropParasites, p => { p.Str("gimp-text-layer"); p.U32(1); var text = Encoding.UTF8.GetBytes(l.Text); p.U32((uint)text.Length); p.Bytes(text); });
            if (l.VectorLayer) w.Property(PropVectorLayer, p => p.U32(1));
            if (l.LinkLayer) w.Property(PropLinkLayer, p => p.U32(1));
            w.U32(PropEnd); w.U32(0);

            var hierarchyAt = new long[1];
            var maskAt = new long[1];
            w.Ptr(Wide, () => hierarchyAt[0]);
            w.Ptr(Wide, () => maskAt[0]);
            if (Version >= 20)
            {
                for (var i = 0; i < l.Effects; i++) w.Ptr(Wide, () => here);
                w.Ptr(Wide, () => 0);
            }

            if (!l.IsGroup && l.Rgba != null)
            {
                var bytesPerSample = l.Type >= 4 ? 1 : BytesPerSample;
                var channels = l.Type switch { 0 => 3, 1 => 4, 2 => 1, 3 => 2, 4 => 1, _ => 2 };
                hierarchyAt[0] = WriteHierarchy(w, l.Width, l.Height, channels * bytesPerSample, (tx, ty, tw, th) => LayerTile(l, channels, bytesPerSample, tx, ty, tw, th), l.PoisonTilePointer);
            }
            if (l.Mask != null)
            {
                maskAt[0] = w.Length;
                w.U32((uint)l.Width);
                w.U32((uint)l.Height);
                w.Str("mask");
                w.U32(PropEnd); w.U32(0);
                var maskHierarchy = new long[1];
                w.Ptr(Wide, () => maskHierarchy[0]);
                maskHierarchy[0] = WriteHierarchy(w, l.Width, l.Height, BytesPerSample, (tx, ty, tw, th) => MaskTile(l, tx, ty, tw, th), false);
            }
        }
        return w.Finish();
    }

    /// <summary>One tile's interleaved samples: RGB(A) from the RGBA bytes, gray from red, an index from red, each at the file's precision.</summary>
    private byte[] LayerTile(XcfWriterLayer l, int channels, int bytesPerSample, int tx, int ty, int tw, int th)
    {
        var tile = new byte[tw * th * channels * bytesPerSample];
        for (var y = 0; y < th; y++)
            for (var x = 0; x < tw; x++)
            {
                var source = ((ty * Tile + y) * l.Width + tx * Tile + x) * 4;
                var at = (y * tw + x) * channels * bytesPerSample;
                for (var c = 0; c < channels; c++)
                {
                    var alpha = c == channels - 1 && l.Type is 1 or 3 or 5;
                    var value = l.Rgba![source + (l.Type <= 1 ? c : alpha ? 3 : 0)];
                    if (l.Type >= 4) tile[at + c] = value;
                    else Encode(value, alpha, tile.AsSpan(at + c * bytesPerSample, bytesPerSample));
                }
            }
        return tile;
    }

    private byte[] MaskTile(XcfWriterLayer l, int tx, int ty, int tw, int th)
    {
        var tile = new byte[tw * th * BytesPerSample];
        for (var y = 0; y < th; y++)
            for (var x = 0; x < tw; x++)
                Encode(l.Mask![(ty * Tile + y) * l.Width + tx * Tile + x], true, tile.AsSpan((y * tw + x) * BytesPerSample, BytesPerSample));
        return tile;
    }

    /// <summary>An 8-bit gamma value as the file's precision stores it; coverage (alpha, masks) is scaled, color goes through the curve when the file is linear.</summary>
    private void Encode(byte value, bool coverage, Span<byte> into)
    {
        var unit = value / 255.0;
        if (LinearSamples && !coverage) unit = unit <= 0.04045 ? unit / 12.92 : Math.Pow((unit + 0.055) / 1.055, 2.4);
        if (!FloatSamples)
        {
            var scaled = (ulong)Math.Round(unit * (Math.Pow(2, 8 * into.Length) - 1));
            for (var i = 0; i < into.Length; i++) into[i] = (byte)(scaled >> (8 * (into.Length - 1 - i)));
            return;
        }
        var bits = into.Length switch
        {
            2 => BitConverter.HalfToUInt16Bits((Half)unit),
            4 => (ulong)BitConverter.SingleToUInt32Bits((float)unit),
            _ => BitConverter.DoubleToUInt64Bits(unit)
        };
        for (var i = 0; i < into.Length; i++) into[i] = (byte)(bits >> (8 * (into.Length - 1 - i)));
    }

    private long WriteHierarchy(Buffer w, int width, int height, int bytesPerPixel, Func<int, int, int, int, byte[]> tile, bool poison)
    {
        var at = w.Length;
        w.U32((uint)width);
        w.U32((uint)height);
        w.U32((uint)bytesPerPixel);
        var levelAt = new long[1];
        w.Ptr(Wide, () => levelAt[0]);
        w.Ptr(Wide, () => 0);
        levelAt[0] = w.Length;
        w.U32((uint)width);
        w.U32((uint)height);
        int across = (width + Tile - 1) / Tile, down = (height + Tile - 1) / Tile;
        var tiles = new List<byte[]>();
        for (var ty = 0; ty < down; ty++)
            for (var tx = 0; tx < across; tx++)
                tiles.Add(EncodeTile(tile(tx, ty, Math.Min(Tile, width - tx * Tile), Math.Min(Tile, height - ty * Tile)), bytesPerPixel));
        var tileAt = tiles.Select(_ => new long[1]).ToList();
        var end = new long[1];
        foreach (var box in tileAt) w.Ptr(Wide, () => poison ? end[0] + 4096 : box[0]);
        w.Ptr(Wide, () => 0);
        for (var i = 0; i < tiles.Count; i++) { tileAt[i][0] = w.Length; w.Bytes(tiles[i]); }
        end[0] = w.Length;
        return at;
    }

    private byte[] EncodeTile(byte[] data, int bytesPerPixel)
    {
        if (Compression == 0) return data;
        if (Compression == 2)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
            return output.ToArray();
        }
        var pixels = data.Length / bytesPerPixel;
        var result = new List<byte>();
        var plane = new byte[pixels];
        for (var p = 0; p < bytesPerPixel; p++)
        {
            for (var i = 0; i < pixels; i++) plane[i] = data[i * bytesPerPixel + p];
            Rle(plane, result);
        }
        return result.ToArray();
    }

    /// <summary>GIMP's run-length scheme for one byte plane: short and long runs, short and long literals.</summary>
    private static void Rle(byte[] plane, List<byte> output)
    {
        var i = 0;
        var n = plane.Length;
        while (i < n)
        {
            var runEnd = i + 1;
            while (runEnd < n && plane[runEnd] == plane[i] && runEnd - i < 0xFFFF) runEnd++;
            var run = runEnd - i;
            if (run >= 3)
            {
                if (run <= 127) { output.Add((byte)(run - 1)); output.Add(plane[i]); }
                else { output.Add(127); output.Add((byte)(run >> 8)); output.Add((byte)run); output.Add(plane[i]); }
                i = runEnd;
                continue;
            }
            var j = i + 1;
            while (j < n && j - i < 0xFFFF)
            {
                if (j + 2 < n && plane[j] == plane[j + 1] && plane[j] == plane[j + 2]) break;
                j++;
            }
            var literal = j - i;
            if (literal <= 127) output.Add((byte)(256 - literal));
            else { output.Add(128); output.Add((byte)(literal >> 8)); output.Add((byte)literal); }
            for (var k = i; k < j; k++) output.Add(plane[k]);
            i = j;
        }
    }

    /// <summary>Big-endian bytes with pointer slots filled in once every offset is known.</summary>
    public sealed class Buffer
    {
        private readonly List<byte> bytes = [];
        private readonly List<(int At, bool Wide, Func<long> Get)> patches = [];

        public int Length => bytes.Count;
        public void U8(byte value) => bytes.Add(value);
        public void U32(uint value) { U8((byte)(value >> 24)); U8((byte)(value >> 16)); U8((byte)(value >> 8)); U8((byte)value); }
        public void I32(int value) => U32((uint)value);
        public void F32(float value) => U32(BitConverter.SingleToUInt32Bits(value));
        public void Bytes(ReadOnlySpan<byte> data) { foreach (var b in data) bytes.Add(b); }
        public void Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));
        /// <summary>An XCF string: the length counts the NUL, then UTF-8 and the NUL.</summary>
        public void Str(string text) { var utf8 = Encoding.UTF8.GetBytes(text); U32((uint)utf8.Length + 1); Bytes(utf8); U8(0); }
        public void Ptr(bool wide, Func<long> get) { patches.Add((bytes.Count, wide, get)); for (var i = 0; i < (wide ? 8 : 4); i++) U8(0); }
        public void Property(uint id, Action<Buffer> payload)
        {
            var inner = new Buffer();
            payload(inner);
            U32(id);
            U32((uint)inner.Length);
            Bytes(inner.Finish());
        }

        public byte[] Finish()
        {
            var result = bytes.ToArray();
            foreach (var (at, wide, get) in patches)
            {
                var value = (ulong)get();
                if (wide) for (var i = 0; i < 8; i++) result[at + i] = (byte)(value >> (8 * (7 - i)));
                else for (var i = 0; i < 4; i++) result[at + i] = (byte)(value >> (8 * (3 - i)));
            }
            return result;
        }
    }
}

public sealed class XcfWriterLayer
{
    public string Name = "Layer";
    public int Width, Height;
    /// <summary>0 RGB, 1 RGBA, 2 gray, 3 gray with alpha, 4 indexed, 5 indexed with alpha.</summary>
    public int Type = 1;
    /// <summary>Straight RGBA, four bytes a pixel; gray and indexed layers take red, alpha layers take alpha.</summary>
    public byte[]? Rgba;
    public int X, Y;
    public bool WriteOffsets;
    public int? Opacity255;
    public float? FloatOpacity;
    public int? Mode;
    public bool Visible = true;
    public bool IsGroup;
    public bool? Expanded;
    public int[]? ItemPath;
    /// <summary>One coverage byte per pixel.</summary>
    public byte[]? Mask;
    public bool? ApplyMask;
    public int? BlendSpace, CompositeSpace, CompositeMode;
    public string? Text;
    public int Effects;
    public bool VectorLayer, LinkLayer;
    public bool PoisonTilePointer;

    /// <summary>A layer of one color.</summary>
    public XcfWriterLayer Filled(SKColor color)
    {
        Rgba = new byte[Width * Height * 4];
        for (var i = 0; i < Rgba.Length; i += 4) { Rgba[i] = color.Red; Rgba[i + 1] = color.Green; Rgba[i + 2] = color.Blue; Rgba[i + 3] = color.Alpha; }
        return this;
    }

    /// <summary>A deterministic spread of opaque colors, so every tile differs from its neighbours.</summary>
    public XcfWriterLayer Patterned(int seed = 1)
    {
        Rgba = new byte[Width * Height * 4];
        for (var i = 0; i < Rgba.Length; i += 4)
        {
            Rgba[i] = (byte)((i * seed + 40) & 0xFF);
            Rgba[i + 1] = (byte)((i * seed * 5 + 3) & 0xFF);
            Rgba[i + 2] = (byte)((i * 11 + seed) & 0xFF);
            Rgba[i + 3] = 255;
        }
        return this;
    }
}
