using System.Net.Http.Json;
using System.Text.Json;
using EnableBankingUploader.Core.EnableBanking.Models;
using Microsoft.Extensions.Logging;

namespace EnableBankingUploader.Core.EnableBanking;

public sealed class EnableBankingClient : IEnableBankingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<EnableBankingClient> _logger;

    public EnableBankingClient(HttpClient httpClient, ILogger<EnableBankingClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Aspsp>> ListAspspsAsync(
        string? country = null,
        CancellationToken cancellationToken = default)
    {
        var url = country is null ? "aspsps" : $"aspsps?country={Uri.EscapeDataString(country)}";
        var response = await _httpClient.GetFromJsonAsync<AspspsResponse>(url, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Enable Banking returned null response for aspsps.");
        return response.Aspsps;
    }

    public async Task<AuthResponse> StartAuthorizationAsync(
        AuthRequest request,
        CancellationToken cancellationToken = default)
    {
        var httpResponse = await _httpClient.PostAsJsonAsync("auth", request, JsonOptions, cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
        {
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Enable Banking POST /auth returned {(int)httpResponse.StatusCode}: {body}");
        }
        return await httpResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Enable Banking returned null response for POST /auth.");
    }

    public async Task<AuthorizedSession> CreateSessionAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var httpResponse = await _httpClient.PostAsJsonAsync(
            "sessions", new CreateSessionRequest(code), JsonOptions, cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
        {
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Enable Banking POST /sessions returned {(int)httpResponse.StatusCode}: {body}");
        }
        return await httpResponse.Content.ReadFromJsonAsync<AuthorizedSession>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Enable Banking returned null response for POST /sessions.");
    }

    public async Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.DeleteAsync($"sessions/{sessionId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Revoking session {SessionId} returned {Status}.", sessionId, (int)response.StatusCode);
    }

    public async Task<Session> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<Session>(
            $"sessions/{sessionId}", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Enable Banking returned null response for session {sessionId}.");
    }

    public async Task<Account> GetAccountAsync(string accountUid, CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<Account>(
            $"accounts/{accountUid}/details", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Enable Banking returned null response for account {accountUid}.");
    }

    public async Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        string accountUid,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken = default)
    {
        var all = new List<Transaction>();
        string? continuationKey = null;

        do
        {
            var url = BuildTransactionsUrl(accountUid, dateFrom, dateTo, continuationKey);
            var page = await _httpClient.GetFromJsonAsync<TransactionsResponse>(url, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"Enable Banking returned null transactions for account {accountUid}.");

            all.AddRange(page.Transactions);
            continuationKey = page.ContinuationKey;

            _logger.LogDebug("Fetched {Count} transactions (continuation_key: {Key}).",
                page.Transactions.Count, continuationKey ?? "none");
        }
        while (continuationKey is not null);

        return all;
    }

    private static string BuildTransactionsUrl(
        string accountUid, DateOnly dateFrom, DateOnly dateTo, string? continuationKey)
    {
        var url = $"accounts/{accountUid}/transactions?date_from={dateFrom:yyyy-MM-dd}&date_to={dateTo:yyyy-MM-dd}";
        if (continuationKey is not null)
            url += $"&continuation_key={Uri.EscapeDataString(continuationKey)}";
        return url;
    }
}
