# Architecture

The solution separates contracts, routing/middleware, Telegram HTTP integration, fake API, storage, scheduling, observability, CLI and samples. `Abstractions` has no implementation dependency; `Core` avoids Telegram.Bot types; the Telegram adapter owns API transport.
