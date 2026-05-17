using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EnableBankingUploader.Core.Options;

namespace EnableBankingUploader.Core.Sessions;

public sealed class FileSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directory;
    private readonly ILogger<FileSessionStore> _logger;

    public FileSessionStore(IOptions<SyncOptions> options, ILogger<FileSessionStore> logger)
    {
        _directory = options.Value.SessionStorePath;
        _logger = logger;
    }

    public Task<IReadOnlyList<StoredSession>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
            return Task.FromResult<IReadOnlyList<StoredSession>>([]);

        var sessions = new List<StoredSession>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var session = TryReadFile(file);
            if (session is not null)
                sessions.Add(session);
        }
        return Task.FromResult<IReadOnlyList<StoredSession>>(sessions);
    }

    public Task<StoredSession?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionId);
        return Task.FromResult(File.Exists(path) ? TryReadFile(path) : null);
    }

    public async Task SaveAsync(StoredSession session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var path = GetPath(session.SessionId);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(session, JsonOptions), cancellationToken);
        File.Move(tmp, path, overwrite: true);
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private StoredSession? TryReadFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StoredSession>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping malformed session file {Path}.", path);
            return null;
        }
    }

    private string GetPath(string sessionId)
    {
        var safeName = Path.GetFileName(sessionId);
        if (string.IsNullOrEmpty(safeName) || safeName != sessionId)
            throw new ArgumentException($"Invalid session ID: {sessionId}", nameof(sessionId));
        return Path.Combine(_directory, safeName + ".json");
    }
}
