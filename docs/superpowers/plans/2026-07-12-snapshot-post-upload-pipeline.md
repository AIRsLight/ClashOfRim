# Snapshot Post-Upload Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a typed, plugin-aware snapshot post-upload pipeline and migrate all existing post-upload behavior into registered processors.

**Architecture:** A public processor contract and deterministic pipeline live in the server plugin runtime. Core server behavior is registered as delegate-backed processors, while plugin processors flow through `ClashOfRimServerPluginDescriptor`, `ClashOfRimServerPluginContext`, and `ServerPluginRegistry` with compatibility-manifest filtering.

**Tech Stack:** C# 12, .NET 8, ASP.NET Core, existing `ClashOfRim.NetworkSmoke` executable tests.

## Global Constraints

- Do not change the wire protocol or database schema.
- Run processors only after `SnapshotUploadResult.Accepted` is true and an accepted snapshot exists.
- Preserve current authoritative-versus-raid-evidence behavior.
- Keep login, disconnect, and colony-abandonment lifecycle handling outside the pipeline.
- Use stable stage/order/ID sorting and explicit failure policies.

---

### Task 1: Processor Contract and Pipeline

**Files:**
- Create: `Source/Server/ClashOfRim.Network/Plugins/Runtime/SnapshotPostUploadProcessors.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Produces: `ISnapshotPostUploadProcessor`, `SnapshotPostUploadContext`, `SnapshotPostUploadKind`, `SnapshotPostUploadStage`, `SnapshotPostUploadFailureMode`, and `SnapshotPostUploadPipeline.Run`.

- [ ] Write smoke assertions for deterministic sorting, kind filtering, continue-on-error, and abort-on-error.
- [ ] Run `dotnet run --project Tools/ClashOfRim.NetworkSmoke/ClashOfRim.NetworkSmoke.csproj` and verify compilation fails because the new contract is absent.
- [ ] Implement the contract and minimal pipeline.
- [ ] Run the smoke executable and verify the new assertions pass.

### Task 2: Plugin Registration

**Files:**
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Descriptors/ClashOfRimServerPluginDescriptor.cs`
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Runtime/ClashOfRimServerPluginContext.cs`
- Modify: `Source/Server/ClashOfRim.Network/Plugins/Runtime/ServerPluginRegistry.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Core/ServerPluginLoader.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Consumes: `ISnapshotPostUploadProcessor` from Task 1.
- Produces: descriptor and runtime registration plus `ActiveSnapshotPostUploadProcessors(CompatibilityManifest?)`.

- [ ] Add a smoke assertion proving required-package filtering includes and excludes a registered processor.
- [ ] Run the smoke executable and verify the assertion fails because the registry has no processor collection.
- [ ] Thread the processor collection through descriptors, contexts, loader merging, registry active selection, and plugin capability diagnostics.
- [ ] Run the smoke executable and verify registration tests pass.

### Task 3: Core Processor Migration

**Files:**
- Modify: `Source/Server/ClashOfRim.Network/Server/Core/ClashOfRimNetworkServer.SnapshotPostUpload.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs`

**Interfaces:**
- Consumes: pipeline and active plugin processors from Tasks 1-2.
- Produces: registered core processors matching the existing six-step behavior.

- [ ] Add smoke assertions that authoritative snapshots run authoritative processors and raid evidence skips them.
- [ ] Run the smoke executable and verify the migration assertion fails against the hard-coded implementation.
- [ ] Register latest-reference, world-layer, colony-site/world-extension, metrics/achievements, support-death, and pending-operation processors.
- [ ] Remove snapshot-upload dispatch from the generic client lifecycle hook list and invoke pending confirmations through the pipeline.
- [ ] Run the smoke executable and verify all assertions pass.

### Task 4: Verification

**Files:**
- Verify all files modified in Tasks 1-3.

- [ ] Run `dotnet build Source/Server/ClashOfRim.Network/ClashOfRim.Network.csproj -c Release` and require zero errors.
- [ ] Run `dotnet run --project Tools/ClashOfRim.NetworkSmoke/ClashOfRim.NetworkSmoke.csproj -c Release` and require successful completion.
- [ ] Run the repository packaging build used for server and client artifacts if available.
- [ ] Inspect `git diff --check`, `git status --short`, and the final diff for accidental protocol or generated-file churn.
