using System.Collections.Concurrent;

namespace EnableBankingUploader.Cli.Web;

public sealed class BankRegistrationState
{
    private readonly ConcurrentDictionary<string, PendingAuth> _pending = new();

    public void Add(string state, PendingAuth auth) => _pending[state] = auth;

    public PendingAuth? TakeIfValid(string state)
    {
        if (_pending.TryRemove(state, out var auth))
        {
            // Discard if older than 15 minutes
            if (DateTimeOffset.UtcNow - auth.CreatedAt < TimeSpan.FromMinutes(15))
                return auth;
        }
        return null;
    }

    public void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryGetValue(key, out var auth) && auth.CreatedAt < cutoff)
                _pending.TryRemove(key, out _);
        }
    }
}

public sealed record PendingAuth(string AspspName, string AspspCountry, DateTimeOffset CreatedAt);
