# Mercenary Guard Activation Notification

## Goal

Notify the defending player when a purchased mercenary guard contract is consumed by a player raid. The notification must remain available when the defender was offline during the raid.

## Event Flow

1. A raid start snapshot is accepted and the raid event is created.
2. The server consumes the defender's active guard contract for that raid.
3. Only when contract consumption succeeds, the server appends a persistent `ServerNotification` event targeted at the defender.
4. The existing event delivery system signals an online defender immediately or delivers the notification after the defender next logs in.

The notification is informational. It requires no player decision and does not participate in raid settlement confirmation.

## Notification Contract

- Event type: `ServerNotification`
- Severity: `Info`
- Online-only: `false`
- Actor: server
- Target: the guard contract owner and colony
- Related event: the raid event ID and `Raid` event type
- Idempotency key and notification ID: `mercenary-guard-activated:<raid-event-id>`
- Title: localized equivalent of `Guard team deployed`
- Message: localized equivalent of `Your hired guard team has deployed to defend against this raid. The contract has been fulfilled.`

The deterministic ID prevents duplicate letters when the raid start request is retried.

## Failure Behavior

- If guard contract consumption fails, no activation notification is created.
- If the notification append is repeated, the ledger returns the existing event and no duplicate is signalled.
- Notification creation does not roll back an already-created raid or an already-consumed contract. A failed append is logged with the existing event append diagnostics.

## Localization

The server localization resources provide the title and message in every currently supported server language. Missing client-side keys are not introduced because the existing server notification letter renders the localized payload text directly.

## Verification

- A successful guard contract consumption creates one persistent notification linked to the raid.
- Retrying the same raid event does not create a second notification.
- A failed contract consumption creates no notification.
- An offline defender can receive the notification on the next event refresh after login.
- Existing raid creation, guard deployment, and settlement behavior remain unchanged.
