# Outbox

The outbox stores pending messages with attempts, due time, idempotency key and dead-letter status after repeated failures. `OutboxProcessor` applies exponential backoff.
