using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.FireflyIii.Models;

public record AccountAttributes(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("iban")] string? Iban);

public record Account(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("attributes")] AccountAttributes Attributes);
