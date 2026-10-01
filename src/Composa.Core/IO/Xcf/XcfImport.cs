// Ported from Lolly (github.com/lolly-tools/lolly, engine/src/raster-layers.ts at 12b26ff, the XCF mode table), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Xcf;

/// <summary>
/// A GIMP file rebuilt as this editor's layers, as far as they can carry it, with a report of everything that had to
/// be converted on the way. Nothing is applied to a document until the caller decides to, so the report can be shown
/// first. GIMP's format is an interchange format here: files are opened, never written.
/// </summary>
public sealed class XcfImport
{
    public int Width { get; }
    public int Height { get; }
    public double Resolution { get; }
    /// <summary>Root layers, bottom to top, with folders holding their children. Meant to be placed once.</summary>
    public List<Layer> Layers { get; }
    public IReadOnlyList<Guide> Guides { get; }
    public IReadOnlyList<ImportConversion> Conversions { get; }

    private XcfImport(int width, int height, double resolution, List<Layer> layers, List<Guide> guides, List<ImportConversion> conversions)
    {
        Width = width;
        Height = height;
        Resolution = resolution;
        Layers = layers;
        Guides = guides;
        Conversions = conversions;
    }

    /// <summary>
    /// GIMP files are known by their <c>gimp xcf </c> signature, whatever their extension, and a compressed one
    /// (<c>.xcf.gz</c>, or <c>.xcf.bz2</c> and <c>.xcf.xz</c>, which are refused with a message) by its name.
    /// </summary>
    public static bool IsXcf(string path) => XcfReader.Matches(path) || XcfReader.MatchesCompressed(path);
    public static bool IsXcf(ReadOnlySpan<byte> data) => XcfReader.Matches(data) || XcfReader.IsGzip(data);

    /// <summary>Reads a file that is to become a document of its own, so the whole document budget is its to use.</summary>
    public static XcfImport Load(string path) => Load(path, DocumentLimits.DocumentPixelBudget);
    public static XcfImport Load(byte[] data) => Load(data, DocumentLimits.DocumentPixelBudget);

    /// <param name="pixelBudget">How much raster the file may add: the document budget, less what the target document already holds.</param>
    public static XcfImport Load(string path, long pixelBudget)
    {
        var info = new FileInfo(path);
        if (info.Length > int.MaxValue) throw XcfException.TooLarge();
        foreach (var (suffix, name) in XcfReader.UnreadableCompression)
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new XcfException($"The GIMP file is compressed with {name}, which Composa can't read. Save it from GIMP as .xcf or .xcf.gz.");
        return Load(File.ReadAllBytes(path), pixelBudget, info.Name);
    }

    public static XcfImport Load(byte[] data, long pixelBudget) => Load(data, pixelBudget, "The image");

    private static XcfImport Load(byte[] data, long pixelBudget, string fileName)
    {
        if (XcfReader.IsGzip(data)) data = XcfReader.Unpack(data);
        var file = XcfReader.Read(data, pixelBudget);
        try { return Build(file, fileName, pixelBudget); }
        catch
        {
            foreach (var layer in file.Layers) { layer.Image?.Dispose(); layer.MaskImage?.Dispose(); }
            throw;
        }
    }

    /// <summary>Frees the layers' pixels when the import is not going ahead.</summary>
    public void Discard()
    {
        foreach (var layer in Document.Flatten(Layers))
        {
            layer.Pixels?.Dispose();
            layer.Mask?.Dispose();
            layer.Pixels = null;
            layer.Mask = null;
        }
        Layers.Clear();
    }

    /// <summary>A new document holding the layers and guides, the topmost root layer active.</summary>
    public Document ToDocument()
    {
        var document = new Document(Width, Height) { Resolution = Resolution };
        document.Layers.AddRange(Layers);
        document.Guides.AddRange(Guides);
        document.SetActive(Layers.LastOrDefault()?.Id);
        return document;
    }

