using System.Net.Http.Json;
using System.Text.Json;
using EnableBankingUploader.Core.FireflyIii.Models;
using Microsoft.Extensions.Logging;

namespace EnableBankingUploader.Core.FireflyIii;

public sealed class FireflyIiiClient : IFireflyIiiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new LenientDateOnlyConverter() },
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<FireflyIiiClient> _logger;

    public FireflyIiiClient(HttpClient httpClient, ILogger<FireflyIiiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Account>> GetAssetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<Account>();
        var page = 1;

        while (true)
        {
            var response = await _httpClient.GetFromJsonAsync<PaginatedResponse<Account>>(
                $"api/v1/accounts?type=asset&page={page}", JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("Firefly III returned null accounts response.");

            all.AddRange(response.Data);

            if (response.Meta.Pagination.CurrentPage >= response.Meta.Pagination.TotalPages)
                break;

            page++;
        }

        _logger.LogInformation("Retrieved {Count} asset accounts from Firefly III.", all.Count);
        return all;
    }

    public async Task<DateOnly?> GetLatestTransactionDateAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<PaginatedResponse<Transaction>>(
            $"api/v1/accounts/{accountId}/transactions?limit=1&page=1", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Firefly III returned null transactions for account {accountId}.");

        var first = response.Data.FirstOrDefault();
        if (first is null)
            return null;

        return first.Attributes.Transactions.FirstOrDefault()?.Date;
    }

    public async Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        string accountId,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken = default)
    {
        var all = new List<Transaction>();
        var page = 1;

        while (true)
        {
            var response = await _httpClient.GetFromJsonAsync<PaginatedResponse<Transaction>>(
                $"api/v1/accounts/{accountId}/transactions?start={dateFrom:yyyy-MM-dd}&end={dateTo:yyyy-MM-dd}&page={page}",
                JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"Firefly III returned null transactions for account {accountId}.");

            all.AddRange(response.Data);

            if (response.Meta.Pagination.CurrentPage >= response.Meta.Pagination.TotalPages)
                break;

            page++;
        }

        return all;
    }

    public async Task<bool> ExistsByExternalIdAsync(
        string externalId,
        CancellationToken cancellationToken = default)
    {
        // Firefly's search endpoint backstops the date-window dedup: a transaction whose
        // value_date precedes its booking_date can be stored outside the date-window query's
        // range while Enable Banking keeps re-fetching it, so we confirm existence directly.
        var query = Uri.EscapeDataString($"external_id_is:\"{externalId}\"");
        var response = await _httpClient.GetFromJsonAsync<PaginatedResponse<Transaction>>(
            $"api/v1/search/transactions?query={query}&page=1", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Firefly III returned null search response.");

        // Confirm an exact external_id match client-side so the result is robust regardless of
        // the Firefly version's operator semantics (e.g. partial vs exact matching).
        var exists = response.Data
            .SelectMany(t => t.Attributes.Transactions)
            .Any(s => string.Equals(s.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));

        if (exists)
            _logger.LogDebug("Found existing transaction via external_id search: {ExternalId}.", externalId);

        return exists;
    }

    public async Task CreateTransactionAsync(TransactionStore transaction, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/v1/transactions", transaction, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Created transaction with external_id: {ExternalId}.",
            transaction.Transactions.FirstOrDefault()?.ExternalId);
    }

}
