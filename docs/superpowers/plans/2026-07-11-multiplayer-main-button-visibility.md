# Multiplayer Main Button Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hide the multiplayer bottom-bar button outside an active ClashOfRim server session.

**Architecture:** Bind the main-button def to a custom vanilla `MainButtonWorker_ToggleTab` subclass. The worker preserves `base.Visible` and reads one dedicated runtime session visibility property from `ClashOfRimMod`.

**Tech Stack:** C#, RimWorld/Verse defs and `MainButtonWorker`, existing source-contract tests.

## Global Constraints

- Use the verified vanilla `MainButtonDef.workerClass` and `MainButtonWorker.Visible` extension points.
- Do not patch `MainButtonsRoot`.
- Do not mutate `MainButtonDef.buttonVisible` at runtime.
- Hide the button in single-player and after disconnect; retain it throughout connected remote-map modes.

---

### Task 1: Session-gated multiplayer button worker

**Files:**
- Modify: `Tools/ClashOfRim.Events.Tests/StableIdentityTests.cs`
- Modify: `Defs/MainButtonDefs/ClashOfRim_MainButtons.xml`
- Modify: `Source/Client/Core/ClashOfRimMod.cs`
- Create: `Source/Client/Multiplayer/MainButton/MainButtonWorker_ClashOfRimMultiplayer.cs`

**Interfaces:**
- Produces: `ClashOfRimMod.ShouldShowMultiplayerMainButton : bool`.
- Produces: `MainButtonWorker_ClashOfRimMultiplayer.Visible : bool`.

- [x] **Step 1: Add a failing source-contract test**

Require the def to declare `AIRsLight.ClashOfRim.Multiplayer.MainButtonWorker_ClashOfRimMultiplayer`, and require the worker source to override `Visible`, preserve `base.Visible`, and read `ShouldShowMultiplayerMainButton`.

- [x] **Step 2: Run the event tests and verify RED**

Run `dotnet run --project Tools\ClashOfRim.Events.Tests\ClashOfRim.Events.Tests.csproj -c Release`.

Expected: FAIL because the custom worker is not assigned.

- [x] **Step 3: Implement the session visibility property and worker**

The runtime property returns `settings.IsConfigured && !string.IsNullOrWhiteSpace(lastSessionId)`. The worker returns `base.Visible && LoadedModManager.GetMod<ClashOfRimMod>()?.ShouldShowMultiplayerMainButton == true`.

- [x] **Step 4: Assign the worker in the main-button def**

Add `<workerClass>AIRsLight.ClashOfRim.Multiplayer.MainButtonWorker_ClashOfRimMultiplayer</workerClass>` to `ClashOfRim_Multiplayer`.

- [x] **Step 5: Verify GREEN**

Run the event tests and `dotnet build Source\Client\ClashOfRim.csproj -c Release --no-restore`.

Expected: tests pass and the client build reports 0 warnings and 0 errors.

- [x] **Step 6: Package and install locally**

Run `Tools\BuildAndInstallLocalMods.ps1 -Configuration Release -NoRestore`.

- [x] **Step 7: Commit**

Commit the implementation, test, def, and completed plan as `hide multiplayer button outside sessions`.
