# Stable Identity Classification Design

## Goal

Remove display-text and substring matching from identity, routing, and workflow decisions found by the July 2026 audit.

## Design

- Main-menu integration is scoped by an explicit `MainMenuDrawer.DoMainMenuControls` execution marker, the verified vanilla option order, and ClashOfRim-owned option types. It never identifies an option from its translated label.
- World objects are classified from exact Core `defName`/runtime class identities, tile-layer structure, or an explicitly registered `IWorldObjectClassifier`. Names and serialized tile text never imply object type.
- All item-arrival events use the authoritative `ItemDelivery` event type. Gift delivery, rejected-gift return, trade delivery, and trade return are serialized `ItemDeliveryPurpose` subtypes; `Message` remains presentation data only.
- Protocol DTOs carry `ServerEventType` and `EventPayloadType` enums. Runtime routing never infers an event or payload type from a string, a CLR type name, or presentation text.
- Player proxy faction ownership comes exclusively from the serialized load-ID-to-user-ID registry. Faction display names are never parsed as identifiers.
- Compatibility issue tabs use an exhaustive `CompatibilityIssueCode` mapping. Unknown codes remain visible in the manifest tab.

## Error Handling

- Missing proxy ownership returns no owner and causes the normal proxy synchronization path to create or bind a valid proxy.
- Unknown compatibility codes are shown as manifest issues instead of being silently omitted.
- Plugin classifier exceptions remain isolated and logged; they cannot fall back to display-name inference.

## Verification

- Shared/server tests cover exact world-object matching, orbital layer routing, item-delivery-purpose serialization, and compatibility-code classification contracts.
- Client source-contract tests ensure translated labels, faction-name parsing, string event-type matching, and message-driven delivery branching are absent.
- Full client and server packaging builds verify the RimWorld/Harmony integration.
