using EnableBankingUploader.Core.FireflyIii.Models;

namespace EnableBankingUploader.Core.FireflyIii;

public interface IFireflyIiiClient
{
    Task<IReadOnlyList<Account>> GetAssetAccountsAsync(CancellationToken cancellationToken = default);

    Task<DateOnly?> GetLatestTransactionDateAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        string accountId,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken = default);

    Task CreateTransactionAsync(TransactionStore transaction, CancellationToken cancellationToken = default);
}
