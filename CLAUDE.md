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
  - Minimal API endpoints (`Web/BankRegistrationEndpoints.cs`) — bank management UI at `WebListenUrl`.

See [`docs/enable_banking_reference.md`](docs/enable_banking_reference.md) for Enable Banking API notes, rate limits, acceptable use, and error handling guidance.

### Key design decisions (from issue #1)

- **Session acquisition**: Enable Banking sessions are created via `POST /auth` → bank consent → `POST /sessions`. Session IDs do **not** appear in the Control Panel and cannot be configured manually. They are obtained through the web UI and stored on disk.
- **Session storage**: one JSON file per session in `SessionStorePath`. The cron job reads sessions from disk; the web UI writes them. Access control for the UI relies on the network (e.g. Tailscale ACLs) rather than app-level auth.
- **Redirect URL**: must be `https://` (Enable Banking rejects `http://`). The container serves plain HTTP internally; TLS is supplied by an external reverse proxy (Tailscale serve, nginx, etc.). `PublicBaseUrl` tells the app its external URL to construct `redirect_url = PublicBaseUrl + "/callback"`.
- **Account matching**: normalize IBANs by removing spaces and separators before comparing Enable Banking accounts to Firefly III asset accounts.
- **Query order**: query Firefly III first to determine the date range needed, minimizing Enable Banking API calls.
- **Lookback window**: always include a one-day lookback when querying Enable Banking to catch late-arriving transactions.
- **Deduplication**: use Enable Banking's unique transaction identifiers to skip transactions already present in Firefly III — safe to re-run without creating duplicates.
- **Run labels**: each sync run generates a Firefly III tag (`eb-sync-<UTC timestamp>`, e.g. `eb-sync-2026-05-18T18:00:00Z`) and stamps it on every transaction created in that run. The label is also logged in the end-of-run summary. Prefix is hardcoded; no config key.
- **Retry logic**: use Polly for transient API failures on both Enable Banking and Firefly III calls.

## Tech Stack

- **Language/Runtime**: C# / .NET 10
- **Web host**: ASP.NET Core (`Microsoft.NET.Sdk.Web`, Kestrel, minimal APIs)
- **HTTP clients**: Hand-written wrappers (`EnableBankingClient`, `FireflyIiiClient`)
- **Retry**: Polly
- **Configuration**: Microsoft.Extensions.Configuration (JSON + Environment Variables)
- **Logging**: Microsoft.Extensions.Logging (console)
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
| `EnableBankingUploader:FireflyIiiUrl` | `EnableBankingUploader__FireflyIiiUrl` | *(required unless WhatIf offline)* | Base URL of the Firefly III instance |
| `EnableBankingUploader:FireflyIiiToken` | `EnableBankingUploader__FireflyIiiToken` | *(required)* | Firefly III personal access token |
| `EnableBankingUploader:PublicBaseUrl` | `EnableBankingUploader__PublicBaseUrl` | *(required for bank registration)* | External HTTPS base URL (e.g. `https://eb.my-tailnet.ts.net`). The redirect URL sent to Enable Banking is `<PublicBaseUrl>/callback` — register that exact URL in the Control Panel. |
| `EnableBankingUploader:SessionStorePath` | `EnableBankingUploader__SessionStorePath` | `/data/sessions` | Directory where bank session files are stored. Map to a Docker volume to persist across restarts. |
| `EnableBankingUploader:WebListenUrl` | `EnableBankingUploader__WebListenUrl` | `http://0.0.0.0:8080` | Internal Kestrel bind URL. |
| `EnableBankingUploader:Schedule` | `EnableBankingUploader__Schedule` | `0 18 * * *` | Cron expression for sync schedule |
| `EnableBankingUploader:LookbackDays` | `EnableBankingUploader__LookbackDays` | `1` | Extra days to look back for late-arriving transactions |
| `EnableBankingUploader:WhatIf` | `EnableBankingUploader__WhatIf` | `false` | Preview mode — no writes. With `FireflyIiiUrl` set: reads Firefly for account mapping, cutoff date, and dedup, then logs `[WHATIF] WOULD IMPORT` / `[WHATIF] SKIP DUPLICATE` per transaction. Without `FireflyIiiUrl`: fully offline — fetches Enable Banking history from `2000-01-01` and logs each booked transaction, no Firefly contact. |

Place the RSA private key PEM file in the `secrets/` directory (gitignored). The `docker-compose.yml` mounts `./secrets/enablebanking.pem` into the container at the configured path.

## Running via Docker Compose

The container runs as a long-lived service (cron sync + bank management web UI):

```bash
docker compose up -d
```

Then open `<PublicBaseUrl>` in your browser to register banks. Sessions are persisted to the `sessions` Docker volume and survive container restarts.

## Idempotency

Each sync run uses Enable Banking's unique transaction identifiers to check whether a transaction already exists in Firefly III before creating it. Re-running the tool against the same time window produces no duplicate transactions.
