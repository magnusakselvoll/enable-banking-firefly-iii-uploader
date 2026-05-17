namespace EnableBankingUploader.Core.Sessions;

public record StoredSession(
    string SessionId,
    string AspspName,
    string AspspCountry,
    IReadOnlyList<string> AccountUids,
    DateTimeOffset ValidUntil,
    DateTimeOffset CreatedAt);
