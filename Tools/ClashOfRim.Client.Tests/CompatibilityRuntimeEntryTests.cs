using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using Verse;

internal static class CompatibilityRuntimeEntryTests
{
    private static readonly List<Task> observed = new();
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var oldSession = Attached<ClientSessionLifecycle>("session");
        var oldAdmin = Attached<ClientAdminAccess>("administration");
        var oldCatalog = Attached<ClientWorldCatalog>("catalog");
        var oldGame = Current.Game;
        using var gui = new ClientOverlayTestRuntime("ClashOfRim.Tests.CompatibilityRuntime");
        var harmony = new Harmony("ClashOfRim.Tests.CompatibilityRuntime.Tasks");
        harmony.Patch(AccessTools.Method(typeof(ClientWorldCatalog), nameof(ClientWorldCatalog.TryRefresh)),
            postfix: new HarmonyMethod(typeof(CompatibilityRuntimeEntryTests), nameof(Observe)));
        try
        {
            Reads();
            foreach (bool translated in new[] { false, true })
            {
                var f = Setup(gui); f.Prepared.WorldConfiguration = f.Read.WorldConfiguration;
                bool accepted = translated ? ClashOfRimCompatibilityApi.RequestServerWorldBaselineRefreshByKey("测试.İ")
                    : ClashOfRimCompatibilityApi.RequestServerWorldBaselineRefresh("手动 İ");
                await Pump(f);
                Check(accepted && f.Calls.Count == 2 && f.Calls.Single(c => c.Phase == "read").Full && f.Projected.Count == 1
                    && new[] { "features", "factions", "roads", "objects", "story-apply" }.All(f.Trace.Contains), "Compatibility refresh lost full baseline publication.");
                Check(f.Statuses.First().Contains(translated ? "测试.İ" : "手动 İ"), "Compatibility reason changed before the original catalog request.");
            }
            foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "world", "binding", "reentrant", "throw", "snapshot" })
            {
                var f = Setup(gui); bool nested = false;
                gui.Translating = key =>
                {
                    if (key != "reason") return;
                    if (change == "binding") ClashOfRimCompatibilityApi.AttachCatalog(new(f.Session, f, f, f, f.Publication.Flow, f));
                    else if (change == "reentrant") nested = ClashOfRimCompatibilityApi.RequestServerWorldBaselineRefresh("new-reason");
                    else if (change == "throw") throw new InvalidOperationException("translation");
                    else f.Change(change);
                };
                bool accepted = ClashOfRimCompatibilityApi.RequestServerWorldBaselineRefreshByKey("reason"); await Pump(f);
                bool publishes = change is "snapshot" or "reentrant";
                Check(accepted == (change == "snapshot") && f.Calls.Count == (publishes ? 2 : 0)
                    && f.Projected.Count == (publishes ? 1 : 0) && !f.Module.Busy, "Translated compatibility request crossed " + change);
                if (change == "reentrant") Check(nested && f.Statuses.Count(s => s.Contains("StatusRefreshing")) == 1 && f.Statuses.First().Contains("new-reason"),
                    "Older translated request replaced the nested catalog read.");
                if (change == "throw")
                {
                    gui.Translating = null;
                    Check(ClashOfRimCompatibilityApi.RequestServerWorldBaselineRefresh("retry"), "Translation failure retained refresh ownership.");
                    await Pump(f); Check(f.Projected.Count == 1, "Translation failure prevented the next refresh.");
                }
            }
            foreach (string failure in new[] { "unbound", "credentials", "network" })
            {
                var f = Setup(gui);
                if (failure == "unbound") ClashOfRimCompatibilityApi.AttachCatalog(null);
                if (failure == "credentials") f.Ports.Auth = "";
                if (failure == "network") { f.FaultPhase = "read"; f.Fault = "network"; }
                bool accepted = ClashOfRimCompatibilityApi.RequestServerWorldBaselineRefresh("failure"); await Pump(f);
                Check(accepted == (failure != "unbound") && f.Projected.Count == 0 && !f.Module.Busy,
                    "Compatibility bool stopped describing dispatch rather than network success: " + failure);
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); observed.Clear(); Current.Game = oldGame;
            ClashOfRimCompatibilityApi.AttachSession(oldSession); ClashOfRimCompatibilityApi.AttachAdministration(oldAdmin); ClashOfRimCompatibilityApi.AttachCatalog(oldCatalog);
            ClientSessionWork.Reset();
        }
    }
    private static void Reads()
    {
        foreach (var (state, expected) in new[] { ("unbound", false), ("local", false), ("configured-only", false), ("connected-no-snapshot", false),
            ("connected-snapshot", true), ("blank-user", false), ("observation", true), ("battle", true), ("battle-only", true), ("inactive-remote", false), ("unavailable", true) })
        {
            var f = new ClientWorldCatalogTests.Fixture(); var game = New<Game>(); Current.Game = game;
            var component = new ClashOfRimGameComponent(game);
            AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { component });
            if (state == "local") { f.Ports.Server = ""; f.Ports.User = ""; f.Ports.Colony = ""; f.Ports.Auth = ""; }
            if (state.StartsWith("connected") || state is "blank-user" or "unavailable") f.Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" });
            if (state == "connected-no-snapshot") f.Ports.Snapshot = "";
            if (state == "blank-user") f.Ports.User = "";
            if (state == "unavailable") f.Session.BeginUnavailable(f.Ports.Auth, null);
            if (state is "observation" or "unbound") ClashOfRimGameComponent.SetActiveObservationSession(ActiveRemoteMapSession.FromObservation("observe", "Scout", "bob", "c", "s"));
            if (state == "inactive-remote") AccessTools.Field(typeof(ClashOfRimGameComponent), "activeRemoteMapSession").SetValue(component, new ActiveRemoteMapSession());
            if (state is "battle" or "battle-only") ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid" });
            if (state == "battle-only") AccessTools.Field(typeof(ClashOfRimGameComponent), "activeRemoteMapSession").SetValue(component, null);
            ClashOfRimCompatibilityApi.AttachSession(state == "unbound" ? null : f.Session);
            Check(ClashOfRimCompatibilityApi.IsActiveMultiplayerSession == expected, "Compatibility multiplayer semantics changed: " + state);
        }
        foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "snapshot", "unavailable", "unbound" })
        {
            var f = new ClientWorldCatalogTests.Fixture(); var access = new ClientAdminAccess(f.Session);
            ClashOfRimCompatibilityApi.AttachAdministration(access); access.SetAdministrator(true);
            Check(ClashOfRimCompatibilityApi.IsCurrentUserAdministrator, "Original administrator permission not visible.");
            if (change == "unavailable") f.Session.BeginUnavailable(f.Ports.Auth, null);
            else if (change == "unbound") ClashOfRimCompatibilityApi.AttachAdministration(null);
            else f.Change(change);
            Check(ClashOfRimCompatibilityApi.IsCurrentUserAdministrator == (change is "game" or "snapshot" or "unavailable"), "Compatibility permission used another ownership rule: " + change);
            if (change == "unavailable") Check(access.Begin(out _) is null, "Informational administrator flag granted unavailable operations.");
        }
    }
    private static ClientWorldCatalogTests.Fixture Setup(ClientOverlayTestRuntime gui)
    {
        gui.Reset(); observed.Clear(); var f = new ClientWorldCatalogTests.Fixture();
        ClashOfRimCompatibilityApi.AttachCatalog(f.Module); return f;
    }
    private static T? Attached<T>(string field) where T : class => (T?)AccessTools.Field(typeof(ClashOfRimCompatibilityApi), field).GetValue(null);
    private static void Observe(Task work) => observed.Add(work);
    private static async Task Pump(ClientWorldCatalogTests.Fixture fixture) { var tasks = Task.WhenAll(observed.ToArray()); observed.Clear(); await fixture.Ports.PumpUntil(tasks); }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
