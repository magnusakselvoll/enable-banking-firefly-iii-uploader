using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.Sessions;
using EnableBankingUploader.Core.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EnableBankingUploader.Cli.Web;

internal static class ManualSyncEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/manual-sync", SelectAccountsAsync);
        app.MapPost("/manual-sync/plan", BuildPlanAsync);
        app.MapPost("/manual-sync/execute", ExecutePlanAsync);
    }

    private static async Task<IResult> SelectAccountsAsync(
        ISessionStore store,
        IEnableBankingClient ebClient,
        IFireflyIiiClient ffClient,
        AccountMatcher matcher,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(ManualSyncEndpoints));
        var sessions = await store.ListAsync(ct);
        sessions = await BankRegistrationEndpoints.EnsureIbansAsync(store, ebClient, sessions, logger, ct);

        var fireflyAccounts = await ffClient.GetAssetAccountsAsync(ct);

        var rows = new List<AccountSelectionRow>();
        foreach (var session in sessions)
        {
            var accounts = session.Accounts ?? session.AccountUids.Select(uid => new StoredAccount(uid, null)).ToList();
            foreach (var account in accounts)
            {
                var iban = account.Iban;
                string? ffName = null;
                string? lastTxDate = null;
                bool mapped = false;

                if (!string.IsNullOrEmpty(iban))
                {
                    var normalizedIban = AccountMatcher.NormalizeIban(iban);
                    var ffAccount = fireflyAccounts.FirstOrDefault(a =>
                        !string.IsNullOrEmpty(a.Attributes.Iban) &&
                        AccountMatcher.NormalizeIban(a.Attributes.Iban) == normalizedIban);

                    if (ffAccount is not null)
                    {
                        ffName = ffAccount.Attributes.Name;
                        mapped = true;
                        try
                        {
                            var latest = await ffClient.GetLatestTransactionDateAsync(ffAccount.Id, ct);
                            lastTxDate = latest.HasValue ? latest.Value.ToString("yyyy-MM-dd") : null;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to fetch latest transaction date for Firefly account {Id}.", ffAccount.Id);
                        }
                    }
                }

                var isExpired = session.ValidUntil < DateTimeOffset.UtcNow;
                rows.Add(new AccountSelectionRow(
                    account.Uid, session.AspspName, iban, session.ValidUntil,
                    ffName, lastTxDate, mapped, isExpired));
            }
        }

        return Results.Content(Html.ManualSyncSelect(rows), "text/html");
    }

    private static async Task<IResult> BuildPlanAsync(
        HttpRequest request,
        TransactionSyncer syncer,
        ManualSyncState state,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var selectedUids = new HashSet<string>(form["accounts"].Where(s => s is not null).Select(s => s!));

        if (selectedUids.Count == 0)
            return Results.Redirect("/manual-sync?err=1&msg=" + Uri.EscapeDataString("No accounts selected."));

        var logger = loggerFactory.CreateLogger(nameof(ManualSyncEndpoints));
        using var manualScope = logger.BeginScope(new Dictionary<string, object> { ["Source"] = "manual", ["SelectedAccounts"] = selectedUids.Count });
        var plan = await syncer.BuildPlanAsync(selectedUids, ct);
        var token = state.Add(plan);

        return Results.Content(Html.ManualSyncPreview(plan, token), "text/html");
    }

    private static async Task<IResult> ExecutePlanAsync(
        HttpRequest request,
        TransactionSyncer syncer,
        ManualSyncState state,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var token = form["token"].FirstOrDefault() ?? string.Empty;

        var plan = state.TakeIfValid(token);
        if (plan is null)
            return Results.Redirect("/manual-sync?err=1&msg=" + Uri.EscapeDataString("Session expired or invalid. Please start again."));

        var logger = loggerFactory.CreateLogger(nameof(ManualSyncEndpoints));
        using var manualScope = logger.BeginScope(new Dictionary<string, object> { ["Source"] = "manual", ["RunLabel"] = plan.RunLabel });
        var summary = await syncer.ExecutePlanAsync(plan, ct);
        return Results.Content(Html.ManualSyncResult(summary), "text/html");
    }
}

internal sealed record AccountSelectionRow(
    string AccountUid,
    string BankName,
    string? Iban,
    DateTimeOffset ValidUntil,
    string? FireflyAccountName,
    string? LastTransactionDate,
    bool Mapped,
    bool Expired);
