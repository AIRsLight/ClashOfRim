using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.RemoteMaps;

internal static class ClientRaidPreparationTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (double? minutes in new double?[] { null, -1, 0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.25, 30 })
            foreach (bool fallbackEvent in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var f = new Fixture(); f.Response.RaidMaxDurationMinutes = f.Response.RaidTimeoutGraceMinutes = minutes;
                if (fallbackEvent) f.Response.RaidEventId = " ";
                f.OnDownload = () => { f.Response.GuardDeployment!.Points = 999; f.Response.RaidEventId = "changed"; f.Response.RaidPreparationId = "changed"; };
                var result = await f.Run(); var prepared = result.Prepared;
                bool invalid = minutes is null || double.IsNaN(minutes.Value) || double.IsInfinity(minutes.Value) || minutes <= 0;
                Check(result.Failure is null && prepared is not null && prepared.EventId == (fallbackEvent ? "raid:key-é-中文-123" : "event-é")
                    && prepared.PreparationId == "prepare-é" && prepared.BattleDuration == TimeSpan.FromMinutes(invalid ? 15 : minutes!.Value)
                    && prepared.SettlementGraceDuration == TimeSpan.FromMinutes(invalid ? 2 : minutes!.Value)
                    && prepared.Guards!.Points == 100 && prepared.Snapshot.SnapshotId == "download-bound-snapshot" && prepared.Snapshot.Payload.Length == 3,
                    "Preparation changed stable IDs, durations, guards or downloaded snapshot.");
                Check(f.Prepared == 1 && f.Downloaded == 1, "Preparation retried a request.");
            }
            foreach (string failure in new[] { "transport", "response", "rejected", "missing-preparation", "metadata", "package", "payload" })
            {
                var f = new Fixture { Failure = failure };
                if (failure == "rejected") f.Response.Result = new() { Accepted = false, ErrorCode = 12, Message = "denied-é" };
                if (failure == "missing-preparation") f.Response.RaidPreparationId = " ";
                var result = await f.Run();
                var expected = failure switch { "transport" or "response" => ClientRaidPreparationFailureKind.Transport,
                    "rejected" => ClientRaidPreparationFailureKind.Rejected, "missing-preparation" => ClientRaidPreparationFailureKind.MissingPreparation,
                    _ => ClientRaidPreparationFailureKind.Download };
                Check(result.Prepared is null && result.Failure?.Kind == expected && f.Prepared == 1
                    && f.Downloaded == (expected == ClientRaidPreparationFailureKind.Download ? 1 : 0), "Preparation continued past a failed prerequisite: " + failure);
                if (failure == "transport") Check(Equals(result.Failure!.Code, "offline") && result.Failure.Message == "network-é", "Lost network error.");
                if (failure == "rejected") Check(Equals(result.Failure!.Code, 12) && result.Failure.Message == "denied-é", "Lost server rejection.");
            }
            var legacy = new Fixture(); legacy.Response.Result = null; legacy.Response.GuardDeployment = null;
            Check((await legacy.Run()).Prepared is { Guards: null }, "Legacy optional result/guard contract changed.");
            var huge = new Fixture(); huge.Response.RaidMaxDurationMinutes = double.MaxValue;
            try { await huge.Run(); throw new Exception("Expected duration overflow."); }
            catch (OverflowException) { Check(huge.Downloaded == 0, "Unrepresentable duration reached download."); }
            foreach (string stage in new[] { "before", "prepare", "download" })
            {
                var f = new Fixture(); using var cancellation = new CancellationTokenSource();
                if (stage == "before") cancellation.Cancel();
                if (stage == "prepare") f.OnPrepare = cancellation.Cancel;
                if (stage == "download") f.OnDownload = cancellation.Cancel;
                try { await f.Run(cancellation.Token); throw new Exception("Canceled preparation returned a launchable result."); }
                catch (OperationCanceledException)
                { Check(f.Prepared == (stage == "before" ? 0 : 1) && f.Downloaded == (stage == "download" ? 1 : 0), "Cancellation crossed another network boundary."); }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private sealed class Fixture : IClientRaidPreparationTransport, IClientRemoteSnapshotTransport
    {
        internal readonly ModPrepareRaidResponseDto Response = new() { Result = new() { Accepted = true }, RaidEventId = "event-é",
            RaidPreparationId = "prepare-é", DefenderSnapshotId = "embedded-unused", DefenderPackage = new() { SnapshotId = "embedded-unused" },
            GuardDeployment = new() { ContractId = "guard", Points = 100, Seed = 7 } };
        private readonly ClashOfRimClientNetworkContext context = new("http://owner", "actor", "colony", "base", null, null, "auth");
        private readonly ClientRaidPreparationCommand command = new("key:é/中文-123", new("target", "target-colony", "world", "map", 12));
        internal string? Failure;
        internal int Prepared, Downloaded;
        internal Action? OnPrepare, OnDownload;
        internal Task<ClientRaidPreparationResult> Run(CancellationToken token = default) => new ClientRaidPreparation(this, this).PrepareAsync(context, command, token);
        public Task<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext owner, ClientRaidPreparationCommand request, CancellationToken token)
        {
            Check(ReferenceEquals(owner, context) && request == command, "Preparation switched the initiating context or command.");
            Prepared++; OnPrepare?.Invoke();
            return Task.FromResult(Failure == "transport" ? ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>.Failed("offline", "network-é")
                : ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>.Ok(Failure == "response" ? null! : Response));
        }
        public Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext owner, ClientSnapshotDownloadTarget target, CancellationToken token)
        {
            Check(ReferenceEquals(owner, context) && target == new ClientSnapshotDownloadTarget("target", "target-colony", "prepare-é", "RaidPreparation"),
                "Target download did not use the accepted preparation as its authorization.");
            Downloaded++; OnDownload?.Invoke();
            return Task.FromResult(new RemoteSnapshotDownloadResult(ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(new()
                { Result = new() { Accepted = Failure != "metadata" }, SnapshotId = "download-bound-snapshot", Package = Failure == "package" ? null : new() }),
                Failure == "payload" ? null : ClashOfRimClientNetworkResult<byte[]>.Ok(new byte[] { 1, 2, 3 })));
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
