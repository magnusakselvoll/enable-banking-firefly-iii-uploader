namespace EnableBankingUploader.Core.Options;

public sealed class SyncOptions
{
    public required string EnableBankingApplicationId { get; init; }
    public required string EnableBankingPrivateKeyPath { get; init; }
    public required string FireflyIiiUrl { get; init; }
    public required string FireflyIiiToken { get; init; }
    public string Schedule { get; init; } = "0 18 * * *";
    public int LookbackDays { get; init; } = 1;
    // External HTTPS base URL (e.g. from Tailscale serve) — used to build the redirect_url sent to
    // Enable Banking during bank registration. Must be https://. Required only when registering banks
    // via the web UI; the cron sync works without it once sessions are stored.
    public string? PublicBaseUrl { get; init; }
    public string SessionStorePath { get; init; } = "/data/sessions";
    public string WebListenUrl { get; init; } = "http://0.0.0.0:8080";
}
