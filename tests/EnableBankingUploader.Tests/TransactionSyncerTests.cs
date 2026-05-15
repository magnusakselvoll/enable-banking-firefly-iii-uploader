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
    private static readonly string SessionId = "session-1";
    private static readonly string AccountUid = "eb-uid-1";
    private static readonly string FireflyAccountId = "ff-1";
    private static readonly string FireflyAccountName = "Test Account";
    private static readonly string Iban = "NO9386011117947";

    private static Core.EnableBanking.Models.Account EbAccount() =>
        new(AccountUid, new AccountIdentification(Iban), "Test", "NOK");

    private static Session Session() => new(SessionId, [AccountUid]);

    private static Core.FireflyIii.Models.Account FfAccount() =>
        new(FireflyAccountId, new Core.FireflyIii.Models.AccountAttributes(FireflyAccountName, "asset", Iban));

    private static Core.EnableBanking.Models.Transaction EbTransaction(
        string? entryRef = "ref-1",
        string? txId = "tx-1",
        string creditDebit = "DBIT",
        string amount = "100.00",
        string status = "BOOK",
        DateOnly? date = null) =>
        new(txId, entryRef,
            new TransactionAmount(amount, "NOK"),
            creditDebit,
            date ?? new DateOnly(2024, 1, 15),
            null, status, ["Test payment"], "Creditor Name", null);

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
            EnableBankingSessionIds = [SessionId],
            FireflyIiiUrl = "http://localhost",
            FireflyIiiToken = "token",
            LookbackDays = lookbackDays,
        });
        var matcher = new AccountMatcher(NullLogger<AccountMatcher>.Instance);
        var syncer = new TransactionSyncer(eb, ff, matcher, options, NullLogger<TransactionSyncer>.Instance);
        return (syncer, eb, ff);
    }

    private static void SetupDefaults(IEnableBankingClient eb, IFireflyIiiClient ff,
        DateOnly? latestDate = null,
        IReadOnlyList<Core.EnableBanking.Models.Transaction>? ebTx = null,
        IReadOnlyList<Core.FireflyIii.Models.Transaction>? ffTx = null)
    {
        eb.GetSessionAsync(SessionId, default).Returns(Session());
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);
        ff.GetLatestTransactionDateAsync(FireflyAccountId, default).Returns(latestDate);
        ff.GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default)
            .Returns(ffTx ?? []);
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default)
            .Returns(ebTx ?? []);
    }

    [TestMethod]
    public async Task SyncAsync_NoSessionsConfigured_DoesNothing()
    {
        var eb = Substitute.For<IEnableBankingClient>();
        var ff = Substitute.For<IFireflyIiiClient>();
        var options = Options.Create(new SyncOptions
        {
            EnableBankingApplicationId = "app-id",
            EnableBankingPrivateKeyPath = "/fake/key.pem",
            EnableBankingSessionIds = [],
            FireflyIiiUrl = "http://localhost",
            FireflyIiiToken = "token",
        });
        var syncer = new TransactionSyncer(eb, ff,
            new AccountMatcher(NullLogger<AccountMatcher>.Instance),
            options, NullLogger<TransactionSyncer>.Instance);

        await syncer.SyncAsync();

        await ff.DidNotReceive().GetAssetAccountsAsync(default);
    }

    [TestMethod]
    public async Task SyncAsync_NewAccount_UsesDefaultFallbackDate()
    {
        var (syncer, eb, ff) = CreateSyncer();
        SetupDefaults(eb, ff);

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
        var (syncer, eb, ff) = CreateSyncer(lookbackDays: 1);
        var latestDate = new DateOnly(2024, 1, 10);
        SetupDefaults(eb, ff, latestDate: latestDate);

        await syncer.SyncAsync();

        await eb.Received(1).GetTransactionsAsync(AccountUid,
            Arg.Is<DateOnly>(d => d == new DateOnly(2024, 1, 9)),
            Arg.Any<DateOnly>(),
            default);
    }

    [TestMethod]
    public async Task SyncAsync_KnownExternalId_SkipsTransaction()
    {
        var (syncer, eb, ff) = CreateSyncer();
        SetupDefaults(eb, ff,
            ebTx: [EbTransaction(entryRef: "ref-1")],
            ffTx: [FfTransaction("ref-1")]);

        await syncer.SyncAsync();

        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
    }

    [TestMethod]
    public async Task SyncAsync_DebitTransaction_CreatesWithdrawal()
    {
        var (syncer, eb, ff) = CreateSyncer();
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: "ref-new", creditDebit: "DBIT")]);

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
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: "ref-new", creditDebit: "CRDT")]);

        await syncer.SyncAsync();

        await ff.Received(1).CreateTransactionAsync(
            Arg.Is<Core.FireflyIii.Models.TransactionStore>(s =>
                s.Transactions[0].Type == "deposit" &&
                s.Transactions[0].DestinationName == FireflyAccountName),
            default);
    }

    [TestMethod]
    public async Task SyncAsync_NullEntryReferenceAndTransactionId_SkipsTransaction()
    {
        var (syncer, eb, ff) = CreateSyncer();
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: null, txId: null)]);

        await syncer.SyncAsync();

        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
    }

    [TestMethod]
    public async Task SyncAsync_PendingTransaction_IsSkipped()
    {
        var (syncer, eb, ff) = CreateSyncer();
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: "ref-pending", status: "PDNG")]);

        await syncer.SyncAsync();

        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
    }

    [TestMethod]
    public async Task SyncAsync_EntryReferenceUsedOverTransactionId()
    {
        var (syncer, eb, ff) = CreateSyncer();
        // entry_reference differs from transaction_id — entry_reference should win
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: "entry-ref-1", txId: "tx-id-1")]);

        await syncer.SyncAsync();

        await ff.Received(1).CreateTransactionAsync(
            Arg.Is<Core.FireflyIii.Models.TransactionStore>(s =>
                s.Transactions[0].ExternalId == "entry-ref-1"),
            default);
    }
}
