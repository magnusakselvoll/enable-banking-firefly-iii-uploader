using EnableBankingUploader.Cli.Web;

namespace EnableBankingUploader.Tests;

[TestClass]
public class AppVersionTests
{
    [TestMethod]
    public void Parse_DevVersion_ReturnsDevDisplayNoUrl()
    {
        var (isRelease, display, url) = AppVersion.Parse("0.0.0-dev");
        Assert.IsFalse(isRelease);
        Assert.AreEqual("dev", display);
        Assert.IsNull(url);
    }

    [TestMethod]
    public void Parse_Null_ReturnsDevDisplayNoUrl()
    {
        var (isRelease, display, url) = AppVersion.Parse(null);
        Assert.IsFalse(isRelease);
        Assert.AreEqual("dev", display);
        Assert.IsNull(url);
    }

    [TestMethod]
    public void Parse_ReleaseVersion_ReturnsVPrefixedDisplayAndUrl()
    {
        var (isRelease, display, url) = AppVersion.Parse("0.1.5");
        Assert.IsTrue(isRelease);
        Assert.AreEqual("v0.1.5", display);
        Assert.AreEqual(
            "https://github.com/magnusakselvoll/enable-banking-firefly-iii-uploader/releases/tag/v0.1.5",
            url);
    }

    [TestMethod]
    public void Parse_VersionWithSourceRevisionSuffix_StripsSuffix()
    {
        var (isRelease, display, url) = AppVersion.Parse("0.1.5+a1b2c3d4");
        Assert.IsTrue(isRelease);
        Assert.AreEqual("v0.1.5", display);
        Assert.AreEqual(
            "https://github.com/magnusakselvoll/enable-banking-firefly-iii-uploader/releases/tag/v0.1.5",
            url);
    }
}
