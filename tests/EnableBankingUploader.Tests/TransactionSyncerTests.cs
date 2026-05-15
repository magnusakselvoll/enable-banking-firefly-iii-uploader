using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.EnableBanking.Models;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EnableBankingUploader.Tests;

[TestClass]
public class TransactionSyncerTests
{
    private static readonly string AccountUid = "eb-uid-1";
    private static readonly string FireflyAccountId = "ff-1";
    private static readonly string FireflyAccountName = "Test Account";
    private static readonly string Iban = "NO9386011117947";

    private static Core.EnableBanking.Models.Account EbAccount() =>
        new(AccountUid, new AccountIdentification(Iban), "Test", "NOK");

    private static Core.FireflyIii.Models.Account FfAccount() =>
        new(FireflyAccountId, new Core.FireflyIii.Models.AccountAttributes(FireflyAccountName, "asset", Iban));

    private static Session Session() => new("session-1", [AccountUid]);

    private static Core.EnableBanking.Models.Transaction EbTransaction(
        string? txId = "tx-1",
        string creditDebit = "DBIT",
        string amount = "100.00",
        DateOnly? date = null) =>
        new(txId, null,
            new TransactionAmount(amount, "NOK"),
            creditDebit,
            date ?? new DateOnly(2024, 1, 15),
            null, null, ["Test payment"], "Creditor Name", null);

    private static Core.FireflyIii.Models.Transaction FfTransaction(string externalId) =>
        new("ff-tx-1", new Core.FireflyIii.Models.TransactionGroupAttributes(
        [
            new Core.FireflyIii.Models.TransactionSplitAttributes(externalId, new DateOnly(2024, 1, 15), "100", "Test"),
        ]));

    private static (TransactionSyncer, IEnableBankingClient, IFireflyIiiClient) CreateSyncer(int lookbackDays = 1)
    {
        var eb = Substitute.For<IEnableBankingClient>();
        var ff = Substitute.For<IFireflyIiiClient>();
        var options = Options.Create(new SyncOptions
        {
            EnableBankingApplicationId = "app-id",
            EnableBankingPrivateKeyPath = "/fake/key.pem",
            FireflyIiiUrl = "http://localhost",
            FireflyIiiToken = "token",
            LookbackDays = lookbackDays,
        });
        var matcher = new AccountMatcher(NullLogger<AccountMatcher>.Instance);
        var syncer = new TransactionSyncer(eb, ff, matcher, options, NullLogger<TransactionSyncer>.Instance);
        return (syncer, eb, ff);
    }

    [TestMethod]
    public async Task SyncAsync_NewAccount_UsesDefaultFallbackDate()
    {
        var (syncer, eb, ff) = CreateSyncer();

        eb.GetSessionsAsync(default).Returns([Session()]);
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);
        ff.GetLatestTransactionDateAsync(FireflyAccountId, default).Returns((DateOnly?)null);
        ff.GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);

        await syncer.SyncAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expectedFrom = today.AddDays(-90);
        await eb.Received(1).GetTransactionsAsync(AccountUid,
            Arg.Is<DateOnly>(d => d == expectedFrom),
            Arg.Is<DateOnly>(d => d == today),
            default);
    }

    [TestMethod]
    public async Task SyncAsync_AccountWithTransactions_UsesLatestDateMinusLookback()
    {
        var latestDate = new DateOnly(2024, 1, 10);
        var (syncer, eb, ff) = CreateSyncer(lookbackDays: 1);

        eb.GetSessionsAsync(default).Returns([Session()]);
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);
        ff.GetLatestTransactionDateAsync(FireflyAccountId, default).Returns(latestDate);
        ff.GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);

        await syncer.SyncAsync();

        var expectedFrom = new DateOnly(2024, 1, 9);
        await eb.Received(1).GetTransactionsAsync(AccountUid,
            Arg.Is<DateOnly>(d => d == expectedFrom),
            Arg.Any<DateOnly>(),
            default);
    }

    [TestMethod]
    public async Task SyncAsync_KnownExternalId_SkipsTransaction()
    {
        var (syncer, eb, ff) = CreateSyncer();

        eb.GetSessionsAsync(default).Returns([Session()]);
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);
        ff.GetLatestTransactionDateAsync(FireflyAccountId, default).Returns((DateOnly?)null);
        ff.GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default)
            .Returns([FfTransaction("tx-1")]);
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default)
            .Returns([EbTransaction("tx-1")]);

        await syncer.SyncAsync();

        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
    }

    [TestMethod]
    public async Task SyncAsync_DebitTransaction_CreatesWithdrawal()
    {
        var (syncer, eb, ff) = CreateSyncer();

        eb.GetSessionsAsync(default).Returns([Session()]);
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);
        ff.GetLatestTransactionDateAsync(FireflyAccountId, default).Returns((DateOnly?)null);
        ff.GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default)
            .Returns([EbTransaction("tx-new", creditDebit: "DBIT")]);

        await syncer.SyncAsync();

        await ff.Received(1).CreateTransactionAsync(
            Arg.Is<Core.FireflyIii.Models.TransactionStore>(s =>
                s.Transactions[0].Type == "withdrawal" &&
                s.Transactions[0].SourceName == FireflyAccountName),
            default);
    }

    [TestMethod]
    public async Task SyncAsync_CreditTransaction_CreatesDeposit()
    {
        var (syncer, eb, ff) = CreateSyncer();

        eb.GetSessionsAsync(default).Returns([Session()]);
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);
        ff.GetLatestTransactionDateAsync(FireflyAccountId, default).Returns((DateOnly?)null);
        ff.GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default)
            .Returns([EbTransaction("tx-new", creditDebit: "CRDT")]);

        await syncer.SyncAsync();

        await ff.Received(1).CreateTransactionAsync(
            Arg.Is<Core.FireflyIii.Models.TransactionStore>(s =>
                s.Transactions[0].Type == "deposit" &&
                s.Transactions[0].DestinationName == FireflyAccountName),
            default);
    }

    [TestMethod]
    public async Task SyncAsync_NullTransactionId_SkipsTransaction()
    {
        var (syncer, eb, ff) = CreateSyncer();

        eb.GetSessionsAsync(default).Returns([Session()]);
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);
        ff.GetLatestTransactionDateAsync(FireflyAccountId, default).Returns((DateOnly?)null);
        ff.GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default)
            .Returns([EbTransaction(txId: null)]);

        await syncer.SyncAsync();

        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
    }
}
