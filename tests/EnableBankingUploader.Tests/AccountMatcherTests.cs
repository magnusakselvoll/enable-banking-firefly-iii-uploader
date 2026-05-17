using EnableBankingUploader.Core.EnableBanking.Models;
using EnableBankingUploader.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnableBankingUploader.Tests;

[TestClass]
public class AccountMatcherTests
{
    private static AccountMatcher CreateMatcher() => new(NullLogger<AccountMatcher>.Instance);

    private static Core.EnableBanking.Models.Account EbAccount(string uid, string? iban) =>
        new(uid, iban is null ? null : new AccountIdentification(iban), null, null);

    private static Core.FireflyIii.Models.Account FfAccount(string id, string name, string? iban) =>
        new(id, new Core.FireflyIii.Models.AccountAttributes(name, "asset", iban));

    [TestMethod]
    public void MatchAccounts_ExactIbanMatch_ReturnsMatch()
    {
        var matcher = CreateMatcher();
        var eb = new[] { EbAccount("uid1", "NO9386011117947") };
        var ff = new[] { FfAccount("ff1", "My Account", "NO9386011117947") };

        var result = matcher.MatchAccounts(eb, ff);

        Assert.HasCount(1, result);
        Assert.AreEqual("uid1", result[0].EnableBanking.Uid);
        Assert.AreEqual("ff1", result[0].FireflyIii.Id);
    }

    [TestMethod]
    public void MatchAccounts_IbanWithSpaces_NormalizesAndMatches()
    {
        var matcher = CreateMatcher();
        var eb = new[] { EbAccount("uid1", "NO93 8601 1117 947") };
        var ff = new[] { FfAccount("ff1", "My Account", "NO9386011117947") };

        var result = matcher.MatchAccounts(eb, ff);

        Assert.HasCount(1, result);
    }

    [TestMethod]
    public void MatchAccounts_NoIbanMatch_ReturnsEmpty()
    {
        var matcher = CreateMatcher();
        var eb = new[] { EbAccount("uid1", "NO9386011117947") };
        var ff = new[] { FfAccount("ff1", "Other Account", "GB33BUKB20201555555555") };

        var result = matcher.MatchAccounts(eb, ff);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void MatchAccounts_NullIban_SkipsAccount()
    {
        var matcher = CreateMatcher();
        var eb = new[] { EbAccount("uid1", null) };
        var ff = new[] { FfAccount("ff1", "My Account", "NO9386011117947") };

        var result = matcher.MatchAccounts(eb, ff);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void MatchAccounts_MultipleAccounts_MatchesCorrectly()
    {
        var matcher = CreateMatcher();
        var eb = new[]
        {
            EbAccount("uid1", "NO9386011117947"),
            EbAccount("uid2", "GB33BUKB20201555555555"),
            EbAccount("uid3", "DE89370400440532013000"),
        };
        var ff = new[]
        {
            FfAccount("ff1", "Norwegian", "NO9386011117947"),
            FfAccount("ff2", "British", "GB33BUKB20201555555555"),
        };

        var result = matcher.MatchAccounts(eb, ff);

        Assert.HasCount(2, result);
    }

    [TestMethod]
    public void NormalizeIban_RemovesSpacesAndDashesAndUppercases()
    {
        Assert.AreEqual("NO9386011117947", AccountMatcher.NormalizeIban("no93 8601-1117 947"));
    }
}
