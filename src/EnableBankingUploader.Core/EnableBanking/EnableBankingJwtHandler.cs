using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using EnableBankingUploader.Core.Options;

namespace EnableBankingUploader.Core.EnableBanking;

internal sealed class EnableBankingJwtHandler : DelegatingHandler
{
    private readonly IOptions<SyncOptions> _options;
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry;

    public EnableBankingJwtHandler(IOptions<SyncOptions> options)
    {
        _options = options;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetToken());
        return await base.SendAsync(request, cancellationToken);
    }

    private string GetToken()
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddSeconds(-60))
            return _cachedToken;

        var opts = _options.Value;
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(opts.EnableBankingPrivateKeyPath));

        var signingCredentials = new SigningCredentials(
            new RsaSecurityKey(rsa) { KeyId = opts.EnableBankingApplicationId },
            SecurityAlgorithms.RsaSha256);

        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddHours(1);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = opts.EnableBankingApplicationId,
            Audience = "api.enablebanking.com",
            IssuedAt = now.UtcDateTime,
            Expires = expiry.UtcDateTime,
            SigningCredentials = signingCredentials,
        };

        var handler = new JsonWebTokenHandler();
        _cachedToken = handler.CreateToken(tokenDescriptor);
        _tokenExpiry = expiry;

        return _cachedToken;
    }
}
