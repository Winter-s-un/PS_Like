namespace Composa.App.Tests;

/// <summary>What Install would start for each download, recorded instead of started.</summary>
public sealed class InstallerTests
{
    private sealed class Recorder
    {
        public List<ShellCommand> Ran { get; } = [];
        public List<string> Opened { get; } = [];
        public Task<bool> Start(InstallKind kind, string path) => Installer.Start(kind, path,
            command => { Ran.Add(command); return true; },
            file => { Opened.Add(file); return Task.FromResult(true); });
    }

    /// <summary>The setup is started as a double-click would start it, so Windows can ask to elevate when installing for every user.</summary>
    [Fact]
    public async Task The_Windows_setup_is_started_through_the_shell()
    {
        var recorder = new Recorder();
        Assert.True(await recorder.Start(InstallKind.WindowsInstaller, @"C:\Users\ada\Downloads\composa-1.3.0-win-x64-setup.exe"));
        Assert.Equivalent(new ShellCommand(@"C:\Users\ada\Downloads\composa-1.3.0-win-x64-setup.exe", [], Shell: true), Assert.Single(recorder.Ran), strict: true);
        Assert.Empty(recorder.Opened);
        Assert.True(Installer.QuitsFirst(InstallKind.WindowsInstaller));
    }

    [Theory]
    [InlineData(InstallKind.Deb, "/home/ada/Downloads/composa_1.3.0_amd64.deb")]
    [InlineData(InstallKind.Rpm, "/home/ada/Downloads/composa-1.3.0-1.x86_64.rpm")]
    public async Task A_package_is_opened_with_the_software_installer(InstallKind kind, string path)
    {
        var recorder = new Recorder();
        Assert.True(await recorder.Start(kind, path));
        Assert.Equal([path], recorder.Opened);
        Assert.Empty(recorder.Ran);
        Assert.False(Installer.QuitsFirst(kind));
    }

    [Theory]
    [InlineData(InstallKind.AppImage)]
    [InlineData(InstallKind.Tarball)]
    [InlineData(InstallKind.WindowsZip)]
    [InlineData(InstallKind.Developer)]
    public async Task A_download_without_an_installer_starts_nothing(InstallKind kind)
    {
        Assert.False(Installer.HasOne(kind));
        var recorder = new Recorder();
        Assert.False(await recorder.Start(kind, "/home/ada/Downloads/whatever"));
        Assert.Empty(recorder.Ran);
        Assert.Empty(recorder.Opened);
        Assert.Null(Installer.TerminalCommand(kind, "/home/ada/Downloads/whatever"));
    }

    [Fact]
    public void Packages_offer_the_command_that_installs_them()
    {
        Assert.Equal("sudo apt install /home/ada/Downloads/composa_1.3.0_amd64.deb",
            Installer.TerminalCommand(InstallKind.Deb, "/home/ada/Downloads/composa_1.3.0_amd64.deb"));
        Assert.Equal("sudo dnf install /home/ada/Downloads/composa-1.3.0-1.x86_64.rpm",
            Installer.TerminalCommand(InstallKind.Rpm, "/home/ada/Downloads/composa-1.3.0-1.x86_64.rpm"));
        // A second copy's name has a space and brackets, which the shell must not split or glob.
        Assert.Equal("sudo apt install '/home/ada/Downloads/composa_1.3.0_amd64 (1).deb'",
            Installer.TerminalCommand(InstallKind.Deb, "/home/ada/Downloads/composa_1.3.0_amd64 (1).deb"));
        Assert.Equal(@"sudo dnf install '/home/ada/it'\''s/composa-1.3.0-1.x86_64.rpm'",
            Installer.TerminalCommand(InstallKind.Rpm, "/home/ada/it's/composa-1.3.0-1.x86_64.rpm"));
    }

    [Fact]
    public void A_downloaded_AppImage_is_made_executable()
    {
        if (OperatingSystem.IsWindows()) return; // No executable bit to set.
        var path = Path.Combine(Path.GetTempPath(), $"composa-{Guid.NewGuid():N}.AppImage");
        File.WriteAllBytes(path, [1]);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Installer.Prepare(InstallKind.AppImage, path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead | UnixFileMode.OtherExecute, File.GetUnixFileMode(path));

            // Nothing else is touched.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Installer.Prepare(InstallKind.Tarball, path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }
}
