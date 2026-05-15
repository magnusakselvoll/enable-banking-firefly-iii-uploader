# Enable Banking → Firefly III Uploader

Downloads transactions from [Enable Banking](https://enablebanking.com/) and uploads them to a self-hosted [Firefly III](https://www.firefly-iii.org/) instance on a configurable schedule, running as a Docker container.

## Prerequisites

- A production Enable Banking application with bank accounts linked via OAuth consent
- A Firefly III instance with asset accounts whose IBANs match your Enable Banking accounts
- The RSA private key used to sign Enable Banking JWTs (generated when setting up your application)

## Configuration

All secrets are supplied via environment variables. Place your RSA private key at `./secrets/enablebanking.pem` (this directory is gitignored).

| Environment variable | Required | Default | Description |
|---|---|---|---|
| `EnableBankingUploader__EnableBankingApplicationId` | yes | — | Enable Banking application UUID |
| `EnableBankingUploader__EnableBankingPrivateKeyPath` | yes | — | Path to RSA private key PEM inside container |
| `EnableBankingUploader__FireflyIiiUrl` | yes | — | Base URL of Firefly III (e.g. `http://firefly:8080`) |
| `EnableBankingUploader__FireflyIiiToken` | yes | — | Firefly III personal access token |
| `EnableBankingUploader__Schedule` | no | `0 18 * * *` | Cron expression (UTC) |
| `EnableBankingUploader__LookbackDays` | no | `1` | Extra days to look back for late-arriving transactions |

## Running with Docker Compose

Create a `.env` file (never commit this):

```env
EB_APPLICATION_ID=<your-application-uuid>
FIREFLY_URL=http://firefly:8080
FIREFLY_TOKEN=<your-personal-access-token>
```

Place your RSA private key at `./secrets/enablebanking.pem`, then:

```bash
docker compose run --rm uploader
```

The `docker-compose.yml` expects an external Docker network named `firefly_iii`. Create it once:

```bash
docker network create firefly_iii
```

## Account matching

Accounts are matched by IBAN between Enable Banking and Firefly III. Spaces and dashes in IBANs are normalized before comparison. Unmatched accounts are logged as warnings but do not cause failure.

## Idempotency

Enable Banking's unique transaction identifiers are stored as `external_id` on Firefly III transactions. Re-running the tool over the same date range never creates duplicates.

## Building locally

```bash
dotnet build
dotnet test
dotnet run --project src/EnableBankingUploader.Cli
```

## Building and publishing the container image

The project uses the .NET SDK's built-in container support — no Dockerfile needed.

```bash
# Build and push to ghcr.io
dotnet publish src/EnableBankingUploader.Cli -c Release -t:PublishContainer

# Build locally (without pushing)
dotnet publish src/EnableBankingUploader.Cli -c Release -t:PublishContainer -p:ContainerRegistry=
```
