using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.FireflyIii.Models;
using EnableBankingUploader.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnableBankingUploader.Core.Sync;

public sealed class TransactionSyncer
{
    private const int DefaultLookbackDays = 90;

    private readonly IEnableBankingClient _enableBanking;
    private readonly IFireflyIiiClient _firefly;
    private readonly AccountMatcher _accountMatcher;
    private readonly SyncOptions _options;
    private readonly ILogger<TransactionSyncer> _logger;

    public TransactionSyncer(
        IEnableBankingClient enableBanking,
        IFireflyIiiClient firefly,
        AccountMatcher accountMatcher,
        IOptions<SyncOptions> options,
        ILogger<TransactionSyncer> logger)
    {
        _enableBanking = enableBanking;
        _firefly = firefly;
        _accountMatcher = accountMatcher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting transaction sync.");

        var sessions = await _enableBanking.GetSessionsAsync(cancellationToken);
        var fireflyAccounts = await _firefly.GetAssetAccountsAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var session in sessions)
        {
            foreach (var accountUid in session.AccountUids)
            {
                await SyncAccountAsync(accountUid, fireflyAccounts, today, cancellationToken);
            }
        }

        _logger.LogInformation("Transaction sync completed.");
    }

    private async Task SyncAccountAsync(
        string accountUid,
        IReadOnlyList<FireflyIii.Models.Account> fireflyAccounts,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        EnableBanking.Models.Account ebAccount;
        try
        {
            ebAccount = await _enableBanking.GetAccountAsync(accountUid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch details for Enable Banking account {Uid}, skipping.", accountUid);
            return;
        }

        var matches = _accountMatcher.MatchAccounts([ebAccount], fireflyAccounts);
        if (matches.Count == 0)
            return;

        var (_, fireflyAccount) = matches[0];

        var latestDate = await _firefly.GetLatestTransactionDateAsync(fireflyAccount.Id, cancellationToken);
        var dateFrom = latestDate.HasValue
            ? latestDate.Value.AddDays(-_options.LookbackDays)
            : today.AddDays(-DefaultLookbackDays);

        _logger.LogInformation(
            "Syncing account {Name} (IBAN: {Iban}) from {DateFrom} to {DateTo}.",
            fireflyAccount.Attributes.Name,
            ebAccount.AccountId?.Iban,
            dateFrom,
            today);

        var ebTransactions = await _enableBanking.GetTransactionsAsync(
            accountUid, dateFrom, today, cancellationToken);

        var existingTransactions = await _firefly.GetTransactionsAsync(
            fireflyAccount.Id, dateFrom, today, cancellationToken);

        var existingExternalIds = existingTransactions
            .SelectMany(t => t.Attributes.Transactions)
            .Where(s => s.ExternalId is not null)
            .Select(s => s.ExternalId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var skipped = 0;

        foreach (var ebTx in ebTransactions)
        {
            if (string.IsNullOrEmpty(ebTx.TransactionId))
            {
                _logger.LogWarning(
                    "Enable Banking transaction with no transaction_id on account {Uid}, skipping.", accountUid);
                skipped++;
                continue;
            }

            if (existingExternalIds.Contains(ebTx.TransactionId))
            {
                skipped++;
                continue;
            }

            var store = BuildTransactionStore(ebTx, fireflyAccount);
            await _firefly.CreateTransactionAsync(store, cancellationToken);
            created++;
        }

        _logger.LogInformation(
            "Account {Name}: created {Created}, skipped {Skipped} transactions.",
            fireflyAccount.Attributes.Name, created, skipped);
    }

    private static TransactionStore BuildTransactionStore(
        EnableBanking.Models.Transaction ebTx,
        FireflyIii.Models.Account fireflyAccount)
    {
        var isCredit = string.Equals(ebTx.CreditDebitIndicator, "CRDT", StringComparison.OrdinalIgnoreCase);
        var type = isCredit ? "deposit" : "withdrawal";
        var date = ebTx.BookingDate ?? ebTx.ValueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var description = ebTx.RemittanceInformation?.FirstOrDefault()
            ?? ebTx.EntryReference
            ?? "(no description)";

        var split = new TransactionSplit(
            Type: type,
            Date: date,
            Amount: ebTx.TransactionAmount.Amount,
            Description: description,
            CurrencyCode: ebTx.TransactionAmount.Currency,
            ExternalId: ebTx.TransactionId!,
            SourceName: isCredit ? (ebTx.DebtorName ?? "Unknown") : fireflyAccount.Attributes.Name,
            DestinationName: isCredit ? fireflyAccount.Attributes.Name : (ebTx.CreditorName ?? "Unknown"));

        return new TransactionStore(
            ErrorIfDuplicateHash: false,
            Transactions: [split]);
    }
}
