using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.EnableBanking.Models;

public record AccountIdentification(
    [property: JsonPropertyName("iban")] string? Iban);

public record Account(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("account_id")] AccountIdentification? AccountId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("currency")] string? Currency);
