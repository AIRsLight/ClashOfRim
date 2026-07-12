# Snapshot Post-Upload Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a typed, plugin-aware snapshot post-upload pipeline and migrate all existing post-upload behavior into registered processors.

**Architecture:** A public processor contract and deterministic pipeline live in the server plugin runtime. Core server behavior is registered as delegate-backed processors, while plugin processors flow through `ClashOfRimServerPluginDescriptor`, `ClashOfRimServerPluginContext`, and `ServerPluginRegistry` with compatibility-manifest filtering. Deferred processors persist compact processor-owned payloads in an SQLite outbox consumed by a hosted retry worker.

**Tech Stack:** C# 12, .NET 8, ASP.NET Core, existing `ClashOfRim.NetworkSmoke` executable tests.

## Global Constraints

- Do not change the wire protocol. Advance the database schema only for the durable deferred-job outbox.
- Run processors only after `SnapshotUploadResult.Accepted` is true and an accepted snapshot exists.
- Preserve current authoritative-versus-raid-evidence behavior.
- Keep login, disconnect, and colony-abandonment lifecycle handling outside the pipeline.
- Use stable stage/order/ID sorting and explicit failure policies.
- Keep authoritative processors inline; deferred processors must not persist full RWS or full snapshot indexes.

---

### Task 1: Processor Contract and Pipeline

**Files:**
- Create: `Source/Server/ClashOfRim.Network/Plugins/Runtime/SnapshotPostUploadProcessors.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Produces: `ISnapshotPostUploadProcessor`, `SnapshotPostUploadContext`, `SnapshotPostUploadKind`, `SnapshotPostUploadStage`, `SnapshotPostUploadFailureMode`, and `SnapshotPostUploadPipeline.Run`.

- [x] Write smoke assertions for deterministic sorting, kind filtering, continue-on-error, and abort-on-error.
- [x] Run `dotnet run --project Tools/ClashOfRim.NetworkSmoke/ClashOfRim.NetworkSmoke.csproj` and verify compilation fails because the new contract is absent.
- [x] Implement the contract and minimal pipeline.
- [x] Run the smoke executable and verify the new assertions pass.

### Task 2: Persistent Deferred Lane

**Files:**
- Create: `Source/Server/ClashOfRim.Network/Plugins/Runtime/SnapshotPostUploadJobExecutor.cs`
- Create: `Source/Server/ClashOfRim.Network/Server/Registries/Snapshots/SnapshotPostUploadJobRegistry.cs`
- Modify: `Source/Server/ClashOfRim.Network/Client/ClashOfRimNetworkState.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Core/ClashOfRimNetworkServer.Bootstrap.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Produces: `IDeferredSnapshotPostUploadProcessor`, durable job records, a retry executor, and the hosted worker.

- [x] Add a failing test proving deferred processors enqueue without executing inline.
- [x] Implement compact payload capture and SQLite-backed outbox persistence.
- [x] Add a failing test proving the executor invokes and removes a ready job.
- [x] Implement hosted execution and bounded exponential retry.
- [x] Run the targeted pipeline smoke test.

### Task 3: Plugin Registration

**Files:**
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Descriptors/ClashOfRimServerPluginDescriptor.cs`
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Runtime/ClashOfRimServerPluginContext.cs`
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Runtime/ServerPluginRegistry.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Core/ServerPluginLoader.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Consumes: `ISnapshotPostUploadProcessor` from Task 1.
- Produces: descriptor and runtime registration plus `ActiveSnapshotPostUploadProcessors(CompatibilityManifest?)`.

- [x] Add a smoke assertion proving required-package filtering includes and excludes a registered processor.
- [x] Run the smoke executable and verify the assertion fails because the registry has no processor collection.
- [x] Thread the processor collection through descriptors, contexts, loader merging, registry active selection, and plugin capability diagnostics.
- [x] Run the smoke executable and verify registration tests pass.

### Task 4: Core Processor Migration

**Files:**
- Modify: `Source/Server/ClashOfRim.Network/Server/Core/ClashOfRimNetworkServer.SnapshotPostUpload.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Consumes: pipeline and active plugin processors from Tasks 1-3.
- Produces: registered core processors matching the existing six-step behavior.

- [x] Add smoke assertions that authoritative snapshots run authoritative processors and raid evidence skips them.
- [x] Run the smoke executable and verify the migration assertion fails against the hard-coded implementation.
- [x] Register latest-reference, world-layer, colony-site/world-extension, metrics/achievements, support-death, and pending-operation processors.
- [x] Remove snapshot-upload dispatch from the generic client lifecycle hook list and invoke pending confirmations through the pipeline.
- [x] Run the smoke executable and verify all assertions pass.

### Task 5: Verification

**Files:**
- Verify all files modified in Tasks 1-4.

- [x] Run `dotnet build Source/Server/ClashOfRim.Network/ClashOfRim.Network.csproj -c Release` and require zero errors.
- [ ] Run `dotnet run --project Tools/ClashOfRim.NetworkSmoke/ClashOfRim.NetworkSmoke.csproj -c Release` and require successful completion. (The pipeline target passes; the complete smoke executable still requires the intentionally removed `SaveSample/SaveHediffFixture.rws` fixture.)
- [x] Run the repository packaging build used for server and client artifacts if available. (The server package and both server plugins build; the combined plugin-copy step is blocked while the local debug server holds its loaded plugin DLLs.)
- [x] Inspect `git diff --check`, `git status --short`, and the final diff for accidental protocol or generated-file churn.
