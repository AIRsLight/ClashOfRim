using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Diagnostics;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

internal static class MultiplayerAccountDiagnosticsTests
{
    internal static void Account() => AccountAsync().GetAwaiter().GetResult();
    private static async Task AccountAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Submit();
            }
            foreach (string change in new[] { "close", "user", "auth", "server", "colony", "login", "clear", "unavailable", "generation" })
                await ReplacedDraft(change);
            foreach (bool queued in new[] { false, true })
                await CloseDuringSubmission(queued);
            await SharedBusy(); await FailureAndRetry();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task Submit()
    {
        var f = new PasswordChangeModuleTests.Fixture(); var panel = new ClientAccountPanel(f.Session, f.Module);
        var draft = Fill(panel, " old-é ", " 新-Iıİi-🔑 ");
        Check(panel.UserId == f.Ports.User && !panel.Busy && panel.Status.StartsWith("StatusIdle"), "Account view did not use the existing module state.");
        f.Ports.Snapshot = "advanced"; f.Ports.GameOwner = new(); f.Ports.HasGame = false;
        Check(ReferenceEquals(draft, panel.Read()) && draft.NewPassword == " 新-Iıİi-🔑 ", "Game/snapshot change discarded an account draft.");
        var work = panel.SubmitAsync(draft); Empty(draft);
        var publish = await f.Ports.Next();
        Check(panel.Busy && f.CurrentPassword == " old-é " && f.NewPassword == " 新-Iıİi-🔑 ", "Submitted credentials were normalized or not frozen.");
        draft.NewPassword = "next draft";
        publish(); await work;
        Check(f.Password == " 新-Iıİi-🔑 " && f.Persisted == 1 && !panel.Busy && panel.Status == "Changed:server-文化"
            && draft.NewPassword == "next draft", "Completion overwrote a later draft or bypassed password persistence.");
        draft.CurrentPassword = f.Password; draft.NewPassword = "";
        await f.Ports.PumpUntil(panel.SubmitAsync(draft)); Empty(draft);
        Check(f.Password == "" && f.Calls == 2 && f.Ports.Captures == 0, "Empty password compatibility changed or account work captured a Game.");
    }

    private static async Task ReplacedDraft(string change)
    {
        var f = new PasswordChangeModuleTests.Fixture(); var panel = new ClientAccountPanel(f.Session, f.Module);
        var draft = Fill(panel, "secret-old", "secret-next");
        if (change == "close") panel.Close(); else f.Change(change);
        // Submit without a fresh render first: it must not borrow the new session.
        await panel.SubmitAsync(draft);
        Empty(draft); var replacement = panel.Read(); Empty(replacement);
        Check(!ReferenceEquals(draft, replacement) && f.Calls == 0 && panel.UserId == f.Ports.User,
            "Old draft reached a new account or retained credentials: " + change);
        draft.NewPassword = "stale UI write";
        await panel.SubmitAsync(draft); Empty(replacement);
        Check(f.Calls == 0, "Detached draft became actionable again: " + change);
    }

    private static async Task CloseDuringSubmission(bool queued)
    {
        var f = new PasswordChangeModuleTests.Fixture(); var panel = new ClientAccountPanel(f.Session, f.Module);
        var held = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queued) f.Send = _ => held.Task;
        var draft = Fill(panel, "old", "accepted"); var work = panel.SubmitAsync(draft);
        Action? publish = null;
        if (queued) publish = await f.Ports.Next(); else await f.Sent.Task;
        draft.NewPassword = "abandoned"; panel.Close(); Empty(draft);
        var reopened = Fill(panel, "accepted", "later");
        Check(panel.Busy, "Page close canceled a submitted account request.");
        if (queued) publish!(); else held.SetResult(f.Result);
        await f.Ports.PumpUntil(work);
        Check(f.Password == "accepted" && f.Persisted == 1 && reopened.NewPassword == "later", "Close/reopen lost committed credentials or new draft.");
        f.Send = null; await f.Ports.PumpUntil(panel.SubmitAsync(reopened));
        Check(f.Password == "later" && f.Calls == 2, "Reopened account page could not submit.");
    }

    private static async Task SharedBusy()
    {
        var f = new PasswordChangeModuleTests.Fixture(); var first = new ClientAccountPanel(f.Session, f.Module);
        var second = new ClientAccountPanel(f.Session, f.Module);
        var work = first.SubmitAsync(Fill(first, "old", "first")); var publish = await f.Ports.Next();
        var otherDraft = Fill(second, "first", "second");
        await second.SubmitAsync(otherDraft);
        Check(second.Busy && f.Calls == 1 && otherDraft.NewPassword == "second", "Busy submit consumed another page's draft or sent a duplicate.");
        first.Close(); Check(otherDraft.NewPassword == "second", "Closing a page cleared another page's draft.");
        publish(); await work;
        await f.Ports.PumpUntil(second.SubmitAsync(otherDraft));
        Check(f.Password == "second" && f.Persisted == 2, "Shared busy state prevented a later explicit submission.");
    }

    private static async Task FailureAndRetry()
    {
        var f = new PasswordChangeModuleTests.Fixture(); var panel = new ClientAccountPanel(f.Session, f.Module);
        f.Result.Response!.Result = new() { Accepted = false, Message = "wrong password" };
        var draft = Fill(panel, "wrong", "not accepted"); var work = panel.SubmitAsync(draft); Empty(draft);
        var publish = await f.Ports.Next(); Fill(panel, "corrected", "retry"); publish(); await work;
        Check(f.Password == "old" && f.Persisted == 0 && panel.Status.StartsWith("Rejected") && draft.NewPassword == "retry",
            "Rejected response restored old secrets or erased a later correction.");
        f.Result.Response.Result.Accepted = true;
        await f.Ports.PumpUntil(panel.SubmitAsync(draft));
        Check(f.CurrentPassword == "corrected" && f.Password == "retry" && f.Calls == 2, "Explicit retry reused stale credentials.");
    }

    internal static void Diagnostics()
    {
        try
        {
            var f = new EventRefreshModuleTests.Fixture(); var diagnostics = new ClientRuntimeDiagnostics(f.Session, f.State);
            f.Ports.Password = "password-secret"; f.Ports.SteamTicket = "steam-secret"; f.Ports.Auth = "auth-secret";
            var initial = diagnostics.Read();
            Check(initial.LoginStatus is null && initial.SnapshotUploadStatus is null && initial.PresenceStatus is null && initial.WorldMapStatus is null
                && initial.QueueStatus.StartsWith("InitialQueue") && initial.DetailsStatus.StartsWith("InitialDetails"), "Diagnostics lost default status semantics.");
            diagnostics.LoginStatus = "login-é"; diagnostics.SnapshotUploadStatus = "upload-文化";
            diagnostics.PresenceStatus = ""; diagnostics.WorldMapStatus = "world";
            f.State.SetQueueStatus("queue"); f.State.SetDetailsStatus("details");
            var view = diagnostics.Read();
            Check(view.ServerBaseUrl == f.Ports.Server && view.UserId == f.Ports.User && view.ColonyId == f.Ports.Colony && view.SnapshotId == f.Ports.Snapshot
                && view.IsConfigured && view.LoginStatus == "login-é" && view.SnapshotUploadStatus == "upload-文化" && view.PresenceStatus == ""
                && view.WorldMapStatus == "world" && view.QueueStatus == "queue" && view.DetailsStatus == "details", "Diagnostics crossed status/identity fields.");
            string json = JsonSerializer.Serialize(view);
            foreach (string secret in new[] { "password-secret", "steam-secret", "auth-secret" })
                Check(!json.Contains(secret) && !view.ToString().Contains(secret), "Diagnostic projection included a credential.");
            diagnostics.WorldMapStatus = "new world"; f.Ports.Snapshot = "next"; f.State.SetQueueStatus("new queue");
            var next = diagnostics.Read();
            Check(next.SnapshotId == "next" && next.QueueStatus == "new queue" && next.WorldMapStatus == "new world"
                && view.SnapshotId == "base" && view.QueueStatus == "queue" && view.WorldMapStatus == "world", "Diagnostic projection was stale or mutable.");
            f.Ports.Server = ""; f.Ports.User = "other"; f.Ports.Colony = "new colony";
            var changed = diagnostics.Read();
            Check(!changed.IsConfigured && changed.UserId == "other" && changed.ColonyId == "new colony"
                && changed.QueueStatus.StartsWith("InitialQueue") && changed.DetailsStatus.StartsWith("InitialDetails")
                && changed.LoginStatus == "login-é", "Read reset publishers' messages or bypassed event ownership.");
            Check(f.Protocol.Calls.Count == 0 && f.Ports.Queued == 0 && f.Ports.Captures == 0 && f.Ports.Published == 0, "Diagnostic reads started domain work.");
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static ClientAccountDraft Fill(ClientAccountPanel panel, string current, string next)
    { var draft = panel.Read(); draft.CurrentPassword = current; draft.NewPassword = next; return draft; }
    private static void Empty(ClientAccountDraft draft) => Check(draft.CurrentPassword == "" && draft.NewPassword == "", "Sensitive draft was retained.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
