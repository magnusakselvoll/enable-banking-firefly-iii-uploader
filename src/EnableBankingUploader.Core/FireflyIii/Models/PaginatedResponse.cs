using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.FireflyIii.Models;

public record Pagination(
    [property: JsonPropertyName("current_page")] int CurrentPage,
    [property: JsonPropertyName("total_pages")] int TotalPages);

public record PaginationMeta(
    [property: JsonPropertyName("pagination")] Pagination Pagination);

public record PaginatedResponse<T>(
    [property: JsonPropertyName("data")] IReadOnlyList<T> Data,
    [property: JsonPropertyName("meta")] PaginationMeta Meta);
