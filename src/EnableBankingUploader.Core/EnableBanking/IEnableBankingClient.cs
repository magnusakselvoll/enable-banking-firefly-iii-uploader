using EnableBankingUploader.Core.EnableBanking.Models;

namespace EnableBankingUploader.Core.EnableBanking;

public interface IEnableBankingClient
{
    Task<IReadOnlyList<Aspsp>> ListAspspsAsync(string? country = null, CancellationToken cancellationToken = default);

    Task<AuthResponse> StartAuthorizationAsync(AuthRequest request, CancellationToken cancellationToken = default);

    Task<AuthorizedSession> CreateSessionAsync(string code, CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<Session> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<Account> GetAccountAsync(string accountUid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        string accountUid,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken = default);
}
