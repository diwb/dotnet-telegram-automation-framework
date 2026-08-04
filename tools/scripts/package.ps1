param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Remove-Item -Recurse -Force artifacts/packages -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force artifacts/packages | Out-Null
$projects = @(
  "src/TelegramAutomation.Abstractions/TelegramAutomation.Abstractions.csproj",
  "src/TelegramAutomation.Core/TelegramAutomation.Core.csproj",
  "src/TelegramAutomation.Telegram/TelegramAutomation.Telegram.csproj",
  "src/TelegramAutomation.Polling/TelegramAutomation.Polling.csproj",
  "src/TelegramAutomation.Webhooks/TelegramAutomation.Webhooks.csproj",
  "src/TelegramAutomation.Persistence.SQLite/TelegramAutomation.Persistence.SQLite.csproj",
  "src/TelegramAutomation.Storage.InMemory/TelegramAutomation.Storage.InMemory.csproj",
  "src/TelegramAutomation.Storage.Sqlite/TelegramAutomation.Storage.Sqlite.csproj",
  "src/TelegramAutomation.Scheduling/TelegramAutomation.Scheduling.csproj",
  "src/TelegramAutomation.Observability/TelegramAutomation.Observability.csproj",
  "src/TelegramAutomation.Cli/TelegramAutomation.Cli.csproj"
)
foreach ($project in $projects) {
  dotnet pack $project -c $Configuration --no-build -o artifacts/packages -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg -p:PackageVersion=1.0.0
}
Get-ChildItem artifacts/packages -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($_.Path))" } | Set-Content artifacts/packages/SHA256SUMS.txt
