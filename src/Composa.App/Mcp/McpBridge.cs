using System.IO.Pipes;

namespace Composa.App.Mcp;

/// <summary>
/// <c>composa --mcp</c>: the process an MCP client launches. Every client speaks stdio to a process it starts, but the
/// editor is already running with a window, so this process only carries bytes between its own stdin and stdout and
/// the running application's pipe. It shows no window and exits when either side closes.
/// </summary>
public static class McpBridge
{
    public static async Task<int> RunAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(2000); }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("Composa is not running, or Help > Allow AI Control is switched off.");
            return 1;
        }
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        // Whichever direction ends first ends the bridge: the client closing stdin, or the application closing the pipe.
        await Task.WhenAny(input.CopyToAsync(pipe), pipe.CopyToAsync(output));
        return 0;
    }
}
