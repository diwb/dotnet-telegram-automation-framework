# Decisions

- Use .NET 10 and C# modern nullable-enabled projects.
- Keep Core free from Telegram.Bot concrete types.
- Use HTTP adapter plus Telegram.Bot package compatibility reference.
- Use Fake API as the default validation path to avoid real tokens.
- Use local scheduler/outbox rather than Hangfire/Quartz for zero external dependencies.
