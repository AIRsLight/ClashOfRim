using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ClientIdeologyReformTests
{
    private static Faction player = null!;
    private static bool enabled;
    private static Task? dispatched;
    private static readonly List<string> warnings = new();
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = ClientIdeologyReforms.Current; var culture = CultureInfo.CurrentCulture;
        using var gui = new ClientOverlayTestRuntime("ClashOfRim.Tests.IdeoReform");
        var harmony = new Harmony("ClashOfRim.Tests.IdeoReform.Ports");
        harmony.Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)), prefix: new HarmonyMethod(typeof(ClientIdeologyReformTests), nameof(Player)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(IdeologyPawnReferenceCompatibility), "HasPawnReference"), prefix: new HarmonyMethod(typeof(ClientIdeologyReformTests), nameof(Enabled)));
        harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning)), prefix: new HarmonyMethod(typeof(ClientIdeologyReformTests), nameof(Warning)));
        harmony.Patch(AccessTools.Method(typeof(ClientIdeologyReforms), nameof(ClientIdeologyReforms.NotifyAsync)), postfix: new HarmonyMethod(typeof(ClientIdeologyReformTests), nameof(Observe)));
        try
        {
            foreach (string language in new[] { "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                foreach (string kind in new[] { "initial", "primary", "registered-primary", "foreign", "remote", "null", "disabled", "unbound", "suppressed-sites", "remote-map" })
                {
                    var f = Setup(gui); var ideo = new Ideo { id = 111, name = "改革 İ", initialPlayerIdeo = kind is "initial" or "disabled" or "unbound" or "suppressed-sites" or "remote-map" };
                    bool registered = kind is "registered-primary" or "remote";
                    if (kind is "primary" or "registered-primary") player.ideos.SetPrimary(ideo);
                    if (registered) RemoteIdeoCatalog.Register(ideo, "owner:bob/ideo:111");
                    if (kind == "disabled") enabled = false;
                    if (kind == "unbound") ClientIdeologyReforms.Attach(null);
                    if (kind == "suppressed-sites") f.Registration.Suppressed = true;
                    if (kind == "remote-map") f.Remote = true;
                    try
                    {
                        await Fire(f, kind == "null" ? null : ideo);
                        bool accepted = kind is "initial" or "primary" or "registered-primary" or "suppressed-sites";
                        Check(f.Calls.Count == (accepted ? 1 : 0), "Reform qualification changed: " + kind);
                        if (accepted)
                        {
                            Check(f.Calls[0].Sites.Length == 0 && !f.Calls[0].Muted && f.Calls[0].Extensions.Single().PayloadJson!.Contains("文化 İ")
                                && f.Projects == 1 && f.Publication.MarkerQueues == 1, "Reform bypassed original extension registration/publication.");
                            Check(gui.TextKeys.Contains("ClashOfRim.WorldCatalog.ReasonIdeoReformed"), "Reform lost its diagnostic reason.");
                        }
                    }
                    finally { if (registered) RemoteIdeoCatalog.Unregister(ideo); }
                }
            }
            foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "game", "world", "world-id", "reset", "binding", "snapshot", "throw" })
            {
                var f = Setup(gui); var ideo = new Ideo { initialPlayerIdeo = true };
                gui.Translating = _ =>
                {
                    gui.Translating = null;
                    if (change == "binding") ClientIdeologyReforms.Attach(new(f.Registration));
                    else if (change == "throw") throw new InvalidOperationException("reform translation");
                    else f.Change(change);
                };
                await Fire(f, ideo);
                Check(f.Calls.Count == (change == "snapshot" ? 1 : 0) && !f.Registration.Busy,
                    "Reform callback adopted a replacement owner: " + change);
                if (change == "snapshot") Check(f.ExtensionsContext.Snapshot == "advanced", "Reform did not capture the latest ordinary snapshot.");
                if (change == "throw")
                {
                    Check(warnings.Single().Contains("reform translation"), "Translation failure escaped or was hidden.");
                    await Fire(f, ideo); Check(f.Calls.Count == 1, "Failed reform capture blocked the next event.");
                }
            }
            var coalesced = Setup(gui); var local = new Ideo { initialPlayerIdeo = true };
            var adapter = ClientIdeologyReforms.Current!;
            await coalesced.Ports.PumpUntil(Task.WhenAll(adapter.NotifyAsync(local), adapter.NotifyAsync(local), adapter.NotifyAsync(local)));
            Check(coalesced.Calls.Count == 1, "Queued reform events bypassed registration coalescing.");
            await Fire(coalesced, local); Check(coalesced.Calls.Count == 2, "A later explicit reform did not invalidate its accepted signature.");
            ClientIdeologyReforms.Attach(new(coalesced.Registration));
            await coalesced.Ports.PumpUntil(adapter.NotifyAsync(local));
            Check(coalesced.Calls.Count == 2, "Detached reform adapter dispatched work.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); ClientIdeologyReforms.Attach(previous); CultureInfo.CurrentCulture = culture;
            warnings.Clear(); dispatched = null; ClientSessionWork.Reset();
        }
    }
    private static ClientColonyRegistrationTests.Fixture Setup(ClientOverlayTestRuntime gui)
    {
        gui.Reset(); enabled = true; warnings.Clear(); dispatched = null;
        player = new Faction { def = new() { isPlayer = true } }; player.ideos = new(player);
        var f = new ClientColonyRegistrationTests.Fixture(); ClientIdeologyReforms.Attach(new(f.Registration)); return f;
    }
    private static async Task Fire(ClientColonyRegistrationTests.Fixture f, Ideo? ideo)
    { dispatched = null; IdeoReformSyncPatches.Postfix(ideo!); await f.Ports.PumpUntil(dispatched ?? Task.CompletedTask); }
    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static bool Enabled(ref bool __result) { __result = enabled; return false; }
    private static bool Warning(string __0) { warnings.Add(__0); return false; }
    private static void Observe(Task __result) => dispatched = __result;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
