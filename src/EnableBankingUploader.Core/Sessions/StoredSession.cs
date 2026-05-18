namespace EnableBankingUploader.Core.Sessions;

public record StoredAccount(string Uid, string? Iban);

public record StoredSession(
    string SessionId,
    string AspspName,
    string AspspCountry,
    IReadOnlyList<string> AccountUids,
    DateTimeOffset ValidUntil,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StoredAccount>? Accounts = null);
