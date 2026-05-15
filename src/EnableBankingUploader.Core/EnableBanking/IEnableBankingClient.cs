using EnableBankingUploader.Core.EnableBanking.Models;

namespace EnableBankingUploader.Core.EnableBanking;

public interface IEnableBankingClient
{
    Task<IReadOnlyList<Session>> GetSessionsAsync(CancellationToken cancellationToken = default);

    Task<Account> GetAccountAsync(string accountUid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        string accountUid,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken = default);
}
