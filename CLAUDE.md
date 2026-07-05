# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

This tool downloads transactions from Enable Banking and uploads them to Firefly III on a configurable schedule. It runs as a long-lived Docker container with two concurrent concerns: a cron-based sync job and a bank management web UI for registering/revoking bank consents.

## Issue Tracking

Issues are tracked in GitHub. Use `gh issue list` to see open issues and `gh issue view <number>` for details.

## Git Workflow (GitHub Flow)

Always use GitHub Flow when working on issues:

1. **Create a feature branch** before making any file edits — no exceptions:
   - First fetch and checkout latest main: `git fetch origin && git checkout main && git pull`
   - Branch name format: `<issue-number>-<short-description>` (e.g., `1-initial-implementation`)
   - Create and checkout the branch: `git checkout -b 1-initial-implementation`
   - **Do not read or edit any files until the branch is created.** This prevents accidentally committing to main (direct pushes to main are blocked).
   - **Only use worktrees** when explicitly asked (e.g., "use a worktree", "work on several issues in parallel")

2. **Commit** changes with descriptive messages:
   - Write commit messages as plain double-quoted strings — no heredocs, no `$()` substitution
   - Each `-m` value must be a single line — newlines inside a `-m` string cause a "quoted characters in flag names" error
   - For multi-line messages use separate `-m` flags, one per line: `git commit -m "title" -m "body line"`

3. **Push** the branch and **create a PR**:
   - **Ask before creating the PR** - the user may have feedback based on the console output or code
   - PR title should be descriptive of the change
   - Reference the issue in the PR body with `Closes #<issue-number>` to auto-close on merge
   - Pass `--title` and `--body` as plain strings to `gh pr create` — no heredocs, no command substitution, and no backticks (backticks in strings trigger a command substitution approval prompt even when used as markdown formatting)
   - Always pass `--head <branch-name> --base main` to `gh pr create` — without these, `gh` picks up the main repo context and fails with "head branch is the same as base branch"

4. **Merge** after review (squash merge preferred for clean history)

5. **Clean up** after the user confirms a PR is merged:
   - `git fetch origin && git checkout main && git pull`
   - `git branch -d <branch-name>`

### Worktree usage (only when explicitly requested)

When the user asks to use a worktree or work on multiple issues in parallel:
   - Create a worktree: `git worktree add .claude/worktrees/1-initial-implementation -b 1-initial-implementation`
   - All file reads/edits/writes must use the full worktree path, e.g. `.claude/worktrees/<branch-name>/src/...`
   - Run all git commands in the worktree using `-C`: `git -C .claude/worktrees/<branch-name> <command>`
   - Do NOT use `cd .claude/worktrees/<branch-name> && git ...` — compound `cd` + `git` commands require special approval
   - Cleanup: `git -C <repo-root> worktree remove .claude/worktrees/<branch-name>` then `git -C <repo-root> branch -d <branch-name>`

## Documentation Updates

When closing issues via PR, consider updating:
- **README.md** — Setup instructions, configuration, deployment, user-facing changes
- **CLAUDE.md** — Architecture, configuration keys, build commands, design decisions

## Build Commands

```bash
dotnet build                                        # Build all projects
dotnet test                                         # Run all tests
dotnet test --filter "FullyQualifiedName~MyTest"    # Run a single test by name

dotnet run --project src/EnableBankingUploader.Cli  # Run (web UI on http://localhost:8080)

# Override config via env vars (double underscore = section separator):
EnableBankingUploader__FireflyIiiUrl=http://localhost:8080 \
  EnableBankingUploader__FireflyIiiToken=my-token \
  EnableBankingUploader__PublicBaseUrl=https://eb.example.ts.net \
  dotnet run --project src/EnableBankingUploader.Cli
```

## Architecture

Two-project layout:

- **EnableBankingUploader.Core** (`src/EnableBankingUploader.Core/`): Enable Banking API client, Firefly III API client, account matching, transaction deduplication, retry logic, file-backed session store. No dependency on the CLI.
- **EnableBankingUploader.Cli** (`src/EnableBankingUploader.Cli/`): ASP.NET Core web host (`Microsoft.NET.Sdk.Web`). Runs two things concurrently:
  - `SyncScheduler` (a `BackgroundService`) — cron-driven transaction sync.
  - Minimal API endpoints (`Web/BankRegistrationEndpoints.cs`, `Web/ManualSyncEndpoints.cs`) — bank management UI and manual sync UI at `WebListenUrl`.

