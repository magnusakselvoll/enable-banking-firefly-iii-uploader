using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.EnableBanking.Models;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sessions;
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

    private static StoredSession StoredSession() =>
        new(SessionId, "TestBank", "FI", [AccountUid], DateTimeOffset.UtcNow.AddDays(180), DateTimeOffset.UtcNow);

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

    private static (TransactionSyncer syncer, IEnableBankingClient eb, IFireflyIiiClient ff) CreateSyncer(
        int lookbackDays = 1,
        ISessionStore? store = null,
        bool whatIf = false,
        string fireflyUrl = "http://localhost")
    {
        var eb = Substitute.For<IEnableBankingClient>();
        var ff = Substitute.For<IFireflyIiiClient>();

        if (store is null)
        {
            store = Substitute.For<ISessionStore>();
            store.ListAsync(default).Returns([StoredSession()]);
        }

        var options = Options.Create(new SyncOptions
        {
            EnableBankingApplicationId = "app-id",
            EnableBankingPrivateKeyPath = "/fake/key.pem",
            FireflyIiiUrl = fireflyUrl,
            FireflyIiiToken = "token",
            LookbackDays = lookbackDays,
            WhatIf = whatIf,
        });
        var matcher = new AccountMatcher(NullLogger<AccountMatcher>.Instance);
        var syncer = new TransactionSyncer(eb, ff, store, matcher, options, NullLogger<TransactionSyncer>.Instance);
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
    public async Task SyncAsync_NoSessionsInStore_DoesNothing()
    {
        var emptyStore = Substitute.For<ISessionStore>();
        emptyStore.ListAsync(default).Returns([]);
        var (syncer, _, ff) = CreateSyncer(store: emptyStore);

        await syncer.SyncAsync();

        await ff.DidNotReceive().GetAssetAccountsAsync(default);
    }

    [TestMethod]
    public async Task SyncAsync_ExpiredSession_IsSkipped()
    {
        var expiredStore = Substitute.For<ISessionStore>();
        expiredStore.ListAsync(default).Returns([
            new Core.Sessions.StoredSession(SessionId, "TestBank", "FI", [AccountUid],
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-91))
        ]);
        var (syncer, _, ff) = CreateSyncer(store: expiredStore);

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
    public async Task SyncAsync_CreatedTransaction_HasRunLabel()
    {
        var (syncer, eb, ff) = CreateSyncer();
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: "ref-new")]);

        var summary = await syncer.SyncAsync();

        Assert.IsNotNull(summary.RunLabel);
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
            summary.RunLabel,
            @"^eb-sync-\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"),
            $"RunLabel '{summary.RunLabel}' does not match expected format");

        await ff.Received(1).CreateTransactionAsync(
            Arg.Is<Core.FireflyIii.Models.TransactionStore>(s =>
                s.Transactions[0].Tags != null &&
                s.Transactions[0].Tags!.Count == 1 &&
                s.Transactions[0].Tags![0] == summary.RunLabel),
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
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: "entry-ref-1", txId: "tx-id-1")]);

        await syncer.SyncAsync();

        await ff.Received(1).CreateTransactionAsync(
            Arg.Is<Core.FireflyIii.Models.TransactionStore>(s =>
                s.Transactions[0].ExternalId == "entry-ref-1"),
            default);
    }

    [TestMethod]
    public async Task SyncAsync_WhatIfOfflineNoFireflyUrl_DoesNotContactFirefly()
    {
        var (syncer, eb, ff) = CreateSyncer(whatIf: true, fireflyUrl: "");
        eb.GetSessionAsync(SessionId, default).Returns(Session());
        eb.GetAccountAsync(AccountUid, default).Returns(EbAccount());
        eb.GetTransactionsAsync(AccountUid, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default).Returns([]);

        await syncer.SyncAsync();

        await ff.DidNotReceive().GetAssetAccountsAsync(default);
        await ff.DidNotReceive().GetLatestTransactionDateAsync(Arg.Any<string>(), default);
        await ff.DidNotReceive().GetTransactionsAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default);
        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
        await eb.Received(1).GetTransactionsAsync(AccountUid,
            Arg.Is<DateOnly>(d => d == new DateOnly(2000, 1, 1)),
            Arg.Any<DateOnly>(), default);
    }

    [TestMethod]
    public async Task SyncAsync_WhatIfConnected_ReadsButDoesNotWrite()
    {
        var (syncer, eb, ff) = CreateSyncer(whatIf: true);
        SetupDefaults(eb, ff, ebTx: [EbTransaction(entryRef: "ref-new")]);

        await syncer.SyncAsync();

        await ff.Received(1).GetAssetAccountsAsync(default);
        await ff.Received(1).GetLatestTransactionDateAsync(FireflyAccountId, default);
        await ff.Received(1).GetTransactionsAsync(FireflyAccountId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), default);
        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
    }

    [TestMethod]
    public async Task SyncAsync_WhatIfConnected_DuplicateAndNew_NeitherWritten()
    {
        var (syncer, eb, ff) = CreateSyncer(whatIf: true);
        SetupDefaults(eb, ff,
            ebTx: [EbTransaction(entryRef: "dup"), EbTransaction(entryRef: "new", txId: "tx-2")],
            ffTx: [FfTransaction("dup")]);

        var summary = await syncer.SyncAsync();

        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
        Assert.AreEqual(1, summary.MappedAccounts);
        Assert.AreEqual(0, summary.UnmappedAccounts);
        Assert.AreEqual(1, summary.Created);
        Assert.AreEqual(1, summary.SkippedDuplicate);
    }

    [TestMethod]
    public async Task SyncAsync_UnmappedAccount_CountedInSummary()
    {
        var (syncer, eb, ff) = CreateSyncer();
        eb.GetSessionAsync(SessionId, default).Returns(Session());
        // EB account has an IBAN that doesn't exist in Firefly
        eb.GetAccountAsync(AccountUid, default).Returns(
            new Core.EnableBanking.Models.Account(AccountUid,
                new Core.EnableBanking.Models.AccountIdentification("SE0000000000000"),
                "Test", "SEK"));
        ff.GetAssetAccountsAsync(default).Returns([FfAccount()]);

        var summary = await syncer.SyncAsync();

        Assert.AreEqual(0, summary.MappedAccounts);
        Assert.AreEqual(1, summary.UnmappedAccounts);
        Assert.AreEqual(0, summary.Created);
        await ff.DidNotReceive().CreateTransactionAsync(Arg.Any<Core.FireflyIii.Models.TransactionStore>(), default);
    }
}
