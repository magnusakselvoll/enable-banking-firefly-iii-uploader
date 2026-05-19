using EnableBankingUploader.Core.FireflyIii.Models;

namespace EnableBankingUploader.Core.Sync;

public sealed record PlannedTransaction(
    SyncDecision Decision,
    DateOnly? Date,
    string Description,
    string Amount,
    string Currency,
    string Direction,
    string? ExternalId,
    TransactionStore? Store);
