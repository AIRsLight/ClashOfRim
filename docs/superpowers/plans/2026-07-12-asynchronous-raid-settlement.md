# Asynchronous Raid Settlement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move online and offline player-raid settlement into the durable deferred snapshot post-upload pipeline without releasing the defender login lock before settlement completes.

**Architecture:** Raid evidence is stored as an immutable operation artifact and referenced by a compact outbox payload. Raid jobs use a two-phase `Prepared -> Ready` state so the attacker latest snapshot, artifact, and outbox can recover after a crash. A deferred core processor loads the artifact, performs settlement under the existing mutation gate, updates both ledgers and snapshots idempotently, then deletes the artifact and job.

**Tech Stack:** C# 12, .NET 8, SQLite, existing snapshot package encoding, ASP.NET Core hosted services, executable smoke tests.

## Global Constraints

- Ordinary autosaves must never prepare, enqueue, or execute raid settlement work.
- The online endpoint may report success only after the evidence artifact, prepared job, attacker latest snapshot, and ready transition are durable.
- A queued or retrying raid remains unsettled, preserving the existing defender login lock.
- Full RWS data is stored only in the file artifact; SQLite stores compact JSON metadata.
- Online and offline timeout settlement use the same deferred processor and settlement executor.
- Duplicate execution must not apply defender losses twice.
- No wire protocol shape change is required; existing response fields carry `SettlementQueued`.

---

### Task 1: Durable Job Preparation State

**Files:**
- Modify: `Source/Server/ClashOfRim.Network/Server/Registries/Snapshots/SnapshotPostUploadJobRegistry.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Registries/Persistence/SqliteStructuredRegistryStores.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Registries/Persistence/ServerDatabaseMigrator.cs`
- Modify: `Tools/ClashOfRim.Save.Tests/Program.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Produces: `SnapshotPostUploadJobState`, `EnqueuePrepared`, `MarkReady`, `ListPrepared`, and schema version 6.
- Preserves: existing `Enqueue` behavior for ordinary deferred processors by inserting them directly as `Ready`.

- [ ] **Step 1: Write failing registry tests**

Add tests that create a prepared job, prove `ListReady` excludes it, call `MarkReady`, and prove it becomes executable. Add a SQLite migration test that starts at schema version 5 and verifies a non-null `job_state` column after migration to version 6.

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet run --project Tools\ClashOfRim.NetworkSmoke\ClashOfRim.NetworkSmoke.csproj -c Debug --no-restore -- --snapshot-pipeline-only
dotnet run --project Tools\ClashOfRim.Save.Tests\ClashOfRim.Save.Tests.csproj -c Debug --no-restore
```

Expected: compilation fails because prepared-job APIs and schema version 6 do not exist.

- [ ] **Step 3: Implement job state and migration**

Add:

```csharp
public enum SnapshotPostUploadJobState
{
    Prepared = 0,
    Ready = 1
}
```

Store the state in every job record. `ListReady` selects only `Ready`; `ListPrepared` selects only `Prepared`; `MarkReady` atomically persists the state before changing memory. Migration `5 -> 6` adds `job_state integer not null default 1`, preserving all existing jobs as ready.

- [ ] **Step 4: Run focused tests**

Run the commands from Step 2. Expected: both pass.

---

### Task 2: Immutable Snapshot Operation Artifacts

**Files:**
- Create: `Source/Shared/ClashOfRim.Save/Snapshots/Packages/SaveSnapshotPackageFileWriter.cs`
- Modify: `Source/Shared/ClashOfRim.Save/Index/Stores/FileColonySnapshotIndexStore.cs`
- Create: `Source/Server/ClashOfRim.Network/Server/Registries/Snapshots/SnapshotPostUploadArtifactStore.cs`
- Modify: `Source/Server/ClashOfRim.Network/Client/ClashOfRimNetworkState.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Core/ClashOfRimNetworkServer.Bootstrap.cs`
- Modify: `Tools/ClashOfRim.Save.Tests/Program.cs`

**Interfaces:**
- Produces: `SaveSnapshotPackageFileWriter.WriteAtomically`, `ISnapshotPostUploadArtifactStore`, `FileSnapshotPostUploadArtifactStore`, and in-memory test implementation.

- [ ] **Step 1: Write failing round-trip tests**

Test that an operation artifact can be written, read through `SaveSnapshotPackageFileReader`, survive a new store instance, and be deleted. Test that an artifact ID containing path separators is rejected.

- [ ] **Step 2: Verify tests fail**

