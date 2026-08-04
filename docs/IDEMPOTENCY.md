# Idempotency

Idempotency keys use `update:{update_id}` or `callback:{callback_query_id}` with TTL protection. Duplicate updates return a deterministic handled response and increment duplicate metrics.
