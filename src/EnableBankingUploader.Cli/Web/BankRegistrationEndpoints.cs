using EnableBankingUploader.Core.EnableBanking;
using EnableBankingUploader.Core.EnableBanking.Models;
using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

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
    }

    private static async Task<IResult> IndexAsync(
        ISessionStore store,
        IEnableBankingClient client,
        ILoggerFactory loggerFactory,
        HttpContext ctx,
        CancellationToken ct)
    {
        var sessions = await store.ListAsync(ct);
        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        sessions = await EnsureIbansAsync(store, client, sessions, logger, ct);
        var banner = ctx.Request.Query["msg"].FirstOrDefault();
        var isError = ctx.Request.Query["err"].FirstOrDefault() == "1";
        return Results.Content(Html.Index(sessions, banner, isError), "text/html");
    }

    internal static async Task<IReadOnlyList<StoredSession>> EnsureIbansAsync(
        ISessionStore store,
        IEnableBankingClient client,
        IReadOnlyList<StoredSession> sessions,
        ILogger logger,
        CancellationToken ct)
    {
        var result = new List<StoredSession>(sessions.Count);
        foreach (var session in sessions)
        {
            if (session.Accounts is not null)
            {
                result.Add(session);
                continue;
            }

            try
            {
                var accounts = new List<StoredAccount>(session.AccountUids.Count);
                foreach (var uid in session.AccountUids)
                {
                    var account = await client.GetAccountAsync(uid, ct);
                    accounts.Add(new StoredAccount(uid, account.AccountId?.Iban));
                }

                var updated = session with { Accounts = accounts };
                await store.SaveAsync(updated, ct);
                result.Add(updated);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to backfill IBANs for session {SessionId}; will retry next page load.", session.SessionId);
                result.Add(session);
            }
        }
        return result;
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
        var aspspName = StripControlChars(parts[0]);
        var aspspCountry = StripControlChars(parts.Length > 1 ? parts[1] : string.Empty);

        if (string.IsNullOrWhiteSpace(aspspName) || string.IsNullOrWhiteSpace(aspspCountry))
            return Results.Content(Html.Error("Invalid bank selection."), "text/html");

        var publicBaseUrl = options.Value.PublicBaseUrl;
        if (string.IsNullOrEmpty(publicBaseUrl))
            return Results.Content(Html.Error(
                "PublicBaseUrl is not configured. Set EnableBankingUploader__PublicBaseUrl to the base URL of this server."),
                "text/html");

        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        using var aspspScope = logger.BeginScope(new Dictionary<string, object> { ["Aspsp"] = aspspName, ["Country"] = aspspCountry });
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

        using var aspspScope = logger.BeginScope(new Dictionary<string, object> { ["Aspsp"] = pending.AspspName, ["Country"] = pending.AspspCountry });
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
            CreatedAt: DateTimeOffset.UtcNow,
            Accounts: session.Accounts.Select(a => new StoredAccount(a.Uid, a.AccountId?.Iban)).ToList());

        await store.SaveAsync(stored, ct);

        logger.LogInformation(
            "Registered session {SessionId} for {Bank} with {Count} account(s).",
            session.SessionId, stored.AspspName, stored.AccountUids.Count);

        return Results.Redirect($"/?msg={Uri.EscapeDataString($"Successfully registered {stored.AspspName}.")}");
    }

    private static async Task<IResult> DeleteSessionAsync(
        string sessionId,
        IEnableBankingClient client,
        ISessionStore store,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(BankRegistrationEndpoints));
        using var sessionScope = logger.BeginScope(new Dictionary<string, object> { ["SessionId"] = sessionId });
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

    // Strips control characters (e.g. CR/LF) from user-supplied values before they reach
    // log scopes or log messages, preventing log forging via a crafted aspsp form field.
    // Uses Regex.Replace rather than a manual char filter: CodeQL's cs/log-forging query
    // only recognizes a fixed set of calls (String.Replace/Remove/ReplaceLineEndings,
    // Regex.Replace) as sanitizing barriers, so an equivalent hand-rolled filter would
    // still be flagged as a false positive.
    private static string StripControlChars(string value) =>
        Regex.Replace(value, @"\p{Cc}", string.Empty);
}