Run the save tests. Expected: missing writer and artifact-store types.

- [ ] **Step 3: Extract the package writer**

Move the existing `CORSPKG1` atomic gzip package writing logic from `FileColonySnapshotIndexStore` into `SaveSnapshotPackageFileWriter`. Keep the byte format unchanged and make the colony store call the extracted writer.

- [ ] **Step 4: Implement the artifact store**

Use `<snapshot-root>/operations/<sha256-artifact-id>.snapshot.gz`. The public operations are:

```csharp
void Store(string artifactId, SaveSnapshotPackage package);
SaveSnapshotPackage? Read(string artifactId);
bool Exists(string artifactId);
void Delete(string artifactId);
```

Hash artifact IDs before producing paths. Register the file store in persistent state and an in-memory store in default test state.

- [ ] **Step 5: Run save tests**

Expected: package format and artifact round-trip tests pass.

---

### Task 3: Raid Settlement Deferred Payload and Scheduler

**Files:**
- Create: `Source/Server/ClashOfRim.Network/Server/Raids/RaidSettlementDeferredPayload.cs`
- Create: `Source/Server/ClashOfRim.Network/Server/Raids/RaidSettlementDeferredScheduler.cs`
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Runtime/SnapshotPostUploadProcessors.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Registries/Snapshots/SnapshotPostUploadJobRegistry.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Produces: `RaidSettlementDeferredPayload`, `RaidSettlementOrigin`, processor-defined job key support, and prepared-job recovery metadata.

- [ ] **Step 1: Write failing scheduler tests**

Test that scheduling raid event `raid-1` twice creates one job with key `raid-settlement:raid-1`, stores one artifact, and returns the same job. Test that a normal autosave context never calls the scheduler.

- [ ] **Step 2: Verify tests fail**

Run the pipeline smoke target. Expected: missing scheduler and processor job-key support.

- [ ] **Step 3: Implement compact payload and custom job keys**

The JSON record contains only IDs, parties, origin, artifact key, and client application result. Extend deferred preparation so a processor can select a stable job key; default processors retain `snapshotId:processorId`.

- [ ] **Step 4: Implement two-phase scheduling**

The scheduler performs:

```text
store immutable artifact
enqueue Prepared job
install attacker evidence as latest under RaidSettlementSnapshotMutationGate
mark job Ready
```

On an exception it deletes the prepared job and artifact where possible, then rethrows. Prepared jobs remain recoverable if the process stops between steps.

- [ ] **Step 5: Run pipeline tests**

Expected: idempotency, autosave short-circuit, and ready transition tests pass.

---

### Task 4: Extract Idempotent Settlement Executor

