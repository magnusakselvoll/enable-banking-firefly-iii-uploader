using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.FireflyIii.Models;

public record TransactionSplitUpdate(
    [property: JsonPropertyName("transaction_journal_id")] int TransactionJournalId,
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("notes")] string? Notes);

public record TransactionUpdate(
    [property: JsonPropertyName("transactions")] IReadOnlyList<TransactionSplitUpdate> Transactions);
