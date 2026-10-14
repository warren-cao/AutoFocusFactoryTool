using FocusIR.Workbench.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FocusIR.Workbench.Update.Tests;

[TestClass]
public sealed class SemanticVersionTests
{
    [TestMethod]
    [DataRow("0.2.0", 0, 2, 0)]
    [DataRow("10.20.30", 10, 20, 30)]
    public void TryParse_AcceptsStrictStableVersion(string value, int major, int minor, int patch)
    {
        Assert.IsTrue(SemanticVersion.TryParse(value, out var version));
        Assert.AreEqual(new SemanticVersion(major, minor, patch), version);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1.2")]
    [DataRow("1.2.3.4")]
    [DataRow("01.2.3")]
    [DataRow("1.02.3")]
    [DataRow("1.2.03")]
    [DataRow("1.2.3-beta")]
    [DataRow("1.2.3+build")]
    [DataRow(" 1.2.3")]
    [DataRow("v1.2.3")]
    public void TryParse_RejectsNonStableOrNonCanonicalVersion(string value)
    {
        Assert.IsFalse(SemanticVersion.TryParse(value, out _));
    }

    [TestMethod]
    public void TryParseTag_RequiresLowercaseVAndThreeParts()
    {
        Assert.IsTrue(SemanticVersion.TryParseTag("v1.2.3", out var version));
        Assert.AreEqual(new SemanticVersion(1, 2, 3), version);
        Assert.IsFalse(SemanticVersion.TryParseTag("1.2.3", out _));
        Assert.IsFalse(SemanticVersion.TryParseTag("V1.2.3", out _));
    }

    [TestMethod]
    public void CompareTo_UsesMajorMinorPatchOrder()
    {
        Assert.IsTrue(new SemanticVersion(2, 0, 0) > new SemanticVersion(1, 99, 99));
        Assert.IsTrue(new SemanticVersion(1, 3, 0) > new SemanticVersion(1, 2, 99));
        Assert.IsTrue(new SemanticVersion(1, 2, 4) > new SemanticVersion(1, 2, 3));
        Assert.IsTrue(new SemanticVersion(1, 2, 3) <= new SemanticVersion(1, 2, 3));
    }
}
