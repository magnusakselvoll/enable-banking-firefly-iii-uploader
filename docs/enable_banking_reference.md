# Enable Banking — Personal-Use Tier Reference

> **Implementation notes** (from exploratory API testing):
> - JWT audience must be `api.enablebanking.com` (not `enablebanking.com`)
> - `GET /sessions` returns 405 — there is no endpoint to list sessions. Session IDs must be provided explicitly in configuration (`EnableBankingUploader:EnableBankingSessionIds`).
> - Dedup key: use `entry_reference` (falls back to `transaction_id` if absent). Only deduplicate `status=BOOK` transactions.
> - See [README.md](../README.md) for full configuration reference.

Notes for the AIS-Restricted production application used to import
transactions into a self-hosted Firefly III instance.

Sources: Enable Banking [Terms of Service](https://enablebanking.com/terms/)
(last updated 9 January 2026) and the
[API FAQ](https://enablebanking.com/docs/faq/).

---

## Application configuration (template)

| Field | Value |
|---|---|
| Status | Active |
| Environment | PRODUCTION |
| Services | Account Information (Restricted) |
| Redirect URLs | _(your callback URL)_ |
| Data protection email | _(your contact email)_ |
| Privacy / ToS URL | _(your published policy URL)_ |

**Note on redirect URLs:** if the callback is on a private hostname
(Tailscale, VPN, LAN), the OAuth flow only works from devices currently
on that network. For reconsent from outside, register a second redirect
URL (e.g. localhost or a publicly reachable host).

**Note on privacy/ToS URL:** the URL must resolve and remain reachable.
Even for a single-user setup, Enable Banking displays it during the
consent screen and may monitor its availability.

---

## Is the API read-only?

**Yes, effectively.** Three independent reasons:

1. **Only AIS is enabled** in the Services list — no Payment Initiation.
   Any `POST /payments` call is rejected by the API.
2. **"Restricted" is the personal-use tier** — the app is whitelisted only
   against linked accounts. The API strips any account from responses that
   wasn't explicitly linked in the Control Panel.
3. **No write-capable endpoints exist** for an AIS-Restricted app. The
   available surface is:
   - `GET /aspsps` — list supported banks
   - `POST /sessions`, `GET /sessions/{id}`, `DELETE /sessions/{id}`
   - `GET /accounts/{id}/balances`
   - `GET /accounts/{id}/transactions`
   - `GET /accounts/{id}/details`

`DELETE /sessions/{id}` only revokes consent; nothing at the bank changes.

---

## Acceptable Use (Restriction of Use section)

Free Terms permit production usage **only via Linked Accounts**, **only for
personal use by a private individual** or for evaluation.

### Explicitly disallowed

- **Business or professional use** — including accessing business
  accounts, accounts not belonging to the Control Panel user who linked
  them, or any commercial purpose.
- **Reselling, sublicensing, sharing access** with third parties.
  Other household members' accounts would need their own Control Panel
  registration.
- **Bypassing security or rate limits**, abusive automation, "bots,
  spiders, web crawlers, indexing agents." (A scheduled importer is an
  automated application, not a crawler — that's fine.)
- **Reverse engineering** the API or building a competing service.
- Malicious code, service interference, unlawful use.

### Pricing

Free under these Terms. Quote: *"Use of the Control Panel and the API
under these Terms is free of charge."* Commercial use requires a
separate agreement.

### Liability

- Enable Banking's liability: capped at **EUR 100**.
- User's liability: **uncapped** if the Terms are breached.

### Governance

- Finnish law; arbitration in Helsinki (Finland Chamber of Commerce).
- Enable Banking Oy is a registered AISP supervised by FIN-FSA.

---

## Operational limits — how often can I fetch?

Four independent mechanisms apply, layered:

### 1. Consent validity (SCA reconsent)

- Set `valid_until` when calling `POST /auth`.
- Max value is per-ASPSP, returned in `maximum_consent_validity` from
  `GET /aspsps`.
- **For most ASPSPs: 180 days.** Some still cap at 90.
- No separate "refresh" flow — when consent expires, do a fresh
  authorisation. Schedule a warning a week ahead.

### 2. Background vs. online fetches — **the limit that matters**

ASPSPs distinguish based on PSU headers (`Psu-Ip-Address`,
`Psu-User-Agent`, etc.):

| Mode | PSU headers | Typical limit |
|---|---|---|
| **Background** (cron/scheduled) | Absent | **~4 fetches per day per account** |
| **Online** (user-triggered) | Present | No daily cap |

On exceeding the limit: `429` with `ASPSP_RATE_LIMIT_EXCEEDED`. Enable
Banking recommends backing off for **6 hours**.

**Rule:** only send PSU headers when an actual person triggered the
fetch. Don't fake them for a cron job — that's exactly the abusive
automation the ToS warns against.

### 3. JWT authorisation token TTL

- Max **24 hours** (86 400 seconds).
- Cheap to regenerate per request using your RSA private key.
- Tokens identify the *application*, not an end user. Never share.

### 4. Historical transaction window

Two separate effects:

- **First ~1 hour after authorisation:** full history the ASPSP exposes
  is available (often 1–3 years, sometimes longer).
- **After that hour:** most ASPSPs restrict to the past **90 days** per
  request.

Implication: pull full history on first connect with `strategy=longest`.
Ongoing syncs only need recent transactions (`strategy=default`).

### 5. Whitelisting

Restricted-mode apps only return data for accounts explicitly linked in
the Control Panel. Any other accounts on the same authorisation are
silently stripped. Link every account you want to import.

---

## Recommended Firefly III sync schedule

- **Once-daily background fetch per account**, fixed time of day.
  Consumes 1 of the ~4 daily quota — leaves headroom for retries.
- **No PSU headers** in the scheduled job (no user present).
- **Reauthorise per ASPSP every ~150 days** with a buffer alarm a week
  before `valid_until`.
- **First sync:** `strategy=longest` to pull all available history.
- **Subsequent syncs:** `strategy=default` (or omit), narrow date range.
- **Dedup key:** `entry_reference` for booked transactions
  (`status=BOOK`). Skip pending (`PDNG`) for matching unless the ASPSP
  gives stable IDs for them.
- **Pagination:** if response contains a non-null `continuation_key`,
  keep calling with that key (and unchanged GET params) until null. An
  empty list with a continuation key is normal — keep going.

### Error handling

| Error | Response | Action |
|---|---|---|
| `ASPSP_RATE_LIMIT_EXCEEDED` | 429 | Back off 6 hours |
| `EXPIRED_SESSION` | 401 | Trigger fresh consent flow (don't retry) |
| `ASPSP_ERROR` | varies | Exponential backoff: 1 min, 1 h, 2 h, 4 h |
| `WRONG_TRANSACTIONS_PERIOD` | — | Requested date range unavailable; narrow it |
| `PSU_HEADER_NOT_PROVIDED` | 422 | Either send *all* required PSU headers or *none* |

### Premature session expiry — common causes

Sessions can expire before `valid_until` for any of these reasons.
Handle `EXPIRED_SESSION` defensively:

- ASPSP doesn't support multiple concurrent sessions per PSU
  (a new session at the same bank invalidates the old one).
- Yearly KYC survey required by the bank.
- Certificate rotation on the TPP side.
- Sandbox ASPSPs sometimes don't honour validity at all.

---

## Quick API reference (AIS-Restricted surface)

```
GET    /aspsps                                  # List supported banks
POST   /sessions                                # Initiate auth (returns redirect URL)
GET    /sessions/{session_id}                   # Inspect session / check validity
DELETE /sessions/{session_id}                   # Revoke consent
GET    /accounts/{account_id}/details           # IBAN, holder, currency
GET    /accounts/{account_id}/balances          # Current/available/booked
GET    /accounts/{account_id}/transactions      # Paginated; uses continuation_key
GET    /accounts/{account_id}/transactions/{transaction_id}  # Extra details
```

Auth header on every request:
`Authorization: Bearer <JWT signed with your RSA private key>`

JWT max TTL: 86 400 s (24 h).

---

## Useful links

- [Enable Banking docs home](https://enablebanking.com/docs/)
- [API reference](https://enablebanking.com/docs/api/reference/)
- [FAQ](https://enablebanking.com/docs/faq/)
- [Terms of Service](https://enablebanking.com/terms/)
- [Whitelisting own accounts](https://enablebanking.com/docs/api/linked-accounts/)
- [Code samples on GitHub](https://github.com/enablebanking/enablebanking-api-samples)
- [Control Panel — applications](https://enablebanking.com/cp/applications)
- [Data Insights (per-ASPSP history availability)](https://enablebanking.com/cp/data-insights)
- [End-user consent management](https://enablebanking.com/data-sharing-consents/)