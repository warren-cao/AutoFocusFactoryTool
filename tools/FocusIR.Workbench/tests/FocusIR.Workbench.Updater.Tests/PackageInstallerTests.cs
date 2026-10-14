using System.IO.Compression;
using System.Security.Cryptography;

namespace FocusIR.Workbench.Updater.Tests;

[TestClass]
public sealed class PackageInstallerTests
{
    [TestMethod]
    public async Task InstallAsync_PromotesNewVersionAndRetainsPreviousVersion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        InstallationLayout layout = CreateLayout(temporaryDirectory);
        var stateStore = new InstallStateStore(layout);
        var installer = new PackageInstaller(layout, stateStore);
        string firstPackage = CreatePackage(temporaryDirectory.Path, "first.zip");
        string secondPackage = CreatePackage(temporaryDirectory.Path, "second.zip");

        await installer.InstallAsync(firstPackage, SemanticVersion.Parse("0.2.0"), ComputeSha256(firstPackage));
        await installer.InstallAsync(secondPackage, SemanticVersion.Parse("0.3.0"), ComputeSha256(secondPackage));

        Assert.AreEqual("0.3.0", stateStore.ReadCurrent()?.Version);
        Assert.AreEqual("0.2.0", stateStore.ReadPrevious()?.Version);
        Assert.IsTrue(File.Exists(Path.Combine(layout.GetVersionDirectory(SemanticVersion.Parse("0.3.0")), UpdaterConstants.ApplicationExecutableName)));
        Assert.IsTrue(Directory.Exists(layout.GetVersionDirectory(SemanticVersion.Parse("0.2.0"))));
    }

    [TestMethod]
    public async Task InstallAsync_RejectsPathTraversalWithoutWritingOutsideVersionDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        InstallationLayout layout = CreateLayout(temporaryDirectory);
        var stateStore = new InstallStateStore(layout);
        var installer = new PackageInstaller(layout, stateStore);
        string packagePath = Path.Combine(temporaryDirectory.Path, "malicious.zip");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../escaped.txt", "should-not-exist");
            WriteEntry(archive, UpdaterConstants.ApplicationExecutableName, "fake application");
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            installer.InstallAsync(packagePath, SemanticVersion.Parse("0.2.0"), ComputeSha256(packagePath)));

        Assert.IsFalse(File.Exists(Path.Combine(layout.VersionsDirectory, "escaped.txt")));
        Assert.IsNull(stateStore.ReadCurrent());
    }

    [TestMethod]
    public async Task InstallAsync_RejectsIncorrectPackageHashBeforeExtraction()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        InstallationLayout layout = CreateLayout(temporaryDirectory);
        var installer = new PackageInstaller(layout, new InstallStateStore(layout));
        string packagePath = CreatePackage(temporaryDirectory.Path, "package.zip");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            installer.InstallAsync(packagePath, SemanticVersion.Parse("0.2.0"), new string('0', 64)));

        Assert.IsFalse(Directory.Exists(layout.GetVersionDirectory(SemanticVersion.Parse("0.2.0"))));
    }

    [TestMethod]
    public async Task InstallAsync_RejectsDowngradeAndKeepsCurrentVersion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        InstallationLayout layout = CreateLayout(temporaryDirectory);
        var stateStore = new InstallStateStore(layout);
        var installer = new PackageInstaller(layout, stateStore);
        string currentPackage = CreatePackage(temporaryDirectory.Path, "current.zip");
        string olderPackage = CreatePackage(temporaryDirectory.Path, "older.zip");
        await installer.InstallAsync(currentPackage, SemanticVersion.Parse("0.3.0"), ComputeSha256(currentPackage));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            installer.InstallAsync(olderPackage, SemanticVersion.Parse("0.2.0"), ComputeSha256(olderPackage)));

        Assert.AreEqual("0.3.0", stateStore.ReadCurrent()?.Version);
        Assert.IsFalse(Directory.Exists(layout.GetVersionDirectory(SemanticVersion.Parse("0.2.0"))));
    }

    private static InstallationLayout CreateLayout(TemporaryDirectory temporaryDirectory) =>
        new(Path.Combine(temporaryDirectory.Path, "managed-root"));

    private static string CreatePackage(string directory, string fileName)
    {
        string packagePath = Path.Combine(directory, fileName);
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteEntry(archive, UpdaterConstants.ApplicationExecutableName, "fake application");
        WriteEntry(archive, "runtime/config.json", "{}");
        return packagePath;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
