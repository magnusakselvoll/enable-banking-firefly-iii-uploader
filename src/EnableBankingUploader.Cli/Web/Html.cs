using System.Text;
using EnableBankingUploader.Core.Sessions;
using EnableBankingUploader.Core.Sync;

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
            body { font-family: system-ui, sans-serif; max-width: 960px; margin: 2rem auto; padding: 0 1rem; color: #222; }
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
            .tx-skip { color: #888; }
            .tx-create { font-weight: 600; }
            .actions { margin-top: 1.5rem; }
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
        sb.Append("<a href=\"/manual-sync\" class=\"btn btn-secondary\">&#9654; Manual sync</a>");

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

    public static string ManualSyncSelect(IReadOnlyList<AccountSelectionRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>Manual sync — select accounts</h2>");

        if (rows.Count == 0)
        {
            sb.Append("<p>No accounts found. <a href=\"/register\">Register a bank</a> first.</p>");
            sb.Append("<p><a href=\"/\">← Back</a></p>");
            return Page("Manual sync", sb.ToString());
        }

        sb.Append("<form method=\"post\" action=\"/manual-sync/plan\">");
        sb.Append("<table><thead><tr><th></th><th>Bank</th><th>IBAN</th><th>Status</th><th>Firefly account</th><th>Last transaction</th></tr></thead><tbody>");

        foreach (var row in rows)
        {
            var (badgeClass, statusLabel) = BadgeFor(row.ValidUntil);
            var disabled = (row.Expired || !row.Mapped) ? " disabled" : "";
            var title = row.Expired ? "Session expired" : (!row.Mapped ? "No matching Firefly account" : "");
            var titleAttr = !string.IsNullOrEmpty(title) ? $" title=\"{Encode(title)}\"" : "";
            var ffName = row.Mapped ? Encode(row.FireflyAccountName) : "<em>Not mapped</em>";
            var lastTx = row.LastTransactionDate is not null ? Encode(row.LastTransactionDate) : "<em>None</em>";

            sb.Append(
                $"<tr>" +
                $"<td><input type=\"checkbox\" name=\"accounts\" value=\"{Encode(row.AccountUid)}\"{disabled}{titleAttr}></td>" +
                $"<td>{Encode(row.BankName)}</td>" +
                $"<td>{Encode(row.Iban ?? row.AccountUid)}</td>" +
                $"<td><span class=\"badge {badgeClass}\">{statusLabel}</span></td>" +
                $"<td>{ffName}</td>" +
                $"<td>{lastTx}</td>" +
                $"</tr>");
        }

        sb.Append("</tbody></table>");
        sb.Append("<div class=\"actions\"><button type=\"submit\" class=\"btn\">Read transactions for selected accounts →</button></div>");
        sb.Append("</form>");
        sb.Append("<p><a href=\"/\">← Back</a></p>");
        return Page("Manual sync", sb.ToString());
    }

    public static string ManualSyncPreview(SyncPlan plan, string token)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>Manual sync — preview</h2>");

        var mappedAccounts = plan.Accounts.Where(a => !a.FetchError && !a.Unmapped).ToList();
        var problemAccounts = plan.Accounts.Where(a => a.FetchError || a.Unmapped).ToList();

        if (problemAccounts.Count > 0)
        {
            sb.Append("<div class=\"banner banner-error\">");
            foreach (var a in problemAccounts)
            {
                var reason = a.FetchError ? "Failed to fetch account details" : "No matching Firefly account";
                sb.Append($"<div>{Encode(a.BankName)} — {Encode(a.Iban ?? a.AccountUid)}: {reason}</div>");
            }
            sb.Append("</div>");
        }

        if (mappedAccounts.Count == 0)
        {
            sb.Append("<p>No transactions to review.</p>");
            sb.Append("<p><a href=\"/manual-sync\">← Back to account selection</a></p>");
            return Page("Manual sync — preview", sb.ToString());
        }

        var totalCreate = plan.Accounts.Sum(a => a.Transactions.Count(t => t.Decision == SyncDecision.Create));

        foreach (var account in mappedAccounts)
        {
            var toCreate = account.Transactions.Count(t => t.Decision == SyncDecision.Create);
            var toDuplicate = account.Transactions.Count(t => t.Decision == SyncDecision.SkipDuplicate);
            sb.Append($"<h2>{Encode(account.BankName)} — {Encode(account.Iban ?? account.AccountUid)} → {Encode(account.FireflyAccountName)}</h2>");
            sb.Append($"<p>{Encode(account.DateFrom.ToString("yyyy-MM-dd"))} to {Encode(account.DateTo.ToString("yyyy-MM-dd"))} — <strong>{toCreate} to create</strong>, {toDuplicate} already in Firefly</p>");

            if (account.Transactions.Count == 0)
            {
                sb.Append("<p><em>No transactions in this date range.</em></p>");
                continue;
            }

            sb.Append("<table><thead><tr><th>Date</th><th>Direction</th><th>Amount</th><th>Description</th><th>Status</th></tr></thead><tbody>");
            foreach (var tx in account.Transactions)
            {
                var (rowClass, statusText) = tx.Decision switch
                {
                    SyncDecision.Create => ("tx-create", "Will import"),
                    SyncDecision.SkipDuplicate => ("tx-skip", "Already in Firefly"),
                    SyncDecision.SkipNonBooked => ("tx-skip", "Not yet booked"),
                    SyncDecision.SkipNoId => ("tx-skip", "No ID — skip"),
                    _ => ("", "Unknown"),
                };
                sb.Append(
                    $"<tr class=\"{rowClass}\">" +
                    $"<td>{Encode(tx.Date?.ToString("yyyy-MM-dd") ?? "—")}</td>" +
                    $"<td>{Encode(tx.Direction)}</td>" +
                    $"<td>{Encode(tx.Amount)} {Encode(tx.Currency)}</td>" +
                    $"<td>{Encode(tx.Description)}</td>" +
                    $"<td>{statusText}</td>" +
                    $"</tr>");
            }
            sb.Append("</tbody></table>");
        }

        sb.Append("<form method=\"post\" action=\"/manual-sync/execute\">");
        sb.Append($"<input type=\"hidden\" name=\"token\" value=\"{Encode(token)}\">");
        sb.Append($"<div class=\"actions\"><button type=\"submit\" class=\"btn\">Sync {totalCreate} transaction(s) to Firefly III</button></div>");
        sb.Append("</form>");
        sb.Append("<p><a href=\"/manual-sync\">← Back to account selection</a></p>");
        return Page("Manual sync — preview", sb.ToString());
    }

    public static string ManualSyncResult(SyncSummary summary)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>Manual sync — done</h2>");
        sb.Append($"<p>Run label: <code>{Encode(summary.RunLabel)}</code></p>");

        if (summary.Accounts.Count == 0)
        {
            sb.Append("<p>No accounts were processed.</p>");
        }
        else
        {
            sb.Append("<table><thead><tr><th>Bank</th><th>IBAN</th><th>Firefly account</th><th>Created</th><th>Duplicates skipped</th><th>Other skipped</th><th>Errors</th></tr></thead><tbody>");
            foreach (var a in summary.Accounts)
            {
                var otherSkipped = a.SkippedNonBooked + a.SkippedNoId;
                var rowNote = a.FetchError ? " (fetch error)" : a.Unmapped ? " (not mapped)" : "";
                sb.Append(
                    $"<tr>" +
                    $"<td>{Encode(a.BankName)}</td>" +
                    $"<td>{Encode(a.Iban ?? a.AccountUid)}</td>" +
                    $"<td>{Encode(a.FireflyAccountName ?? "—")}{rowNote}</td>" +
                    $"<td>{a.Created}</td>" +
                    $"<td>{a.SkippedDuplicate}</td>" +
                    $"<td>{otherSkipped}</td>" +
                    $"<td>{(a.CreateErrors > 0 ? $"<span class=\"badge expired\">{a.CreateErrors} error(s)</span>" : "—")}</td>" +
                    $"</tr>");
            }
            sb.Append("</tbody></table>");
        }

        sb.Append("<div class=\"actions\"><a href=\"/\" class=\"btn btn-secondary\">← Back to bank management</a></div>");
        return Page("Manual sync — done", sb.ToString());
    }

    public static string Error(string message) =>
        Page("Error", $"<div class=\"banner banner-error\">{Encode(message)}</div><p><a href=\"/\">← Back to bank management</a></p>");

    internal static (string cls, string label) BadgeFor(DateTimeOffset validUntil)
    {
        var remaining = validUntil - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) return ("expired", "Expired");
        if (remaining < TimeSpan.FromDays(14)) return ("expiring", "Expiring soon");
        return ("valid", "Valid");
    }

    private static string Encode(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
