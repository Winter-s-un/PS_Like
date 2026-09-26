using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Composa.App.Mcp;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>
/// An agent reaches the editor through <c>composa --mcp</c>, a stdio bridge to the pipe the window listens on. These
/// tests go the whole way: the bridge is the built application, launched the way an MCP client launches it, and every
/// tool call lands on the window's dispatcher, which a headless test drives by hand.
/// </summary>
public class McpTests
{
    private static readonly string App = Path.Combine(AppContext.BaseDirectory, "composa.dll");

    private static string PipeName() => OperatingSystem.IsWindows()
        ? $"composa-test-{Guid.NewGuid():N}"
        : Path.Combine(Path.GetTempPath(), $"composa-test-{Guid.NewGuid():N}.sock");

    private static StdioClientTransport Bridge(string pipe) => new(new StdioClientTransportOptions
    {
        Name = "composa", Command = "dotnet", Arguments = [App, "--mcp"],
        EnvironmentVariables = new Dictionary<string, string?> { [McpPipe.Variable] = pipe }
    });

    /// <summary>The server's work is posted to the UI thread, which is this thread; it runs only while the test pumps.</summary>
    private static Task<T> Pumped<T>(ValueTask<T> task) => Pumped(task.AsTask());

    private static async Task<T> Pumped<T>(Task<T> task)
    {
        for (var i = 0; i < 3000 && !task.IsCompleted; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
        return await task;
    }

    private static async Task Pumped(Func<bool> until)
    {
        for (var i = 0; i < 1000 && !until(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [AvaloniaFact]
    public async Task Tools_change_the_open_document_through_the_bridge_and_every_change_is_undoable()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var host = new McpHost(window, PipeName());
        Assert.True(await host.StartAsync());

        await using var client = await Pumped(McpClient.CreateAsync(Bridge(host.PipeName)));
        await Pumped(() => host.Connections == 1);
        var tools = await client.ListToolsAsync();
        Assert.Equal(["add_text", "delete_layer", "describe_document", "duplicate_layer", "fill_layer", "list_documents", "new_document", "new_layer", "place_image", "render", "reorder_layer", "select_layer", "set_layer", "transform_layer", "undo"], tools.Select(t => t.Name).Order());

        var tooBig = await Pumped(client.CallToolAsync("new_document", new Dictionary<string, object?> { ["width"] = 40000, ["height"] = 10 }));
        Assert.Equal(true, tooBig.IsError);
        Assert.Contains("at most", Text(tooBig));
        var created = await Pumped(client.CallToolAsync("new_document", new Dictionary<string, object?> { ["width"] = 400, ["height"] = 300, ["background"] = "#FFFFFF" }));
        Assert.Equal("Created document 1: \"Untitled\" 400×300 px, now active.", Text(created));
        var session = window.Session!;
        Assert.Equal("Background", session.ActiveLayer!.Name);

        var listed = await Pumped(client.CallToolAsync("list_documents"));
        Assert.Contains("1: \"Untitled\" 400×300 px, 1 layers", Text(listed));

        var filled = await Pumped(client.CallToolAsync("fill_layer", new Dictionary<string, object?> { ["color"] = "#FF0000" }));
        Assert.NotEqual(true, filled.IsError);
        Assert.Equal(SKColors.Red, session.Document.Layers[0].Pixels!.GetPixel(5, 5));
        Assert.Equal("Fill", session.History.UndoName);

        var added = await Pumped(client.CallToolAsync("add_text", new Dictionary<string, object?> { ["text"] = "Hello", ["x"] = 20, ["y"] = 30, ["size"] = 48, ["color"] = "#0000FF" }));
        Assert.NotEqual(true, added.IsError);
        Assert.Equal(2, session.Document.Layers.Count);
        Assert.Equal("Hello", session.ActiveLayer!.Text!.Text);
        Assert.Equal(0xFF0000FFu, session.ActiveLayer.Text.Color);

        var described = Text(await Pumped(client.CallToolAsync("describe_document")));
        Assert.Contains("400×300 px", described);
        Assert.Matches("\\* \"Hello\" \\[[0-9a-f]{8}\\]: text \"Hello\"", described);
        Assert.Matches("- \"Background\" \\[[0-9a-f]{8}\\]: pixels", described);

        var rendered = await Pumped(client.CallToolAsync("render", new Dictionary<string, object?> { ["maxSide"] = 200 }));
        var image = Assert.Single(rendered.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        using var png = SKBitmap.Decode(image.DecodedData.ToArray());
        Assert.Equal(200, png.Width);
        Assert.Equal(150, png.Height);
        Assert.Equal(SKColors.Red, png.GetPixel(190, 140));

        var picture = Path.Combine(Path.GetTempPath(), $"composa-place-{Guid.NewGuid():N}.png");
        using (var wide = new SKBitmap(800, 200)) { wide.Erase(SKColors.Lime); ImageFiles.Save(wide, picture, ExportFormat.Png); }
        try
        {
            var placed = await Pumped(client.CallToolAsync("place_image", new Dictionary<string, object?> { ["path"] = picture }));
            Assert.Equal($"Placed \"{Path.GetFileNameWithoutExtension(picture)}\" (800×200 px) as a layer at 0,100 size 400×100, now active.", Text(placed));
            Assert.Equal(3, session.Document.Layers.Count);                                          // Scaled down to fit and centered.
            var half = await Pumped(client.CallToolAsync("place_image", new Dictionary<string, object?> { ["path"] = picture, ["scale"] = 0.5, ["x"] = 100, ["y"] = 100 }));
            Assert.Contains("as a layer at 0,75 size 200×50", Text(half));                             // Half the fitted size, centered on 100,100.
            Assert.Equal("Undid Add Image.", Text(await Pumped(client.CallToolAsync("undo"))));
            var missing = await Pumped(client.CallToolAsync("place_image", new Dictionary<string, object?> { ["path"] = picture + ".missing" }));
            Assert.Equal(true, missing.IsError);
            Assert.Equal("Undid Add Image.", Text(await Pumped(client.CallToolAsync("undo"))));
        }
        finally { File.Delete(picture); }

        var refused = await Pumped(client.CallToolAsync("fill_layer", new Dictionary<string, object?> { ["color"] = "nonsense" }));
        Assert.Equal(true, refused.IsError);
        Assert.Contains("not a color", Text(refused));

        Assert.Equal("Undid Text.", Text(await Pumped(client.CallToolAsync("undo"))));
        Assert.Single(session.Document.Layers);

        // The layer tools, on a fresh text layer.
        await Pumped(client.CallToolAsync("add_text", new Dictionary<string, object?> { ["text"] = "Hello", ["x"] = 20, ["y"] = 30 }));
        var hello = session.ActiveLayer!;
        var set = await Pumped(client.CallToolAsync("set_layer", new Dictionary<string, object?> { ["layer"] = "hello", ["name"] = "Greeting", ["visible"] = false, ["opacity"] = 0.5, ["blend"] = "soft light" }));
        Assert.Equal("\"Greeting\": named \"Greeting\", hidden, opacity 50%, blend Soft Light.", Text(set));
        Assert.False(hello.Visible);
        Assert.Equal(0.5, hello.Opacity);
        Assert.Equal(BlendMode.SoftLight, hello.Blend);
        Assert.Equal("Blend Mode", session.History.UndoName);
        await Pumped(client.CallToolAsync("set_layer", new Dictionary<string, object?> { ["layer"] = "Greeting", ["visible"] = true }));
        var moved = await Pumped(client.CallToolAsync("transform_layer", new Dictionary<string, object?> { ["layer"] = "Greeting", ["x"] = 50, ["y"] = 60 }));
        Assert.StartsWith("\"Greeting\" is now at 50,60 size", Text(moved));
        Assert.Equal(50, hello.Transform.X);
        Assert.Equal("Background", session.Document.Layers[0].Name);
        Assert.Equal("Duplicated \"Greeting\" as \"Greeting copy\", now active.", Text(await Pumped(client.CallToolAsync("duplicate_layer", new Dictionary<string, object?> { ["layer"] = "Greeting" }))));
        Assert.Equal(3, session.Document.Layers.Count);
        await Pumped(client.CallToolAsync("reorder_layer", new Dictionary<string, object?> { ["layer"] = "Greeting copy", ["direction"] = "bottom" }));
        Assert.Equal("Greeting copy", session.Document.Layers[0].Name);
        Assert.Contains("already at the bottom", Text(await Pumped(client.CallToolAsync("reorder_layer", new Dictionary<string, object?> { ["layer"] = "Greeting copy", ["direction"] = "down" }))));
        var byId = await Pumped(client.CallToolAsync("select_layer", new Dictionary<string, object?> { ["layer"] = hello.Id.ToString("N")[..8] }));
        Assert.Equal("\"Greeting\" is the active layer.", Text(byId));
        Assert.Equal("Deleted \"Greeting copy\".", Text(await Pumped(client.CallToolAsync("delete_layer", new Dictionary<string, object?> { ["layer"] = "Greeting copy" }))));
        Assert.Equal(2, session.Document.Layers.Count);
        var unknown = await Pumped(client.CallToolAsync("set_layer", new Dictionary<string, object?> { ["layer"] = "Nope", ["visible"] = true }));
        Assert.Equal(true, unknown.IsError);
        Assert.Contains("no layer", Text(unknown));

        await client.DisposeAsync();
        await Pumped(() => host.Connections == 0);
    }

    [AvaloniaFact]
    public async Task The_bridge_says_when_nothing_is_listening()
    {
        var start = new ProcessStartInfo("dotnet", [App, "--mcp"]) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        start.Environment[McpPipe.Variable] = PipeName();
        using var bridge = Process.Start(start)!;
        var error = await bridge.StandardError.ReadToEndAsync();
        await bridge.WaitForExitAsync();
        Assert.Equal(1, bridge.ExitCode);
        Assert.Contains("not running", error);
    }

    [AvaloniaFact]
    public async Task A_second_window_leaves_the_pipe_to_the_first()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var first = new McpHost(window, PipeName());
        Assert.True(await first.StartAsync());
        using var second = new McpHost(window, first.PipeName);
        Assert.False(await second.StartAsync());
        await Pumped(() => first.Connections == 0);                       // The probe's connection has gone again.
    }
}
