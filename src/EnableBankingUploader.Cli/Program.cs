using EnableBankingUploader.Core;
using EnableBankingUploader.Cli;
using EnableBankingUploader.Cli.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

var listenUrl = builder.Configuration.GetSection("EnableBankingUploader")["WebListenUrl"]
    ?? "http://0.0.0.0:8080";
builder.WebHost.UseUrls(listenUrl);

builder.Services.AddEnableBankingUploader(builder.Configuration);
builder.Services.AddHostedService<SyncScheduler>();
builder.Services.AddSingleton<BankRegistrationState>();

var app = builder.Build();

BankRegistrationEndpoints.Map(app);

await app.RunAsync();
