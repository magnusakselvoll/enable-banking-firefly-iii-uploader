# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

This tool downloads transactions from Enable Banking and uploads them to Firefly III, running on a configurable schedule inside a Docker container.

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

dotnet run --project src/EnableBankingUploader.Cli  # Run with default config

# Override config via env vars (double underscore = section separator):
EnableBankingUploader__FireflyIiiUrl=http://localhost:8080 \
  EnableBankingUploader__FireflyIiiToken=my-token \
  dotnet run --project src/EnableBankingUploader.Cli
```

## Architecture

Two-project layout:

- **EnableBankingUploader.Core** (`src/EnableBankingUploader.Core/`): Enable Banking API client, Firefly III API client, account matching, transaction deduplication, retry logic. No dependency on the CLI.
- **EnableBankingUploader.Cli** (`src/EnableBankingUploader.Cli/`): Console executable. Reads configuration, drives the sync loop, schedules execution.

### Key design decisions (from issue #1)

- **Account matching**: normalize IBANs by removing spaces and separators before comparing Enable Banking accounts to Firefly III asset accounts.
- **Query order**: query Firefly III first to determine the date range needed, minimizing Enable Banking API calls.
- **Lookback window**: always include a one-day lookback when querying Enable Banking to catch late-arriving transactions.
- **Deduplication**: use Enable Banking's unique transaction identifiers to skip transactions already present in Firefly III — safe to re-run without creating duplicates.
- **Retry logic**: use Polly (or equivalent) for transient API failures on both Enable Banking and Firefly III calls.

## Tech Stack

- **Language/Runtime**: C# / .NET 10
- **HTTP clients**: Generated from Enable Banking OpenAPI spec + Firefly III OpenAPI spec (or hand-written wrappers)
- **Retry**: Polly
- **Configuration**: Microsoft.Extensions.Configuration (JSON + Environment Variables)
- **Logging**: Microsoft.Extensions.Logging (console)
- **Testing**: MSTest
- **Deployment**: Docker (publicly available image)

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
| `EnableBankingUploader:EnableBankingApiKey` | `EnableBankingUploader__EnableBankingApiKey` | *(required)* | Enable Banking production API key |
| `EnableBankingUploader:FireflyIiiUrl` | `EnableBankingUploader__FireflyIiiUrl` | *(required)* | Base URL of the Firefly III instance |
| `EnableBankingUploader:FireflyIiiToken` | `EnableBankingUploader__FireflyIiiToken` | *(required)* | Firefly III personal access token |
| `EnableBankingUploader:Schedule` | `EnableBankingUploader__Schedule` | `0 18 * * *` | Cron expression for sync schedule |
| `EnableBankingUploader:LookbackDays` | `EnableBankingUploader__LookbackDays` | `1` | Extra days to look back for late-arriving transactions |

## Running via Docker Compose

```bash
docker compose run --rm uploader
```

Example cron entry (runs daily at 18:00 via Docker):
```cron
0 18 * * * cd /path/to/enable-banking-firefly-iii-uploader && /usr/local/bin/docker compose run --rm uploader >> /var/log/eb-uploader.log 2>&1
```

## Idempotency

Each sync run uses Enable Banking's unique transaction identifiers to check whether a transaction already exists in Firefly III before creating it. Re-running the tool against the same time window produces no duplicate transactions.
