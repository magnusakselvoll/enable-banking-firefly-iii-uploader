using System.Text.Json;
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
    private readonly SyncGate _gate;
    private readonly SyncOptions _options;
    private readonly ILogger<TransactionSyncer> _logger;

    public TransactionSyncer(
        IEnableBankingClient enableBanking,
        IFireflyIiiClient firefly,
        ISessionStore sessionStore,
        AccountMatcher accountMatcher,
        SyncGate gate,
        IOptions<SyncOptions> options,
        ILogger<TransactionSyncer> logger)
    {
        _enableBanking = enableBanking;
        _firefly = firefly;
        _sessionStore = sessionStore;
        _accountMatcher = accountMatcher;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    // Automatic entrypoint used by SyncScheduler.
    public async Task<SyncSummary> SyncAsync(CancellationToken cancellationToken = default)
    {
        var plan = await BuildPlanAsync(null, cancellationToken);
        return await ExecutePlanAsync(plan, cancellationToken);
    }

    // Compute what would be synced for the given account UIDs (null = all valid sessions).
    // Makes Enable Banking and read-only Firefly calls. Never writes.
    public async Task<SyncPlan> BuildPlanAsync(
        IReadOnlySet<string>? accountUidFilter,
        CancellationToken cancellationToken = default)
    {
        var runLabel = $"eb-sync-{DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'")}";
        var plan = new SyncPlan
        {
            RunLabel = runLabel,
            Accounts = [],
        };

        var allSessions = await _sessionStore.ListAsync(cancellationToken);
        if (allSessions.Count == 0)
        {
            _logger.LogWarning("No bank sessions registered. Register banks via the web UI.");
            return plan;
        }

        var now = DateTimeOffset.UtcNow;
        var validSessions = allSessions.Where(s => s.ValidUntil >= now).ToList();

        foreach (var expired in allSessions.Where(s => s.ValidUntil < now))
        {
            _logger.LogWarning(
                "Session for {Bank} expired at {ValidUntil}. Re-authorize via the web UI.",
                expired.AspspName, expired.ValidUntil);
            plan.ExpiredSessions++;
        }

        plan.ValidSessions = validSessions.Count;

        if (validSessions.Count == 0)
        {
            _logger.LogWarning("All registered sessions are expired. Re-authorize banks via the web UI.");
            return plan;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fireflyAccounts = await _firefly.GetAssetAccountsAsync(cancellationToken);
        var accountPlans = new List<AccountSyncPlan>();

        foreach (var stored in validSessions)
        {
            EnableBanking.Models.Session session;
            try
            {
                session = await _enableBanking.GetSessionAsync(stored.SessionId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch session {SessionId}. It may be expired — re-authorize via the web UI.", stored.SessionId);
                plan.SessionFetchErrors++;
                continue;
            }

            foreach (var accountUid in session.AccountUids)
            {
                if (accountUidFilter is not null && !accountUidFilter.Contains(accountUid))
                    continue;

                var accountPlan = await BuildAccountPlanAsync(
                    accountUid, stored.AspspName, fireflyAccounts, today, runLabel, cancellationToken);
                accountPlans.Add(accountPlan);
            }
        }

        plan.Accounts = accountPlans;
        return plan;
    }

    // Execute a computed plan: write Create transactions to Firefly.
    // Acquires the sync gate so no concurrent sync (manual or scheduled) can overlap.
    public async Task<SyncSummary> ExecutePlanAsync(
        SyncPlan plan,
        CancellationToken cancellationToken = default)
    {
        var summary = new SyncSummary { RunLabel = plan.RunLabel };
        summary.ValidSessions = plan.ValidSessions;
        summary.ExpiredSessions = plan.ExpiredSessions;
        summary.SessionFetchErrors = plan.SessionFetchErrors;

        if (plan.Accounts.Count == 0 && plan.ValidSessions == 0)
            return summary;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var accountResults = new List<AccountSyncResult>();

            foreach (var accountPlan in plan.Accounts)
            {
                var result = await ExecuteAccountPlanAsync(accountPlan, cancellationToken);
                accountResults.Add(result);

                summary.Created += result.Created;
                summary.SkippedDuplicate += result.SkippedDuplicate;
                summary.SkippedNonBooked += result.SkippedNonBooked;
                summary.SkippedNoId += result.SkippedNoId;
                if (result.FetchError)
                    summary.AccountFetchErrors++;
                else if (result.Unmapped)
                    summary.UnmappedAccounts++;
                else
                    summary.MappedAccounts++;
            }

            summary.Accounts = accountResults;
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation(
            "Run {RunLabel}: {Mapped} account(s) mapped, {Unmapped} unmapped, {FetchErrors} fetch error(s); " +
            "{Created} created, {Duplicate} duplicate(s) skipped, {NonBooked} non-booked skipped, {NoId} no-id skipped.",
            summary.RunLabel, summary.MappedAccounts, summary.UnmappedAccounts, summary.AccountFetchErrors,
            summary.Created, summary.SkippedDuplicate, summary.SkippedNonBooked, summary.SkippedNoId);

        return summary;
    }

    private async Task<AccountSyncPlan> BuildAccountPlanAsync(
        string accountUid,
        string bankName,
        IReadOnlyList<Account> fireflyAccounts,
        DateOnly today,
        string runLabel,
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
            return new AccountSyncPlan(accountUid, bankName, null, null, null,
                today, today, [], Unmapped: false, FetchError: true);
        }

        var iban = ebAccount.AccountId?.Iban;
        var matches = _accountMatcher.MatchAccounts([ebAccount], fireflyAccounts);
        if (matches.Count == 0)
        {
            return new AccountSyncPlan(accountUid, bankName, iban, null, null,
                today, today, [], Unmapped: true, FetchError: false);
        }

        var (_, fireflyAccount) = matches[0];
        var latestDate = await _firefly.GetLatestTransactionDateAsync(fireflyAccount.Id, cancellationToken);
        var dateFrom = latestDate.HasValue
            ? latestDate.Value.AddDays(-_options.LookbackDays)
            : today.AddDays(-DefaultLookbackDays);
        // If the latest stored transaction is future-dated (the date bug), extend the Firefly
        // query to cover it so those transactions appear in existingExternalIds for deduplication,
        // and to avoid the equal start==end date that causes Firefly to return HTML.
        var fireflyDateTo = latestDate.HasValue && latestDate.Value > today ? latestDate.Value : today;

        _logger.LogInformation(
            "Planning {Name} (IBAN: {Iban}) from {DateFrom} to {DateTo}.",
            fireflyAccount.Attributes.Name, iban, dateFrom, fireflyDateTo);

        var ebTransactions = await _enableBanking.GetTransactionsAsync(accountUid, dateFrom, today, cancellationToken);
        var existingTransactions = await _firefly.GetTransactionsAsync(fireflyAccount.Id, dateFrom, fireflyDateTo, cancellationToken);

        var existingExternalIds = existingTransactions
            .SelectMany(t => t.Attributes.Transactions)
            .Where(s => s.ExternalId is not null)
            .Select(s => s.ExternalId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var planned = new List<PlannedTransaction>();

        foreach (var ebTx in ebTransactions)
        {
            if (!string.Equals(ebTx.Status, "BOOK", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping non-booked transaction (status={Status}).", ebTx.Status);
                planned.Add(new PlannedTransaction(
                    SyncDecision.SkipNonBooked,
                    ActualDate(ebTx),
                    ebTx.RemittanceInformation?.FirstOrDefault() ?? "(no description)",
                    ebTx.TransactionAmount.Amount,
                    ebTx.TransactionAmount.Currency,
                    DirectionOf(ebTx.CreditDebitIndicator),
                    null, null));
                continue;
            }

            var externalId = ebTx.EntryReference ?? ebTx.TransactionId;
            if (string.IsNullOrEmpty(externalId))
            {
                _logger.LogWarning("Booked transaction has no entry_reference or transaction_id on account {Uid}, skipping.", accountUid);
                planned.Add(new PlannedTransaction(
                    SyncDecision.SkipNoId,
                    ActualDate(ebTx),
                    ebTx.RemittanceInformation?.FirstOrDefault() ?? "(no description)",
                    ebTx.TransactionAmount.Amount,
                    ebTx.TransactionAmount.Currency,
                    DirectionOf(ebTx.CreditDebitIndicator),
                    null, null));
                continue;
            }

            if (existingExternalIds.Contains(externalId))
            {
                planned.Add(new PlannedTransaction(
                    SyncDecision.SkipDuplicate,
                    ActualDate(ebTx),
                    ebTx.RemittanceInformation?.FirstOrDefault() ?? ebTx.EntryReference ?? "(no description)",
                    ebTx.TransactionAmount.Amount,
                    ebTx.TransactionAmount.Currency,
                    DirectionOf(ebTx.CreditDebitIndicator),
                    externalId, null));
                continue;
            }

            var store = BuildTransactionStore(ebTx, externalId, fireflyAccount, runLabel);
            planned.Add(new PlannedTransaction(
                SyncDecision.Create,
                ActualDate(ebTx),
                ebTx.RemittanceInformation?.FirstOrDefault() ?? ebTx.EntryReference ?? "(no description)",
                ebTx.TransactionAmount.Amount,
                ebTx.TransactionAmount.Currency,
                DirectionOf(ebTx.CreditDebitIndicator),
                externalId, store));
        }

        return new AccountSyncPlan(
            accountUid, bankName, iban,
            fireflyAccount.Id, fireflyAccount.Attributes.Name,
            dateFrom, today, planned,
            Unmapped: false, FetchError: false);
    }

    private async Task<AccountSyncResult> ExecuteAccountPlanAsync(
        AccountSyncPlan accountPlan,
        CancellationToken cancellationToken)
    {
        if (accountPlan.FetchError)
            return new AccountSyncResult(accountPlan.AccountUid, accountPlan.BankName, accountPlan.Iban,
                null, 0, 0, 0, 0, 0, Unmapped: false, FetchError: true);

        if (accountPlan.Unmapped)
            return new AccountSyncResult(accountPlan.AccountUid, accountPlan.BankName, accountPlan.Iban,
                null, 0, 0, 0, 0, 0, Unmapped: true, FetchError: false);

        var created = 0;
        var createErrors = 0;
        var skippedDuplicate = accountPlan.Transactions.Count(t => t.Decision == SyncDecision.SkipDuplicate);
        var skippedNonBooked = accountPlan.Transactions.Count(t => t.Decision == SyncDecision.SkipNonBooked);
        var skippedNoId = accountPlan.Transactions.Count(t => t.Decision == SyncDecision.SkipNoId);

        foreach (var tx in accountPlan.Transactions.Where(t => t.Decision == SyncDecision.Create))
        {
            try
            {
                await _firefly.CreateTransactionAsync(tx.Store!, cancellationToken);
                created++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create transaction {ExternalId} for account {Uid}.", tx.ExternalId, accountPlan.AccountUid);
                createErrors++;
            }
        }

        _logger.LogInformation(
            "{Name}: created {Created}, skipped {Skipped} transactions.",
            accountPlan.FireflyAccountName, created,
            skippedDuplicate + skippedNonBooked + skippedNoId);

        return new AccountSyncResult(
            accountPlan.AccountUid, accountPlan.BankName, accountPlan.Iban,
            accountPlan.FireflyAccountName,
            created, skippedDuplicate, skippedNonBooked, skippedNoId, createErrors,
            Unmapped: false, FetchError: false);
    }

    private static TransactionStore BuildTransactionStore(
        EnableBanking.Models.Transaction ebTx,
        string externalId,
        Account fireflyAccount,
        string runLabel)
    {
        var isCredit = string.Equals(ebTx.CreditDebitIndicator, "CRDT", StringComparison.OrdinalIgnoreCase);
        var type = isCredit ? "deposit" : "withdrawal";
        var date = ActualDate(ebTx) ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var description = ebTx.RemittanceInformation?.FirstOrDefault()
            ?? ebTx.EntryReference
            ?? "(no description)";

        var notes = BuildNotes(ebTx, descriptionUsesEntryReference: ebTx.RemittanceInformation is null or { Count: 0 });

        var split = new TransactionSplit(
            Type: type,
            Date: date,
            Amount: ebTx.TransactionAmount.Amount,
            Description: description,
            CurrencyCode: ebTx.TransactionAmount.Currency,
            ExternalId: externalId,
            SourceName: isCredit ? (ebTx.DebtorName ?? "Unknown") : fireflyAccount.Attributes.Name,
            DestinationName: isCredit ? fireflyAccount.Attributes.Name : (ebTx.CreditorName ?? "Unknown"),
            Tags: [runLabel],
            Notes: notes);

        return new TransactionStore(
            ErrorIfDuplicateHash: false,
            Transactions: [split]);
    }

    public async Task<RepairPlan> BuildRepairPlanAsync(
        IReadOnlySet<string>? accountUidFilter,
        DateOnly startDate,
        CancellationToken cancellationToken = default)
    {
        var allSessions = await _sessionStore.ListAsync(cancellationToken);
        var validSessions = allSessions.Where(s => s.ValidUntil >= DateTimeOffset.UtcNow).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fireflyEnd = today.AddDays(180);
        var fireflyAccounts = await _firefly.GetAssetAccountsAsync(cancellationToken);
        var changes = new List<RepairChange>();

        foreach (var stored in validSessions)
        {
            EnableBanking.Models.Session session;
            try
            {
                session = await _enableBanking.GetSessionAsync(stored.SessionId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch session {SessionId} during repair plan build.", stored.SessionId);
                continue;
            }

            foreach (var accountUid in session.AccountUids)
            {
                if (accountUidFilter is not null && !accountUidFilter.Contains(accountUid))
                    continue;

                EnableBanking.Models.Account ebAccount;
                try
                {
                    ebAccount = await _enableBanking.GetAccountAsync(accountUid, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch Enable Banking account {Uid} during repair plan build.", accountUid);
                    continue;
                }

                var matches = _accountMatcher.MatchAccounts([ebAccount], fireflyAccounts);
                if (matches.Count == 0)
                    continue;

                var (_, fireflyAccount) = matches[0];
                var accountChanges = await BuildAccountRepairChangesAsync(
                    accountUid, fireflyAccount, startDate, fireflyEnd, cancellationToken);
                changes.AddRange(accountChanges);
            }
        }

        return new RepairPlan { StartDate = startDate, Changes = changes };
    }

    public async Task<RepairSummary> ExecuteRepairPlanAsync(
        RepairPlan plan,
        CancellationToken cancellationToken = default)
    {
        var summary = new RepairSummary { StartDate = plan.StartDate };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var change in plan.Changes)
            {
                try
                {
                    await _firefly.UpdateTransactionAsync(
                        change.FireflyTransactionId,
                        change.TransactionJournalId,
                        change.NewDate,
                        change.NewNotes,
                        cancellationToken);
                    summary.Updated++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update transaction {ExternalId} (Firefly ID: {Id}).",
                        change.ExternalId, change.FireflyTransactionId);
                    summary.Errors++;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Repair: {Updated} transaction(s) updated, {Errors} error(s).",
            summary.Updated, summary.Errors);
        return summary;
    }

    private async Task<IReadOnlyList<RepairChange>> BuildAccountRepairChangesAsync(
        string accountUid,
        Account fireflyAccount,
        DateOnly startDate,
        DateOnly fireflyEnd,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Building repair plan for {Name} from {StartDate}.",
            fireflyAccount.Attributes.Name, startDate);

        var ebTransactions = await _enableBanking.GetTransactionsAsync(accountUid, startDate, fireflyEnd, cancellationToken);

        var ebMap = new Dictionary<string, (DateOnly Date, string? Notes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ebTx in ebTransactions)
        {
            var actualDate = ActualDate(ebTx);
            if (actualDate is null || actualDate.Value < startDate) continue;
            var externalId = ebTx.EntryReference ?? ebTx.TransactionId;
            if (string.IsNullOrEmpty(externalId)) continue;
            var richNotes = BuildNotes(ebTx, descriptionUsesEntryReference: ebTx.RemittanceInformation is null or { Count: 0 });
            ebMap[externalId] = (actualDate.Value, richNotes);
        }

        var ffTransactions = await _firefly.GetTransactionsAsync(fireflyAccount.Id, startDate, fireflyEnd, cancellationToken);

        var changes = new List<RepairChange>();
        foreach (var ffTxGroup in ffTransactions)
        foreach (var split in ffTxGroup.Attributes.Transactions)
        {
            if (string.IsNullOrEmpty(split.ExternalId)) continue;
            if (string.IsNullOrEmpty(split.TransactionJournalId)) continue;
            if (!ebMap.TryGetValue(split.ExternalId, out var correction)) continue;
            if (split.Date == correction.Date) continue;

            changes.Add(new RepairChange(
                FireflyTransactionId: ffTxGroup.Id,
                TransactionJournalId: split.TransactionJournalId,
                ExternalId: split.ExternalId,
                Description: split.Description ?? "(no description)",
                FireflyAccountName: fireflyAccount.Attributes.Name,
                OldDate: split.Date,
                NewDate: correction.Date,
                NewNotes: correction.Notes));
        }

        return changes;
    }

    private static string? BuildNotes(EnableBanking.Models.Transaction ebTx, bool descriptionUsesEntryReference)
    {
        var sb = new System.Text.StringBuilder();

        if (ebTx.RemittanceInformation is { Count: > 0 })
        {
            sb.AppendLine("Remittance information:");
            foreach (var line in ebTx.RemittanceInformation)
                sb.AppendLine(line);
        }

        var extras = new List<string>();
        if (!descriptionUsesEntryReference && !string.IsNullOrEmpty(ebTx.EntryReference))
            extras.Add($"Entry reference: {ebTx.EntryReference}");
        if (!string.IsNullOrEmpty(ebTx.TransactionId))
            extras.Add($"Transaction ID: {ebTx.TransactionId}");
        if (!string.IsNullOrEmpty(ebTx.CreditorName))
            extras.Add($"Creditor: {ebTx.CreditorName}");
        if (!string.IsNullOrEmpty(ebTx.DebtorName))
            extras.Add($"Debtor: {ebTx.DebtorName}");

        if (extras.Count > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            foreach (var line in extras)
                sb.AppendLine(line);
        }

        var ebData = new List<string>();
        if (ebTx.TransactionDate.HasValue)
            ebData.Add($"transaction_date: {ebTx.TransactionDate.Value:yyyy-MM-dd}");
        if (ebTx.BookingDate.HasValue)
            ebData.Add($"booking_date: {ebTx.BookingDate.Value:yyyy-MM-dd}");
        if (ebTx.ValueDate.HasValue)
            ebData.Add($"value_date: {ebTx.ValueDate.Value:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(ebTx.Status))
            ebData.Add($"status: {ebTx.Status}");
        if (!string.IsNullOrEmpty(ebTx.CreditDebitIndicator))
            ebData.Add($"credit_debit_indicator: {ebTx.CreditDebitIndicator}");
        if (ebTx.AdditionalData is not null)
        {
            foreach (var (key, value) in ebTx.AdditionalData)
                ebData.Add($"{key}: {value}");
        }

        if (ebData.Count > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine("Enable Banking data:");
            foreach (var line in ebData)
                sb.AppendLine(line);
        }

        var result = sb.ToString().TrimEnd();
        return result.Length > 0 ? result : null;
    }

    private static DateOnly? ActualDate(EnableBanking.Models.Transaction ebTx) =>
        ebTx.TransactionDate ?? ebTx.BookingDate ?? ebTx.ValueDate;

    private static string DirectionOf(string? creditDebitIndicator) =>
        string.Equals(creditDebitIndicator, "CRDT", StringComparison.OrdinalIgnoreCase) ? "CREDIT" : "DEBIT";
}
