# Snapshot Post-Upload Pipeline Design

## Goal

Replace the hard-coded snapshot post-upload sequence with one typed registration and execution mechanism shared by core server processors and server plugins.

## Boundaries

- Snapshot identity, lineage, gameplay continuity, raid-event identity, and payload validation remain pre-acceptance validation concerns.
- The pipeline runs only after a snapshot has been durably accepted.
- Processors must be idempotent for a snapshot ID. Existing registries and ledgers remain the source of idempotency rather than adding a second execution ledger.
- Client login, disconnect, and colony-abandonment lifecycle hooks remain separate. Snapshot-specific pending-operation confirmation moves into the snapshot pipeline.
- No protocol contract change is required. The durable deferred-job outbox advances the server database schema from version 4 to version 5 through the existing explicit migration system.

## Registration Model

`ISnapshotPostUploadProcessor` declares a stable ID, stage, order, execution mode, failure mode, and supported snapshot kinds. Server plugin descriptors and runtime plugin contexts can register processors. `ServerPluginRegistry` filters plugin processors using the same compatibility manifest rules as other plugin capabilities.

Core processors are registered by the network server and combined with active plugin processors for each invocation. Processor IDs are globally unique. Ordering is deterministic by stage, order, then processor ID.

## Stages

1. `AuthoritativeProjection`: latest snapshot reference, world tile layers, colony site, and world configuration extensions.
2. `DerivedMetrics`: wealth cache, snapshot metrics, and achievements.
3. `EventReconciliation`: support-pawn death detection and pending bank or mercenary confirmation.
4. `Notification`: reserved for processors whose only responsibility is notification or downstream signalling.

## Snapshot Kinds

- `AuthoritativeColonySnapshot` runs all current authoritative processors.
- `RaidSettlementEvidence` runs only processors that explicitly opt in. Initially this is support-pawn death detection and pending-operation confirmation, preserving current behavior.

## Failure Handling

- `AbortPipeline` logs processor identity and rethrows, preserving synchronous failure behavior for required core state transitions.
- `ContinuePipeline` logs the failure and continues. This is suitable only for optional compatibility projections or diagnostics.
- A processor result is not allowed to reinterpret an accepted snapshot as rejected. Endpoint error handling must continue to distinguish upload rejection from post-processing failure.

## Execution Modes

- `Inline` processors complete before the server responds. They are used for authoritative projections and event confirmation that subsequent requests must observe immediately.
- `Deferred` processors capture a compact processor-owned JSON payload and enqueue it in a persistent SQLite outbox before the server responds.
- Deferred processors never receive or persist the complete RWS or snapshot index. A processor that requires the complete accepted snapshot must remain inline.
- A hosted worker resolves the processor by stable ID, executes ready jobs, removes successful jobs, and retries failures with bounded exponential backoff after server restart.
- `FailureMode` governs inline execution and deferred payload capture/enqueue. Once a deferred job is durable, execution failures are retried rather than changing the already returned upload response.

## Testing

Network smoke tests verify deterministic ordering, kind filtering, manifest filtering, continue-on-error behavior, and abort-on-error behavior. Existing end-to-end smoke coverage verifies that migrated core processing remains reachable from snapshot upload endpoints.
