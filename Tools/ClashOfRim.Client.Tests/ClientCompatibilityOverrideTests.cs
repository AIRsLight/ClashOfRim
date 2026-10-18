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
using AIRsLight.ClashOfRim.Modules.Transport;

internal static class ClientCompatibilityOverrideTests
{
    private static int scenarios;
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; scenarios = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (bool loggedIn in new[] { false, true }) await Success(loggedIn);
                await Success(false, expired: true);
                await Admission(); await Freeze();
                foreach (string phase in new[] { "override", "requirements", "submit" })
                {
                    foreach (string fault in new[] { "network", "null", "missing", "rejected", "exception", "canceled" }) await Failure(phase, fault);
                    foreach (bool oldFirst in new[] { false, true })
                        foreach (string change in new[] { "user", "password", "steam" }) await Replacement(phase, oldFirst, change);
                }
                foreach (string phase in new[] { "text-StageManifest", "StageManifest", "manifest", "StageUpload", "override", "queue-1", "dlc", "invalidate", "StageAdminBaseline", "requirements", "capture-1", "capture-7", "StageSubmitAdminBaseline", "submit", "text-Accepted", "text-AdminBaselineUpdated", "StageComplete" })
                    foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "unavailable", "permission", "password", "steam", "snapshot", "game" }) await Ownership(phase, change);
                foreach (string phase in new[] { "manifest", "StageUpload", "dlc", "invalidate", "StageAdminBaseline", "capture-3", "capture-7", "StageSubmitAdminBaseline", "StageComplete", "complete" }) await CallbackFailure(phase);
                foreach (int discard in Enumerable.Range(1, 4)) await QueueDiscard(discard);
                await EmptyManifest(); await CompletionReentry();
            }
            Console.WriteLine($"  compatibility override: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success(bool loggedIn, bool expired = false)
    {
        var f = new Fixture();
        if (loggedIn) { f.Ports.Auth = "token"; f.Access.SetAdministrator(true); }
        if (expired) f.Session.BeginUnavailable(null, null);
        await f.Run();
        Check(f.Completed.Single().Accepted && f.Completed[0].Message.Contains("AdminBaselineUpdated"), "Successful override lost catalog completion.");
        Check(f.Calls.SequenceEqual(new[] { "override", "requirements", "submit" }) && f.Source.Calls == 8, "Wrong stages or repeated capture.");
        Check(f.Contexts.All(c => c.UserId == "alice" && c.OfflinePassword == "original-password" && c.AuthToken == (loggedIn ? "token" : "")), "Override borrowed credentials.");
        Check(f.Baseline.Manifest == f.Response.ServerCompatibilityManifestJson && f.Dlcs.Single()!.SequenceEqual(new[] { "Biotech", "Odyssey" }) && f.Invalidations == 1, "Baseline was not applied.");
        Check(f.Submitted!.StandardMarketValues.Single().MarketValue == 1.25f && f.Source.Requirement == "provider", "Catalog capture lost values/requirements.");
        Check(!f.Access.Busy && f.Access.IsAdministrator == loggedIn, "Credential admission leaked or granted local privilege.");
        Check(f.ProgressValues.SequenceEqual(new[] { 0.12f, 0.24f, 0.56f, 0.72f, 1f }), "Progress stages changed."); scenarios++;
    }
    private static async Task Admission()
    {
        foreach (string gate in new[] { "server", "user", "credential-busy", "admin-busy", "no-colony" })
        {
            var f = new Fixture(); ClientAdminAccess.Ticket? held = null;
            switch (gate)
            {
                case "server": f.Ports.Server = ""; break; case "user": f.Ports.User = " "; break;
                case "credential-busy": held = f.Access.BeginCredentials(out _); break;
                case "admin-busy": f.Ports.Auth = "token"; f.Access.SetAdministrator(true); held = f.Access.Begin(out _); break;
                case "no-colony": f.Ports.Colony = ""; break;
            }
            await f.Run();
            Check((f.Calls.Count > 0) == (gate == "no-colony"), "Credential admission changed: " + gate);
            if (held is not null) { Check(f.Access.Busy && f.Access.IsCurrent(held), "Rejected duplicate released another operation."); f.Access.End(held); }
            else Check(!f.Access.Busy, "Admission remained busy.");
            scenarios++;
        }
        var owner = new Fixture(); var credential = owner.Access.BeginCredentials(out _)!;
        Check(owner.Access.Begin(out var denied) is null && denied == "ClashOfRim.Admin.NotConnected", "Recovery enabled normal unauthenticated admin calls.");
        owner.Access.End(credential); owner.Ports.Auth = "token"; owner.Access.SetAdministrator(true);
        credential = owner.Access.BeginCredentials(out _)!;
        Check(owner.Access.Begin(out denied) is null && denied is null, "Recovery did not serialize authenticated admin writes.");
        owner.Access.End(credential); scenarios++;
    }
    private static async Task Ownership(string phase, string change)
    {
        var f = new Fixture(); bool changed = false;
        f.Hook = point => { if (point == phase && !changed) { changed = true; f.Change(change); } };
        await f.Run();
        bool retained = change is "snapshot" or "game";
        Check(changed, "Missed ownership boundary: " + phase);
        Check(f.Completed.Count == (retained ? 1 : 0) && !f.Access.Busy, "Stale completion/admission: " + phase + "/" + change);
        if (!retained)
        {
            int boundary = f.Trace.IndexOf(phase);
            Check(boundary == f.Trace.Count - 1, "Old operation continued: " + phase + "/" + change + " / " + string.Join(",", f.Trace.Skip(boundary)));
        }
        scenarios++;
    }
    private static async Task Failure(string phase, string fault)
    {
        var f = new Fixture { FaultPhase = phase, Fault = fault }; await f.Run();
        Check(f.Completed.Single().Accepted == (phase != "override") && !f.Access.Busy, "Failure changed accepted outcome: " + phase + "/" + fault);
        Check(f.Completed[0].Message.Contains(phase == "override" ? fault is "exception" or "canceled" ? "Exception" : fault is "null" or "missing" ? "Failed" : "expected" : "AdminBaselineNotUpdated"), "Failure summary lost: " + phase + "/" + fault);
        Check(f.Calls.Count == (phase == "override" ? 1 : phase == "requirements" ? 2 : 3), "Failure continued dependent writes.");
        f.Fault = null; await f.Run();
        Check(f.Completed.Last().Accepted && f.Completed.Last().Message.Contains("AdminBaselineUpdated"), "Failed operation poisoned retry."); scenarios++;
    }
    private static async Task CallbackFailure(string phase)
    {
        var f = new Fixture(); bool failed = false;
        f.Hook = point => { if (point == phase && !failed) { failed = true; throw new InvalidOperationException("injected"); } };
        await f.Run();
        Check(failed && f.Completed.Count == 1 && !f.Access.Busy, "Callback failure repeated completion/stranded admission: " + phase);
        Check(f.Completed[0].Accepted == (phase is not ("manifest" or "StageUpload")), "Partial success lost: " + phase);
        f.Hook = null; await f.Run(); Check(f.Completed.Count == 2 && f.Completed.Last().Accepted, "Callback failure poisoned retry."); scenarios++;
    }
    private static async Task Replacement(string phase, bool oldFirst, string change)
    {
        var f = new Fixture(); var entered = Signal(); var release = Signal();
        f.Before = async point => { if (point == phase) { entered.TrySetResult(); await release.Task; } };
        Task old = f.Start(); await f.Ports.PumpUntil(entered.Task);
        f.Change(change); f.Before = null;
        Task next = f.Start(); Action queue = await f.Ports.Next();
        if (oldFirst) { release.SetResult(); await old; Check(f.Access.Busy, "Old finally released replacement."); }
        queue(); await f.Ports.PumpUntil(next);
        if (!oldFirst) { release.SetResult(); await old; }
        Check(f.Completed.Count == 1 && f.Completed[0].Accepted && !f.Access.Busy, "Old result crossed replacement."); scenarios++;
    }
    private static async Task QueueDiscard(int discard)
    {
        var f = new Fixture { DiscardAt = discard };
        await f.Run();
        Check(!f.Access.Busy, "Discard stranded admission: " + discard);
        Check(f.Calls.Count == (discard == 1 ? 1 : discard is 2 or 3 ? 2 : 3), "Discard continued dependent writes: " + discard);
        f.DiscardAt = 0; await f.Run(); Check(f.Completed.Last().Accepted, "Discard prevented retry."); scenarios++;
    }
    private static async Task EmptyManifest()
    {
        var f = new Fixture { Manifest = " " }; await f.Run();
        Check(f.Calls.Count == 0 && !f.Completed.Single().Accepted && !f.Access.Busy, "Empty manifest uploaded."); scenarios++;
    }
    private static async Task Freeze()
    {
        var f = new Fixture(); f.Hook = point =>
        {
            if (point == "queue-1") f.Response.ServerCompatibilityManifestJson = "mutated";
            if (point == "queue-2") f.Requirements.BaselineExtensions.Clear();
            if (point == "queue-4") f.Result.StandardMarketValueCount = 999;
        };
        await f.Run();
        Check(f.Baseline.Manifest.Contains("Biotech") && f.Source.Requirement == "provider" && !f.Completed.Single().Message.Contains("999"), "Queued output borrowed response DTOs."); scenarios++;
    }
    private static async Task CompletionReentry()
    {
        var f = new Fixture(); Task? next = null;
        f.Hook = point => { if (point == "complete") { f.Hook = null; f.Change("login"); next = f.Start(); } };
        await f.Run(); await f.Ports.PumpUntil(next!);
        Check(f.Completed.Count == 2 && f.Completed.All(c => c.Accepted) && !f.Access.Busy, "Completion reentry lost successor."); scenarios++;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientCompatibilityManifestSource, IClientCompatibilityOverridePresentation,
        IClientCompatibilityBaselineRuntime, IClientCompatibilityOverrideTransport, IClientAdminBaselineTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientAdminBaselineCaptureTests.Source Source = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientAdminAccess Access;
        internal readonly ClientCompatibilityBaseline Baseline;
        internal readonly ClientCompatibilityOverride Flow;
        internal readonly List<string> Trace = new(), Calls = new();
        internal readonly List<ClashOfRimClientNetworkContext> Contexts = new();
        internal readonly List<(string Message, bool Accepted)> Completed = new();
        internal readonly List<float> ProgressValues = new();
        internal readonly List<string[]?> Dlcs = new();
        internal readonly ModOverrideCompatibilityBaselineResponseDto Response = new() { Result = new() { Accepted = true }, ServerCompatibilityManifestJson = "{\"dlcIds\":[\"Biotech\",\"Odyssey\"]}" };
        internal readonly ModGetAdminBaselineRequirementsResponseDto Requirements = new() { Result = new() { Accepted = true }, BaselineExtensions = new() { new() { ProviderId = "provider" } } };
        internal readonly ModSubmitAdminBaselineResponseDto Result = new() { Result = new() { Accepted = true }, StandardMarketValueCount = 12, TrapAutoApprovedCount = 7, TrapCandidateCount = 3 };
        internal ModSubmitAdminBaselineRequestDto? Submitted;
        internal string? FaultPhase, Fault, Manifest = "{\"manifest\":\"文化 İ\"}";
        internal Action<string>? Hook;
        internal Func<string, Task>? Before;
        internal int Invalidations, DiscardAt, Queues;
        private bool engine = true;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Ports.Auth = ""; Ports.Password = "original-password";
            Session = new(Ports, Ports, new(Ports)); Access = new(Session); Baseline = new(this);
            Source.After = step => { Check(engine, "Capture off engine queue."); Step("capture-" + ((step - 1) % 8 + 1)); };
            Flow = new(Access, this, Baseline, new(Session, Source, Enqueue), this, this, this, Enqueue);
        }
        internal Task Start() { engine = true; return Flow.StartAsync((message, value) => { ProgressValues.Add(value); Step(message); },
            (message, accepted) => { Check(engine, "Completion off engine queue."); Completed.Add((message, accepted)); Step("complete"); }); }
        internal Task Run() => Ports.PumpUntil(Start(), _ => { });
        internal void Step(string point) { Trace.Add(point); Hook?.Invoke(point); }
        public string? Id => throw new NotSupportedException(); public string? SummaryJson => throw new NotSupportedException();
        public string? BuildJson(IReadOnlyCollection<string>? packages) { Check(engine && packages is null, "Partial/off-thread manifest capture."); Step("manifest"); return Manifest; }
        public string Text(string key, params (string Name, object? Value)[] args)
        { Check(engine, "Formatting off engine queue."); string local = key.Replace("ClashOfRim.Compatibility.Override.", ""); Step("text-" + local); return local + (args.Length == 0 ? "" : ":" + string.Join(",", args.Select(a => a.Value))); }
        public void Diagnose(string message) { Check(engine, "Diagnostic off engine queue."); Step("diagnostic"); }
        public void ApplyDlcIds(IReadOnlyList<string>? ids) { Check(engine, "DLC publication off engine queue."); Dlcs.Add(ids?.ToArray()); Step("dlc"); }
        public void InvalidateManifestCache() { Invalidations++; Step("invalidate"); }
        private void Enqueue(Action action, Action discard)
        {
            int index = ++Queues;
            Ports.Enqueue(() => { engine = true; Step("queue-" + index); if (index == DiscardAt) discard(); else action(); action(); });
        }
        private async Task<string?> Network(string phase, ClashOfRimClientNetworkContext context)
        {
            engine = false; Calls.Add(phase); Contexts.Add(context); string? fault = FaultPhase == phase ? Fault : null;
            Step(phase); if (Before is not null) await Before(phase);
            engine = false; return fault;
        }
        public async Task<ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, string manifest, CancellationToken token)
        {
            Check(manifest == Manifest, "Manifest changed before upload."); string? fault = await Network("override", context);
            Response.Result = Protocol(fault); return Reply(fault, Response);
        }
        public async Task<ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto>> RequirementsAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        { string? fault = await Network("requirements", context); Requirements.Result = Protocol(fault); return Reply(fault, Requirements); }
        public async Task<ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModSubmitAdminBaselineRequestDto request, CancellationToken token)
        { Submitted = ClientProtocolJson.Copy(request); string? fault = await Network("submit", context); Result.Result = Protocol(fault); return Reply(fault, Result); }
        private static ModProtocolResponseDto? Protocol(string? fault) => fault == "missing" ? null : new() { Accepted = fault != "rejected", Message = "expected" };
        private static ClashOfRimClientNetworkResult<T> Reply<T>(string? fault, T value) => fault switch
        {
            "network" => ClashOfRimClientNetworkResult<T>.Failed("network", "expected"),
            "null" => ClashOfRimClientNetworkResult<T>.Ok(default!),
            "exception" => throw new InvalidOperationException("injected"), "canceled" => throw new OperationCanceledException(),
            _ => ClashOfRimClientNetworkResult<T>.Ok(value)
        };
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break; case "colony": Ports.Colony = "other"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "generation": ClientSessionWork.Reset(); break; case "unavailable": Session.BeginUnavailable(null, null); break;
                case "permission": Access.SetAdministrator(false); break; case "password": Ports.Password = "new"; break;
                case "steam": Ports.SteamTicket = "new"; break; case "snapshot": Ports.Snapshot = "next"; break; case "game": Ports.GameOwner = new(); break;
            }
        }
    }
}
