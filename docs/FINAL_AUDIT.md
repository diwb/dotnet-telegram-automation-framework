# Final Audit

Date: 2026-08-03
OS: Windows 10.0.26200
SDK: .NET SDK 10.0.110
Repository URL: git@github.com-diwb:diwb/dotnet-telegram-automation-framework.git
Current branch: fix/telegram-framework-hardening
Telegram Bot API version: 10.2, 2026-07-14
Telegram.Bot package version: 22.10.2.1

## Commands Executed

- `dotnet build TelegramAutomation.slnx -c Release`
- `dotnet test TelegramAutomation.slnx -c Release --no-build`
- `dotnet test tests/TelegramAutomation.UnitTests/TelegramAutomation.UnitTests.csproj -c Release --no-build /p:CollectCoverage=true ... /p:Threshold=75`
- `dotnet test tests/TelegramAutomation.IntegrationTests/TelegramAutomation.IntegrationTests.csproj -c Release --no-build /p:CollectCoverage=true ... /p:Threshold=70`
- `dotnet list TelegramAutomation.slnx package --vulnerable --include-transitive`
- `docker compose config`
- `docker compose build`
- `docker compose up -d`, health checks on ports 5080 and 5090, `docker compose down -v`
- `dotnet run --project src/TelegramAutomation.Cli -- doctor`
- `dotnet run --project src/TelegramAutomation.Cli -- fake-api health`
- `dotnet run --project src/TelegramAutomation.Cli -- bot run-polling`
- `dotnet run --project src/TelegramAutomation.Cli -- bot run-webhook`
- `./tools/scripts/package.ps1`
- token scan with `rg` for Telegram token-shaped literals

## Test Counts

- Unit: 75
- Integration: 6
- Security: 17
- Architecture: 19
- Total: 117

## Coverage

Core module coverage gate from UnitTests:

- TelegramAutomation.Abstractions: 91.3% line
- TelegramAutomation.Cli: 72.46% line
- TelegramAutomation.Core: 90.37% line
- TelegramAutomation.Polling: 74.54% line
- TelegramAutomation.Scheduling: 91.66% line
- TelegramAutomation.Storage.InMemory: 86.2% line
- TelegramAutomation.Webhooks: 100% line
- Total: 85.04% line

SQLite persistence gate from IntegrationTests:

- TelegramAutomation.Persistence.SQLite: 80.1% line

## Evidence

- Fake API HTTP integration: getMe, getUpdates, sendMessage with inline keyboard, callback update, setWebhook, deliver-webhook simulation, rate-limit response, retryable and non-retryable errors.
- Polling evidence: offset read/save, duplicate suppression, retry-after backoff, exception backoff and cancellation path.
- Webhook evidence: secret validation, invalid secret, replay/idempotency, payload limit, malformed update, callback data size and SSRF/allowed-host validation.
- SQLite evidence: schema initialization, idempotency, conversation state, outbox status, schedules, checkpoint restart and concurrent idempotency race.
- Docker evidence: compose config passed, compose build passed, both containers returned `{ "status": "healthy" }` locally.
- CLI evidence: doctor, fake-api health, bot run-polling and bot run-webhook returned exit code 0.
- Package audit: no vulnerable NuGet packages reported.
- Secret scan: no Telegram token-shaped literals in source/docs excluding generated outputs and artifacts.

## Packages

Generated in `artifacts/packages`:

- TelegramAutomation.Abstractions.1.0.0.nupkg / .snupkg
- TelegramAutomation.Core.1.0.0.nupkg / .snupkg
- TelegramAutomation.Telegram.1.0.0.nupkg / .snupkg
- TelegramAutomation.Polling.1.0.0.nupkg / .snupkg
- TelegramAutomation.Webhooks.1.0.0.nupkg / .snupkg
- TelegramAutomation.Persistence.SQLite.1.0.0.nupkg / .snupkg
- TelegramAutomation.Storage.InMemory.1.0.0.nupkg / .snupkg
- TelegramAutomation.Storage.Sqlite.1.0.0.nupkg / .snupkg
- TelegramAutomation.Scheduling.1.0.0.nupkg / .snupkg
- TelegramAutomation.Observability.1.0.0.nupkg / .snupkg
- TelegramAutomation.Cli.1.0.0.nupkg / .snupkg
- SHA256SUMS.txt

## Remote Publication

Remote publication, GitHub CI/CodeQL run URLs, default branch verification and GitHub release URL are filled after push and release creation.
