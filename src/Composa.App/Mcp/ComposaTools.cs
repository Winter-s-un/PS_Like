using System.ComponentModel;
using System.Reflection;
using System.Text;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Composa.App.Mcp;

/// <summary>
/// The tools an agent gets. Each one runs on the UI thread and goes through <see cref="EditorSession"/>, so what an
/// agent does is one undoable step, refreshes the window through the session's events and can be taken back with
/// Ctrl+Z like anything else. Documents are addressed by their tab number, the way <c>list_documents</c> reports them.
/// </summary>
public sealed class ComposaTools(MainWindow window)
{
    public McpServerPrimitiveCollection<McpServerTool> Collection()
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(ComposaTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null) tools.Add(McpServerTool.Create(method, this));
        return tools;
    }

    [McpServerTool(Name = "list_documents", ReadOnly = true, Idempotent = true)]
    [Description("The documents open in Composa, numbered as their tabs are. Other tools take that number as `document`; leave it out for the active one.")]
    public Task<string> ListDocuments() => OnUi(() =>
    {
        var sessions = window.Sessions;
        if (sessions.Count == 0) return "No document is open.";
        var text = new StringBuilder();
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            text.Append(s == window.Session ? "* " : "  ").Append(i + 1).Append(": \"").Append(s.Title).Append("\" ")
                .Append(s.Document.Width).Append('×').Append(s.Document.Height).Append(" px, ")
                .Append(s.Document.AllLayers().Count()).Append(" layers").Append(s.IsModified ? ", unsaved changes" : "").AppendLine();
        }
        return text.ToString().TrimEnd();
    });

    [McpServerTool(Name = "describe_document", ReadOnly = true, Idempotent = true)]
    [Description("The canvas and the layer stack of a document, top layer first. The active layer is marked with *.")]
    public Task<string> DescribeDocument(int? document = null) => OnUi(() =>
    {
        var s = Session(document);
        var doc = s.Document;
        var text = new StringBuilder();
        text.Append('"').Append(s.Title).Append("\": ").Append(doc.Width).Append('×').Append(doc.Height).Append(" px at ")
            .Append(doc.Resolution.ToString("0.#")).Append(" ppi");
        if (doc.Selection != null) text.Append(", part of the canvas is selected");
        text.AppendLine().AppendLine("Layers, top first:");
        Describe(text, doc, doc.Layers, 0);
        return text.ToString().TrimEnd();
    });

    private static void Describe(StringBuilder text, Document doc, List<Layer> layers, int depth)
    {
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            text.Append(' ', depth * 2).Append(doc.ActiveLayerId == layer.Id ? "* " : "- ").Append('"').Append(layer.Name).Append("\": ").Append(Kind(layer));
            if (layer.Text != null) text.Append(" \"").Append(layer.Text.Text.Replace("\n", "\\n")).Append('"');
            if (layer.Pixels != null)
            {
                var b = layer.Bounds;
                text.Append(" at ").Append(b.Left.ToString("0")).Append(',').Append(b.Top.ToString("0")).Append(" size ")
                    .Append(b.Width.ToString("0")).Append('×').Append(b.Height.ToString("0"));
            }
            if (!layer.Visible) text.Append(", hidden");
            if (layer.Opacity < 1) text.Append(", opacity ").Append((layer.Opacity * 100).ToString("0")).Append('%');
            if (layer.Blend != BlendMode.Normal) text.Append(", blend ").Append(layer.Blend.DisplayName());
            if (layer.Mask != null) text.Append(", masked");
            if (layer.Clipped) text.Append(", clipped to the layer below");
            if (layer.Effects != null) text.Append(", with effects");
            text.AppendLine();
            if (layer.IsGroup) Describe(text, doc, layer.Children, depth + 1);
        }
    }

    private static string Kind(Layer layer) =>
        layer.IsGroup ? "group" : layer.IsAdjustment ? $"{layer.Adjustment?.GetType().Name ?? "adjustment"} adjustment" :
        layer.Text != null ? "text" : layer.Shape != null ? $"{ShapeStyle.DisplayName(layer.Shape.Kind).ToLowerInvariant()} shape" : "pixels";

    [McpServerTool(Name = "new_layer")]
    [Description("Adds an empty, transparent layer the size of the canvas above the active layer and makes it the active layer.")]
    public Task<string> NewLayer(
        [Description("The layer's name; leave it out for the next free \"Layer n\"")] string? name = null,
        int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        var layer = s.AddBlankLayer();
        if (!string.IsNullOrWhiteSpace(name)) s.Rename(layer, name.Trim());
        return $"Added layer \"{layer.Name}\", now active.";
    });

    [McpServerTool(Name = "fill_layer")]
    [Description("Fills the active layer with a color, within the selection when there is one. A text layer is recolored instead of filled.")]
    public Task<string> FillLayer(
        [Description("A color as #rrggbb or #aarrggbb")] string color,
        int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        if (s.ActiveLayer is not { } layer) throw new McpException("No layer is active.");
        if (!s.CanFill) throw new McpException($"\"{layer.Name}\" cannot be filled: it is a {Kind(layer)} layer.");
        s.Fill(ParseColor(color));
        return $"Filled \"{layer.Name}\" with {color}.";
    });

    [McpServerTool(Name = "add_text")]
    [Description("Adds a text layer above the active layer. x and y are the top-left corner of the text in canvas pixels.")]
    public Task<string> AddText(
        [Description("The text; a newline starts a new line")] string text,
        [Description("Left edge in canvas pixels")] double x,
        [Description("Top edge in canvas pixels")] double y,
        [Description("Font size in pixels")] double size = 72,
        [Description("A color as #rrggbb or #aarrggbb")] string color = "#000000",
        [Description("Font family; leave it out for the default")] string? font = null,
        bool bold = false,
        bool italic = false,
        int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        if (string.IsNullOrEmpty(text)) throw new McpException("The text is empty.");
        var style = s.TextDefaults with
        {
            Text = text, Size = size, Color = (uint)ParseColor(color), Bold = bold, Italic = italic,
            FontFamily = string.IsNullOrWhiteSpace(font) ? s.TextDefaults.FontFamily : font.Trim()
        };
        var layer = s.AddText(new SKPoint((float)x, (float)y), style);
        return $"Added text layer \"{layer.Name}\" at {x:0},{y:0}, now active.";
    });

    [McpServerTool(Name = "undo")]
    [Description("Takes back the last step in the document, whoever made it.")]
    public Task<string> Undo(int? document = null) => OnUi(() =>
    {
        var s = Editable(document);
        if (!s.CanUndo) throw new McpException("There is nothing to undo.");
        var name = s.History.UndoName;
        s.Undo();
        return $"Undid {name}.";
    });

    [McpServerTool(Name = "render", ReadOnly = true, Idempotent = true)]
    [Description("The document as it looks now, flattened to a PNG. Call it to see the result of your changes.")]
    public Task<CallToolResult> Render(
        [Description("The longest side of the image in pixels; the document is scaled down to fit, never up")] int maxSide = 1024,
        int? document = null) => OnUi(() =>
    {
        var s = Session(document);
        var composite = s.Composite();                                      // Owned by the session: never disposed here.
        var scale = Math.Min(1.0, (double)Math.Clamp(maxSide, 16, 4096) / Math.Max(composite.Width, composite.Height));
        using var scaled = scale < 1
            ? composite.Resize(new SKSizeI(Math.Max(1, (int)Math.Round(composite.Width * scale)), Math.Max(1, (int)Math.Round(composite.Height * scale))),
                               new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : null;
        var picture = scaled ?? composite;
        using var image = SKImage.FromBitmap(picture);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = $"{picture.Width}×{picture.Height} px view of the {composite.Width}×{composite.Height} px canvas." },
                ImageContentBlock.FromBytes(data.ToArray(), "image/png")
            ]
        };
    });

    // ---- Plumbing -----------------------------------------------------------------------------------------------

    private static async Task<T> OnUi<T>(Func<T> work) => await Dispatcher.UIThread.InvokeAsync(work);

    private EditorSession Session(int? document)
    {
        var sessions = window.Sessions;
        if (sessions.Count == 0) throw new McpException("No document is open in Composa.");
        if (document is { } number)
        {
            if (number < 1 || number > sessions.Count) throw new McpException($"There is no document {number}; list_documents shows {sessions.Count}.");
            return sessions[number - 1];
        }
        return window.Session ?? sessions[0];
    }

    /// <summary>A session ready for an edit: not mid-drag, and with any text being typed committed first, as a menu command would.</summary>
    private EditorSession Editable(int? document)
    {
        if (window.IsDragging) throw new McpException("The person is dragging on the canvas; try again in a moment.");
        var s = Session(document);
        if (s.IsEditingText) s.FinishText();
        if (s.IsInteracting) throw new McpException("An edit is still open in the window; try again in a moment.");
        return s;
    }

    private static SKColor ParseColor(string color) =>
        SKColor.TryParse(color, out var parsed) ? parsed : throw new McpException($"\"{color}\" is not a color; use #rrggbb or #aarrggbb.");
}
