namespace FocusIR.Workbench.Updater.Tests;

[TestClass]
public sealed class DownloadArtifactCleanerTests
{
    [TestMethod]
    public void CleanupFiles_RemovesOnlyTopLevelManagedDownloadFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var layout = new InstallationLayout(Path.Combine(temporaryDirectory.Path, "managed-root"));
        layout.EnsureDirectories();
        var packagePath = Path.Combine(layout.DownloadsDirectory, "package.zip");
        var partialPath = packagePath + ".partial";
        File.WriteAllText(packagePath, "package");
        File.WriteAllText(partialPath, "partial");
        var unexpectedDirectory = Path.Combine(layout.DownloadsDirectory, "unexpected-directory");
        Directory.CreateDirectory(unexpectedDirectory);
        var nestedFile = Path.Combine(unexpectedDirectory, "preserve.txt");
        File.WriteAllText(nestedFile, "preserve");

        new DownloadArtifactCleaner(layout).CleanupFiles();

        Assert.IsFalse(File.Exists(packagePath));
        Assert.IsFalse(File.Exists(partialPath));
        Assert.IsTrue(File.Exists(nestedFile));
    }
}
