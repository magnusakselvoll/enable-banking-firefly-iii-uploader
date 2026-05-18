# Enable Banking → Firefly III Uploader

Downloads transactions from [Enable Banking](https://enablebanking.com/) and uploads them to a self-hosted [Firefly III](https://www.firefly-iii.org/) instance on a configurable schedule, running as a Docker container. It also provides a built-in web UI for registering bank consents (sessions).

## Prerequisites

- A production Enable Banking application with accounts linked in the Control Panel
- The RSA private key used to sign Enable Banking JWTs (generated when setting up your application)
- An externally-reachable HTTPS URL for the container (e.g. via [Tailscale serve](https://tailscale.com/kb/1242/tailscale-serve)) — required for bank registration, since Enable Banking only accepts `https://` redirect URLs
- A Firefly III instance with asset accounts whose IBANs match your Enable Banking accounts

## Configuration

All settings are supplied via environment variables. Place your RSA private key at `./secrets/enablebanking.pem` (this directory is gitignored).

| Environment variable | Required | Default | Description |
|---|---|---|---|
| `EnableBankingUploader__EnableBankingApplicationId` | yes | — | Enable Banking application UUID |
| `EnableBankingUploader__EnableBankingPrivateKeyPath` | yes | — | Path to RSA private key PEM inside container |
| `EnableBankingUploader__FireflyIiiUrl` | yes (unless WhatIf offline) | — | Base URL of Firefly III (e.g. `http://firefly:8080`) |
| `EnableBankingUploader__FireflyIiiToken` | yes | — | Firefly III personal access token |
| `EnableBankingUploader__PublicBaseUrl` | yes | — | External HTTPS base URL (e.g. `https://eb.my-tailnet.ts.net`) — used to construct the Enable Banking redirect URL |
| `EnableBankingUploader__SessionStorePath` | no | `/data/sessions` | Directory where bank session files are stored (map to a volume) |
| `EnableBankingUploader__WebListenUrl` | no | `http://0.0.0.0:8080` | Internal Kestrel bind URL |
| `EnableBankingUploader__Schedule` | no | `0 18 * * *` | Cron expression (UTC) for the sync schedule |
| `EnableBankingUploader__LookbackDays` | no | `1` | Extra days to look back for late-arriving transactions |
| `EnableBankingUploader__WhatIf` | no | `false` | Preview mode — no writes. With `FireflyIiiUrl` set: reads Firefly for account matching, cutoff date, and dedup, then logs each transaction as `WOULD IMPORT` or `SKIP DUPLICATE`. Without `FireflyIiiUrl`: fully offline — fetches all Enable Banking history and logs it, no Firefly contact. |

## Bank registration — first-time setup

The container runs both the cron sync **and** a bank management web UI. Sessions (bank consents) are obtained through the UI and stored to disk — there are no session IDs to configure manually.

**Before you start:**

1. In your Enable Banking Control Panel, add `<PublicBaseUrl>/callback` to your application's **Redirect URLs**. This must be `https://` — Enable Banking rejects plain `http://`.
2. Start the container (see below).
3. Open `<PublicBaseUrl>` in your browser.
4. Click **Register new bank**, pick your bank, and complete the consent flow.
5. Repeat for each additional bank.

Sessions are stored in `SessionStorePath` as JSON files and persist across container restarts. The UI shows each bank's validity status (Enable Banking consents last up to 180 days depending on the bank). Use the **Re-authorize** button before a session expires.

## Running with Docker Compose

Create a `.env` file (never commit this):

```env
EB_APPLICATION_ID=<your-application-uuid>
FIREFLY_URL=http://firefly:8080
FIREFLY_TOKEN=<your-personal-access-token>
PUBLIC_BASE_URL=https://eb.my-tailnet.ts.net
```

Place your RSA private key at `./secrets/enablebanking.pem`, then start the container:

```bash
docker compose up -d
```

### Tailscale example

If you expose the container via Tailscale serve:

```bash
tailscale serve https / http://127.0.0.1:8080
```

Then set `PUBLIC_BASE_URL=https://<your-tailnet-name>.ts.net` and register `https://<your-tailnet-name>.ts.net/callback` as a Redirect URL in the Control Panel.

The `docker-compose.yml` expects an external Docker network named `firefly_iii`. Create it once:

```bash
docker network create firefly_iii
```

See [`docs/enable_banking_reference.md`](docs/enable_banking_reference.md) for Enable Banking API notes, rate limits, consent validity, and error handling guidance.

## Account matching

Accounts are matched by IBAN between Enable Banking and Firefly III. Spaces and dashes in IBANs are normalized before comparison. Unmatched accounts are logged as warnings but do not cause failure.

## Idempotency

Enable Banking's unique transaction identifiers are stored as `external_id` on Firefly III transactions. Re-running the tool over the same date range never creates duplicates.

## Run labels

Every sync run stamps each transaction it creates with a Firefly III tag of the form `eb-sync-2026-05-18T18:00:00Z` (the UTC start time of that run). If a run goes wrong, you can find and bulk-delete its transactions in Firefly III by filtering on that tag. The run label is also printed in the end-of-run log summary line, so you can identify it immediately from the container logs.

## Building locally

```bash
dotnet build
dotnet test
dotnet run --project src/EnableBankingUploader.Cli
```

The web UI is available at `http://localhost:8080` when running locally (set `EnableBankingUploader__PublicBaseUrl` if you want to test the full bank registration flow).

## Building and publishing the container image

The project uses the .NET SDK's built-in container support — no Dockerfile needed.

Releases are published automatically: push a `vX.Y.Z` tag and the [Release workflow](.github/workflows/release.yml) builds and pushes both a versioned tag and `:latest` to `ghcr.io/magnusakselvoll/enable-banking-firefly-iii-uploader`.

```bash
# Build and push to ghcr.io manually
dotnet publish src/EnableBankingUploader.Cli -c Release -t:PublishContainer -p:ContainerRegistry=ghcr.io

# Build locally (without pushing)
dotnet publish src/EnableBankingUploader.Cli -c Release -t:PublishContainer
```
