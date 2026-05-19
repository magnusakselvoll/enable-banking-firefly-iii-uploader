using System.Collections.Concurrent;
using EnableBankingUploader.Core.Sync;

namespace EnableBankingUploader.Cli.Web;

public sealed class ManualSyncState
{
    private readonly ConcurrentDictionary<string, (SyncPlan Plan, DateTimeOffset CreatedAt)> _pending = new();

    public string Add(SyncPlan plan)
    {
        PurgeExpired();
        var token = Guid.NewGuid().ToString("N");
        _pending[token] = (plan, DateTimeOffset.UtcNow);
        return token;
    }

    public SyncPlan? TakeIfValid(string token)
    {
        if (_pending.TryRemove(token, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.CreatedAt < TimeSpan.FromMinutes(15))
                return entry.Plan;
        }
        return null;
    }

    private void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryGetValue(key, out var entry) && entry.CreatedAt < cutoff)
                _pending.TryRemove(key, out _);
        }
    }
}
