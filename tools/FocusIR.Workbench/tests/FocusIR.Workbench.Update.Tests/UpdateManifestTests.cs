using FocusIR.Workbench.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FocusIR.Workbench.Update.Tests;

[TestClass]
public sealed class UpdateManifestTests
{
    [TestMethod]
    public void Validate_AcceptsPublishedContract()
    {
        var manifest = CreateManifest();

        Assert.AreEqual(new SemanticVersion(0, 2, 0), manifest.Validate());
    }

    [TestMethod]
    public void Validate_RejectsUnknownSchema()
    {
        var manifest = CreateManifest() with { SchemaVersion = 2 };

        Assert.ThrowsExactly<UpdateValidationException>(() => manifest.Validate());
    }

    [TestMethod]
    public void Validate_RejectsTagVersionMismatch()
    {
        var manifest = CreateManifest() with { Tag = "v0.3.0" };

        Assert.ThrowsExactly<UpdateValidationException>(() => manifest.Validate());
    }

    [TestMethod]
    public void Validate_RejectsUnexpectedPackageName()
    {
        var manifest = CreateManifest() with
        {
            Package = CreateManifest().Package with { FileName = "other.zip" }
        };

        Assert.ThrowsExactly<UpdateValidationException>(() => manifest.Validate());
    }

    internal static UpdateManifest CreateManifest(string packageUrl =
        "https://gitee.com/warren-cao/focusir-workbench-releases/releases/download/v0.2.0/FocusIR.Workbench-v0.2.0-win-x64.zip") =>
        new(
            1,
            "0.2.0",
            "v0.2.0",
            new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero),
            "release-one",
            "1.0.0",
            "focusir-release-2026",
            new UpdatePackageManifest(
                "win-x64",
                "FocusIR.Workbench-v0.2.0-win-x64.zip",
                packageUrl,
                12,
                new string('0', 64)));
}
