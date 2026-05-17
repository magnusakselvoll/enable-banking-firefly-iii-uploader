using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.FireflyIii.Models;
using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnableBankingUploader.Core.Sync;

public sealed class TransactionSyncer
{
    private const int DefaultLookbackDays = 90;

    private readonly IEnableBankingClient _enableBanking;
    private readonly IFireflyIiiClient _firefly;
    private readonly ISessionStore _sessionStore;
    private readonly AccountMatcher _accountMatcher;
    private readonly SyncOptions _options;
    private readonly ILogger<TransactionSyncer> _logger;

    public TransactionSyncer(
        IEnableBankingClient enableBanking,
        IFireflyIiiClient firefly,
        ISessionStore sessionStore,
        AccountMatcher accountMatcher,
        IOptions<SyncOptions> options,
        ILogger<TransactionSyncer> logger)
    {
        _enableBanking = enableBanking;
        _firefly = firefly;
        _sessionStore = sessionStore;
        _accountMatcher = accountMatcher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var allSessions = await _sessionStore.ListAsync(cancellationToken);
        if (allSessions.Count == 0)
        {
            _logger.LogWarning("No bank sessions registered. Register banks via the web UI.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var validSessions = allSessions.Where(s => s.ValidUntil >= now).ToList();

        foreach (var expired in allSessions.Where(s => s.ValidUntil < now))
        {
            _logger.LogWarning(
                "Session for {Bank} expired at {ValidUntil}. Re-authorize via the web UI.",
                expired.AspspName, expired.ValidUntil);
        }

        if (validSessions.Count == 0)
        {
            _logger.LogWarning("All registered sessions are expired. Re-authorize banks via the web UI.");
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (_options.WhatIf && !_options.HasFireflyIiiUrl)
        {
            _logger.LogInformation("[WHATIF] No Firefly III URL configured — running offline preview for {Count} session(s). Nothing will be written.", validSessions.Count);
            foreach (var stored in validSessions)
                await OfflineWhatIfSessionAsync(stored.SessionId, today, cancellationToken);
            _logger.LogInformation("[WHATIF] Offline preview complete.");
            return;
        }

        if (_options.WhatIf)
            _logger.LogInformation("[WHATIF] Connected preview for {Count} session(s) — reading from Firefly III, nothing will be written.", validSessions.Count);
        else
            _logger.LogInformation("Starting transaction sync for {Count} session(s).", validSessions.Count);

        var fireflyAccounts = await _firefly.GetAssetAccountsAsync(cancellationToken);

        foreach (var stored in validSessions)
        {
            await SyncSessionAsync(stored.SessionId, fireflyAccounts, today, cancellationToken);
        }

        _logger.LogInformation(_options.WhatIf ? "[WHATIF] Connected preview complete." : "Transaction sync completed.");
    }

    private async Task OfflineWhatIfSessionAsync(string sessionId, DateOnly today, CancellationToken ct)
    {
        EnableBanking.Models.Session session;
        try
        {
            session = await _enableBanking.GetSessionAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WHATIF] Failed to fetch session {SessionId}.", sessionId);
            return;
        }

        foreach (var accountUid in session.AccountUids)
            await OfflineWhatIfAccountAsync(accountUid, today, ct);
    }

    private async Task OfflineWhatIfAccountAsync(string accountUid, DateOnly today, CancellationToken ct)
    {
        EnableBanking.Models.Account ebAccount;
        try
        {
            ebAccount = await _enableBanking.GetAccountAsync(accountUid, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WHATIF] Failed to fetch details for account {Uid}, skipping.", accountUid);
            return;
        }

        var dateFrom = new DateOnly(2000, 1, 1); // fetch all available history in offline preview mode
        _logger.LogInformation("[WHATIF] Account {Uid} IBAN={Iban} — fetching {DateFrom} to {DateTo}.",
            accountUid, ebAccount.AccountId?.Iban, dateFrom, today);

        var transactions = await _enableBanking.GetTransactionsAsync(accountUid, dateFrom, today, ct);
        var booked = transactions
            .Where(t => string.Equals(t.Status, "BOOK", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var tx in booked)
        {
            var dir = string.Equals(tx.CreditDebitIndicator, "CRDT", StringComparison.OrdinalIgnoreCase) ? "CREDIT" : "DEBIT";
            var desc = tx.RemittanceInformation?.FirstOrDefault() ?? tx.EntryReference ?? "(no description)";
            _logger.LogInformation("[WHATIF]   {Date} {Dir} {Amount} {Currency} — {Desc}",
                tx.BookingDate ?? tx.ValueDate, dir, tx.TransactionAmount.Amount, tx.TransactionAmount.Currency, desc);
        }

        _logger.LogInformation("[WHATIF] Found {Count} booked transaction(s) for account {Uid}.", booked.Count, accountUid);
    }

    private async Task SyncSessionAsync(
        string sessionId,
        IReadOnlyList<FireflyIii.Models.Account> fireflyAccounts,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        EnableBanking.Models.Session session;
        try
        {
            session = await _enableBanking.GetSessionAsync(sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch session {SessionId}. It may be expired — re-authorize via the web UI.", sessionId);
            return;
        }

        foreach (var accountUid in session.AccountUids)
        {
            await SyncAccountAsync(accountUid, fireflyAccounts, today, cancellationToken);
        }
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

        if (_options.WhatIf)
            _logger.LogInformation(
                "[WHATIF] Account mapping: Enable Banking {Uid} (IBAN {Iban}) -> Firefly III {FfId} ({FfName}).",
                accountUid, ebAccount.AccountId?.Iban, fireflyAccount.Id, fireflyAccount.Attributes.Name);

        var latestDate = await _firefly.GetLatestTransactionDateAsync(fireflyAccount.Id, cancellationToken);
        var dateFrom = latestDate.HasValue
            ? latestDate.Value.AddDays(-_options.LookbackDays)
            : today.AddDays(-DefaultLookbackDays);

        _logger.LogInformation(
            "Syncing {Name} (IBAN: {Iban}) from {DateFrom} to {DateTo}.",
            fireflyAccount.Attributes.Name,
            ebAccount.AccountId?.Iban,
            dateFrom,
            today);

        if (_options.WhatIf)
            _logger.LogInformation(
                "[WHATIF] {Name}: computed cutoff date range {DateFrom} to {DateTo} (latest Firefly date: {Latest}).",
                fireflyAccount.Attributes.Name, dateFrom, today,
                latestDate.HasValue ? latestDate.Value.ToString() : "none");

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
            // Only deduplicate booked transactions; pending IDs may be unstable
            if (!string.Equals(ebTx.Status, "BOOK", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping non-booked transaction (status={Status}).", ebTx.Status);
                skipped++;
                continue;
            }

            // entry_reference is the stable dedup key for booked transactions
            var externalId = ebTx.EntryReference ?? ebTx.TransactionId;
            if (string.IsNullOrEmpty(externalId))
            {
                _logger.LogWarning("Booked transaction has no entry_reference or transaction_id on account {Uid}, skipping.", accountUid);
                skipped++;
                continue;
            }

            var desc = ebTx.RemittanceInformation?.FirstOrDefault() ?? ebTx.EntryReference ?? "(no description)";
            var txDate = ebTx.BookingDate ?? ebTx.ValueDate;

            if (existingExternalIds.Contains(externalId))
            {
                if (_options.WhatIf)
                    _logger.LogInformation(
                        "[WHATIF] SKIP DUPLICATE: external_id={ExternalId} date={Date} amount={Amount} {Currency} — {Desc}",
                        externalId, txDate, ebTx.TransactionAmount.Amount, ebTx.TransactionAmount.Currency, desc);
                skipped++;
                continue;
            }

            var store = BuildTransactionStore(ebTx, externalId, fireflyAccount);

            if (_options.WhatIf)
            {
                _logger.LogInformation(
                    "[WHATIF] WOULD IMPORT: external_id={ExternalId} date={Date} amount={Amount} {Currency} — {Desc}",
                    externalId, txDate, ebTx.TransactionAmount.Amount, ebTx.TransactionAmount.Currency, desc);
                created++;
                continue;
            }

            await _firefly.CreateTransactionAsync(store, cancellationToken);
            created++;
        }

        _logger.LogInformation(
            _options.WhatIf
                ? "[WHATIF] {Name}: would create {Created}, would skip {Skipped} transactions."
                : "{Name}: created {Created}, skipped {Skipped} transactions.",
            fireflyAccount.Attributes.Name, created, skipped);
    }

    private static TransactionStore BuildTransactionStore(
        EnableBanking.Models.Transaction ebTx,
        string externalId,
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
            ExternalId: externalId,
            SourceName: isCredit ? (ebTx.DebtorName ?? "Unknown") : fireflyAccount.Attributes.Name,
            DestinationName: isCredit ? fireflyAccount.Attributes.Name : (ebTx.CreditorName ?? "Unknown"));

        return new TransactionStore(
            ErrorIfDuplicateHash: false,
            Transactions: [split]);
    }
}
