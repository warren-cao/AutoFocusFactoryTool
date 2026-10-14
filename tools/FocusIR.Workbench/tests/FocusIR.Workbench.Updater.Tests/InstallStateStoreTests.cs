namespace FocusIR.Workbench.Updater.Tests;

[TestClass]
public sealed class InstallStateStoreTests
{
    [TestMethod]
    public void CleanupObsoleteVersions_RetainsOnlyCurrentAndPreviousManagedVersions()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var layout = new InstallationLayout(Path.Combine(temporaryDirectory.Path, "managed-root"));
        var stateStore = new InstallStateStore(layout);
        CreateInstalledVersion(layout, "0.2.0");
        CreateInstalledVersion(layout, "0.3.0");
        CreateInstalledVersion(layout, "0.1.0");
        Directory.CreateDirectory(Path.Combine(layout.VersionsDirectory, "operator-notes"));

        stateStore.Promote(SemanticVersion.Parse("0.2.0"));
        stateStore.Promote(SemanticVersion.Parse("0.3.0"));
        stateStore.CleanupObsoleteVersions();

        Assert.IsTrue(Directory.Exists(layout.GetVersionDirectory(SemanticVersion.Parse("0.3.0"))));
        Assert.IsTrue(Directory.Exists(layout.GetVersionDirectory(SemanticVersion.Parse("0.2.0"))));
        Assert.IsFalse(Directory.Exists(layout.GetVersionDirectory(SemanticVersion.Parse("0.1.0"))));
        Assert.IsTrue(Directory.Exists(Path.Combine(layout.VersionsDirectory, "operator-notes")));
    }

    [TestMethod]
    public void Rollback_AtomicallySwapsCurrentAndPreviousVersionStates()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var layout = new InstallationLayout(Path.Combine(temporaryDirectory.Path, "managed-root"));
        var stateStore = new InstallStateStore(layout);
        CreateInstalledVersion(layout, "0.2.0");
        CreateInstalledVersion(layout, "0.3.0");
        stateStore.Promote(SemanticVersion.Parse("0.2.0"));
        stateStore.Promote(SemanticVersion.Parse("0.3.0"));

        InstalledVersionState rolledBack = stateStore.Rollback();

        Assert.AreEqual("0.2.0", rolledBack.Version);
        Assert.AreEqual("0.2.0", stateStore.ReadCurrent()?.Version);
        Assert.AreEqual("0.3.0", stateStore.ReadPrevious()?.Version);
    }

    private static void CreateInstalledVersion(InstallationLayout layout, string version)
    {
        string directory = layout.GetVersionDirectory(SemanticVersion.Parse(version));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, UpdaterConstants.ApplicationExecutableName), "fake application");
    }
}
