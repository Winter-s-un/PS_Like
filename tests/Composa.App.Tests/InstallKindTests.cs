using static Composa.App.AppPaths;

namespace Composa.App.Tests;

/// <summary>
/// Every way Composa can be installed, told apart through made-up answers from the machine, so each
/// is checked on any platform without installing anything.
/// </summary>
public class InstallKindTests
{
    private const string WindowsFolder = @"C:\Users\ada\AppData\Local\Programs\Composa\";

    private static InstallProbes Probe(
        Platform platform = Platform.Linux,
        bool published = true,
        string folder = "/home/ada/composa-1.2.0-linux-x64/",
        Dictionary<string, string>? variables = null,
        Dictionary<string, string>? files = null,
        Func<string, IReadOnlyList<string>, string?>? run = null,
        params string[] installerLocations) =>
        new(platform, published, folder,
            name => variables?.GetValueOrDefault(name),
            path => files?.GetValueOrDefault(path),
            run ?? ((_, _) => null),
            () => installerLocations);

    [Fact]
    public void A_build_published_for_no_platform_is_a_developer_build()
    {
        Assert.Equal(InstallKind.Developer, Install.Detect(Probe(published: false)));
        Assert.Equal(InstallKind.Developer, Install.Detect(Probe(Platform.Windows, published: false, folder: WindowsFolder, installerLocations: WindowsFolder)));
    }

    [Fact]
    public void Unpacked_anywhere_on_Linux_is_the_tarball() => Assert.Equal(InstallKind.Tarball, Install.Detect(Probe()));

    [Fact]
    public void The_AppImage_is_known_by_the_variables_its_runtime_sets()
    {
        var variables = new Dictionary<string, string> { ["APPIMAGE"] = "/home/ada/Composa-1.2.0-x86_64.AppImage", ["APPDIR"] = "/tmp/.mount_ComposXYZ" };
        Assert.Equal(InstallKind.AppImage, Install.Detect(Probe(folder: "/tmp/.mount_ComposXYZ/usr/lib/composa/", variables: variables)));
    }

    /// <summary>A program started from an AppImage inherits its variables, so the tarball started from one is still the tarball.</summary>
    [Fact]
    public void Inheriting_another_AppImages_variables_is_not_being_one()
    {
        var variables = new Dictionary<string, string> { ["APPIMAGE"] = "/home/ada/Terminal.AppImage", ["APPDIR"] = "/tmp/.mount_TermABC" };
        Assert.Equal(InstallKind.Tarball, Install.Detect(Probe(variables: variables)));
        // A mount whose name merely begins the same way is another mount.
        var near = new Dictionary<string, string> { ["APPIMAGE"] = "/home/ada/Composa.AppImage", ["APPDIR"] = "/tmp/.mount_Com" };
        Assert.Equal(InstallKind.Tarball, Install.Detect(Probe(folder: "/tmp/.mount_ComposXYZ/usr/lib/composa/", variables: near)));
    }

    [Fact]
    public void The_deb_is_known_by_dpkgs_list_of_its_files()
    {
        var files = new Dictionary<string, string> { ["/var/lib/dpkg/info/composa.list"] = "/.\n/usr\n/usr/lib\n/usr/lib/composa\n/usr/lib/composa/composa\n" };
        Assert.Equal(InstallKind.Deb, Install.Detect(Probe(folder: "/usr/lib/composa/", files: files)));
        var multiArch = new Dictionary<string, string> { ["/var/lib/dpkg/info/composa:arm64.list"] = "/usr/lib/composa\n" };
        Assert.Equal(InstallKind.Deb, Install.Detect(Probe(folder: "/usr/lib/composa/", files: multiArch)));
    }

    [Fact]
    public void The_rpm_is_known_by_rpm_naming_its_package()
    {
        var asked = new List<string>();
        string? Rpm(string program, IReadOnlyList<string> arguments)
        {
            asked.Add(program + " " + string.Join(" ", arguments));
            return program == "rpm" ? "composa" : null;
        }
        Assert.Equal(InstallKind.Rpm, Install.Detect(Probe(folder: "/usr/lib/composa/", run: Rpm)));
        Assert.Equal(["rpm -qf --queryformat %{NAME} /usr/lib/composa"], asked);
    }

    /// <summary>Files copied into /usr/lib/composa by hand belong to no package, and a tarball replaces them.</summary>
    [Fact]
    public void The_package_folder_owned_by_no_package_is_the_tarball()
    {
        Assert.Equal(InstallKind.Tarball, Install.Detect(Probe(folder: "/usr/lib/composa/")));
        Assert.Equal(InstallKind.Tarball, Install.Detect(Probe(folder: "/usr/lib/composa/", run: (_, _) => "someone-elses-package")));
        // dpkg's list for some other package that happens to be called composa, installed elsewhere.
        var elsewhere = new Dictionary<string, string> { ["/var/lib/dpkg/info/composa.list"] = "/opt/composa\n" };
        Assert.Equal(InstallKind.Tarball, Install.Detect(Probe(folder: "/usr/lib/composa/", files: elsewhere)));
    }

    /// <summary>Only the running folder counts: dpkg owning /usr/lib/composa says nothing about a copy unpacked in the home folder.</summary>
    [Fact]
    public void A_tarball_next_to_an_installed_package_is_still_the_tarball()
    {
        var files = new Dictionary<string, string> { ["/var/lib/dpkg/info/composa.list"] = "/usr/lib/composa\n" };
        Assert.Equal(InstallKind.Tarball, Install.Detect(Probe(files: files, run: (_, _) => "composa")));
    }

    [Fact]
    public void The_Windows_installer_is_known_by_its_uninstall_entry()
    {
        // Inno Setup writes the folder with a trailing separator; Windows compares folders without regard to case.
        Assert.Equal(InstallKind.WindowsInstaller, Install.Detect(Probe(Platform.Windows, folder: WindowsFolder, installerLocations: WindowsFolder)));
        Assert.Equal(InstallKind.WindowsInstaller, Install.Detect(Probe(Platform.Windows, folder: WindowsFolder,
            installerLocations: @"c:\users\ada\appdata\local\programs\composa")));
        // Installed for every user as well as for this one: either entry will do.
        Assert.Equal(InstallKind.WindowsInstaller, Install.Detect(Probe(Platform.Windows, folder: @"C:\Program Files\Composa\",
            installerLocations: [WindowsFolder, @"C:\Program Files\Composa\"])));
    }

    [Fact]
    public void Anything_else_on_Windows_is_the_zip()
    {
        Assert.Equal(InstallKind.WindowsZip, Install.Detect(Probe(Platform.Windows, folder: @"D:\Tools\composa-1.2.0-win-x64\")));
        // The zip unpacked on a machine where the installer is also present is still the zip.
        Assert.Equal(InstallKind.WindowsZip, Install.Detect(Probe(Platform.Windows, folder: @"D:\Tools\composa-1.2.0-win-x64\", installerLocations: WindowsFolder)));
    }

    [Fact]
    public void MacOS_has_no_download_yet() => Assert.Equal(InstallKind.Developer, Install.Detect(Probe(Platform.MacOS, folder: "/Applications/Composa.app/Contents/MacOS/")));

    /// <summary>The test run is a developer build: no RuntimeIdentifier, so nothing to download.</summary>
    [Fact]
    public void This_build_is_a_developer_build() => Assert.Equal(InstallKind.Developer, Install.Current);
}
