namespace EnableBankingUploader.Core.Sync;

public sealed class SyncSummary
{
    public int ValidSessions { get; set; }
    public int ExpiredSessions { get; set; }
    public int SessionFetchErrors { get; set; }
    public int MappedAccounts { get; set; }
    public int UnmappedAccounts { get; set; }
    public int AccountFetchErrors { get; set; }
    public int Created { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedNonBooked { get; set; }
    public int SkippedNoId { get; set; }

    // Offline WhatIf only (no Firefly mapping or dedup)
    public int OfflineAccounts { get; set; }
    public int OfflineBookedFound { get; set; }
}
