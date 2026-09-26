using System.ComponentModel;
using Composa.Model;
using Composa.Painting;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Composa.App.Mcp;

/// <summary>The painting and shape tools: a brush stroke through given points, and the live shapes the Shape tool draws.</summary>
public sealed partial class ComposaTools
{
    [McpServerTool(Name = "paint_stroke")]
    [Description("Paints one brush stroke through the given canvas points on the active layer (or the layer named), as a drag with the Brush tool would. A single point is a dab. Live text and shape layers cannot be painted on.")]
    public Task<string> PaintStroke(
        [Description("The stroke's points as [[x, y], [x, y], ...] in canvas pixels; a curve needs a point every few pixels")] double[][] points,
        [Description("A color as #rrggbb or #aarrggbb; ignored by the erase and smearing modes")] string color = "#000000",
        [Description("Brush diameter in pixels")] double size = 40,
        [Description("Edge hardness from 0 (soft) to 1 (hard)")] double hardness = 0.8,
        [Description("The most the stroke covers, 0 to 1; the strength for blur, smudge, dodge and burn")] double opacity = 1,
        [Description("paint, erase, blur, smudge, dodge or burn")] string mode = "paint",
        [Description("The layer to paint on; the active one when left out")] string? layer = null,
        int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        var brushMode = mode.Trim().ToLowerInvariant() switch
        {
            "paint" => BrushMode.Paint, "erase" => BrushMode.Erase, "blur" => BrushMode.Blur,
            "smudge" => BrushMode.Smudge, "dodge" => BrushMode.Dodge, "burn" => BrushMode.Burn,
            _ => throw new McpException("mode is paint, erase, blur, smudge, dodge or burn.")
        };
        if (points.Length == 0 || points.Any(p => p.Length != 2)) throw new McpException("points is a list of [x, y] pairs with at least one pair.");
        if (size is < 1 or > 5000 || double.IsNaN(size)) throw new McpException("size is 1 to 5000 pixels.");
        if (layer != null) s.SelectLayer(Find(s, layer).Id);
        var target = s.ActiveLayer ?? throw new McpException("No layer is active.");
        var brush = new BrushSettings { Size = size, Hardness = Math.Clamp(hardness, 0, 1), Opacity = Math.Clamp(opacity, 0, 1) };
        var path = points.Select(p => new SKPoint((float)p[0], (float)p[1])).ToList();
        if (s.PaintStroke(path, brush, ParseColor(color), brushMode) is { } problem) throw new McpException(problem);
        return $"Painted a {mode} stroke of {path.Count} point{(path.Count == 1 ? "" : "s")} on \"{target.Name}\".";
    });

    [McpServerTool(Name = "add_shape")]
    [Description("Adds a live rectangle, rounded rectangle or ellipse as a new layer above the active one. Live shapes stay editable: transform_layer redraws them at the new size.")]
    public Task<string> AddShape(
        [Description("rectangle, rounded or ellipse")] string kind,
        [Description("Left edge in canvas pixels")] double x,
        [Description("Top edge in canvas pixels")] double y,
        double width,
        double height,
        [Description("Fill color as #rrggbb or #aarrggbb")] string color = "#000000",
        [Description("Corner radius in pixels, for a rounded rectangle")] double cornerRadius = 24,
        int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        var shapeKind = kind.Trim().ToLowerInvariant() switch
        {
            "rectangle" => ShapeKind.Rectangle, "rounded" or "rounded rectangle" or "roundedrectangle" => ShapeKind.RoundedRectangle,
            "ellipse" or "circle" => ShapeKind.Ellipse,
            _ => throw new McpException("kind is rectangle, rounded or ellipse; a line is added with add_line.")
        };
        if (width < 1 || height < 1) throw new McpException("The width and height must be at least 1 px.");
        var layer = s.AddShape(new ShapeStyle(shapeKind, (uint)ParseColor(color), Math.Max(0, cornerRadius)), SKRect.Create((float)x, (float)y, (float)width, (float)height))
                    ?? throw new McpException($"That shape is too large: a shape covers at most {DocumentLimits.MaxSurfaceMegapixels} megapixels.");
        return $"Added {ShapeStyle.DisplayName(shapeKind).ToLowerInvariant()} \"{layer.Name}\" at {x:0},{y:0} size {width:0}×{height:0}, now active.";
    });

    [McpServerTool(Name = "add_line")]
    [Description("Adds a live straight line with round ends as a new layer above the active one.")]
    public Task<string> AddLine(
        double x1, double y1, double x2, double y2,
        [Description("Line color as #rrggbb or #aarrggbb")] string color = "#000000",
        [Description("Thickness in pixels")] double width = 4,
        int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        if (width < 1) throw new McpException("The width must be at least 1 px.");
        var layer = s.AddLine(new SKPoint((float)x1, (float)y1), new SKPoint((float)x2, (float)y2), ParseColor(color), width)
                    ?? throw new McpException("The two ends are the same point, or too far apart for one layer.");
        return $"Added line \"{layer.Name}\" from {x1:0},{y1:0} to {x2:0},{y2:0}, now active.";
    });
}
