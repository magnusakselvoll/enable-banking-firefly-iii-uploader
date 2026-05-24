using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.Sessions;
using EnableBankingUploader.Core.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EnableBankingUploader.Cli.Web;

internal static class RepairEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/repair-dates", SelectAccountsAsync);
        app.MapPost("/repair-dates/plan", BuildPlanAsync);
        app.MapPost("/repair-dates/execute", ExecutePlanAsync);
    }

    private static async Task<IResult> SelectAccountsAsync(
        ISessionStore store,
        IEnableBankingClient ebClient,
        IFireflyIiiClient ffClient,
        AccountMatcher matcher,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(RepairEndpoints));
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
                    }
                }

                var isExpired = session.ValidUntil < DateTimeOffset.UtcNow;
                rows.Add(new AccountSelectionRow(
                    account.Uid, session.AspspName, iban, session.ValidUntil,
                    ffName, null, mapped, isExpired));
            }
        }

        return Results.Content(Html.RepairSelect(rows), "text/html");
    }

    private static async Task<IResult> BuildPlanAsync(
        HttpRequest request,
        TransactionSyncer syncer,
        RepairState state,
        CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var selectedUids = new HashSet<string>(form["accounts"].Where(s => s is not null).Select(s => s!));

        if (selectedUids.Count == 0)
            return Results.Redirect("/repair-dates?err=1&msg=" + Uri.EscapeDataString("No accounts selected."));

        var startDateStr = form["start_date"].FirstOrDefault() ?? "2026-05-01";
        if (!DateOnly.TryParse(startDateStr, out var startDate))
            return Results.Redirect("/repair-dates?err=1&msg=" + Uri.EscapeDataString("Invalid start date."));

        var plan = await syncer.BuildRepairPlanAsync(selectedUids, startDate, ct);
        var token = state.Add(plan);

        return Results.Content(Html.RepairPreview(plan, token), "text/html");
    }

    private static async Task<IResult> ExecutePlanAsync(
        HttpRequest request,
        TransactionSyncer syncer,
        RepairState state,
        CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var token = form["token"].FirstOrDefault() ?? string.Empty;

        var plan = state.TakeIfValid(token);
        if (plan is null)
            return Results.Redirect("/repair-dates?err=1&msg=" + Uri.EscapeDataString("Session expired or invalid. Please start again."));

        var summary = await syncer.ExecuteRepairPlanAsync(plan, ct);
        return Results.Content(Html.RepairResult(summary), "text/html");
    }
}
