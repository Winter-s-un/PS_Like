using System.Diagnostics;
using Avalonia.Platform.Storage;
using Tmds.DBus.Protocol;

namespace Composa.App;

/// <summary>A program and its arguments, as a platform helper would start it.</summary>
/// <param name="Verbatim">
/// The arguments are one command line passed exactly as written, for a program that parses its
/// command line its own way, as Explorer does; otherwise each is quoted as a separate argument.
/// </param>
/// <param name="Shell">Started through the desktop, as a double-click would, so Windows can ask to elevate an installer.</param>
public sealed record ShellCommand(string Program, IReadOnlyList<string> Arguments, bool Verbatim = false, bool Shell = false)
{
    /// <summary>Starts it without waiting, and says whether it started.</summary>
    public static bool Start(ShellCommand command)
    {
        try
        {
            var start = new ProcessStartInfo(command.Program) { UseShellExecute = command.Shell };
            if (command.Verbatim) start.Arguments = string.Join(" ", command.Arguments);
            else foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Shows a file selected in the desktop's file manager, the way a browser's Show in Folder does.
/// Each platform has its own way; where it fails, or the desktop has none, the file's folder is
/// opened instead, which is always something.
/// </summary>
public static class FileReveal
{
    /// <summary>
    /// The program that shows a file selected, or null on Linux, where the freedesktop FileManager1
    /// interface is asked over D-Bus instead (Nautilus, Dolphin, Nemo, Caja and Thunar all answer it).
    /// </summary>
    public static ShellCommand? CommandFor(AppPaths.Platform platform, string path) => platform switch
    {
        // Explorer wants /select and the quoted path as one argument with no space between them, which
        // ordinary quoting would get wrong.
        AppPaths.Platform.Windows => new ShellCommand("explorer.exe", [$"/select,\"{path}\""], Verbatim: true),
        AppPaths.Platform.MacOS => new ShellCommand("open", ["-R", path]),
        _ => null,
    };

    /// <summary>
    /// A file URI with every part of the path escaped, since a name may hold spaces, letters outside
    /// ASCII or a '#' that <see cref="Uri"/> would read as a fragment.
    /// </summary>
    public static string FileUri(string path) => "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <param name="run">Starts a program and says whether it started.</param>
    /// <param name="showItems">Asks the freedesktop file manager to show a file URI, and says whether one answered.</param>
    /// <param name="openFolder">Opens a folder, the fallback everywhere.</param>
    public static async Task<bool> Show(string path, AppPaths.Platform platform, Func<ShellCommand, bool> run,
        Func<string, Task<bool>> showItems, Func<string, Task<bool>> openFolder)
    {
        if (File.Exists(path))
        {
            var shown = CommandFor(platform, path) is { } command ? run(command) : await showItems(FileUri(path));
            if (shown) return true;
        }
        return Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) && await openFolder(folder);
    }

    /// <summary>Shows a file on this machine, falling back to opening its folder with Avalonia's launcher.</summary>
    public static Task<bool> Show(string path, ILauncher launcher) =>
        Show(Path.GetFullPath(path), AppPaths.CurrentPlatform, ShellCommand.Start, ShowItems,
            folder => launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)));

    /// <summary>
    /// Calls <c>org.freedesktop.FileManager1.ShowItems</c> on the session bus through the D-Bus library
    /// Avalonia already brings for its Linux backend. A connection of its own, so the call cannot
    /// disturb Avalonia's.
    /// </summary>
    private static async Task<bool> ShowItems(string uri)
    {
        if (DBusAddress.Session is not { Length: > 0 } address) return false;
        try
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader("org.freedesktop.FileManager1", "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1",
                    "ShowItems", "ass", MessageFlags.None);
                writer.WriteArray(new[] { uri });
                writer.WriteString(""); // No startup notification id.
                message = writer.CreateMessage();
            }
            // Starting a file manager that was not running can take a moment; one that never answers is given up on.
            await connection.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception)
        {
            // Whatever keeps a file manager from answering (no session bus, an address the library
            // does not accept, a bus that refuses the call), the folder is opened instead.
            return false;
        }
    }
}
