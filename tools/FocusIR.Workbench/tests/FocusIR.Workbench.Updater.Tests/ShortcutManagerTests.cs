namespace FocusIR.Workbench.Updater.Tests;

[TestClass]
public sealed class ShortcutManagerTests
{
    [TestMethod]
    public void BuildDefinition_AlwaysTargetsManagedLauncherWithLaunchArgument()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string root = Path.Combine(temporaryDirectory.Path, "managed-root");
        string programs = Path.Combine(temporaryDirectory.Path, "Start Menu", "Programs");
        var layout = new InstallationLayout(root);
        var manager = new ShortcutManager(layout, programsDirectory: programs);

        ShortcutDefinition definition = manager.BuildDefinition();

        Assert.AreEqual(
            Path.Combine(root, "launcher", UpdaterConstants.UpdaterExecutableName),
            definition.TargetPath);
        Assert.AreEqual("--launch", definition.Arguments);
        Assert.AreEqual(Path.Combine(root, "launcher"), definition.WorkingDirectory);
        Assert.AreEqual(
            Path.Combine(programs, "ThermEye", "FocusIR Workbench.lnk"),
            definition.ShortcutPath);
    }

    [TestMethod]
    public void CreateOrRepair_WhenManagedLauncherIsMissing_ReturnsFalseWithoutCreatingShortcut()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string programs = Path.Combine(temporaryDirectory.Path, "Start Menu", "Programs");
        var manager = new ShortcutManager(
            new InstallationLayout(Path.Combine(temporaryDirectory.Path, "managed-root")),
            programsDirectory: programs);

        bool created = manager.CreateOrRepair();

        Assert.IsFalse(created);
        Assert.IsFalse(Directory.Exists(programs));
    }
}
