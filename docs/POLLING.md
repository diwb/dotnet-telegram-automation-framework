# Polling

The reference processing model supports offset/idempotency by `update_id`. Fake API exposes `getUpdates` and drains queued updates. A production worker should persist the last committed offset and honor Telegram 429 retry_after.
