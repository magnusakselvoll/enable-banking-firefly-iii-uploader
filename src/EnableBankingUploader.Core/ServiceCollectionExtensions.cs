using System.Net;
using System.Net.Http.Headers;
using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sessions;
using EnableBankingUploader.Core.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace EnableBankingUploader.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEnableBankingUploader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SyncOptions>(configuration.GetSection("EnableBankingUploader"));

        services.AddTransient<EnableBankingJwtHandler>();

        services.AddHttpClient<IEnableBankingClient, EnableBankingClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.enablebanking.com/");
        })
        .AddHttpMessageHandler<EnableBankingJwtHandler>()
        .AddResilienceHandler("enablebanking", ConfigureResiliencePipeline);

        services.AddHttpClient<IFireflyIiiClient, FireflyIiiClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<SyncOptions>>().Value;
            if (opts.HasFireflyIiiUrl)
            {
                client.BaseAddress = new Uri(opts.FireflyIiiUrl.TrimEnd('/') + '/');
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", opts.FireflyIiiToken);
            }
        })
        .AddResilienceHandler("fireflyiii", ConfigureResiliencePipeline);

        services.AddSingleton<ISessionStore, FileSessionStore>();
        services.AddTransient<AccountMatcher>();
        services.AddTransient<TransactionSyncer>();

        return services;
    }

    private static void ConfigureResiliencePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(2),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldRetryAfterHeader = true,
            ShouldHandle = args => args.Outcome switch
            {
                { Result: { StatusCode: HttpStatusCode.TooManyRequests } } => PredicateResult.True(),
                { Result: { StatusCode: HttpStatusCode.ServiceUnavailable } } => PredicateResult.True(),
                _ => new ValueTask<bool>(HttpClientResiliencePredicates.IsTransient(args.Outcome)),
            },
        });

        builder.AddTimeout(TimeSpan.FromSeconds(30));
    }
}
