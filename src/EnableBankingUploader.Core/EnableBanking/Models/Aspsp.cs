using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.EnableBanking.Models;

public record Aspsp(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("maximum_consent_validity")] int? MaximumConsentValiditySeconds);

public record AspspsResponse(
    [property: JsonPropertyName("aspsps")] IReadOnlyList<Aspsp> Aspsps);
