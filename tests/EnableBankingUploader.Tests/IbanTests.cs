using EnableBankingUploader.Cli.Web;
using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.EnableBanking.Models;
using EnableBankingUploader.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EnableBankingUploader.Tests;

[TestClass]
public class IbanTests
{
    private static readonly DateTimeOffset ValidUntil = new(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StoredSession SessionWithoutAccounts(string id = "s1") =>
        new(id, "TestBank", "FI", ["uid-1", "uid-2"], ValidUntil, CreatedAt);

    private static StoredSession SessionWithAccounts(string id = "s1") =>
        new(id, "TestBank", "FI", ["uid-1"],
            ValidUntil, CreatedAt,
            Accounts: [new StoredAccount("uid-1", "NO9386011117947")]);

    private static Account AccountWithIban(string uid, string iban) =>
        new(uid, new AccountIdentification(iban), null, null);

    // --- Html.Index rendering ---

    [TestMethod]
    public void HtmlIndex_AccountsPopulated_ShowsIban()
    {
        var session = SessionWithAccounts();
        var html = Html.Index([session], null, false);
        Assert.Contains("NO9386011117947", html);
    }

    [TestMethod]
    public void HtmlIndex_AccountsNull_ShowsUid()
    {
        var session = SessionWithoutAccounts();
        var html = Html.Index([session], null, false);
        Assert.Contains("uid-1", html);
    }

    [TestMethod]
    public void HtmlIndex_AccountsPopulatedIbanNull_ShowsUid()
    {
        var session = new StoredSession("s1", "TestBank", "FI", ["uid-1"],
            ValidUntil, CreatedAt,
            Accounts: [new StoredAccount("uid-1", null)]);
        var html = Html.Index([session], null, false);
        Assert.Contains("uid-1", html);
    }

    // --- EnsureIbansAsync backfill logic ---

    [TestMethod]
    public async Task EnsureIbans_SessionWithNullAccounts_FetchesAndPersists()
    {
        var session = SessionWithoutAccounts();
        var client = Substitute.For<IEnableBankingClient>();
        client.GetAccountAsync("uid-1", default).Returns(AccountWithIban("uid-1", "NO1111111111111"));
        client.GetAccountAsync("uid-2", default).Returns(AccountWithIban("uid-2", "NO2222222222222"));
        var store = Substitute.For<ISessionStore>();
        var logger = NullLogger.Instance;

        var result = await BankRegistrationEndpoints.EnsureIbansAsync(
            store, client, [session], logger, default);

        Assert.HasCount(1, result);
        Assert.IsNotNull(result[0].Accounts);
        Assert.HasCount(2, result[0].Accounts!);
        Assert.AreEqual("NO1111111111111", result[0].Accounts![0].Iban);
        Assert.AreEqual("NO2222222222222", result[0].Accounts![1].Iban);
        await store.Received(1).SaveAsync(Arg.Is<StoredSession>(s => s.SessionId == "s1"), default);
    }

    [TestMethod]
    public async Task EnsureIbans_SessionWithAccounts_SkipsFetchAndSave()
    {
        var session = SessionWithAccounts();
        var client = Substitute.For<IEnableBankingClient>();
        var store = Substitute.For<ISessionStore>();
        var logger = NullLogger.Instance;

        var result = await BankRegistrationEndpoints.EnsureIbansAsync(
            store, client, [session], logger, default);

        Assert.HasCount(1, result);
        await client.DidNotReceive().GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredSession>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task EnsureIbans_GetAccountThrows_FallsBackToOriginalSession()
    {
        var session = SessionWithoutAccounts();
        var client = Substitute.For<IEnableBankingClient>();
        client.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new HttpRequestException("rate limited"));
        var store = Substitute.For<ISessionStore>();
        var logger = NullLogger.Instance;

        var result = await BankRegistrationEndpoints.EnsureIbansAsync(
            store, client, [session], logger, default);

        Assert.HasCount(1, result);
        Assert.IsNull(result[0].Accounts);
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredSession>(), Arg.Any<CancellationToken>());
    }
}
