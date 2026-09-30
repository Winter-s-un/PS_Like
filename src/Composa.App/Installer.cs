namespace Composa.App;

/// <summary>
/// What can be done with a downloaded update once it is checked. Composa never installs anything
/// itself and never replaces its own files: Install hands the file to the program whose job that
/// is, and only when the person presses it.
/// </summary>
public static class Installer
{
    /// <summary>
    /// Whether the download has an installer to start: the Windows setup, or a .deb or .rpm, which
    /// the desktop's software installer opens. An AppImage, a tarball and the Windows zip have none;
    /// they are shown in their folder instead.
    /// </summary>
    public static bool HasOne(InstallKind kind) => kind is InstallKind.WindowsInstaller or InstallKind.Deb or InstallKind.Rpm;

    /// <summary>
    /// Whether Install has to quit Composa first. The Windows setup replaces files the running
    /// application holds open, so Install quits as File &gt; Quit does before starting it; a package
    /// manager replaces files under a running program without trouble.
    /// </summary>
    public static bool QuitsFirst(InstallKind kind) => kind == InstallKind.WindowsInstaller;

    /// <summary>
    /// Starts the installer for a downloaded file, or returns false for a kind that has none.
    /// </summary>
    /// <param name="run">Starts a program; the Windows setup goes through the shell, so it can ask to elevate.</param>
    /// <param name="open">Opens a file with the desktop's default application for it, which for a package is the software installer.</param>
    public static async Task<bool> Start(InstallKind kind, string path, Func<ShellCommand, bool> run, Func<string, Task<bool>> open) => kind switch
    {
        InstallKind.WindowsInstaller => run(new ShellCommand(path, [], Shell: true)),
        InstallKind.Deb or InstallKind.Rpm => await open(path),
        _ => false,
    };

    /// <summary>
    /// The command that installs a downloaded package from a terminal, offered beside Install because
    /// some software centres refuse a package that does not come from a repository. Installing a
    /// newer version of an installed package upgrades it. Null for anything but a .deb or .rpm.
    /// </summary>
    public static string? TerminalCommand(InstallKind kind, string path) => kind switch
    {
        InstallKind.Deb => "sudo apt install " + ShellQuote(path),
        InstallKind.Rpm => "sudo dnf install " + ShellQuote(path),
        _ => null,
    };

    /// <summary>Quotes a path for a POSIX shell when it needs it; a plain path is left readable.</summary>
    public static string ShellQuote(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+,:@%=~".Contains(c))
            ? text
            : "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Gets a download ready to use: an AppImage is made executable, so it starts from the file
    /// manager right away, as it would after <c>chmod +x</c>. Nothing else needs anything.
    /// </summary>
    public static void Prepare(InstallKind kind, string path)
    {
        if (kind != InstallKind.AppImage || OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            // Executable for whoever may read it, as chmod +x does under the usual umask.
            if (mode.HasFlag(UnixFileMode.UserRead)) mode |= UnixFileMode.UserExecute;
            if (mode.HasFlag(UnixFileMode.GroupRead)) mode |= UnixFileMode.GroupExecute;
            if (mode.HasFlag(UnixFileMode.OtherRead)) mode |= UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // It is still the right file; starting it just takes a chmod first.
        }
    }
}
