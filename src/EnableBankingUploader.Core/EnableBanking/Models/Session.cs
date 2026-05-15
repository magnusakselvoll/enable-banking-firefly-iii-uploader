using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.EnableBanking.Models;

public record Session(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("accounts")] IReadOnlyList<string> AccountUids);

public record SessionsResponse(
    [property: JsonPropertyName("sessions")] IReadOnlyList<Session> Sessions);
