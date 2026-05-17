namespace EnableBankingUploader.Core.Sessions;

public interface ISessionStore
{
    Task<IReadOnlyList<StoredSession>> ListAsync(CancellationToken cancellationToken = default);
    Task<StoredSession?> GetAsync(string sessionId, CancellationToken cancellationToken = default);
    Task SaveAsync(StoredSession session, CancellationToken cancellationToken = default);
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}
