using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ClientStorytellerEditorTests
{
    private static Task? closed;
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Module().GetAwaiter().GetResult();
            }
            Windows().GetAwaiter().GetResult();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static ClientWorldWrites.StorytellerEdit Open(ClientWorldWritesTests.Fixture f)
    {
        Check(f.Module.OpenStorytellerEdit(out var edit) && edit is not null, "Administrator editor was denied.");
        return edit!;
    }
    private static void Changed(ClientWorldWritesTests.Fixture f)
        => f.CurrentBaseline = f.CurrentBaseline with { ValuesXml = "<difficulty><threatScale>2.5</threatScale></difficulty>" };
    private static void Replace(ClientWorldWritesTests.Fixture f, string change)
    {
        if (change == "unavailable") f.Session.BeginUnavailable(f.Ports.Auth, null);
        else if (change == "regrant") { f.Access.SetAdministrator(false); f.Access.SetAdministrator(true); }
        else { f.Change(change); if (change != "permission") f.Access.SetAdministrator(true); }
    }
    private static async Task Module()
    {
        foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "world", "permission", "regrant", "observation", "unavailable" })
        {
            foreach (bool duringCapture in new[] { false, true })
            {
                var f = new ClientWorldWritesTests.Fixture(ClientWorldWriteKind.Storyteller); var edit = Open(f); Changed(f); int reads = 0;
                if (!duringCapture) Replace(f, change);
                await f.Ports.PumpUntil(edit.CloseAsync(() => { reads++; Replace(f, change); return new(); }));
                Check(reads == (duringCapture ? 1 : 0) && f.Calls.IsEmpty && !f.Access.Busy,
                    "Old storyteller editor crossed " + change + (duringCapture ? " during appearance capture" : " before close"));
            }
        }
        foreach (string gate in new[] { "offline", "observing", "not-admin", "busy-scribe", "reentrant" })
        {
            var f = new ClientWorldWritesTests.Fixture(ClientWorldWriteKind.Storyteller);
            switch (gate)
            {
                case "offline": f.Ports.Snapshot = ""; break;
                case "observing": f.Observing = true; break;
                case "not-admin": f.Access.SetAdministrator(false); break;
                case "busy-scribe": f.CaptureError = "Scribe is busy"; break;
                case "reentrant": f.Hook = stage => { if (stage == "signature") Replace(f, "user"); }; break;
            }
            bool allowed = f.Module.OpenStorytellerEdit(out var edit);
            Check(allowed == (gate is "offline" or "observing") && edit is null && !f.Access.Busy && f.Calls.IsEmpty,
                "Storyteller opening policy changed: " + gate);
            Check(f.Rejections == (gate is "not-admin" or "busy-scribe" ? 1 : 0), "Opening refusal lost message or reached another account.");
        }
        var unchanged = new ClientWorldWritesTests.Fixture(ClientWorldWriteKind.Storyteller);
        await unchanged.Ports.PumpUntil(Open(unchanged).CloseAsync(() => null));
        Check(unchanged.Calls.IsEmpty && !unchanged.Access.Busy, "Unchanged editor uploaded a baseline.");
        var discarded = new ClientWorldWritesTests.Fixture(ClientWorldWriteKind.Storyteller); var ended = Open(discarded); Changed(discarded);
        discarded.Observing = true; await ended.CloseAsync(() => throw new Exception("stale")); discarded.Observing = false;
        await ended.CloseAsync(() => throw new Exception("revived"));
        Check(discarded.Calls.IsEmpty, "An obsolete close revived when observation ended.");
        var live = new ClientWorldWritesTests.Fixture(ClientWorldWriteKind.Storyteller); var original = Open(live); Changed(live);
        live.Change("snapshot"); int captured = 0;
        var work = original.CloseAsync(() =>
        {
            captured++;
            Check(original.CloseAsync(() => throw new Exception("duplicate")).IsCompletedSuccessfully, "Reentrant close was not consumed.");
            return new() { ColorHex = "123abc" };
        });
        await live.Ports.PumpUntil(work); await original.CloseAsync(() => throw new Exception("replayed"));
        Check(captured == 1 && live.Successes == 1 && live.Calls.Count == 2 && live.CapturedAppearance?.ColorHex == "123abc"
            && live.Sent?.DifficultyValuesXml == live.CurrentBaseline.ValuesXml && !live.Access.Busy,
            "Editor did not reuse one metadata/substrate write with current appearance and difficulty.");
    }
    private static async Task Windows()
    {
        var binding = ClientStorytellerEditor.Current;
        var harmony = new Harmony("ClashOfRim.Tests.StorytellerEditor");
        harmony.Patch(AccessTools.Method(typeof(ClientStorytellerEditor), nameof(ClientStorytellerEditor.ClosedAsync)),
            postfix: new HarmonyMethod(typeof(ClientStorytellerEditorTests), nameof(Closed)));
        try
        {
            var a = new ClientColonyAppearanceTests.Fixture(); var f = new ClientWorldWritesTests.Fixture(ClientWorldWriteKind.Storyteller);
            var editor = new ClientStorytellerEditor(f.Module, a.Flow); ClientStorytellerEditor.Attach(editor);
            var first = New<CollisionPage>(); var second = New<CollisionPage>();
            Check(first.GetHashCode() == second.GetHashCode(), "Collision fixture did not collide.");
            Check(ClashOfRimStorytellerSelectionPatches.Prefix(first), "First editor denied.");
            Changed(f); Check(ClashOfRimStorytellerSelectionPatches.Prefix(second), "Second editor denied.");
            Check(ClashOfRimStorytellerSelectionPatches.Prefix(first), "Repeated opening of the same page denied.");
            ClashOfRimStorytellerInGamePostClosePatch.Postfix(second); await f.Ports.PumpUntil(closed!);
            Check(f.Calls.IsEmpty, "Unchanged colliding window used another page's signature.");
            ClashOfRimStorytellerInGamePostClosePatch.Postfix(first); await f.Ports.PumpUntil(closed!);
            Check(f.Successes == 1 && f.Calls.Count == 2, "Hash-colliding page lost its original difficulty baseline.");
            ClashOfRimStorytellerInGamePostClosePatch.Postfix(first); await closed!;
            Check(f.Calls.Count == 2, "Repeated PostClose resubmitted a baseline.");

            f = new(ClientWorldWriteKind.Storyteller); editor = new(f.Module, a.Flow); ClientStorytellerEditor.Attach(editor);
            var offline = New<CollisionPage>(); f.Ports.Snapshot = "";
            Check(editor.Opening(offline), "Offline page was blocked."); f.Ports.Snapshot = "later"; Changed(f);
            await f.Ports.PumpUntil(editor.ClosedAsync(offline)); Check(f.Calls.IsEmpty, "Offline page adopted a later multiplayer session.");
            var replaced = New<CollisionPage>(); Check(editor.Opening(replaced), "Editor missing before replacement."); Changed(f);
            ClientStorytellerEditor.Attach(new(f.Module, a.Flow)); await editor.ClosedAsync(replaced); Check(f.Calls.IsEmpty, "Replaced adapter submitted its old page.");
            ClientStorytellerEditor.Attach(editor); var denied = New<CollisionPage>(); Check(editor.Opening(denied), "Editor missing before revocation.");
            f.Access.SetAdministrator(false); Check(!editor.Opening(denied), "Repeated Add revived a revoked editor.");
            f.Access.SetAdministrator(true); await editor.ClosedAsync(denied); Check(f.Calls.IsEmpty, "Regrant revived an old editor.");
            var weak = Abandoned(editor); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(!weak.TryGetTarget(out _), "Editor storage retained an abandoned window.");
            ClientStorytellerEditor.Attach(null);
            Check(ClashOfRimStorytellerSelectionPatches.Prefix(New<CollisionPage>()), "Unbound adapter blocked vanilla.");
        }
        finally { harmony.UnpatchAll(harmony.Id); ClientStorytellerEditor.Attach(binding); closed = null; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static System.WeakReference<Window> Abandoned(ClientStorytellerEditor editor)
    { var page = New<CollisionPage>(); Check(editor.Opening(page), "Abandoned-page fixture was not registered."); return new(page); }
    private sealed class CollisionPage : Page_SelectStorytellerInGame { public override int GetHashCode() => 17; }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Closed(Task __result) => closed = __result;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
