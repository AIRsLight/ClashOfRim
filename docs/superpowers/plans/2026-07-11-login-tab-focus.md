# Login Tab Focus Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add predictable `Tab` and `Shift+Tab` focus cycling to the server-entry dialog.

**Architecture:** Give each login input a stable Unity GUI control name and handle `KeyCode.Tab` inside the dialog after all fields are drawn. Keep the focus-order calculation in a deterministic helper on the dialog so behavior is explicit and the source contract can guard every required branch.

**Tech Stack:** C#, RimWorld/Verse UI, Unity IMGUI, existing `ClashOfRim.Events.Tests` source-contract test harness.

## Global Constraints

- Focus order is server address, user ID, password, then wrap to server address.
- `Shift+Tab` reverses the order and wraps.
- Buttons are excluded.
- `Enter` continues to submit.
- No initial focus is forced.

---

### Task 1: Server-entry keyboard focus cycle

**Files:**
- Modify: `Tools/ClashOfRim.Events.Tests/StableIdentityTests.cs`
- Modify: `Source/Client/MainMenu/Entry/ClashOfRimServerEntryDialog.cs`

**Interfaces:**
- Consumes: Unity `Event.current`, `GUI.SetNextControlName`, `GUI.GetNameOfFocusedControl`, and `GUI.FocusControl`.
- Produces: `ResolveNextInputControl(string? currentControl, bool reverse) : string`, used by the dialog's Tab handler.

- [x] **Step 1: Write the failing source-contract test**

Extend `VerifyClientAndServerDoNotBranchOnPresentationStrings()` to read `ClashOfRimServerEntryDialog.cs` and require the three control names, `KeyCode.Tab`, reverse traversal, and event consumption:

```csharp
string serverEntry = Read(root, "Source", "Client", "MainMenu", "Entry", "ClashOfRimServerEntryDialog.cs");
Require(serverEntry.Contains("ServerAddressInputControl", StringComparison.Ordinal), "登录页必须注册服务器地址焦点");
Require(serverEntry.Contains("UserIdInputControl", StringComparison.Ordinal), "登录页必须注册用户 ID 焦点");
Require(serverEntry.Contains("PasswordInputControl", StringComparison.Ordinal), "登录页必须注册密码焦点");
Require(serverEntry.Contains("KeyCode.Tab", StringComparison.Ordinal), "登录页必须处理 Tab 键");
Require(serverEntry.Contains("Event.current.shift", StringComparison.Ordinal), "登录页必须支持 Shift+Tab");
Require(serverEntry.Contains("Event.current.Use()", StringComparison.Ordinal), "登录页必须消费 Tab 事件");
```

- [x] **Step 2: Run the test to verify RED**

Run:

```powershell
dotnet run --project Tools\ClashOfRim.Events.Tests\ClashOfRim.Events.Tests.csproj -c Release
```

Expected: FAIL with `登录页必须注册服务器地址焦点`.

- [x] **Step 3: Implement the minimal focus behavior**

Add stable control-name constants, call `GUI.SetNextControlName(...)` immediately before each input, and invoke this handler after drawing the fields:

```csharp
private static void HandleTabFocus()
{
    if (Event.current is not { type: EventType.KeyDown, keyCode: KeyCode.Tab })
    {
        return;
    }

    GUI.FocusControl(ResolveNextInputControl(
        GUI.GetNameOfFocusedControl(),
        Event.current.shift));
    Event.current.Use();
}
```

Implement `ResolveNextInputControl` with the exact three-field forward/reverse cycle and use server address as the forward fallback and password as the reverse fallback.

- [x] **Step 4: Run tests and client build to verify GREEN**

Run:

```powershell
dotnet run --project Tools\ClashOfRim.Events.Tests\ClashOfRim.Events.Tests.csproj -c Release
dotnet build Source\Client\ClashOfRim.csproj -c Release --no-restore
```

Expected: all event tests pass; client build reports 0 warnings and 0 errors.

- [x] **Step 5: Package and install locally**

Run:

```powershell
Tools\BuildAndInstallLocalMods.ps1 -Configuration Release -NoRestore
```

Expected: both client mods are installed under the detected RimWorld `Mods` directory.

- [x] **Step 6: Commit**

```powershell
git add Tools\ClashOfRim.Events.Tests\StableIdentityTests.cs Source\Client\MainMenu\Entry\ClashOfRimServerEntryDialog.cs docs\superpowers\plans\2026-07-11-login-tab-focus.md
git commit -m "add tab focus navigation to login"
```
