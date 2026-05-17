using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.EnableBanking.Models;
using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sessions;
using EnableBankingUploader.Core.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnableBankingUploader.Cli.Web;

internal static class BankRegistrationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/", IndexAsync);
        app.MapGet("/register", RegisterFormAsync);
        app.MapPost("/register", StartRegistrationAsync);
        app.MapGet("/callback", CallbackAsync);
        app.MapPost("/sessions/{sessionId}/delete", DeleteSessionAsync);
        app.MapPost("/sync", TriggerSyncAsync);
    }

    private static async Task<IResult> IndexAsync(
        ISessionStore store,
        HttpContext ctx,
        CancellationToken ct)
    {
        var sessions = await store.ListAsync(ct);
        var banner = ctx.Request.Query["msg"].FirstOrDefault();
        var isError = ctx.Request.Query["err"].FirstOrDefault() == "1";
        return Results.Content(Html.Index(sessions, banner, isError), "text/html");
    }

    private static async Task<IResult> RegisterFormAsync(
        IEnableBankingClient client,
        ILoggerFactory loggerFactory,
        string? country,
        string? aspsp,
        CancellationToken ct)
    {
        string? selectedName = null;
        string? selectedCountry = null;

        if (!string.IsNullOrEmpty(aspsp))
        {
            // When re-authorizing, aspsp param holds just the name; country comes separately
            selectedName = aspsp;
            selectedCountry = country;
        }

        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        List<Aspsp> aspsps;
        try
        {
            aspsps = [.. await client.ListAspspsAsync(country, ct)];
            aspsps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load ASPSPs.");
            return Results.Content(Html.Error($"Failed to load banks: {ex.Message}"), "text/html");
        }

        return Results.Content(Html.RegisterForm(aspsps, selectedName, selectedCountry, country, error: null), "text/html");
    }

    private static async Task<IResult> StartRegistrationAsync(
        HttpRequest request,
        IEnableBankingClient client,
        ISessionStore store,
        BankRegistrationState state,
        IOptions<SyncOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var aspspValue = form["aspsp"].FirstOrDefault() ?? string.Empty;
        var parts = aspspValue.Split('|', 2);
        var aspspName = parts[0];
        var aspspCountry = parts.Length > 1 ? parts[1] : string.Empty;

        if (string.IsNullOrWhiteSpace(aspspName) || string.IsNullOrWhiteSpace(aspspCountry))
            return Results.Content(Html.Error("Invalid bank selection."), "text/html");

        var publicBaseUrl = options.Value.PublicBaseUrl;
        if (string.IsNullOrEmpty(publicBaseUrl))
            return Results.Content(Html.Error(
                "PublicBaseUrl is not configured. Set EnableBankingUploader__PublicBaseUrl to the base URL of this server."),
                "text/html");

        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        if (!publicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("PublicBaseUrl {Url} is not https://. Production Enable Banking requires https; sandbox may allow http.", publicBaseUrl);

        // Resolve the aspsp to get maximum_consent_validity
        Aspsp? aspsp;
        try
        {
            var all = await client.ListAspspsAsync(aspspCountry, ct);
            aspsp = all.FirstOrDefault(a =>
                string.Equals(a.Name, aspspName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Country, aspspCountry, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to look up ASPSP {Name}/{Country}.", aspspName, aspspCountry);
            return Results.Content(Html.Error($"Failed to look up bank: {ex.Message}"), "text/html");
        }

        const int maxConsentSeconds = 180 * 24 * 3600; // 180 days — cap sandbox extremes
        var consentSeconds = Math.Min(aspsp?.MaximumConsentValiditySeconds ?? maxConsentSeconds, maxConsentSeconds);
        var validUntil = DateTimeOffset.UtcNow.AddSeconds(consentSeconds);
        var redirectUrl = publicBaseUrl.TrimEnd('/') + "/callback";
        var oauthState = Guid.NewGuid().ToString("N");

        state.PurgeExpired();
        state.Add(oauthState, new PendingAuth(aspspName, aspspCountry, DateTimeOffset.UtcNow));

        AuthResponse authResponse;
        try
        {
            authResponse = await client.StartAuthorizationAsync(new AuthRequest(
                Access: new AuthAccess(validUntil),
                Aspsp: new AuthAspsp(aspspName, aspspCountry),
                PsuType: "personal",
                RedirectUrl: redirectUrl,
                State: oauthState), ct);
        }
        catch (Exception ex)
        {
            state.TakeIfValid(oauthState); // clean up
            logger.LogError(ex, "Failed to start authorization for {Name}/{Country}.", aspspName, aspspCountry);
            return Results.Content(Html.Error($"Failed to start bank authorization: {ex.Message}"), "text/html");
        }

        return Results.Redirect(authResponse.Url);
    }

    private static async Task<IResult> CallbackAsync(
        HttpRequest request,
        IEnableBankingClient client,
        ISessionStore store,
        BankRegistrationState state,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        var error = request.Query["error"].FirstOrDefault();
        if (error is not null)
        {
            var description = request.Query["error_description"].FirstOrDefault() ?? error;
            return Results.Redirect($"/?msg={Uri.EscapeDataString("Authorization failed: " + description)}&err=1");
        }

        var code = request.Query["code"].FirstOrDefault();
        var oauthState = request.Query["state"].FirstOrDefault();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(oauthState))
            return Results.Redirect("/?msg=Authorization+callback+missing+code+or+state.&err=1");

        var pending = state.TakeIfValid(oauthState);
        if (pending is null)
            return Results.Redirect("/?msg=Authorization+state+invalid+or+expired.+Please+try+again.&err=1");

        AuthorizedSession session;
        try
        {
            session = await client.CreateSessionAsync(code, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create session for {Name}/{Country}.", pending.AspspName, pending.AspspCountry);
            return Results.Redirect($"/?msg={Uri.EscapeDataString("Failed to create session: " + ex.Message)}&err=1");
        }

        var stored = new StoredSession(
            SessionId: session.SessionId,
            AspspName: session.Aspsp?.Name ?? pending.AspspName,
            AspspCountry: session.Aspsp?.Country ?? pending.AspspCountry,
            AccountUids: session.Accounts.Select(a => a.Uid).ToList(),
            ValidUntil: session.Access?.ValidUntil ?? DateTimeOffset.UtcNow.AddDays(90),
            CreatedAt: DateTimeOffset.UtcNow);

        await store.SaveAsync(stored, ct);

        logger.LogInformation(
            "Registered session {SessionId} for {Bank} with {Count} account(s).",
            session.SessionId, stored.AspspName, stored.AccountUids.Count);

        return Results.Redirect($"/?msg={Uri.EscapeDataString($"Successfully registered {stored.AspspName}.")}");
    }

    private static IResult TriggerSyncAsync(
        TransactionSyncer syncer,
        IOptions<SyncOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        var label = options.Value.DryRun ? "dry-run sync" : "sync";
        logger.LogInformation("Manual {Label} triggered via web UI.", label);
        _ = Task.Run(async () =>
        {
            try { await syncer.SyncAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Manual {Label} failed.", label); }
        });
        return Results.Redirect($"/?msg={Uri.EscapeDataString($"Manual {label} started — check the logs for progress.")}");
    }

    private static async Task<IResult> DeleteSessionAsync(
        string sessionId,
        IEnableBankingClient client,
        ISessionStore store,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        try
        {
            await client.RevokeSessionAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to revoke session {SessionId} at Enable Banking (continuing with local removal).", sessionId);
        }

        await store.DeleteAsync(sessionId, ct);
        logger.LogInformation("Removed session {SessionId}.", sessionId);

        return Results.Redirect("/?msg=Bank+removed.");
    }
}
