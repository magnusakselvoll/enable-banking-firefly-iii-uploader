using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.EnableBanking.Models;

public record AuthAccess(
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil);

public record AuthAspsp(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("country")] string Country);

public record AuthRequest(
    [property: JsonPropertyName("access")] AuthAccess Access,
    [property: JsonPropertyName("aspsp")] AuthAspsp Aspsp,
    [property: JsonPropertyName("psu_type")] string PsuType,
    [property: JsonPropertyName("redirect_url")] string RedirectUrl,
    [property: JsonPropertyName("state")] string State);

public record AuthResponse(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("authorization_id")] string AuthorizationId);

public record CreateSessionRequest(
    [property: JsonPropertyName("code")] string Code);

public record AuthorizedAccount(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("account_id")] AccountIdentification? AccountId);

public record AuthorizedSession(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("accounts")] IReadOnlyList<AuthorizedAccount> Accounts,
    [property: JsonPropertyName("aspsp")] AuthAspsp? Aspsp,
    [property: JsonPropertyName("access")] AuthAccess? Access);
