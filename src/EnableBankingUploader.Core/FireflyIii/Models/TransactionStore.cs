using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.FireflyIii.Models;

public record TransactionSplit(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("currency_code")] string? CurrencyCode,
    [property: JsonPropertyName("external_id")] string ExternalId,
    [property: JsonPropertyName("source_name")] string? SourceName,
    [property: JsonPropertyName("destination_name")] string? DestinationName,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("notes")] string? Notes);

public record TransactionStore(
    [property: JsonPropertyName("error_if_duplicate_hash")] bool ErrorIfDuplicateHash,
    [property: JsonPropertyName("transactions")] IReadOnlyList<TransactionSplit> Transactions);
