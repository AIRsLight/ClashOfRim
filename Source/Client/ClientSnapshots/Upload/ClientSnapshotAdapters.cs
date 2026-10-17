using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

// Composition for the one running game. Both the Mod and the public compatibility
// facade use this coordinator; there is no second upload semaphore or settings cache.
internal static class ClientSnapshotComposition
{
    internal static readonly ClientSynchronizationCoordinator Synchronization = new();
    internal static ClientSnapshotPreparationAdapter CreatePreparation() => new(new ClientSnapshotGame());
    internal static ClientSnapshotPublication CreatePublication(ClashOfRimSettings settings) =>
        new(new ClientSnapshotStore(settings), new ClientSnapshotGame());
    internal static ClientSnapshotUploader Create(ClashOfRimSettings settings) =>
        new(new ClientSnapshotStore(settings), new ClientSnapshotGame(), new ClientSnapshotTransport(), Synchronization);
}

internal sealed class ClientSnapshotStore : IClientSnapshotStore
{
    private readonly ClashOfRimSettings settings;
    internal ClientSnapshotStore(ClashOfRimSettings settings) => this.settings = settings;
    public ClashOfRimClientNetworkContext ReadContext() => ClashOfRimClientNetworkContext.FromSettings(settings);
    public void Publish(string snapshotId, string? lineageToken)
    {
        settings.CurrentSnapshotId = snapshotId;
        settings.CurrentLineageToken = lineageToken ?? settings.CurrentLineageToken;
    }
    public void Persist() => settings.Write();
}

internal sealed class ClientSnapshotGame : IClientSnapshotGame
{
    public IClientSnapshotGameSession BindCurrentGame() => new Session(Current.Game);

    private sealed class Session : IClientSnapshotGameSession
    {
        private readonly Game? owner;
        internal Session(Game? owner) => this.owner = owner;
        public void Enqueue(Action action, Action onDiscard) => ClashOfRimGameComponent.EnqueueMainThreadAction(
            () => { if (IsCurrent) action(); else onDiscard(); }, onDiscard);
        public bool HasGame => owner is not null;
        public bool IsCurrent => ReferenceEquals(owner, Current.Game);
        public string RuntimeSnapshotId => ClashOfRimGameComponent.CopySnapshotLineage().SnapshotId;
        public SnapshotSaveCapture Capture(bool removeRaidBattleSessions)
        {
            if (!ClashOfRimGameComponent.TrySaveCurrentGameToBytes(removeRaidBattleSessions, out byte[] saveBytes, out string reason))
                return new(null, null, Array.Empty<ModSnapshotAchievementCandidateDto>(),
                    new("SaveToMemoryFailed", reason, SnapshotUploadMessage.SaveFailed));
            return new(saveBytes, SnapshotDefenderThreatPointsCapture.TryCapture(), ClashOfRimGameComponent.CopyPendingAchievementCandidates());
        }
        public void Publish(string snapshotId, string? lineageToken, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements)
        {
            if (!string.IsNullOrWhiteSpace(lineageToken)) ClashOfRimGameComponent.SetSnapshotLineage(snapshotId, lineageToken);
            if (achievements.Count > 0) ClashOfRimGameComponent.MarkPendingAchievementCandidatesUploaded(achievements);
        }
    }
}

internal sealed class ClientSnapshotTransport : IClientSnapshotTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(
        ClashOfRimClientNetworkContext context, string idempotencyKey, ModSnapshotPackageMetadataDto package,
        byte[] payload, string? confirmationOperation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements,
        CancellationToken cancellationToken)
    {
        if (Prefs.DevMode)
        {
            string token = package.LineageToken ?? string.Empty;
            ClashLog.Message("[ClashOfRim][SnapshotUpload] package lineage: snapshot=" + package.SnapshotId
                + ", previous=" + (package.PreviousSnapshotId ?? string.Empty)
                + ", token=" + token.Substring(0, Math.Min(12, token.Length))
                + ", settingsSnapshot=" + (context.CurrentSnapshotId ?? string.Empty) + ".");
        }
        using var http = new HttpClient();
        return await new ClashOfRimModNetworkClient(http, context).UploadSnapshotAsync(
            idempotencyKey, package, payload, confirmationOperation, achievements, cancellationToken);
    }
}
