namespace EnableBankingUploader.Core.Sync;

public sealed record AccountSyncPlan(
    string AccountUid,
    string BankName,
    string? Iban,
    string? FireflyAccountId,
    string? FireflyAccountName,
    DateOnly DateFrom,
    DateOnly DateTo,
    IReadOnlyList<PlannedTransaction> Transactions,
    bool Unmapped,
    bool FetchError);