    private static XcfImport Build(XcfFile file, string fileName, long pixelBudget)
    {
        var conversions = new List<ImportConversion>();
        void Note(string layer, string message) => conversions.Add(new ImportConversion(layer, message));
        var canvas = new SKSizeI(file.Width, file.Height);
        // What text laid out afresh may still take, after the pixels the reader already holds.
        var remaining = pixelBudget - file.Layers.Sum(l => (long)(l.Image?.Width ?? 0) * (l.Image?.Height ?? 0) + (long)(l.MaskImage?.Width ?? 0) * (l.MaskImage?.Height ?? 0));

        if (file.Version > XcfReader.KnownVersion) Note(fileName, $"The file was saved by a GIMP newer than this version of Composa knows (format {file.Version}), so anything added since may be missing.");
        if (file.Precision.Deep) Note(fileName, $"The file keeps {file.Precision.Name} pixels; Composa holds 8 bits per channel, so some precision was lost.");
        else if (file.Precision.Linear) Note(fileName, "The file keeps linear-light pixels; they were converted to sRGB.");
        if (file.BaseType == 2) Note(fileName, "The indexed palette became RGB colors; the palette itself isn't kept.");
        if (file.HasIccProfile) Note(fileName, "The file carries a color profile, which Composa doesn't apply yet; colors are taken as sRGB.");
        if (file.ChannelCount > 0) Note(fileName, file.ChannelCount == 1 ? "A saved channel (selection or alpha channel) was left out." : $"{file.ChannelCount} saved channels (selections or alpha channels) were left out.");
        if (file.PathCount > 0) Note(fileName, file.PathCount == 1 ? "A path was left out; Composa has no paths." : $"{file.PathCount} paths were left out; Composa has no paths.");

        // Groups by their item path: a member's path is its group's path with one more index. The file lists layers
        // top to bottom and so do the paths, so each group's children are gathered top first and reversed.
        var records = Enumerable.Reverse(file.Layers).ToList();   // top first, as the paths count
        var roots = new List<Layer>();
        var groups = new Dictionary<string, Layer>();
        var childrenOf = new Dictionary<Layer, List<Layer>>();
        var built = new List<(XcfLayer Record, Layer Layer)>();
        foreach (var record in records)
        {
            var name = record.Name.Length == 0 ? (record.IsGroup ? "Folder" : "Layer") : record.Name;
            var layer = record.IsGroup ? BuildGroup(record, name, canvas, Note) : BuildLayer(record, name, canvas, file.Resolution, ref remaining, Note);
            built.Add((record, layer));
            if (record.IsGroup && record.ItemPath != null) groups[string.Join('/', record.ItemPath)] = layer;
            var parent = record.ItemPath is { Length: > 1 } path && groups.TryGetValue(string.Join('/', path[..^1]), out var group) ? group : null;
            if (parent == null) roots.Add(layer);
            else
            {
                if (!childrenOf.TryGetValue(parent, out var siblings)) childrenOf[parent] = siblings = [];
                siblings.Add(layer);
            }
        }
        foreach (var (group, children) in childrenOf) { children.Reverse(); group.Children.AddRange(children); }
        roots.Reverse();

        var guides = file.Guides.Select(g => new Guide(Guid.NewGuid(), g.Vertical ? GuideAxis.Vertical : GuideAxis.Horizontal, g.Position)).Where(g => g.IsValid).Take(1000).ToList();
        return new XcfImport(file.Width, file.Height, file.Resolution, roots, guides, conversions);
    }

    private static Layer BuildGroup(XcfLayer record, string name, SKSizeI canvas, Action<string, string> note)
    {
        var layer = Layer.Group(name);
        layer.Collapsed = record.Collapsed;
        layer.Visible = record.Visible;
        layer.Opacity = record.Opacity;
        var mode = XcfMode.Find(record.Mode);
        if (mode is { Blend: BlendMode.Normal, Supported: true } || record.Mode == XcfMode.PassThrough) layer.Blend = BlendMode.Normal;
        else note(name, $"Folder blend mode \"{mode?.Name ?? record.Mode.ToString()}\" isn't supported. The folder will be pass-through.");
        ApplyMask(record, layer, canvas, note);
        return layer;
    }