**Files:**
- Create: `Source/Server/ClashOfRim.Network/Server/Raids/RaidSettlementOperationExecutor.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.SessionWorld.Confirmations.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Produces: `RaidSettlementOperationExecutor.Execute` returning `Completed`, `AlreadyCompleted`, `RetryableFailure`, or `ManualReview`.

- [ ] **Step 1: Add failing executor tests**

Using the existing raid fixture builders, prove one execution edits the defender and records settlement; a second execution returns `AlreadyCompleted` without changing the defender snapshot ID or loss records.

- [ ] **Step 2: Verify tests fail**

Run the pipeline smoke target. Expected: missing executor.

- [ ] **Step 3: Extract settlement logic**

Move diffing, defender package editing, identity checks, ledger recording, delivery marking, source-event application, and notification signalling from `ConfirmPlayerRaidSettlementAfterSnapshot` into the executor. Re-read the source event inside `RaidSettlementSnapshotMutationGate` immediately before writes.

- [ ] **Step 4: Define failure classification**

I/O, SQLite, and temporary package-read failures throw for retry. Identity/event mismatches return `ManualReview`, update application diagnostics, and leave the source raid unsettled. Existing settlement records return `AlreadyCompleted`.

- [ ] **Step 5: Run executor tests**

Expected: first execution completes and duplicate execution is a no-op.

---

### Task 5: Register the Deferred Raid Processor

**Files:**
- Create: `Source/Server/ClashOfRim.Network/Server/Raids/RaidSettlementPostUploadProcessor.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Core/ClashOfRimNetworkServer.SnapshotPostUpload.cs`
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Runtime/SnapshotPostUploadJobExecutor.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Produces: core processor ID `core.raid-settlement`, supporting only `RaidSettlementEvidence`.

- [ ] **Step 1: Add failing processor tests**

Prove the processor is absent for `AuthoritativeColonySnapshot`, queues for `RaidSettlementEvidence`, keeps the defender login lock while queued, retries a transient executor failure, and removes the artifact/job after success.

- [ ] **Step 2: Verify tests fail**

Run the pipeline smoke target. Expected: no registered raid settlement processor.

- [ ] **Step 3: Implement processing and recovery**

`ProcessDeferred` loads the payload and artifact, calls the executor, and deletes the artifact only for `Completed` or `AlreadyCompleted`. Add prepared-job recovery that verifies the artifact and attacker snapshot, completes missing commit steps, then marks the job ready.

- [ ] **Step 4: Run processor tests**

Expected: queued/retry/completion/restart cases pass.

---

### Task 6: Online Endpoint and Client Semantics

**Files:**
- Modify: `Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.SessionWorld.Confirmations.cs`
- Modify: `Source/Client/RemoteMaps/Runtime/ClashOfRimMod.RemoteMaps.cs`
- Modify: `Languages/ChineseSimplified/Keyed/ClashOfRim.xml`
- Modify: `Languages/English/Keyed/ClashOfRim.xml`
- Modify matching keyed files for other shipped languages
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Consumes: scheduler and processor from Tasks 3-5.
- Produces: accepted response with `ServerValidationResult = "SettlementQueued"` and evidence snapshot ID.

- [ ] **Step 1: Add failing endpoint test**

Submit valid raid evidence and assert that the response returns before the executor runs, reports `SettlementQueued`, and leaves the source raid unsettled and defender login blocked.

- [ ] **Step 2: Verify endpoint test fails**

Run the pipeline smoke target. Expected: current endpoint completes settlement synchronously.

- [ ] **Step 3: Route online settlement through the scheduler**

For player raid settlement, run inline validation and support-loss processors, schedule the deferred operation, return the evidence snapshot ID, and skip `ConfirmPlayerRaidSettlementAfterSnapshot`. Other event confirmations keep their existing synchronous paths.

- [ ] **Step 4: Update client wording**

On accepted `SettlementQueued`, close the battle map and show “settlement submitted” rather than “settlement completed.” Preserve existing retry UI only for request or durable-commit failure.

- [ ] **Step 5: Run server and client builds**

```powershell
dotnet build Source\Server\ClashOfRim.Network\ClashOfRim.Network.csproj -c Release --no-restore
dotnet build Source\Client\ClashOfRim.csproj -c Release --no-restore
```

Expected: zero errors.

---

### Task 7: Offline Timeout Unification

**Files:**
- Modify: `Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Consumes: `RaidSettlementDeferredScheduler`.
- Removes: duplicate direct settlement/editor path from offline timeout reconciliation.

- [ ] **Step 1: Add failing offline timeout test**

Expire an offline attacker raid, assert one `OfflineTimeout` job is ready, and assert the defender remains locked before worker execution.

- [ ] **Step 2: Verify test fails**

Run the pipeline smoke target. Expected: timeout path still settles synchronously.

- [ ] **Step 3: Replace direct settlement with scheduling**

Copy the selected attacker latest package into the operation artifact store, schedule the same processor with `OfflineTimeout`, and leave the source raid unresolved until the worker completes.

- [ ] **Step 4: Add completion assertions**

Run the worker, then assert defender snapshot mutation, attacker/support loss events, source raid completion, notification delivery, artifact deletion, and login unlock.

- [ ] **Step 5: Run pipeline smoke tests**

Expected: online and offline settlement use the same processor behavior.

---

### Task 8: Verification and Packaging

**Files:**
- Verify all files modified in Tasks 1-7.

- [ ] **Step 1: Run focused tests sequentially**

```powershell
dotnet run --project Tools\ClashOfRim.NetworkSmoke\ClashOfRim.NetworkSmoke.csproj -c Release --no-restore -- --snapshot-pipeline-only
dotnet run --project Tools\ClashOfRim.Save.Tests\ClashOfRim.Save.Tests.csproj -c Release --no-restore
```

- [ ] **Step 2: Build server, client, and plugins sequentially**

Require zero errors from the network server, client mod, AdaptiveStorage plugin, and VehicleFramework plugin projects.

- [ ] **Step 3: Run server packaging**

```powershell
& .\Tools\BuildServerPackage.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

If the local debug server holds plugin DLLs, stop it through its normal shutdown path before packaging.

- [ ] **Step 4: Inspect repository state**

Run `git diff --check`, `git status --short`, and inspect the final diff for generated artifacts, protocol churn, duplicate settlement code, or temporary compatibility paths.
