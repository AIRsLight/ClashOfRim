using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal sealed class ClientSnapshotUploader
{
    private readonly IClientSnapshotStore store;
    private readonly IClientSnapshotGame game;
    private readonly IClientSnapshotTransport transport;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientSnapshotPublication publication;

    internal ClientSnapshotUploader(IClientSnapshotStore store, IClientSnapshotGame game,
        IClientSnapshotTransport transport, ClientSynchronizationCoordinator synchronization)
    {
        this.store = store; this.game = game; this.transport = transport; this.synchronization = synchronization;
        publication = new(store, game);
    }

    internal async Task<ClientSnapshotUploadResult> UploadAsync(bool removeRaidBattleSessions,
        string? confirmationOperation, string? snapshotUploadKind, IReadOnlyList<ModSnapshotAchievementCandidateDto>? directAchievements,
        ClientSynchronizationCoordinator.Lease? continuation, CancellationToken cancellationToken)
    {
        CancellationToken generation = ClientSessionWork.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
        CancellationToken token = cancellation.Token;
        ClashOfRimClientNetworkContext initial;
        IClientSnapshotGameSession gameSession;
        using (ClientSessionWork.EnterPublication())
        {
            token.ThrowIfCancellationRequested();
            initial = store.ReadContext();
            gameSession = game.BindCurrentGame();
        }
        if (!initial.IsConfigured)
            return ClientSnapshotUploadResult.Failed("NotConfigured", null, SnapshotUploadMessage.NotConfigured);
        if (!gameSession.HasGame)
            return ClientSnapshotUploadResult.Failed("UntrackedSaveFile", null, SnapshotUploadMessage.NoRunningGame);
        var direct = CopyCandidates(directAchievements);
        ClientSynchronizationCoordinator.Lease admission;
        if (continuation is null) admission = await synchronization.AcquireSnapshotAsync(token);
        else
        {
            // A caller's preparation and domain completion retain their original lease.
            if (!continuation.IsSnapshot || !synchronization.TryAcquireSnapshot(out var accepted, continuation))
                throw new OperationCanceledException("Snapshot preparation no longer owns admission.", token);
            admission = accepted;
        }
        try
        {
            using var execution = admission.BeginUpload();
            ClashOfRimClientNetworkContext context;
            using (ClientSessionWork.EnterPublication())
            {
                CheckOwner();
                context = store.ReadContext();
                if (!SameIdentity(initial, context)) throw new OperationCanceledException("Snapshot identity changed while waiting.", token);
            }

            SnapshotSaveCapture capture = await OnGameThread(() =>
            {
                SnapshotUploadFailure? invalid = ValidateGame(context, gameSession);
                if (invalid is not null) return new SnapshotSaveCapture(null, null, Array.Empty<ModSnapshotAchievementCandidateDto>(), invalid);
                return ClientSnapshotPreparation.Capture(gameSession, removeRaidBattleSessions);
            });
            VerifyContext();
            if (capture.Failure is not null) return new(null, null, capture.Failure);
            var achievements = MergeCandidates(capture.Achievements, direct);
            ModSnapshotPackageBuildResult build = ClientSnapshotPreparation.Build(
                capture, context.UserId, context.ColonyId, snapshotUploadKind, achievements);
            if (!build.Success || build.Package is null || build.Payload is null)
                return new(null, null, SnapshotUploadFailure.FromPackageBuild(build));
            VerifyContext();
            var upload = await transport.UploadAsync(context,
                $"snapshot-upload:{context.UserId}:{context.ColonyId}:{build.Package.SnapshotId}",
                build.Package, build.Payload, confirmationOperation, achievements, token);
            VerifyContext();
            if (!upload.Success || upload.Response is null)
                return ClientSnapshotUploadResult.Failed(upload.ErrorCode ?? "SnapshotUploadFailed", upload.Message, SnapshotUploadMessage.UploadFailed);
            ModProtocolResponseDto? response = upload.Response.Result;
            if (response is not null && !response.Accepted)
                return ClientSnapshotUploadResult.Failed(response.ErrorCode.ToString(), response.Message, SnapshotUploadMessage.ServerRejected);
            string acceptedId = string.IsNullOrWhiteSpace(upload.Response.AcceptedSnapshotId)
                ? build.Package.SnapshotId : upload.Response.AcceptedSnapshotId!;
            await OnGameThread(() =>
            {
                publication.Publish(gameSession, acceptedId, upload.Response.NextLineageToken, achievements);
                return true;
            });
            CheckOwner();
            return new(acceptedId, build.SourcePath ?? "memory", null);

            void CheckOwner()
            {
                token.ThrowIfCancellationRequested();
                if (generation != ClientSessionWork.Token || !admission.IsCurrent)
                    throw new OperationCanceledException("Snapshot upload owner changed.", token);
            }

            void VerifyContext()
            {
                using var publication = ClientSessionWork.EnterPublication();
                CheckOwner();
                var current = store.ReadContext();
                if (!SameIdentity(context, current) || context.CurrentSnapshotId != current.CurrentSnapshotId)
                    throw new OperationCanceledException("Snapshot identity or lineage changed.", token);
            }

            async Task<T> OnGameThread<T>(Func<T> action)
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => completion.TrySetCanceled(token));
                gameSession.Enqueue(() =>
                {
                    try
                    {
                        using var publication = ClientSessionWork.EnterPublication();
                        VerifyContext();
                        completion.TrySetResult(action());
                    }
                    catch (OperationCanceledException) { completion.TrySetCanceled(token); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }, () => completion.TrySetCanceled(token));
                return await completion.Task;
            }
        }
        finally { if (continuation is null) admission.Dispose(); }
    }

    private static bool SameIdentity(ClashOfRimClientNetworkContext expected, ClashOfRimClientNetworkContext current) =>
        expected.ServerBaseUrl == current.ServerBaseUrl && expected.UserId == current.UserId
        && expected.ColonyId == current.ColonyId && expected.AuthToken == current.AuthToken;

    private static SnapshotUploadFailure? ValidateGame(ClashOfRimClientNetworkContext context, IClientSnapshotGameSession game)
    {
        if (string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
            return null;
        string runtime = game.RuntimeSnapshotId;
        if (string.IsNullOrWhiteSpace(runtime)) return new("UntrackedSaveFile", null, SnapshotUploadMessage.LineageMissing);
        return context.CurrentSnapshotId == runtime ? null
            : new("UntrackedSaveFile", null, SnapshotUploadMessage.LineageMismatch, context.CurrentSnapshotId, runtime);
    }

    private static IReadOnlyList<ModSnapshotAchievementCandidateDto> MergeCandidates(
        IReadOnlyList<ModSnapshotAchievementCandidateDto> queued, IReadOnlyList<ModSnapshotAchievementCandidateDto> direct)
    {
        var merged = new List<ModSnapshotAchievementCandidateDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Add(queued); Add(direct);
        return merged;
        void Add(IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates)
        {
            foreach (var candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate.AchievementId) && !string.IsNullOrWhiteSpace(candidate.EventKey)
                    && seen.Add(candidate.AchievementId.Trim() + ":" + candidate.EventKey.Trim())) merged.Add(candidate);
        }
    }

    private static IReadOnlyList<ModSnapshotAchievementCandidateDto> CopyCandidates(IReadOnlyList<ModSnapshotAchievementCandidateDto>? candidates)
    {
        var copy = new List<ModSnapshotAchievementCandidateDto>();
        if (candidates is null) return copy;
        foreach (var value in candidates) copy.Add(new ModSnapshotAchievementCandidateDto
        {
            AchievementId = value.AchievementId, EventKey = value.EventKey, Value = value.Value,
            Category = value.Category, LabelKey = value.LabelKey, IconId = value.IconId, Color = value.Color,
            AggregationKind = value.AggregationKind, MetadataJson = value.MetadataJson
        });
        return copy;
    }
}
