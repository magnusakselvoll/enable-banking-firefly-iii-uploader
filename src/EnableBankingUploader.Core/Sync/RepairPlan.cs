namespace EnableBankingUploader.Core.Sync;

public sealed class RepairPlan
{
    public DateOnly StartDate { get; init; }
    public IReadOnlyList<RepairChange> Changes { get; init; } = [];
}

public sealed record RepairChange(
    string FireflyTransactionId,
    string TransactionJournalId,
    string ExternalId,
    string Description,
    string FireflyAccountName,
    DateOnly OldDate,
    DateOnly NewDate,
    string? NewNotes);

public sealed class RepairSummary
{
    public DateOnly StartDate { get; init; }
    public int Updated { get; set; }
    public int Errors { get; set; }
}
