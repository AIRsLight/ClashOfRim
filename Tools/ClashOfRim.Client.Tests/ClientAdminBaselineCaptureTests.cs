using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;

internal static class ClientAdminBaselineCaptureTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Values(); count++;
                foreach (string change in new[] { "user", "server", "colony", "auth", "login", "work", "unavailable", "world", "snapshot", "game" })
                foreach (int phase in Enumerable.Range(0, 9)) { await Ownership(change, phase); count++; }
                foreach (int phase in Enumerable.Range(1, 8)) { await Failure(phase); count++; }
                foreach (string ending in new[] { "discard", "cancel-before", "cancel-queued", "stale-before" }) { await QueueEnding(ending); count++; }
                await LateCallback(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: administrator baseline capture ({count} scenarios)");
    }
    private static async Task Values()
    {
        var f = new Fixture(); var requirements = new List<ModAdminBaselineExtensionRequirementDto> { new() { ProviderId = "provider", Kind = "kind", DisplayName = "中文 İ" } };
        var capture = f.Module.Capture(); var work = f.Module.CaptureAsync(capture, requirements);
        requirements[0].ProviderId = "changed"; requirements.Clear();
        f.Queue(); f.Queue(); var result = await work;
        Check(f.Source.Calls == 8 && f.Source.Requirement == "provider", "Capture repeated or borrowed mutable requirements.");
        Check(DateTimeOffset.TryParseExact(result.GeneratedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var generated)
            && generated.Offset == TimeSpan.Zero, "Capture timestamp depends on culture.");
        Check(result.StandardMarketValues.Single().MarketValue == 1.25f && result.StuffMarketValues.Single().MarketValue == 3.75f
            && result.QualityMarketValueModifiers.Single().Factor == 1.2f && result.PackableBuildings.Single().MinifiedDefName == "packed"
            && result.Buildings.Single().EstimatedMaxHitPoints == 100 && result.StuffHitPointModifiers.Single().MaxHitPointsOffset == -3.5f,
            "Baseline omitted a catalog or changed a value.");
        Check(result.TrapClassifications.Select(t => t.DefName).SequenceEqual(new[] { "auto", "candidate" })
            && result.TrapClassifications[0].InheritsBuildingTrap && !result.TrapClassifications[0].AdminApproved
            && !result.TrapClassifications[1].InheritsBuildingTrap && !result.TrapClassifications[1].AdminApproved,
            "Trap sorting or approval semantics changed.");
        f.Source.Borrowed!.StandardMarketValues[0].MarketValue = 999;
        f.Source.Borrowed.BaselineExtensions[0].Records[0].Values["label"] = "changed";
        Check(result.StandardMarketValues[0].MarketValue == 1.25f && result.BaselineExtensions[0].Records[0].Values["label"] == "中文 İ",
            "An extension retained mutable references to the published baseline.");
        f.Ports.Snapshot = "latest"; Check(f.Module.Capture().Context.CurrentSnapshotId == "latest", "Capture cached an old snapshot context.");
    }
    private static async Task Ownership(string change, int phase)
    {
        var f = new Fixture(); var owner = f.Module.Capture();
        if (phase == 0) f.Change(change);
        else f.Source.After = step => { if (step == phase) f.Change(change); };
        var work = f.Module.CaptureAsync(owner, null, current: () => f.WorldCurrent);
        f.Queued?.Invoke();
        bool allowed = change is "snapshot" or "game";
        if (allowed) { await work; Check(f.Source.Calls == 8, "Account capture acquired a Game/snapshot dependency."); }
        else { await Canceled(work); Check(f.Source.Calls == phase, $"Stale capture continued: {change}/{phase} -> {f.Source.Calls}."); }
    }
    private static async Task Failure(int phase)
    {
        var f = new Fixture(); f.Source.After = step => { if (step == phase) throw new InvalidOperationException("source failure"); };
        var work = f.Module.CaptureAsync(f.Module.Capture(), null); f.Queue();
        try { await work; throw new Exception("Source failure accepted."); } catch (InvalidOperationException ex) when (ex.Message == "source failure") { }
        Check(f.Source.Calls == phase, "Capture continued after source failure.");
        var old = f.Queued!; f.Source.Calls = 0; f.Source.After = null;
        var retry = f.Module.CaptureAsync(f.Module.Capture(), null); old(); f.Queue(); await retry;
        Check(f.Source.Calls == 8, "Old callback retried a failed capture or blocked the successor.");
    }
    private static async Task QueueEnding(string ending)
    {
        var f = new Fixture(); var owner = f.Module.Capture(); using var cancellation = new CancellationTokenSource();
        if (ending == "cancel-before") cancellation.Cancel();
        if (ending == "stale-before") f.Ports.User = "other";
        var work = f.Module.CaptureAsync(owner, null, cancellation.Token);
        if (ending == "discard") f.Discard!();
        if (ending == "cancel-queued") cancellation.Cancel();
        f.Queued?.Invoke(); await Canceled(work);
        f.Queued?.Invoke(); Check(f.Source.Calls == 0, "Ended queue callback performed capture.");
    }
    private static async Task LateCallback()
    {
        var f = new Fixture(); var old = f.Module.CaptureAsync(f.Module.Capture(), null); var oldAction = f.Queued!;
        f.Ports.User = "new"; var next = f.Module.CaptureAsync(f.Module.Capture(), null);
        f.Queue(); await next; oldAction(); await Canceled(old);
        Check(f.Source.Calls == 8, "Old callback captured or interfered with the successor.");
    }
    private static async Task Canceled(Task work) { try { await work.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected canceled capture."); } catch (OperationCanceledException) { } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly Source Source = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientAdminBaselineCapture Module;
        internal Action? Queued, Discard; internal bool WorldCurrent = true;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            Module = new(Session, Source, (action, discard) => { Queued = action; Discard = discard; });
        }
        internal void Queue() => Queued!();
        internal void Change(string kind)
        {
            switch (kind)
            {
                case "user": Ports.User = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "work": ClientSessionWork.Reset(); break;
                case "unavailable": Session.BeginUnavailable(Ports.Auth, null); break;
                case "world": WorldCurrent = false; break;
                case "snapshot": Ports.Snapshot = "next"; break;
                case "game": Ports.GameOwner = new(); Ports.HasGame = false; break;
            }
        }
    }
    private sealed class Source : IClientAdminBaselineSource
    {
        internal int Calls; internal Action<int>? After; internal string? Requirement;
        internal ModSubmitAdminBaselineRequestDto? Borrowed;
        private T Read<T>(T value) { Calls++; After?.Invoke(Calls); return value; }
        public IReadOnlyList<TrapClassificationScanEntry> ScanTraps() => Read(new[] {
            new TrapClassificationScanEntry("candidate", "type", "z", "mod", TrapClassificationScanStatus.CandidateRequiresApproval, "name"),
            new TrapClassificationScanEntry("auto", "type", "a", "mod", TrapClassificationScanStatus.ApprovedByInheritance, "inherits") });
        public List<ModStandardMarketValueDto> ReadMarketValues() => Read(new List<ModStandardMarketValueDto> { new() { DefName = "thing", MarketValue = 1.25f } });
        public List<ModStuffMarketValueDto> ReadStuffMarketValues() => Read(new List<ModStuffMarketValueDto> { new() { ThingDefName = "thing", StuffDefName = "stuff", MarketValue = 3.75f } });
        public List<ModQualityMarketValueModifierDto> ReadQualityModifiers() => Read(new List<ModQualityMarketValueModifierDto> { new() { Quality = "Normal", Factor = 1.2f, MaxGain = 123.5f } });
        public List<ModPackableBuildingDto> ReadPackableBuildings() => Read(new List<ModPackableBuildingDto> { new() { DefName = "building", MinifiedDefName = "packed" } });
        public List<ModBuildingBaselineDto> ReadBuildings() => Read(new List<ModBuildingBaselineDto> { new() { DefName = "building", UseHitPoints = true, EstimatedMaxHitPoints = 100 } });
        public List<ModStuffHitPointModifierDto> ReadStuffHitPointModifiers() => Read(new List<ModStuffHitPointModifierDto> { new() { DefName = "stuff", MaxHitPointsFactor = 1.25f, MaxHitPointsOffset = -3.5f } });
        public void AppendExtensions(ModSubmitAdminBaselineRequestDto baseline, IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? requirements)
        {
            Check(baseline.StandardMarketValues.Count == 1 && baseline.Buildings.Count == 1 && baseline.TrapClassifications.Count == 0
                && baseline.StuffHitPointModifiers.Count == 0, "Extension stage order changed.");
            Requirement = requirements?.Single().ProviderId; Borrowed = baseline;
            baseline.BaselineExtensions.Add(new() { ProviderId = "provider", Kind = "kind", Records = new() { new() { Key = "record", Values = new() { ["label"] = "中文 İ" } } } });
            Read(0);
        }
    }
}
