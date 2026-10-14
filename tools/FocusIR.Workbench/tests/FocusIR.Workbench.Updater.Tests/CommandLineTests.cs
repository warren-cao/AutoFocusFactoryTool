namespace FocusIR.Workbench.Updater.Tests;

[TestClass]
public sealed class CommandLineTests
{
    [TestMethod]
    public void Parse_ApplyPackageAcceptsDocumentedPositionalPackagePath()
    {
        UpdaterCommand command = CommandLine.Parse(
        [
            "--apply-package",
            "C:\\Downloads\\FocusIR.zip",
            "--version",
            "0.3.0",
            "--sha256",
            new string('A', 64),
            "--parent-pid",
            "1234",
        ]);

        Assert.AreEqual(UpdaterCommandKind.ApplyPackage, command.Kind);
        Assert.AreEqual("C:\\Downloads\\FocusIR.zip", command.PackagePath);
        Assert.AreEqual(SemanticVersion.Parse("0.3.0"), command.Version);
        Assert.AreEqual(1234, command.ParentProcessId);
    }

    [TestMethod]
    public void Parse_ApplyPackageRequiresSha256()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CommandLine.Parse(
        [
            "--apply-package",
            "C:\\Downloads\\FocusIR.zip",
            "--version",
            "0.3.0",
            "--parent-pid",
            "1234",
        ]));
    }

    [TestMethod]
    [DataRow("v1.2.3")]
    [DataRow("1.2")]
    [DataRow("01.2.3")]
    [DataRow("1.2.3-beta")]
    public void SemanticVersion_RejectsUnsupportedVersionForms(string value)
    {
        Assert.IsFalse(SemanticVersion.TryParse(value, out _));
    }
}