See [`docs/enable_banking_reference.md`](docs/enable_banking_reference.md) for Enable Banking API notes, rate limits, acceptable use, and error handling guidance.

### Key design decisions

- **Session acquisition**: Enable Banking sessions are created via `POST /auth` → bank consent → `POST /sessions`. Session IDs do **not** appear in the Control Panel and cannot be configured manually. They are obtained through the web UI and stored on disk.
- **Session storage**: one JSON file per session in `SessionStorePath`. The cron job reads sessions from disk; the web UI writes them. Access control for the UI relies on the network (e.g. Tailscale ACLs) rather than app-level auth.
- **Redirect URL**: must be `https://` (Enable Banking rejects `http://`). The container serves plain HTTP internally; TLS is supplied by an external reverse proxy (Tailscale serve, nginx, etc.). `PublicBaseUrl` tells the app its external URL to construct `redirect_url = PublicBaseUrl + "/callback"`.
- **Account matching**: normalize IBANs by removing spaces and separators before comparing Enable Banking accounts to Firefly III asset accounts.
- **Query order**: query Firefly III first to determine the date range needed, minimizing Enable Banking API calls.
- **Lookback window**: always include a one-day lookback when querying Enable Banking to catch late-arriving transactions.
- **Deduplication**: use Enable Banking's unique transaction identifiers to skip transactions already present in Firefly III — safe to re-run without creating duplicates. Two layers: a cheap batched date-window query builds a set of existing `external_id`s (fast path), backstopped by a direct Firefly `external_id` search (`GET /api/v1/search/transactions?query=external_id_is:"..."`, `FireflyIiiClient.ExistsByExternalIdAsync`) that runs only when the fast path misses. The search is needed because the EB fetch is anchored on **booking date** while Firefly stores transactions under their value/transaction date; a transaction with `value_date < booking_date` can sit outside the date window yet keep being re-fetched, so without the search it would be recreated every run (issue #24). The search confirms an exact `external_id` match client-side to stay correct across Firefly versions.
- **Run labels**: each sync run generates a Firefly III tag (`eb-sync-<UTC timestamp>`, e.g. `eb-sync-2026-05-18T18:00:00Z`) and stamps it on every transaction created in that run. The label is also logged in the end-of-run summary. Prefix is hardcoded; no config key.
- **Retry logic**: use Polly for transient API failures on both Enable Banking and Firefly III calls.
- **Rate limits**: Enable Banking API calls are rate-limited (background fetches ~4/day/account). Firefly III is self-hosted and not rate-limited; GET requests can be made freely. Minimize Enable Banking calls — in particular the manual sync fetches transactions from Enable Banking exactly once (at the preview step) and reuses the computed plan for execution.
- **Plan/execute split** (from issue #14): `TransactionSyncer.BuildPlanAsync` computes the full set of transactions that would be synced (with per-transaction decisions: Create / SkipDuplicate / SkipNonBooked / SkipNoId) without writing anything. `ExecutePlanAsync` writes the Create transactions to Firefly III and acquires a global `SyncGate` lock so manual and scheduled runs cannot overlap. Both the cron scheduler and the manual web flow use these same two methods — no separate code paths.
- **Manual sync flow** (from issue #14): three-step web UI at `/manual-sync`: (1) select accounts by checkbox, (2) preview transactions (fetches EB once), (3) confirm to execute. Plan held in `ManualSyncState` singleton with a 15-min TTL so execution reuses the previewed plan without re-fetching Enable Banking.
- **Notes field**: Firefly III transaction notes capture the full raw Enable Banking context — all `remittance_information` lines, `entry_reference` (when not already used as the description), `transaction_id`, and creditor/debtor names. Description stays as the first remittance line (or EntryReference fallback) so list views are unchanged.
- **Logging format** (from issue #21): the container (Production environment) emits structured JSON — one line per log event, so stack traces land as a single Loki entry rather than many lines. Local `dotnet run` (Development, via `Properties/launchSettings.json`) uses the human-readable simple console formatter with scopes shown inline. The formatter is selected by `builder.Environment.IsDevelopment()` in `Program.cs`; no appsettings key controls it. Log-level defaults live in `appsettings.json` under `Logging:LogLevel`. All log calls use named message-template parameters; never string interpolation. `IncludeScopes = true` on both formatters. Scope keys: `RunLabel` (per sync run, from `TransactionSyncer`), `AccountUid` + `Bank` (per account), `SessionId` (delete-session endpoint), `Aspsp` + `Country` (bank registration endpoints), `Source=manual` (manual sync web flow).

## Tech Stack

- **Language/Runtime**: C# / .NET 10
- **Web host**: ASP.NET Core (`Microsoft.NET.Sdk.Web`, Kestrel, minimal APIs)
- **HTTP clients**: Hand-written wrappers (`EnableBankingClient`, `FireflyIiiClient`)
- **Retry**: Polly
- **Configuration**: Microsoft.Extensions.Configuration (JSON + Environment Variables)
- **Logging**: Microsoft.Extensions.Logging — JSON console formatter in Production (container), simple readable console in Development (`dotnet run`)
- **Testing**: MSTest
- **Deployment**: Docker via .NET SDK container publish (`dotnet publish -t:PublishContainer`) — no Dockerfile needed; uses the `aspnet` runtime base image

## Dependency Policy

Minimize external dependencies. Only add well-established, widely-used libraries when genuinely needed.

## Coding Conventions

- Use nullable reference types (`<Nullable>enable</Nullable>`)
- Prefer records for DTOs
- Use `CancellationToken` for async operations where applicable
- Tests use MSTest with descriptive method names
- MSTest analyzers enforce strict assertion methods (MSTEST0037 is an error, not a warning):
  - Use `Assert.HasCount(expected, collection)` not `Assert.AreEqual(expected, collection.Count)`
  - Use `Assert.IsEmpty(collection)` not `Assert.AreEqual(0, collection.Count)`

## Configuration

| Key | Env var | Default | Description |
|-----|---------|---------|-------------|
| `EnableBankingUploader:EnableBankingApplicationId` | `EnableBankingUploader__EnableBankingApplicationId` | *(required)* | Enable Banking application UUID (used as JWT issuer/kid) |
| `EnableBankingUploader:EnableBankingPrivateKeyPath` | `EnableBankingUploader__EnableBankingPrivateKeyPath` | *(required)* | Path to RSA private key PEM file for Enable Banking JWT signing |
| `EnableBankingUploader:FireflyIiiUrl` | `EnableBankingUploader__FireflyIiiUrl` | *(required)* | Base URL of the Firefly III instance |
| `EnableBankingUploader:FireflyIiiToken` | `EnableBankingUploader__FireflyIiiToken` | *(required)* | Firefly III personal access token |
| `EnableBankingUploader:PublicBaseUrl` | `EnableBankingUploader__PublicBaseUrl` | *(required for bank registration)* | External HTTPS base URL (e.g. `https://eb.my-tailnet.ts.net`). The redirect URL sent to Enable Banking is `<PublicBaseUrl>/callback` — register that exact URL in the Control Panel. |
| `EnableBankingUploader:SessionStorePath` | `EnableBankingUploader__SessionStorePath` | `/data/sessions` | Directory where bank session files are stored. Map to a Docker volume to persist across restarts. |
| `EnableBankingUploader:WebListenUrl` | `EnableBankingUploader__WebListenUrl` | `http://0.0.0.0:8080` | Internal Kestrel bind URL. |
| `EnableBankingUploader:Schedule` | `EnableBankingUploader__Schedule` | `0 18 * * *` | Cron expression for sync schedule |
| `EnableBankingUploader:LookbackDays` | `EnableBankingUploader__LookbackDays` | `1` | Extra days to look back for late-arriving transactions |

Place the RSA private key PEM file in the `secrets/` directory (gitignored). The `docker-compose.yml` mounts `./secrets/enablebanking.pem` into the container at the configured path.

## Running via Docker Compose

The container runs as a long-lived service (cron sync + bank management web UI):

```bash
docker compose up -d
```

Then open `<PublicBaseUrl>` in your browser to register banks. Sessions are persisted to the `sessions` Docker volume and survive container restarts.

## Idempotency

Each sync run uses Enable Banking's unique transaction identifiers to check whether a transaction already exists in Firefly III before creating it. Re-running the tool against the same time window produces no duplicate transactions.
