# Asynchronous Raid Settlement Design

## Goal

Use player-raid settlement as the first production `Deferred` snapshot post-upload processor. Snapshot evidence acceptance returns promptly, while durable background work computes losses, edits the defender snapshot, records settlement events, and releases the defender login lock.

## Commit Boundary

An online settlement request is accepted only after all of the following succeed:

1. The raid settlement evidence snapshot passes the existing identity, lineage, event-ID, and map validation.
2. The immutable evidence package is durably written to an operation-artifact store.
3. The deferred outbox job is durably inserted.
4. The evidence snapshot is installed as the attacker's latest authoritative snapshot and its latest-snapshot reference is updated.

The endpoint then returns an accepted, queued result. The client may close the battle map and make its best-effort post-battle save. The response means that settlement responsibility has transferred to the server; it does not mean that defender snapshot editing has already finished.

If any commit step fails, the endpoint returns failure and compensates by removing the outbox job and artifact where possible. Repeating the same request is idempotent by raid event ID and evidence snapshot ID.

## Processor Selection and Short Circuiting

`core.raid-settlement` is a `Deferred` processor that supports only `SnapshotPostUploadKind.RaidSettlementEvidence`. Ordinary autosaves and all other authoritative colony snapshots are filtered out before payload preparation, artifact persistence, or settlement lookup.

The existing authoritative projection processors continue to skip raid settlement evidence. Support-pawn loss detection remains inline because the submitted snapshot must not be acknowledged before its directly observable pawn-loss consequences have been validated.

## Durable Inputs

The SQLite outbox contains only compact operation metadata:

- source raid event ID;
- attacker and defender user/colony IDs;
- evidence snapshot ID;
- defender base snapshot ID;
- evidence artifact key;
- settlement origin (`OnlineEvidence` or `OfflineTimeout`);
- submitted client application result when present.

The complete RWS payload is stored once as an immutable snapshot-package artifact outside SQLite. The artifact uses the existing snapshot package encoding and is deleted only after terminal settlement success. It is never placed in the JSON outbox row.

## Background Settlement Flow

The deferred processor performs these steps:

1. Reload the source raid event and return successfully if an equivalent settlement is already recorded.
2. Verify that the source raid is still a player raid requiring settlement and that the job identities match the event.
3. Load the immutable evidence artifact and the defender snapshot identified at raid creation.
4. Resolve the raid map by embedded raid event ID, then calculate the settlement diff.
5. Apply losses to a new defender snapshot package.
6. Under `RaidSettlementSnapshotMutationGate`, re-check the source raid, store the defender snapshot, update the defender latest-snapshot reference, record the settlement event, and mark the source raid applied.
7. Signal settlement notifications and remove the outbox job and evidence artifact.

The worker must not overwrite a newer attacker snapshot. The attacker evidence is installed before the request returns; subsequent post-battle saves advance normally from that snapshot.

## Login Locks

No separate lock table is introduced. The existing defender login check derives its lock from an unsettled source raid. Enqueuing does not mark the source raid applied, finished, failed, or cancelled, so the defender remains locked throughout queued and retrying states.

The lock is released only when the background transaction records a terminal settlement and updates the source raid. Server restart is safe because both the outbox row and evidence artifact are durable.

## Failure Handling

- Transient I/O, SQLite, and snapshot-edit failures throw from the deferred processor and use the existing bounded exponential retry schedule. The source raid remains unsettled and the defender remains locked.
- A missing processor or artifact is a manual-review condition. It remains visible in the outbox and server diagnostics instead of silently unlocking the defender.
- A deterministic identity or event mismatch is recorded as manual review and is not converted into a successful settlement.
- Duplicate execution is harmless: the worker re-checks the source raid and existing settlement event before mutating snapshots.
- Administrators can diagnose a stuck job by processor ID, raid event ID, snapshot ID, attempt count, and last error. Administrative recovery is outside this implementation.

## Offline Timeout Reuse

Offline timeout settlement uses the same deferred processor. It copies the selected attacker snapshot into an immutable evidence artifact, enqueues an `OfflineTimeout` job, and leaves both participants governed by the existing unsettled-raid projections until completion. This removes the current duplicate settlement implementation from the timeout path.

## Client Semantics

The accepted response and client text mean "settlement submitted" rather than "settlement completed." The client closes the battle map after durable acceptance. Defender loss notification remains the authoritative completion signal. A lightweight attacker notification is emitted when background settlement finishes or enters manual review.

## Testing

Tests must prove:

- autosaves do not prepare or enqueue raid settlement work;
- online evidence is durably queued before the endpoint reports success;
- the defender remains login-locked while the job is queued or retrying;
- a successful worker run edits the defender snapshot, records settlement, clears the lock, and removes the artifact/job;
- a transient worker failure preserves the job, artifact, and login lock;
- duplicate execution does not apply losses twice;
- offline timeout uses the same processor and produces the same settlement state;
- restart reloads and completes a pending settlement job.
