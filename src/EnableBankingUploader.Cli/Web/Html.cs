using System.Text;
using EnableBankingUploader.Core.Sessions;

namespace EnableBankingUploader.Cli.Web;

internal static class Html
{
    // $$""" uses {{expr}} for interpolation so CSS { } are literal.
    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{Encode(title)}} — Enable Banking Uploader</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 860px; margin: 2rem auto; padding: 0 1rem; color: #222; }
            h1 { font-size: 1.4rem; margin-bottom: 1.5rem; }
            h2 { font-size: 1.1rem; margin-top: 2rem; }
            table { border-collapse: collapse; width: 100%; }
            th, td { text-align: left; padding: .4rem .7rem; border-bottom: 1px solid #ddd; }
            th { background: #f5f5f5; }
            .badge { display: inline-block; padding: .15rem .5rem; border-radius: 4px; font-size: .8rem; font-weight: 600; }
            .valid { background: #d4edda; color: #155724; }
            .expiring { background: #fff3cd; color: #856404; }
            .expired { background: #f8d7da; color: #721c24; }
            .btn { display: inline-block; padding: .3rem .8rem; border: none; border-radius: 4px; cursor: pointer; font-size: .9rem; text-decoration: none; background: #0d6efd; color: #fff; }
            .btn-sm { padding: .2rem .6rem; font-size: .8rem; }
            .btn-danger { background: #dc3545; }
            .btn-secondary { background: #6c757d; }
            .banner { padding: .6rem 1rem; border-radius: 4px; margin-bottom: 1rem; }
            .banner-success { background: #d4edda; color: #155724; }
            .banner-error { background: #f8d7da; color: #721c24; }
            form { display: inline; }
            label { display: block; margin-bottom: .3rem; font-weight: 600; }
            input, select { padding: .4rem .6rem; border: 1px solid #ccc; border-radius: 4px; width: 100%; max-width: 400px; box-sizing: border-box; margin-bottom: 1rem; }
            .field { margin-bottom: 1rem; }
          </style>
        </head>
        <body>
          <h1>Enable Banking Uploader — Bank Management</h1>
          {{body}}
        </body>
        </html>
        """;

    public static string Index(IReadOnlyList<StoredSession> sessions, string? banner, bool isError)
    {
        var sb = new StringBuilder();
        if (banner is not null)
        {
            var cls = isError ? "banner-error" : "banner-success";
            sb.Append($"<div class=\"banner {cls}\">{Encode(banner)}</div>");
        }

        sb.Append("<a href=\"/register\" class=\"btn\">+ Register new bank</a> ");
        sb.Append("<form method=\"post\" action=\"/sync\"><button class=\"btn btn-secondary\">&#9654; Run sync now</button></form>");

        if (sessions.Count == 0)
        {
            sb.Append("<p>No banks registered yet.</p>");
        }
        else
        {
            sb.Append("<table><thead><tr><th>Bank</th><th>Country</th><th>Accounts</th><th>Valid until</th><th>Status</th><th>Actions</th></tr></thead><tbody>");
            foreach (var s in sessions)
            {
                var (badgeClass, label) = BadgeFor(s.ValidUntil);
                var accounts = s.Accounts is not null
                    ? string.Join(", ", s.Accounts.Select(a => !string.IsNullOrEmpty(a.Iban) ? a.Iban : a.Uid))
                    : string.Join(", ", s.AccountUids);
                sb.Append(
                    $"<tr>" +
                    $"<td>{Encode(s.AspspName)}</td>" +
                    $"<td>{Encode(s.AspspCountry)}</td>" +
                    $"<td>{Encode(accounts)}</td>" +
                    $"<td>{s.ValidUntil:yyyy-MM-dd}</td>" +
                    $"<td><span class=\"badge {badgeClass}\">{label}</span></td>" +
                    $"<td>" +
                    $"<a href=\"/register?aspsp={Uri.EscapeDataString(s.AspspName)}&amp;country={Uri.EscapeDataString(s.AspspCountry)}\" class=\"btn btn-sm btn-secondary\">Re-authorize</a>" +
                    $"&nbsp;" +
                    $"<form method=\"post\" action=\"/sessions/{Uri.EscapeDataString(s.SessionId)}/delete\" onsubmit=\"return confirm('Remove this bank session?')\">" +
                    $"<button type=\"submit\" class=\"btn btn-sm btn-danger\">Remove</button>" +
                    $"</form>" +
                    $"</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        return Page("Bank Management", sb.ToString());
    }

    public static string RegisterForm(
        IReadOnlyList<EnableBankingUploader.Core.EnableBanking.Models.Aspsp> aspsps,
        string? selectedAspsp,
        string? selectedCountry,
        string? country,
        string? error)
    {
        var sb = new StringBuilder();

        if (error is not null)
            sb.Append($"<div class=\"banner banner-error\">{Encode(error)}</div>");

        sb.Append("<h2>Register a bank</h2>");
        sb.Append(
            "<form method=\"get\" action=\"/register\">" +
            "<div class=\"field\">" +
            "<label for=\"country\">Filter by country (optional)</label>" +
            $"<input type=\"text\" id=\"country\" name=\"country\" maxlength=\"2\" placeholder=\"e.g. FI\" value=\"{Encode(country)}\">" +
            "</div>" +
            "<button type=\"submit\" class=\"btn btn-secondary btn-sm\">Filter</button>" +
            "</form>");

        if (aspsps.Count == 0)
        {
            sb.Append("<p>No banks found. Try a different country filter.</p>");
        }
        else
        {
            sb.Append(
                "<form method=\"post\" action=\"/register\">" +
                "<div class=\"field\">" +
                "<label for=\"aspsp\">Select bank</label>" +
                "<select id=\"aspsp\" name=\"aspsp\" required>");

            foreach (var a in aspsps)
            {
                var value = $"{a.Name}|{a.Country}";
                var selected = (a.Name == selectedAspsp && a.Country == selectedCountry) ? " selected" : "";
                var validity = a.MaximumConsentValiditySeconds.HasValue ? $" — up to {a.MaximumConsentValiditySeconds / 86400} days" : "";
                sb.Append($"<option value=\"{Encode(value)}\"{selected}>{Encode(a.Name)} ({Encode(a.Country)}){Encode(validity)}</option>");
            }

            sb.Append(
                "</select></div>" +
                "<button type=\"submit\" class=\"btn\">Authorize →</button>" +
                "</form>");
        }

        sb.Append("<p><a href=\"/\">← Back</a></p>");
        return Page("Register Bank", sb.ToString());
    }

    public static string Error(string message) =>
        Page("Error", $"<div class=\"banner banner-error\">{Encode(message)}</div><p><a href=\"/\">← Back to bank management</a></p>");

    private static (string cls, string label) BadgeFor(DateTimeOffset validUntil)
    {
        var remaining = validUntil - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) return ("expired", "Expired");
        if (remaining < TimeSpan.FromDays(14)) return ("expiring", "Expiring soon");
        return ("valid", "Valid");
    }

    private static string Encode(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
