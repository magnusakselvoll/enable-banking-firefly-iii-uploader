using Cronos;
using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnableBankingUploader.Cli;

internal sealed class SyncScheduler : BackgroundService
{
    private readonly TransactionSyncer _syncer;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncScheduler> _logger;

    public SyncScheduler(
        TransactionSyncer syncer,
        IOptions<SyncOptions> options,
        ILogger<SyncScheduler> logger)
    {
        _syncer = syncer;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var expression = CronExpression.Parse(_options.Schedule);

        _logger.LogInformation("Scheduler started. Schedule: {Schedule}", _options.Schedule);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = expression.GetNextOccurrence(now, TimeZoneInfo.Utc);
            if (next is null)
            {
                _logger.LogWarning("Cron expression produced no next occurrence; stopping.");
                break;
            }

            var delay = next.Value - now;
            _logger.LogInformation("Next sync scheduled at {Next} (in {Delay:hh\\:mm\\:ss}).", next.Value, delay);

            await Task.Delay(delay, stoppingToken);

            _logger.LogInformation("Starting scheduled sync.");
            try
            {
                await _syncer.SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync failed. Will retry at next scheduled time.");
            }
        }
    }
}
