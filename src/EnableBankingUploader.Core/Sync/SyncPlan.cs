namespace EnableBankingUploader.Core.Sync;

public sealed class SyncPlan
{
    public required string RunLabel { get; init; }
    public IReadOnlyList<AccountSyncPlan> Accounts { get; set; } = [];
    public int ValidSessions { get; set; }
    public int ExpiredSessions { get; set; }
    public int SessionFetchErrors { get; set; }
}
