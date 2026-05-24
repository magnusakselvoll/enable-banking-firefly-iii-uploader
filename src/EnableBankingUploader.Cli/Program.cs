using EnableBankingUploader.Core;
using EnableBankingUploader.Cli;
using EnableBankingUploader.Cli.Web;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(o =>
    {
        o.IncludeScopes = true;
        o.SingleLine = true;
    });
}
else
{
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "o";
    });
}

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

var listenUrl = builder.Configuration.GetSection("EnableBankingUploader")["WebListenUrl"]
    ?? "http://0.0.0.0:8080";
builder.WebHost.UseUrls(listenUrl);

builder.Services.AddEnableBankingUploader(builder.Configuration);
builder.Services.AddHostedService<SyncScheduler>();
builder.Services.AddSingleton<BankRegistrationState>();
builder.Services.AddSingleton<ManualSyncState>();

var app = builder.Build();

BankRegistrationEndpoints.Map(app);
ManualSyncEndpoints.Map(app);

await app.RunAsync();