    private static Layer BuildLayer(XcfLayer record, string name, SKSizeI canvas, double resolution, ref long remaining, Action<string, string> note)
    {
        if (record.Cropped) note(name, PsdImport.CroppedNote);
        if (record.DamagedTiles > 0) note(name, record.DamagedTiles == 1 ? "One tile of the layer couldn't be read and is transparent." : $"{record.DamagedTiles} tiles of the layer couldn't be read and are transparent.");
        if (record.TextParasite != null)
        {
            // Text in one style is laid out afresh and can be retyped; text with markup keeps GIMP's pixels.
            if (XcfText.Parse(record.TextParasite, resolution, out var why) is { } source && XcfText.Place(source, record, name, ref remaining) is { } text)
            {
                record.Image?.Dispose();
                record.MaskImage?.Dispose();
                foreach (var message in source.Notes) note(name, message);
                text.Visible = record.Visible;
                text.Opacity = record.Opacity;
                text.Blend = XcfMode.Find(record.Mode) is { Supported: true } textMode ? textMode.Blend : BlendMode.Normal;
                return text;
            }
            note(name, why);
        }
        if (record.EffectCount > 0) note(name, record.EffectCount == 1 ? "The layer's effect (a GIMP filter) can't be applied here and was dropped." : $"The layer's {record.EffectCount} effects (GIMP filters) can't be applied here and were dropped.");
        if (record.IsVectorLayer) note(name, "The vector layer was imported as pixels; its shapes can't be edited here.");
        if (record.IsLinkLayer) note(name, "The link layer was imported as pixels; it no longer follows its file.");

        // No pixel area (an empty or wholly off-canvas layer): an empty layer over the canvas.
        var layer = record.Image is { } image ? Layer.Raster(name, image, record.Left, record.Top) : Layer.Raster(name, Pixels.NewColor(canvas.Width, canvas.Height));
        layer.Visible = record.Visible;
        layer.Opacity = record.Opacity;
        var mode = XcfMode.Find(record.Mode);
        if (mode == null) note(name, $"Blend mode {record.Mode} isn't known and will be applied as Normal.");
        else if (!mode.Supported) note(name, $"Blend mode \"{mode.Name}\" isn't supported and will be applied as Normal.");
        else if (mode.Approximate) note(name, $"Blend mode \"{mode.Name}\" has no exact equivalent; \"{mode.Blend.DisplayName()}\" is the closest.");
        layer.Blend = mode is { Supported: true } ? mode.Blend : BlendMode.Normal;

        // GIMP composites in linear light unless told otherwise; Composa composites in sRGB. A Normal layer at full
        // opacity looks the same either way, anything else does not. The value is negative when GIMP chose it itself.
        var blendSpace = Math.Abs(record.BlendSpace);
        var compositeSpace = Math.Abs(record.CompositeSpace);
        var changesColor = layer.Blend != BlendMode.Normal || layer.Opacity < 1 || (record.HasMask && record.ApplyMask);
        if (changesColor && (blendSpace == 1 || compositeSpace == 1)) note(name, "GIMP blends this layer in linear light; here it's blended in sRGB, so the result may differ a little.");
        else if (changesColor && (blendSpace == 3 || compositeSpace == 3)) note(name, "GIMP blends this layer in LAB; here it's blended in sRGB, so the result may differ.");
        var compositeMode = Math.Abs(record.CompositeMode);
        if (compositeMode is 2 or 3 or 4) note(name, $"The \"{XcfMode.CompositeModeName(compositeMode)}\" compositing isn't supported; the layer composites as GIMP's Union does.");

        ApplyMask(record, layer, canvas, note);
        return layer;
    }

    /// <summary>
    /// A layer's mask shares its pixel grid here; a folder's mask covers the document, so the folder's mask is placed
    /// into one that size (GIMP keeps it over the folder's own rectangle).
    /// </summary>
    private static void ApplyMask(XcfLayer record, Layer layer, SKSizeI canvas, Action<string, string> note)
    {
        if (record.MaskImage is not { } plane) return;
        if (layer.Pixels != null && plane.Width == layer.Pixels.Width && plane.Height == layer.Pixels.Height)
        {
            layer.Mask = plane;
        }
        else
        {
            var mask = Pixels.NewMask(canvas.Width, canvas.Height, 255);
            using (plane)
            using (var surface = new SKCanvas(mask))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
                surface.DrawBitmap(plane, record.Left, record.Top, paint);
            Pixels.Invalidate(mask);
            layer.Mask = mask;
        }
        layer.MaskEnabled = record.ApplyMask;
    }
}

/// <summary>
/// GIMP's layer modes, both generations in one number space: 0 to 22 are the legacy modes, 23 to 27 the LCH ones
/// GIMP 2.10 added, 28 to 61 the current class. Each maps onto the blend mode here whose math agrees, or the closest
/// one with <see cref="Approximate"/> set; the modes with no counterpart (painting modes and GIMP's own compositing
/// tricks) fall back to Normal and say so.
/// </summary>
internal sealed record XcfMode(string Name, BlendMode Blend, bool Approximate = false, bool Supported = true)
{
    public const int PassThrough = 61;

