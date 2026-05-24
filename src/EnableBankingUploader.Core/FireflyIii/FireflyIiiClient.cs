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

    public async Task CreateTransactionAsync(TransactionStore transaction, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/v1/transactions", transaction, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Created transaction with external_id: {ExternalId}.",
            transaction.Transactions.FirstOrDefault()?.ExternalId);
    }

    public async Task UpdateTransactionAsync(string id, int journalId, DateOnly date, string? notes, CancellationToken cancellationToken = default)
    {
        var body = new TransactionUpdate([new TransactionSplitUpdate(journalId, date, notes)]);
        var response = await _httpClient.PutAsJsonAsync($"api/v1/transactions/{id}", body, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Updated transaction {Id} (journal {JournalId}) to date {Date}.", id, journalId, date);
    }
}
