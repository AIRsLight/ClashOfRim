using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientWorldPreparationTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        int count = 0; var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "network", "null", "rejected", "missing-result", "major", "minimum-major", "minor", "minimum-minor", "version", "exception" })
                { await HelloFailure(mode); count++; }
                foreach (bool existing in new[] { false, true })
                    foreach (bool configured in new[] { false, true })
                        foreach (bool included in new[] { false, true })
                            foreach (bool create in new[] { false, true }) { await Preparation(existing, configured, included, create); count++; }
                foreach (bool existing in new[] { false, true })
                    foreach (string mode in new[] { "network", "null", "rejected", "missing-result", "missing-config" }) { await ReadFailure(existing, mode); count++; }
                foreach (string change in new[] { "user", "server", "colony", "auth", "generation", "login", "game", "snapshot" })
                {
                    foreach (string phase in new[] { "hello", "prepare", "read", "publication" }) { await Ownership(change, phase); count++; }
                    SetupOwnership(change); count++;
                }
                foreach (bool oldFirst in new[] { false, true })
                    foreach (bool throws in new[] { false, true }) { await Replacement(oldFirst, throws); count++; }
                foreach (string phase in new[] { "hello", "read", "exception" }) { await Reentry(phase); count++; }
                foreach (string mode in new[] { "network", "null", "rejected", "missing-result", "null-assignment", "empty-assignment" })
                { await PreparationBoundary(mode); count++; }
                await Completion(); await Cancellation(); Setup(); count += 3;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: client world preparation ({count} scenarios)");
    }
    private static async Task HelloFailure(string mode)
    {
        var f = new Fixture();
        switch (mode)
        {
            case "network": f.Hello = ClashOfRimClientNetworkResult<ModServerHelloResponseDto>.Failed("offline", "连接失败"); break;
            case "null": f.Hello = ClashOfRimClientNetworkResult<ModServerHelloResponseDto>.Ok(null!); break;
            case "rejected": f.Hello.Response!.Result!.Accepted = false; f.Hello.Response.Result.Message = "denied"; break;
            case "missing-result": f.Hello.Response!.Result = null; break;
            case "major": f.Hello.Response!.ProtocolMajor++; break;
            case "minimum-major": f.Hello.Response!.MinimumSupportedProtocolMajor++; break;
            case "minor": f.Hello.Response!.ProtocolMinor = ClashOfRimVersion.ProtocolMinor - 1; break;
            case "minimum-minor": f.Hello.Response!.MinimumSupportedProtocolMinor = ClashOfRimVersion.ProtocolMinor + 1; break;
            case "version": f.Hello.Response!.ProtocolVersion += "-other"; break;
            case "exception": f.Before = (_, _, _) => throw new InvalidOperationException("offline"); break;
        }
        var prepared = await f.Module.PrepareAsync(false);
        Check(prepared?.Failure is not null && prepared.Result is null && f.Calls.Count == 1, "Bad hello reached preparation: " + mode);
        Check(f.Module.Complete(prepared!, _ => { }), "Current hello failure was lost.");
    }
    private static async Task Preparation(bool existing, bool configured, bool included, bool create)
    {
        var f = new Fixture(); var response = f.Prepare.Response!;
        response.HasExistingColony = existing; response.WorldConfigured = configured;
        response.WorldConfiguration = included ? Configuration() : null;
        var prepared = await f.Module.PrepareAsync(create);
        int expected = configured && !included ? 3 : 2;
        Check(prepared?.Failure is null && prepared?.Result?.Response?.Result?.Accepted == true && f.Calls.Count == expected
            && f.Create == create, "Preparation changed create/existing/configuration branches.");
        Check(f.Calls[0].Context.ColonyId == "colony" && f.Calls[1].Context.ColonyId == "colony", "Handshake changed captured identity.");
        if (expected == 3)
            Check(f.Calls[2].Context.ColonyId == "assigned" && f.Calls[2].Context.UserId == "alice" && f.Calls[2].Context.AuthToken == "auth"
                && f.Generation == !existing, "Configuration refresh lost assigned context or generation flag.");
        if (included || expected == 3)
        {
            var input = included ? response.WorldConfiguration! : f.Read.Response!.WorldConfiguration!;
            input.Extensions[0].Metadata["culture"] = "changed"; input.DifficultyValuesXml = "changed";
            Check(prepared!.Result!.Response!.WorldConfiguration!.Extensions[0].Metadata["culture"] == "tr-TR"
                && prepared.Result.Response.WorldConfiguration.DifficultyValuesXml == "<difficulty>0.35</difficulty>", "Prepared configuration aliases transport data.");
        }
        response.AssignedColonyId = "mutated";
        Check(prepared!.Result!.Response!.AssignedColonyId == "assigned", "Prepared response aliases transport data.");
        f.Module.Discard(prepared);
    }
    private static async Task PreparationBoundary(string mode)
    {
        var f = new Fixture();
        switch (mode)
        {
            case "network": f.Prepare = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Failed("offline", "failed"); break;
            case "null": f.Prepare = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(null!); break;
            case "rejected": f.Prepare.Response!.Result!.Accepted = false; break;
            case "missing-result": f.Prepare.Response!.Result = null; break;
            case "null-assignment": f.Prepare.Response!.AssignedColonyId = null; break;
            case "empty-assignment": f.Prepare.Response!.AssignedColonyId = ""; break;
        }
        var prepared = (await f.Module.PrepareAsync(false))!;
        Check(prepared.Failure is null && prepared.Result!.Success == f.Prepare.Success, "Preparation failure was rewritten: " + mode);
        if (mode.EndsWith("assignment", StringComparison.Ordinal))
            Check(f.Calls.Count == 3 && f.Calls[2].Context.ColonyId == (mode == "null-assignment" ? "colony" : ""), "Assigned-colony fallback changed.");
        else Check(f.Calls.Count == 2, "Rejected or missing preparation triggered configuration refresh.");
        f.Module.Discard(prepared);
    }
    private static async Task ReadFailure(bool existing, string mode)
    {
        var f = new Fixture(); f.Prepare.Response!.HasExistingColony = existing;
        switch (mode)
        {
            case "network": f.Read = ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>.Failed("offline", "failed"); break;
            case "null": f.Read = ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>.Ok(null!); break;
            case "rejected": f.Read.Response!.Result!.Accepted = false; f.Read.Response.Result.ErrorCode = 123; break;
            case "missing-result": f.Read.Response!.Result = null; break;
            case "missing-config": f.Read.Response!.WorldConfiguration = null; break;
        }
        var prepared = (await f.Module.PrepareAsync(false))!;
        var result = prepared.Result!;
        bool accepted = result.Success && result.Response?.Result?.Accepted == true;
        Check(accepted == (existing || mode == "missing-config") && result.Response?.WorldConfiguration is null
            && f.Diagnostics == (existing && mode != "missing-config" ? 1 : 0), "Configuration fallback semantics changed: " + existing + "/" + mode);
        f.Module.Discard(prepared);
    }
    private static async Task Ownership(string change, string phase)
    {
        var f = new Fixture();
        f.Before = (at, _, _) => { if (at == phase) f.Change(change); return Task.CompletedTask; };
        var prepared = await f.Module.PrepareAsync(false);
        if (phase == "publication") f.Change(change);
        int published = 0;
        if (prepared is not null) f.Module.Complete(prepared, _ => published++);
        Check(published == (change == "snapshot" ? 1 : 0), "Preparation crossed ownership: " + change + "/" + phase);
        if (change != "snapshot" && phase != "publication")
            Check(prepared is null && f.Calls.Count == (phase == "hello" ? 1 : phase == "prepare" ? 2 : 3), "Stale flow continued sending requests.");
        if (f.Calls.Count == 3) Check(f.Calls[2].Context.CurrentSnapshotId == "base", "Configuration refresh reread mutable settings.");
        Check(f.Ports.Published == 0 && f.Ports.Captures == 0 && f.Ports.Queued == 0, "Preparation mutated engine state.");
    }
    private static async Task Replacement(bool oldFirst, bool throws)
    {
        var f = new Fixture(); var oldGate = Gate(); var newGate = Gate(); int calls = 0;
        f.Before = async (phase, _, _) =>
        {
            if (phase != "prepare") return;
            int number = ++calls; await (number == 1 ? oldGate.Task : newGate.Task);
            if (number == 1 && throws) throw new InvalidOperationException("late");
        };
        var old = f.Module.PrepareAsync(false); var current = f.Module.PrepareAsync(true);
        Check(calls == 2, "Preparation did not reach controlled requests.");
        if (oldFirst) { oldGate.SetResult(); Check(await old is null, "Old pending request published."); }
        newGate.SetResult(); var prepared = (await current)!;
        if (!oldFirst) { oldGate.SetResult(); Check(await old is null, "Old completion replaced a prepared result."); }
        Check(f.Module.Complete(prepared, _ => { }) && !f.Module.Complete(prepared, _ => throw new Exception("duplicate")), "Replacement completion ownership changed.");
    }
    private static async Task Completion()
    {
        var f = new Fixture(); var old = (await f.Module.PrepareAsync(false))!;
        var current = (await f.Module.PrepareAsync(true))!;
        f.Module.Discard(old);
        Check(!f.Module.Complete(old, _ => throw new Exception("stale")), "Discarded response published.");
        int count = 0;
        Check(f.Module.Complete(current, _ =>
        {
            count++;
            Check(!f.Module.Complete(current, _ => count++), "Reentrant completion published twice.");
        }) && count == 1, "Current response was lost.");
        var failed = (await f.Module.PrepareAsync(false))!;
        try { f.Module.Complete(failed, _ => throw new ApplicationException()); } catch (ApplicationException) { }
        Check(!f.Module.Complete(failed, _ => count++), "Throwing publication could be replayed.");
    }
    private static async Task Reentry(string phase)
    {
        var f = new Fixture();
        if (phase == "hello") f.Hello.Response!.Result!.Accepted = false;
        if (phase == "read")
        { f.Prepare.Response!.HasExistingColony = true; f.Read = ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>.Failed("offline", "failed"); }
        if (phase == "exception") f.Before = (_, _, _) => throw new InvalidOperationException();
        f.OnPresentation = () => f.Change("generation");
        Check(await f.Module.PrepareAsync(false) is null, "Presentation reentry retained stale output: " + phase);
    }
    private static async Task Cancellation()
    {
        var f = new Fixture(); CancellationToken observed = default;
        f.Before = async (_, _, token) => { observed = token; await Task.Delay(Timeout.Infinite, token); };
        var task = f.Module.PrepareAsync(false); ClientSessionWork.Reset();
        Check(await task.WaitAsync(TimeSpan.FromSeconds(3)) is null && observed.IsCancellationRequested, "Cancelled preparation leaked a failure.");
    }
    private static void Setup()
    {
        var f = new Fixture(); var state = f.State; var response = SetupResponse();
        state.LanguageAccepted = true; state.Prepare(response);
        response.WorldConfiguration!.Extensions[0].Metadata["culture"] = "caller";
        var copy = state.Configuration!; copy.Extensions[0].Metadata["culture"] = "reader";
        Check(state.Configuration!.Extensions[0].Metadata["culture"] == "tr-TR" && state.Pending && state.LanguageAccepted, "Pending plan aliases caller data or loses language choice.");
        var ticket = state.CaptureGeneration()!;
        ticket.Configuration.DifficultyValuesXml = "download mutation";
        Check(state.TakeGeneration() is null && state.Pending, "Generation consumed configuration before substrate was ready.");
        var package = Package();
        Check(state.AttachSubstrate(ticket, package), "Current substrate rejected.");
        package.TileGeometryPayload![0] = 99;
        Check(!state.AttachSubstrate(ticket, Package()) && !state.RejectGeneration(ticket) && state.CaptureGeneration() is null, "Duplicate download modified a ready plan.");
        var generation = state.TakeGeneration()!;
        Check(generation.Substrate.TileGeometryPayload![0] == 1 && generation.Configuration.DifficultyValuesXml == "<difficulty>0.35</difficulty>"
            && !state.Pending && state.TakeGeneration() is null && !state.AttachSubstrate(ticket, Package()), "Generation handoff lost data or replayed.");
        state.Prepare(SetupResponse()); var stale = state.CaptureGeneration()!;
        state.Prepare(SetupResponse()); var next = state.CaptureGeneration()!;
        Check(!state.AttachSubstrate(stale, Package()) && !state.RejectGeneration(stale) && state.Pending, "Old ticket erased replacement plan.");
        Check(state.RejectGeneration(next) && !state.RejectGeneration(next) && !state.Pending && state.LanguageAccepted, "Reject did not retire exactly one plan.");
        var initial = SetupResponse(); initial.WorldConfigured = false; initial.RequiresInitialWorldConfiguration = true;
        state.Prepare(initial); var initialTicket = state.TakeInitial()!;
        Check(initialTicket is not null && !state.Pending && state.TakeInitial() is null, "Initial submit was not consumed once.");
        state.RestoreInitial(initialTicket!); Check(state.Pending, "Busy initial submit cannot retry.");
        state.Prepare(SetupResponse()); state.RestoreInitial(initialTicket!);
        Check(state.TakeInitial() is null, "Old initial submit restored into a new plan.");
        foreach (string mode in new[] { "existing", "rejected", "missing", "unconfigured" })
        {
            var input = SetupResponse();
            if (mode == "existing") input.HasExistingColony = true;
            if (mode == "rejected") input.Result!.Accepted = false;
            if (mode == "missing") input.WorldConfiguration = null;
            if (mode == "unconfigured") input.WorldConfigured = false;
            state.Prepare(input); Check(!state.Pending && state.Configuration is null, "Invalid response retained generation: " + mode);
        }
    }
    private static void SetupOwnership(string change)
    {
        var f = new Fixture(); f.State.LanguageAccepted = true; f.State.Prepare(SetupResponse());
        var ticket = f.State.CaptureGeneration()!; f.Change(change);
        bool retained = change is "game" or "snapshot";
        Check(f.State.Pending == retained && f.State.LanguageAccepted == retained && f.State.AttachSubstrate(ticket, Package()) == retained, "Setup ownership mismatch: " + change);
        Check((f.State.TakeGeneration() is not null) == retained, "Setup failed normal Game transition or retained old identity.");
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ModWorldConfigurationDto Configuration() => new()
    {
        WorldConfigurationId = "world", PlanetCoverage = "0.35", DifficultyValuesXml = "<difficulty>0.35</difficulty>",
        Extensions = new() { new() { ProviderId = "culture", PayloadJson = "{\"name\":\"地球\"}", Metadata = new() { ["culture"] = "tr-TR" } } }
    };
    private static WorldSubstratePackage Package() => new(42, "<grid/>", "<features/>", "<landmarks/>", new byte[] { 1, 2, 3 });
    private static ModPrepareWorldSessionResponseDto SetupResponse() => new() { Result = new() { Accepted = true }, WorldConfigured = true, WorldConfiguration = Configuration() };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientWorldPreparationTransport, IClientWorldPreparationPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldPreparation Module;
        internal readonly ClientWorldSetupState State;
        internal ClashOfRimClientNetworkResult<ModServerHelloResponseDto> Hello = ClashOfRimClientNetworkResult<ModServerHelloResponseDto>.Ok(new()
        {
            Result = new() { Accepted = true }, ProtocolVersion = ClashOfRimVersion.ProtocolVersion,
            ProtocolMajor = ClashOfRimVersion.ProtocolMajor, MinimumSupportedProtocolMajor = ClashOfRimVersion.ProtocolMajor,
            ProtocolMinor = ClashOfRimVersion.ProtocolMinor, MinimumSupportedProtocolMinor = ClashOfRimVersion.ProtocolMinor
        });
        internal ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> Prepare = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(new()
        { Result = new() { Accepted = true }, WorldConfigured = true, AssignedColonyId = "assigned" });
        internal ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto> Read = ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>.Ok(new()
        { Result = new() { Accepted = true }, WorldConfiguration = Configuration() });
        internal readonly List<(string Phase, ClashOfRimClientNetworkContext Context)> Calls = new();
        internal Func<string, int, CancellationToken, Task>? Before;
        internal Action? OnPresentation;
        internal bool Create, Generation;
        internal int Diagnostics;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Module = new(Session, Ports, this, this); State = new(Session);
        }
        private Task Enter(string phase, ClashOfRimClientNetworkContext context, CancellationToken token)
        { Calls.Add((phase, context)); return Before?.Invoke(phase, Calls.Count, token) ?? Task.CompletedTask; }
        public async Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> HelloAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        { await Enter("hello", context, token); return Hello; }
        public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, bool createAccount, CancellationToken token)
        { Create = createAccount; await Enter("prepare", context, token); return Prepare; }
        public async Task<ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, bool generation, CancellationToken token)
        { Generation = generation; await Enter("read", context, token); return Read; }
        public string Text(string key, params (string Name, object? Value)[] arguments) { OnPresentation?.Invoke(); return key; }
        public void Diagnose(string message) { Diagnostics++; OnPresentation?.Invoke(); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "server": Ports.Server += "/other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break;
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "same-credentials" }); break;
            }
        }
    }
}
