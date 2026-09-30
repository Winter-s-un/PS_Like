using static Composa.App.AppPaths;

namespace Composa.App.Tests;

/// <summary>What each platform helper would run, recorded instead of run.</summary>
public sealed class FileRevealTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "composa-reveal-" + Guid.NewGuid().ToString("N"));

    public FileRevealTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string NewFile(string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    private sealed class Desktop(bool runs = true, bool answers = true)
    {
        public List<ShellCommand> Ran { get; } = [];
        public List<string> Shown { get; } = [];
        public List<string> Opened { get; } = [];

        public Task<bool> Show(string path, Platform platform) => FileReveal.Show(path, platform,
            command => { Ran.Add(command); return runs; },
            uri => { Shown.Add(uri); return Task.FromResult(answers); },
            folder => { Opened.Add(folder); return Task.FromResult(true); });
    }

    [Fact]
    public void Windows_asks_Explorer_to_select_the_file()
    {
        var command = FileReveal.CommandFor(Platform.Windows, @"C:\Users\ada\Downloads\composa-1.3.0-win-x64-setup (1).exe");
        Assert.Equivalent(new ShellCommand("explorer.exe", [@"/select,""C:\Users\ada\Downloads\composa-1.3.0-win-x64-setup (1).exe"""], Verbatim: true), command, strict: true);
    }

    [Fact]
    public void MacOS_asks_open_to_reveal_the_file()
        => Assert.Equivalent(new ShellCommand("open", ["-R", "/Users/ada/Downloads/x.zip"]), FileReveal.CommandFor(Platform.MacOS, "/Users/ada/Downloads/x.zip"), strict: true);

    [Fact]
    public async Task Linux_asks_the_file_manager_over_DBus()
    {
        var path = NewFile("composa_1.3.0_amd64 (1).deb");
        var desktop = new Desktop();
        Assert.True(await desktop.Show(path, Platform.Linux));
        Assert.Empty(desktop.Ran);
        Assert.Empty(desktop.Opened);
        Assert.Equal([FileReveal.FileUri(path)], desktop.Shown);
    }

    [Theory]
    [InlineData("/home/ada/Downloads/composa_1.3.0_amd64.deb", "file:///home/ada/Downloads/composa_1.3.0_amd64.deb")]
    [InlineData("/home/ada/Downloads/composa_1.3.0_amd64 (1).deb", "file:///home/ada/Downloads/composa_1.3.0_amd64%20%281%29.deb")]
    [InlineData("/home/ada/Téléchargements/a.deb", "file:///home/ada/T%C3%A9l%C3%A9chargements/a.deb")]
    [InlineData("/home/ada/C#/a.deb", "file:///home/ada/C%23/a.deb")] // Not a fragment.
    public void A_file_uri_escapes_every_part_of_the_path(string path, string expected) => Assert.Equal(expected, FileReveal.FileUri(path));

    /// <summary>A desktop whose file manager does not answer, or none at all, still gets the folder opened.</summary>
    [Fact]
    public async Task Without_a_file_manager_to_answer_the_folder_is_opened()
    {
        var path = NewFile("Composa-1.3.0-x86_64.AppImage");
        var linux = new Desktop(answers: false);
        Assert.True(await linux.Show(path, Platform.Linux));
        Assert.Equal([folder], linux.Opened);

        var windows = new Desktop(runs: false);
        Assert.True(await windows.Show(path, Platform.Windows));
        Assert.Single(windows.Ran);
        Assert.Equal([folder], windows.Opened);
    }

    [Fact]
    public async Task A_file_that_is_gone_opens_its_folder_and_a_folder_that_is_gone_nothing()
    {
        var desktop = new Desktop();
        Assert.True(await desktop.Show(Path.Combine(folder, "deleted.deb"), Platform.Linux));
        Assert.Empty(desktop.Shown);
        Assert.Equal([folder], desktop.Opened);

        var nowhere = new Desktop();
        Assert.False(await nowhere.Show(Path.Combine(folder, "gone", "deleted.deb"), Platform.Linux));
        Assert.Empty(nowhere.Opened);
    }
}
