# Webhooks

`TelegramAutomation.Samples.WebhookHost` exposes `/telegram/webhook`, validates `X-Telegram-Bot-Api-Secret-Token`, limits payloads to 64 KB and returns quickly after processing. Configure reverse proxies to forward the secret header and enforce HTTPS.
