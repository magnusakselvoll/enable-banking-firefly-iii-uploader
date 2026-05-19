namespace EnableBankingUploader.Core.Sync;

public sealed record AccountSyncResult(
    string AccountUid,
    string BankName,
    string? Iban,
    string? FireflyAccountName,
    int Created,
    int SkippedDuplicate,
    int SkippedNonBooked,
    int SkippedNoId,
    int CreateErrors,
    bool Unmapped,
    bool FetchError);
