using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientServerMetadataTests
{
    private const string Manifest = "{\"dlcIds\":[\"Biotech\",\"İ文化\"]}";
    private const string SuccessorManifest = "{\"dlcIds\":[\"successor\"]}";
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture; int cases = 0;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (string source in new[] { "identity", "entry", "catalog" })
                {
                    foreach (string? manifest in new[] { null, "", "{}", "{\"dlcIds\":null}", "{", Manifest })
                    foreach (bool administrator in new[] { false, true })
                    { await Publish(source, manifest, administrator); cases++; }
                    foreach (string phase in new[] { "diagnostic", "dlc", "invalidate", "permission", "enforce" })
                    foreach (string change in new[] { "user", "auth", "server", "colony", "login", "same-login", "generation", "game", "world", "snapshot", "password", "request" })
                    { await Reentry(source, phase, change); cases++; }
                }
                foreach (string phase in new[] { "dlc", "invalidate", "permission", "enforce" })
                { await Failure(phase); cases++; }
                await GrantAndClear(); cases++;
                await Obsolete(); cases++;
            }
            Console.WriteLine($"PASS: server metadata ({cases} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static async Task Publish(string source, string? manifest, bool administrator)
    {
        var f = new Fixture(); await f.Ports.PumpUntil(f.Publish(source, manifest, administrator));
        Require(f.Baseline.Manifest == (manifest ?? "") && f.Access.IsAdministrator == administrator && f.Invalidations == 1
            && f.Enforcements == (source == "catalog" && administrator ? 2 : 1),
            "Metadata did not reach both real domains: " + source);
        Require(f.Reads == (source == "catalog" && administrator ? 1 : 0), "Wrong administrator refresh policy: " + source);
        Require(f.CompatibilityErrors == (manifest == "{" ? 1 : 0), "Malformed manifest policy changed.");
        Require(f.Trace.IndexOf("invalidate") < f.Trace.IndexOf("permission") && f.Trace.IndexOf("permission") < f.Trace.IndexOf("enforce"),
            "Metadata publication changed compatibility/permission order.");
        if (manifest == Manifest) Require(f.Dlcs!.SequenceEqual(new[] { "Biotech", "İ文化" }), "Culture altered server DLC IDs.");
    }
    private static async Task Reentry(string source, string phase, string change)
    {
        var f = new Fixture(); bool reached = false;
        f.Hook = point => { if (point == phase) { f.Hook = null; reached = true; f.Change(change); } };
        await f.Ports.PumpUntil(f.Publish(source, phase == "diagnostic" ? "{" : Manifest, true));
        Require(reached, "Metadata callback not exercised: " + source + "/" + phase);
        bool unrelated = change is "snapshot" or "password" || source == "identity" && change is "game" or "world" or "request";
        Require(f.Reads == (source == "catalog" && unrelated ? 1 : 0), "Obsolete observation started administrator refresh: " + source + "/" + phase + "/" + change);
        if (change is "login" or "same-login")
            Require(f.Baseline.Manifest == SuccessorManifest && !f.Access.IsAdministrator && f.Dlcs!.Single() == "successor",
                "Old metadata overwrote a successor login: " + phase);
        else if (!unrelated && phase is "diagnostic" or "dlc")
            Require(f.Invalidations == 0 && f.Enforcements == 0, "Old baseline/permission effects continued after source loss.");
        else if (unrelated)
            Require(f.Access.IsAdministrator && f.Invalidations == 1 && f.Enforcements >= 1, "Account metadata was suppressed by unrelated state.");
    }
    private static async Task Failure(string phase)
    {
        var f = new Fixture(); f.Hook = point => { if (point == phase) throw new InvalidOperationException("callback"); };
        bool threw = false;
        try { await f.Publish("entry", Manifest, true); }
        catch (InvalidOperationException ex) when (ex.Message == "callback") { threw = true; }
        Require(threw && f.Reads == 0, "Failed metadata callback was hidden or started refresh.");
        f.Hook = null; await f.Publish("entry", SuccessorManifest, false);
        Require(f.Baseline.Manifest == SuccessorManifest && !f.Access.IsAdministrator, "Callback failure poisoned next observation.");
    }
    private static async Task GrantAndClear()
    {
        var f = new Fixture(); await f.Ports.PumpUntil(f.Publish("catalog", Manifest, true));
        await f.Ports.PumpUntil(f.Publish("catalog", Manifest, true));
        Require(f.Reads == 1 && f.Administration.Snapshot is not null, "Repeated administrator observation refreshed again.");
        await f.Publish("entry", Manifest, false);
        Require(!f.Access.IsAdministrator && f.Administration.Snapshot is null, "Revocation retained administrator data.");
        await f.Publish("identity", Manifest, true);
        f.Session.ClearIdentity();
        Require(f.Baseline.Manifest == "" && f.Dlcs is null && !f.Access.IsAdministrator && f.Reads == 1,
            "Cleared authentication retained metadata or issued a refresh.");
    }
    private static async Task Obsolete()
    {
        var f = new Fixture(); bool current = true;
        f.Hook = point => { if (point == "enforce") current = false; };
        await f.Ports.PumpUntil(f.Metadata.Apply(Manifest, true, true, () => current));
        Require(f.Reads == 0 && f.Access.IsAdministrator, "Permission UI invalidation did not stop the pending refresh.");
        int effects = f.Trace.Count;
        await f.Metadata.Apply("{", false, true, () => false);
        Require(f.Trace.Count == effects && f.Baseline.Manifest == Manifest && f.Access.IsAdministrator, "Already obsolete metadata changed state.");
    }
    private sealed class Fixture : IClientCompatibilityBaselineRuntime, IClientAdminPresentation, IClientAdminTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientCompatibilityBaseline Baseline;
        internal readonly ClientAdminAccess Access;
        internal readonly ClientAdministration Administration;
        internal readonly ClientServerMetadata Metadata;
        internal readonly List<string> Trace = new();
        internal Action<string>? Hook;
        internal int Invalidations, CompatibilityErrors, Enforcements, Reads;
        internal string[]? Dlcs;
        private object world = new();
        private bool request = true;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" });
            Baseline = new(this); Access = new(Session); Administration = new(Access, this, this); Metadata = new(Session, Baseline, Administration);
            Access.SetAdministrator(false);
            Access.PermissionChanged += _ => Step("permission");
            Session.IdentityChanged += Metadata.ObserveIdentity;
        }
        internal Task Publish(string source, string? manifest, bool administrator)
        {
            if (source == "identity")
            {
                Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session", ServerCompatibilityManifestJson = manifest, IsAdministrator = administrator });
                return Task.CompletedTask;
            }
            var owner = Session.CaptureOwnership(); var context = Ports.ReadContext(); object game = Ports.GameOwner, initialWorld = world;
            return Metadata.Apply(manifest, administrator, source == "catalog", () => request && Session.Owns(owner, context, false)
                && ReferenceEquals(game, Ports.GameOwner) && ReferenceEquals(initialWorld, world));
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "bob"; break;
                case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "login": case "same-login": Session.PublishLogin(new() { AuthToken = change == "same-login" ? "auth" : "new-token", SessionId = "session", ServerCompatibilityManifestJson = SuccessorManifest, IsAdministrator = false }); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "game": Ports.GameOwner = new(); break;
                case "world": world = new(); break;
                case "snapshot": Ports.Snapshot = "next"; break;
                case "password": Ports.Password = "changed"; break;
                case "request": request = false; break;
            }
        }
        private void Step(string point) { Trace.Add(point); Hook?.Invoke(point); }
        public void ApplyDlcIds(IReadOnlyList<string>? ids) { Dlcs = ids?.ToArray(); Step("dlc"); }
        public void InvalidateManifestCache() { Invalidations++; Step("invalidate"); }
        void IClientCompatibilityBaselineRuntime.Diagnose(string message) { CompatibilityErrors++; Step("diagnostic"); }
        public void Diagnose(string message) => Step("admin-diagnostic");
        public void EnforcePermission() { Enforcements++; Step("enforce"); }
        public string Text(string key, params (string Name, object? Value)[] args) => key;
        public void Success(string message) => Step("message");
        public void Enqueue(Action action, Action discard) => Ports.Enqueue(action);
        public Task<ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        {
            Require(context.UserId == "alice" && context.AuthToken == "auth", "Administrator refresh borrowed another account.");
            Reads++; return Task.FromResult(ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>.Ok(new() { Result = new() { Accepted = true }, IsAdministrator = true, Configuration = new() }));
        }
        public Task<ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto>> SaveAsync(ClashOfRimClientNetworkContext context, ModAdminConfigurationDto config, CancellationToken token) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModAdminActionResponseDto>> ActAsync(ClashOfRimClientNetworkContext context, ClientAdminCommand command, CancellationToken token) => throw new NotSupportedException();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
