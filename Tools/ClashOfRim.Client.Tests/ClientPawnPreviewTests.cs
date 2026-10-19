using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;

internal static class ClientPawnPreviewTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Preview(); await Details(); await Inline(); await Capacity(); await Slots(); await Discard();
                foreach (string fault in new[] { "network", "empty", "exception", "decode", "decode-throw" }) await PreviewFailure(fault);
                foreach (string fault in new[] { "network", "missing", "rejected", "protocol", "exception", "restore", "restore-throw" }) await DetailFailure(fault);
                foreach (string change in new[] { "reset", "user", "server", "colony", "auth", "login", "game", "generation", "unavailable" })
                    foreach (bool details in new[] { false, true }) await Stale(change, details);
                foreach (string phase in new[] { "decode", "restore", "text", "release" }) await Reentry(phase);
            }
            Console.WriteLine("PASS: pawn previews (four cultures; single requests, scope, bounds, faults, inline state and engine queue)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static ModThingReferenceDto Reference(string id = "包-İ") => new() { PawnPackageId = id, GlobalKey = "owner/pawn" };
    private static ModPawnExchangePackageDto Package(string state = "original") => new()
    {
        Reference = new() { GlobalId = "owner/pawn", Metadata = new() { ["ideology"] = state } },
        Scribe = new() { Xml = "<pawn />", XmlSha256 = "unchanged" },
        Extensions = new() { new() { ProviderId = "provider", Kind = "state", PayloadJson = state } }
    };
    private static async Task Preview()
    {
        var f = new Fixture(); f.Duplicate = true; var reference = Reference();
        var work = f.Model.RequestPreviewAsync(reference);
        for (int i = 0; i < 20; i++) Check(ReferenceEquals(work, f.Model.RequestPreviewAsync(reference)), "Preview duplicate lost the in-flight request.");
        reference.PawnPackageId = "changed-input";
        await f.Pump(work);
        Check(f.PreviewCalls.Count == 1 && f.PreviewCalls.Single().Id == "包-İ" && f.Textures.Count == 1
            && f.Model.ReadTexture(Reference()) == f.Textures[0], "Preview adopted changed input or published twice.");
        f.Ports.Snapshot = "advanced";
        await f.Pump(f.Model.RequestPreviewAsync(Reference()));
        Check(f.PreviewCalls.Count == 1 && f.Model.ReadTexture(Reference()) is not null, "Snapshot progression discarded preview cache.");
        f.Model.Reset(); await f.Pump(Task.CompletedTask);
        Check(f.Model.ReadTexture(Reference()) is null && f.Textures[0].Disposed == 1, "Reset did not release its texture on the engine queue.");
        f.Model.Reset(); await f.Pump(Task.CompletedTask); Check(f.Textures[0].Disposed == 1, "Repeated reset released a texture twice.");
    }
    private static async Task Details()
    {
        var f = new Fixture(); f.Duplicate = true;
        var work = f.Model.OpenDetailsAsync(Reference());
        for (int i = 0; i < 20; i++) Check(ReferenceEquals(work, f.Model.OpenDetailsAsync(Reference())), "Details duplicated pending work.");
        await f.Pump(work);
        Check(f.PackageCalls.Count == 1 && f.Cards.Count == 1 && f.Shown.Count == 1 && f.LoadingCount == 1, "Details duplicated capture, loading or window publication.");
        f.Ports.Snapshot = "advanced";
        await f.Pump(f.Model.OpenDetailsAsync(Reference()));
        Check(f.PackageCalls.Count == 1 && f.Shown.Count == 2 && ReferenceEquals(f.Shown[0], f.Shown[1]), "Cached detail click restored another pawn.");
        Check(f.Restored.Single().Reference!.Metadata["ideology"] == "original", "Restore payload lost metadata.");
        f.Model.Reset(); await f.Pump(Task.CompletedTask);
        Check(f.Cards[0].Disposed == 1, "Detail cache reference was retained after reset.");
    }
    private static async Task Inline()
    {
        var f = new Fixture(); var package = Package(); var reference = new ModThingReferenceDto { PawnPackage = package };
        var first = f.Model.OpenDetailsAsync(reference);
        package.Extensions[0].PayloadJson = "new-extension";
        await f.Pump(first);
        Check(f.PackageCalls.Count == 0 && f.Restored[0].Extensions[0].PayloadJson == "original"
            && package.Reference!.Metadata["ideology"] == "original", "Inline capture borrowed later edits or restoration mutated its input.");
        await f.Pump(f.Model.OpenDetailsAsync(reference));
        Check(f.Cards.Count == 2 && f.Restored[1].Extensions[0].PayloadJson == "new-extension", "Same global ID/XML hash aliased different DLC state.");
        package.Reference!.Metadata["ideology"] = "new-ideology";
        await f.Pump(f.Model.OpenDetailsAsync(reference));
        Check(f.Cards.Count == 3 && f.Restored[2].Reference!.Metadata["ideology"] == "new-ideology", "Metadata change reused a stale pawn card.");
        f.Ports.Colony = "";
        await f.Pump(f.Model.OpenDetailsAsync(new() { PawnPackage = Package() }));
        Check(f.PackageCalls.Count == 0 && f.Shown.Count == 4, "Offline inline preview started a download or lost its display.");
        var absent = new ModThingReferenceDto { GlobalKey = "missing-inline" };
        await f.Pump(f.Model.OpenDetailsAsync(absent));
        Check(f.Failures.Last().Contains("DownloadMissingPackage", StringComparison.Ordinal), "Missing package did not report its reason.");
        await f.Pump(f.Model.OpenDetailsAsync(Reference("unconfigured")));
        Check(f.Failures.Last().Contains("NotConfigured", StringComparison.Ordinal) && f.PackageCalls.Count == 0, "Unconfigured detail sent network work.");
    }
    private static async Task PreviewFailure(string fault)
    {
        var f = new Fixture(); f.PreviewFault = fault;
        await f.Pump(f.Model.RequestPreviewAsync(Reference()));
        for (int i = 0; i < 10; i++) await f.Pump(f.Model.RequestPreviewAsync(Reference()));
        Check(f.PreviewCalls.Count == 1 && f.Model.ReadTexture(Reference()) is null && f.Failures.Count == 0, "Failed portrait retried every frame or showed detail errors: " + fault);
        f.PreviewFault = null; f.Model.Reset(); await f.Pump(f.Model.RequestPreviewAsync(Reference()));
        Check(f.PreviewCalls.Count == 2 && f.Model.ReadTexture(Reference()) is not null, "Reset could not retry failed preview.");
    }
    private static async Task DetailFailure(string fault)
    {
        var f = new Fixture(); f.DetailFault = fault;
        await f.Pump(f.Model.OpenDetailsAsync(Reference()));
        Check(f.Shown.Count == 0 && f.Failures.Count == 1, "Failed details opened a pawn or lost failure feedback: " + fault);
        f.DetailFault = null; await f.Pump(f.Model.OpenDetailsAsync(Reference()));
        Check(f.PackageCalls.Count == 2 && f.Shown.Count == 1 && f.Failures.Count == 1, "Detail failure could not retry explicitly.");
    }
    private static async Task Capacity()
    {
        var f = new Fixture(2);
        for (int i = 0; i < 9; i++)
        {
            await f.Pump(f.Model.RequestPreviewAsync(Reference("portrait-" + i)));
            await f.Pump(f.Model.OpenDetailsAsync(Reference("detail-" + i)));
        }
        Check(f.Textures.Count == 9 && f.Cards.Count == 9 && f.Textures.Take(8).All(x => x.Disposed == 1)
            && f.Cards.Take(8).All(x => x.Disposed == 1) && f.Textures[8].Disposed == 0 && f.Cards[8].Disposed == 0,
            "Bounded cache eviction leaked or released its replacement.");
        f.Model.Reset(); await f.Pump(Task.CompletedTask);
        Check(f.Textures.All(x => x.Disposed == 1) && f.Cards.All(x => x.Disposed == 1), "Reset after eviction released resources twice.");
    }
    private static async Task Slots()
    {
        var f = new Fixture(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforePreview = async () => await release.Task;
        var tasks = Enumerable.Range(0, 12).Select(i => f.Model.RequestPreviewAsync(Reference("parallel-" + i))).ToArray();
        for (int i = 0; i < 4; i++) await f.Started.WaitAsync(TimeSpan.FromSeconds(3));
        Check(f.PreviewCalls.Count == 4 && f.MaxConcurrent == 4, "Portrait requests exceeded four download slots.");
        release.SetResult(); await f.Pump(Task.WhenAll(tasks));
        Check(f.PreviewCalls.Count == 12 && f.MaxConcurrent == 4 && f.Active == 0 && f.Textures.Count == 12, "Download slot was leaked or never reused.");
    }
    private static async Task Discard()
    {
        foreach (bool details in new[] { false, true })
        {
            var f = new Fixture(); f.Discard = true;
            await f.Pump(details ? f.Model.OpenDetailsAsync(Reference()) : f.Model.RequestPreviewAsync(Reference()));
            f.Discard = false;
            await f.Pump(details ? f.Model.OpenDetailsAsync(Reference()) : f.Model.RequestPreviewAsync(Reference()));
            Check((details ? f.Cards.Count : f.Textures.Count) == 1, "Discarded queue left a permanently pending request.");
        }
    }
    private static async Task Stale(string change, bool details)
    {
        var f = new Fixture();
        Task Start() => details ? f.Model.OpenDetailsAsync(Reference()) : f.Model.RequestPreviewAsync(Reference());
        await f.Pump(Start());
        var retained = details ? f.Cards[0] : f.Textures[0];
        var pending = details ? f.Model.OpenDetailsAsync(Reference("late")) : f.Model.RequestPreviewAsync(Reference("late"));
        var queued = await f.Ports.Next(); // Loading or completed PNG publication, held across the identity change.
        if (change == "reset") f.Model.Reset(); else f.Change(change);
        queued(); await f.Pump(pending);
        Check((details ? f.Shown.Count : f.Textures.Count) == 1 && f.Failures.Count == 0, "Old preview publication crossed " + change);
        // New reads normalize the cache even if generation cancellation skipped the old worker.
        Check(f.Model.ReadTexture(Reference()) is null, "Old texture survived " + change);
        await f.Pump(Task.CompletedTask); Check(retained.Disposed == 1, "Owner change did not release old resources: " + change);
        if (change == "unavailable") f.Session.ClearIdentity();
        await f.Pump(Start());
        Check((details ? f.Shown.Count : f.Textures.Count) == 2, "Fresh owner could not request the old package id.");
    }
    private static async Task Reentry(string phase)
    {
        var f = new Fixture(1);
        if (phase == "release")
        {
            await f.Pump(f.Model.RequestPreviewAsync(Reference("first")));
            f.OnRelease = () => f.Change("login");
            await f.Pump(f.Model.RequestPreviewAsync(Reference("second")));
            Check(f.Model.ReadTexture(Reference("second")) is null, "Eviction callback installed stale texture in a new login.");
            await f.Pump(Task.CompletedTask);
            Check(f.Textures.All(t => t.Disposed == 1), "Eviction reentry leaked an owned texture."); return;
        }
        f.OnEffect = at => { if (at == phase) f.Change("login"); };
        if (phase == "text") f.DetailFault = "network";
        await f.Pump(phase == "decode" ? f.Model.RequestPreviewAsync(Reference()) : f.Model.OpenDetailsAsync(Reference()));
        Check(f.Shown.Count == 0 && f.Failures.Count == 0 && f.Model.ReadTexture(Reference()) is null,
            "Engine callback published into a later session: " + phase);
        await f.Pump(Task.CompletedTask);
        Check(f.Textures.Concat(f.Cards).All(x => x.Disposed == 1), "Reentrant creation leaked the rejected resource.");
    }

    private sealed class Fixture : IClientPawnPreviewTransport, IClientPawnPreviewRuntime
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientPawnPreviews Model;
        internal readonly ConcurrentQueue<(ClashOfRimClientNetworkContext Context, string Id)> PreviewCalls = new(), PackageCalls = new();
        internal readonly SemaphoreSlim Started = new(0);
        internal readonly List<Resource> Textures = new(), Cards = new(), Shown = new();
        internal readonly List<ModPawnExchangePackageDto> Restored = new();
        internal readonly List<string> Failures = new();
        internal int LoadingCount, Active, MaxConcurrent;
        internal string? PreviewFault, DetailFault;
        internal bool Duplicate, Discard;
        internal Func<Task>? BeforePreview;
        internal Action<string>? OnEffect;
        internal Action? OnRelease;
        private readonly AsyncLocal<bool> engine = new();
        internal Fixture(int capacity = 256)
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            Model = new(Session, Ports, this, this, capacity, capacity);
        }
        internal Task Pump(Task task) => Ports.PumpUntil(task, ex =>
        { if (ex.Message is not ("decode" or "restore")) throw ex; });
        public async Task<ClashOfRimClientNetworkResult<byte[]>> PreviewAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token)
        {
            PreviewCalls.Enqueue((context, id)); int active = Interlocked.Increment(ref Active);
            lock (Started) MaxConcurrent = Math.Max(MaxConcurrent, active);
            Started.Release();
            try
            {
                if (BeforePreview is not null) await BeforePreview();
                if (PreviewFault == "exception") throw new InvalidOperationException("preview");
                return PreviewFault == "network" ? ClashOfRimClientNetworkResult<byte[]>.Failed("network", "expected")
                    : ClashOfRimClientNetworkResult<byte[]>.Ok(PreviewFault == "empty" ? Array.Empty<byte>() : new byte[] { 1, 2, 3 });
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public Task<ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>> PackageAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token)
        {
            PackageCalls.Enqueue((context, id));
            if (DetailFault == "exception") throw new InvalidOperationException("details");
            return Task.FromResult(DetailFault == "network" ? ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>.Failed("network", "expected")
                : ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>.Ok(new()
                { Result = DetailFault == "protocol" ? null : new() { Accepted = DetailFault != "rejected", ErrorCode = 42, Message = "rejected" },
                    PawnPackage = DetailFault == "missing" ? null : Package() }));
        }
        public void Enqueue(Action action, Action discard) => Ports.Enqueue(() => Engine(() =>
        { if (Discard) discard(); else { action(); if (Duplicate) action(); } }));
        public void Release(IReadOnlyList<IDisposable> resources)
        {
            OnRelease?.Invoke();
            Ports.Enqueue(() => Engine(() => { foreach (var resource in resources) resource.Dispose(); }));
        }
        public IClientPawnPreviewTexture? Texture(byte[] png)
        {
            CheckEngine(); Check(png.SequenceEqual(new byte[] { 1, 2, 3 }), "Preview payload changed.");
            if (PreviewFault == "decode") return null;
            if (PreviewFault == "decode-throw") throw new InvalidOperationException("decode");
            var resource = new Resource(this); Textures.Add(resource); OnEffect?.Invoke("decode"); return resource;
        }
        public IClientPawnPreviewCard? Restore(ModPawnExchangePackageDto package, out string message)
        {
            CheckEngine(); message = "restore-failure";
            if (DetailFault == "restore") return null;
            if (DetailFault == "restore-throw") throw new InvalidOperationException("restore");
            Restored.Add(ClientProtocolJson.Copy(package)); package.Reference!.Metadata["ideology"] = "restorer mutation";
            var resource = new Resource(this); Cards.Add(resource); OnEffect?.Invoke("restore"); return resource;
        }
        public void Show(IClientPawnPreviewCard card)
        { CheckEngine(); var resource = (Resource)card; Check(resource.Disposed == 0, "Displayed a released card."); Shown.Add(resource); }
        public string Text(string key, params (string Name, object? Value)[] args) { CheckEngine(); OnEffect?.Invoke("text"); return key; }
        public void Loading() { CheckEngine(); LoadingCount++; }
        public void Failure(string message) { CheckEngine(); Failures.Add(message); }
        private void CheckEngine() => Check(engine.Value, "Engine effect ran outside the engine queue.");
        private void Engine(Action action) { bool before = engine.Value; engine.Value = true; try { action(); } finally { engine.Value = before; } }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "game": Ports.GameOwner = new(); break; case "generation": ClientSessionWork.Reset(); break;
                case "unavailable": Session.BeginUnavailable(null, null); break;
            }
        }
        internal sealed class Resource : IClientPawnPreviewTexture, IClientPawnPreviewCard
        {
            private readonly Fixture fixture;
            internal int Disposed;
            internal Resource(Fixture fixture) => this.fixture = fixture;
            public void Dispose() { fixture.CheckEngine(); Check(++Disposed == 1, "Resource disposed twice."); }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
