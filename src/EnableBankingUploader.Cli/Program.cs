using EnableBankingUploader.Core;
using EnableBankingUploader.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});
services.AddEnableBankingUploader(configuration);
services.AddTransient<SyncScheduler>();

var provider = services.BuildServiceProvider();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var scheduler = provider.GetRequiredService<SyncScheduler>();
await scheduler.RunAsync(cts.Token);
