using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.EnableBanking.Models;

public record TransactionsResponse(
    [property: JsonPropertyName("transactions")] IReadOnlyList<Transaction> Transactions,
    [property: JsonPropertyName("continuation_key")] string? ContinuationKey);
