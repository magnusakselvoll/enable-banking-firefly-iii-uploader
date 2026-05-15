namespace EnableBankingUploader.Core.Options;

public sealed class SyncOptions
{
    public required string EnableBankingApplicationId { get; init; }
    public required string EnableBankingPrivateKeyPath { get; init; }
    // Session IDs from the Enable Banking Control Panel (one per bank consent).
    // There is no API endpoint to list sessions — these must be provided explicitly.
    public IReadOnlyList<string> EnableBankingSessionIds { get; init; } = [];
    public required string FireflyIiiUrl { get; init; }
    public required string FireflyIiiToken { get; init; }
    public string Schedule { get; init; } = "0 18 * * *";
    public int LookbackDays { get; init; } = 1;
}
