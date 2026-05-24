using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.FireflyIii.Models;

public record TransactionSplitAttributes(
    [property: JsonPropertyName("external_id")] string? ExternalId,
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("transaction_journal_id")] string? TransactionJournalId = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public record TransactionGroupAttributes(
    [property: JsonPropertyName("transactions")] IReadOnlyList<TransactionSplitAttributes> Transactions);

public record Transaction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("attributes")] TransactionGroupAttributes Attributes);
