using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using EbAccount = EnableBankingUploader.Core.EnableBanking.Models.Account;
using FfAccount = EnableBankingUploader.Core.FireflyIii.Models.Account;

namespace EnableBankingUploader.Core.Sync;

public sealed class AccountMatcher
{
    private readonly ILogger<AccountMatcher> _logger;

    public AccountMatcher(ILogger<AccountMatcher> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<(EbAccount EnableBanking, FfAccount FireflyIii)> MatchAccounts(
        IReadOnlyList<EbAccount> enableBankingAccounts,
        IReadOnlyList<FfAccount> fireflyAccounts)
    {
        var fireflyByIban = fireflyAccounts
            .Where(a => a.Attributes.Iban is not null)
            .ToDictionary(a => NormalizeIban(a.Attributes.Iban!), a => a);

        var matches = new List<(EbAccount, FfAccount)>();

        foreach (var ebAccount in enableBankingAccounts)
        {
            var iban = ebAccount.AccountId?.Iban;
            if (string.IsNullOrWhiteSpace(iban))
            {
                _logger.LogWarning("Enable Banking account {Uid} has no IBAN, skipping.", ebAccount.Uid);
                continue;
            }

            var normalized = NormalizeIban(iban);
            if (fireflyByIban.TryGetValue(normalized, out var fireflyAccount))
            {
                matches.Add((ebAccount, fireflyAccount));
                _logger.LogInformation(
                    "Matched account {Iban}: Enable Banking {EbUid} ↔ Firefly III {FfId} ({FfName}).",
                    normalized, ebAccount.Uid, fireflyAccount.Id, fireflyAccount.Attributes.Name);
            }
            else
            {
                _logger.LogWarning(
                    "Enable Banking account {Uid} (IBAN: {Iban}) has no matching Firefly III asset account.",
                    ebAccount.Uid, normalized);
            }
        }

        return matches;
    }

    public static string NormalizeIban(string iban) =>
        Regex.Replace(iban, @"[\s\-]", "").ToUpperInvariant();
}