    private static readonly Dictionary<int, XcfMode> Table = new()
    {
        [0] = new("Normal (legacy)", BlendMode.Normal),
        [1] = new("Dissolve", BlendMode.Normal, Supported: false),
        [2] = new("Behind", BlendMode.Normal, Supported: false),
        [3] = new("Multiply (legacy)", BlendMode.Multiply),
        [4] = new("Screen (legacy)", BlendMode.Screen),
        [5] = new("Overlay (legacy)", BlendMode.SoftLight, true),      // GIMP's old Overlay was soft light's math
        [6] = new("Difference (legacy)", BlendMode.Difference),
        [7] = new("Addition (legacy)", BlendMode.LinearDodge),
        [8] = new("Subtract (legacy)", BlendMode.Subtract),
        [9] = new("Darken only (legacy)", BlendMode.Darken),
        [10] = new("Lighten only (legacy)", BlendMode.Lighten),
        [11] = new("HSV Hue (legacy)", BlendMode.Hue, true),
        [12] = new("HSV Saturation (legacy)", BlendMode.Saturation, true),
        [13] = new("HSL Color (legacy)", BlendMode.Color, true),
        [14] = new("HSV Value (legacy)", BlendMode.Luminosity, true),
        [15] = new("Divide (legacy)", BlendMode.Divide),
        [16] = new("Dodge (legacy)", BlendMode.ColorDodge),
        [17] = new("Burn (legacy)", BlendMode.ColorBurn),
        [18] = new("Hard light (legacy)", BlendMode.HardLight),
        [19] = new("Soft light (legacy)", BlendMode.SoftLight, true),
        [20] = new("Grain extract (legacy)", BlendMode.Subtract, true),
        [21] = new("Grain merge (legacy)", BlendMode.LinearDodge, true),
        [22] = new("Color erase (legacy)", BlendMode.Normal, Supported: false),
        [23] = new("Overlay", BlendMode.Overlay),
        [24] = new("LCH Hue", BlendMode.Hue, true),
        [25] = new("LCH Chroma", BlendMode.Saturation, true),
        [26] = new("LCH Color", BlendMode.Color, true),
        [27] = new("LCH Lightness", BlendMode.Luminosity, true),
        [28] = new("Normal", BlendMode.Normal),
        [29] = new("Behind", BlendMode.Normal, Supported: false),
        [30] = new("Multiply", BlendMode.Multiply),
        [31] = new("Screen", BlendMode.Screen),
        [32] = new("Difference", BlendMode.Difference),
        [33] = new("Addition", BlendMode.LinearDodge),
        [34] = new("Subtract", BlendMode.Subtract),
        [35] = new("Darken only", BlendMode.Darken),
        [36] = new("Lighten only", BlendMode.Lighten),
        [37] = new("HSV Hue", BlendMode.Hue, true),
        [38] = new("HSV Saturation", BlendMode.Saturation, true),
        [39] = new("HSL Color", BlendMode.Color, true),
        [40] = new("HSV Value", BlendMode.Luminosity, true),
        [41] = new("Divide", BlendMode.Divide),
        [42] = new("Dodge", BlendMode.ColorDodge),
        [43] = new("Burn", BlendMode.ColorBurn),
        [44] = new("Hard light", BlendMode.HardLight),
        [45] = new("Soft light", BlendMode.SoftLight, true),
        [46] = new("Grain extract", BlendMode.Subtract, true),
        [47] = new("Grain merge", BlendMode.LinearDodge, true),
        [48] = new("Vivid light", BlendMode.VividLight),
        [49] = new("Pin light", BlendMode.PinLight),
        [50] = new("Linear light", BlendMode.LinearLight),
        [51] = new("Hard mix", BlendMode.HardMix),
        [52] = new("Exclusion", BlendMode.Exclusion),
        [53] = new("Linear burn", BlendMode.LinearBurn),
        [54] = new("Luma darken only", BlendMode.Darken, true),
        [55] = new("Luma lighten only", BlendMode.Lighten, true),
        [56] = new("Luminance", BlendMode.Luminosity, true),
        [57] = new("Color erase", BlendMode.Normal, Supported: false),
        [58] = new("Erase", BlendMode.Normal, Supported: false),
        [59] = new("Merge", BlendMode.Normal, Supported: false),
        [60] = new("Split", BlendMode.Normal, Supported: false),
        [61] = new("Pass through", BlendMode.Normal)
    };

    public static XcfMode? Find(int mode) => Table.GetValueOrDefault(mode);

    public static string CompositeModeName(int mode) => mode switch { 2 => "Clip to backdrop", 3 => "Clip to layer", 4 => "Intersection", _ => "Union" };
}
