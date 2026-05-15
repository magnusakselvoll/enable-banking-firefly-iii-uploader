using EnableBankingUploader.Core.EnableBanking.Models;

namespace EnableBankingUploader.Core.EnableBanking;

public interface IEnableBankingClient
{
    Task<Session> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<Account> GetAccountAsync(string accountUid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        string accountUid,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken = default);
}
