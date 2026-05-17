using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var appId = "0aa600b9-3a03-4783-970d-73e15a40239d";
var pemPath = "secrets/0aa600b9-3a03-4783-970d-73e15a40239d.pem";
// Pass a session ID as the first argument: dotnet run --project tools/explore -- <session-id>
var sessionId = args.Length > 0 ? args[0] : null;

var rsa = RSA.Create();
rsa.ImportFromPem(File.ReadAllText(pemPath));
var creds = new SigningCredentials(
    new RsaSecurityKey(rsa) { KeyId = appId },
    SecurityAlgorithms.RsaSha256);
var now = DateTimeOffset.UtcNow;
var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
{
    Issuer = appId,
    Audience = "api.enablebanking.com",
    IssuedAt = now.UtcDateTime,
    Expires = now.AddHours(1).UtcDateTime,
    SigningCredentials = creds,
});

var http = new HttpClient { BaseAddress = new Uri("https://api.enablebanking.com/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

var prettyJson = new JsonSerializerOptions { WriteIndented = true };

async Task<JsonElement> Call(string path)
{
    Console.WriteLine($"\n=== GET /{path} ===");
    var resp = await http.GetAsync(path);
    var raw = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"Status: {(int)resp.StatusCode} {resp.StatusCode}");
    var doc = JsonDocument.Parse(raw).RootElement;
    Console.WriteLine(JsonSerializer.Serialize(doc, prettyJson));
    return doc;
}

if (sessionId == null)
{
    Console.WriteLine("Usage: dotnet run --project tools/explore -- <session-id>");
    Console.WriteLine("Session IDs are created via the bank registration web UI (open <PublicBaseUrl> in your browser).");
    return;
}

// Shape of GET /sessions/{id}
var session = await Call($"sessions/{sessionId}");

// Extract first account UID from the session response
string? firstUid = null;
if (session.TryGetProperty("accounts", out var accounts) && accounts.GetArrayLength() > 0)
{
    var a = accounts[0];
    if (a.ValueKind == JsonValueKind.String) firstUid = a.GetString();
    else if (a.TryGetProperty("uid", out var uid)) firstUid = uid.GetString();
}

if (firstUid == null)
{
    Console.WriteLine("\nCould not extract account UID from session. Check the shape above.");
    return;
}

// Shape of GET /accounts/{uid}/details
await Call($"accounts/{firstUid}/details");

// Shape of GET /accounts/{uid}/transactions (7 days)
var today = DateOnly.FromDateTime(DateTime.UtcNow);
var from = today.AddDays(-7);
await Call($"accounts/{firstUid}/transactions?date_from={from:yyyy-MM-dd}&date_to={today:yyyy-MM-dd}");
