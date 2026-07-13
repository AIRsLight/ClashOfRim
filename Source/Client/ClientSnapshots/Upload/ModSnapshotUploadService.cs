using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

public sealed class ModSnapshotUploadService
{
    private static readonly SemaphoreSlim UploadGate = new(1, 1);
    private readonly ClashOfRimSettings settings;

    public ModSnapshotUploadService(ClashOfRimSettings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ModSnapshotUploadResult> UploadConfiguredSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await UploadConfiguredSnapshotAsync(
            removeRaidBattleSessions: false,
            confirmationOperation: null,
            snapshotUploadKind: null,
            cancellationToken: cancellationToken);
    }

    public async Task<ModSnapshotUploadResult> UploadConfiguredSnapshotAsync(
        bool removeRaidBattleSessions = false,
        string? confirmationOperation = null,
        string? snapshotUploadKind = null,
        IReadOnlyList<ModSnapshotAchievementCandidateDto>? achievementCandidates = null,
        CancellationToken cancellationToken = default)
    {
        await UploadGate.WaitAsync(cancellationToken);
        try
        {
            if (!settings.IsConfigured)
            {
                return ModSnapshotUploadResult.Failed(
                    "NotConfigured",
                    ClashOfRimText.Key("ClashOfRim.SnapshotUpload.NotConfigured"));
            }

            SnapshotSaveCapture saveCapture = await CaptureSaveBytesOnMainThreadAsync(
                removeRaidBattleSessions,
                cancellationToken);
            if (!saveCapture.Success)
            {
                return ModSnapshotUploadResult.Failed(
                    saveCapture.ErrorCode ?? "SaveToMemoryFailed",
                    saveCapture.Message ?? ClashOfRimText.Key("ClashOfRim.SnapshotUpload.BuildFailed"));
            }

            IReadOnlyList<ModSnapshotAchievementCandidateDto> uploadAchievementCandidates =
                MergeAchievementCandidates(
                    ClashOfRimGameComponent.CopyPendingAchievementCandidates(),
                    achievementCandidates);
            byte[] saveBytes = saveCapture.SaveBytes ?? Array.Empty<byte>();
            if (uploadAchievementCandidates.Count > 0)
            {
                saveBytes = SnapshotSaveSanitizer.RemovePendingAchievementQueue(saveBytes);
            }

            ModSnapshotPackageBuildResult build = ModSnapshotPackageBuilder.FromSaveBytes(
                saveBytes,
                "memory",
                settings.UserId,
                settings.ColonyId,
                DateTime.UtcNow);

            if (!build.Success || build.Package is null || build.Payload is null)
            {
                return ModSnapshotUploadResult.Failed(build.ErrorCode ?? "SnapshotBuildFailed", build.Message ?? ClashOfRimText.Key("ClashOfRim.SnapshotUpload.BuildFailed"));
            }

            build.Package.SnapshotUploadKind = snapshotUploadKind;
            build.Package.DefenderThreatPoints = saveCapture.DefenderThreatPoints;

            using var httpClient = new HttpClient();
            var context = ClashOfRimClientNetworkContext.FromSettings(settings);
            var client = new ClashOfRimModNetworkClient(httpClient, context);
            string idempotencyKey = $"snapshot-upload:{settings.UserId}:{settings.ColonyId}:{build.Package.SnapshotId}";
            if (Prefs.DevMode)
            {
                ClashLog.Message("[ClashOfRim][SnapshotUpload] package lineage: snapshot="
                    + build.Package.SnapshotId
                    + ", previous="
                    + (build.Package.PreviousSnapshotId ?? string.Empty)
                    + ", token="
                    + ShortToken(build.Package.LineageToken)
                    + ", settingsSnapshot="
                    + (settings.CurrentSnapshotId ?? string.Empty)
                    + ".");
            }

            ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto> upload =
                await client.UploadSnapshotAsync(
                    idempotencyKey,
                    build.Package,
                    build.Payload,
                    confirmationOperation,
                    uploadAchievementCandidates,
                    cancellationToken);

            if (!upload.Success || upload.Response is null)
            {
                return ModSnapshotUploadResult.Failed(upload.ErrorCode ?? "SnapshotUploadFailed", upload.Message ?? ClashOfRimText.Key("ClashOfRim.SnapshotUpload.UploadFailed"));
            }

            ModProtocolResponseDto? serverResult = upload.Response.Result;
            if (serverResult is not null && !serverResult.Accepted)
            {
                return ModSnapshotUploadResult.Failed(
                    serverResult.ErrorCode.ToString(),
                    serverResult.Message ?? ClashOfRimText.Key("ClashOfRim.SnapshotUpload.ServerRejected"));
            }

            string acceptedSnapshotId = upload.Response.AcceptedSnapshotId ?? build.Package.SnapshotId;
            settings.CurrentSnapshotId = acceptedSnapshotId;
            settings.CurrentLineageToken = upload.Response.NextLineageToken ?? settings.CurrentLineageToken;
            ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
            {
                if (!string.IsNullOrWhiteSpace(upload.Response.NextLineageToken))
                {
                    ClashOfRimGameComponent.SetSnapshotLineage(acceptedSnapshotId, upload.Response.NextLineageToken);
                }

                if (uploadAchievementCandidates.Count > 0)
                {
                    ClashOfRimGameComponent.MarkPendingAchievementCandidatesUploaded(uploadAchievementCandidates);
                }

                settings.Write();
            });

            return ModSnapshotUploadResult.Ok(acceptedSnapshotId, build.SourcePath ?? "memory");
        }
        finally
        {
            UploadGate.Release();
        }
    }

    private static string ShortToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return token!.Length <= 12 ? token : token.Substring(0, 12);
    }

    private static IReadOnlyList<ModSnapshotAchievementCandidateDto> MergeAchievementCandidates(
        IReadOnlyList<ModSnapshotAchievementCandidateDto>? queuedCandidates,
        IReadOnlyList<ModSnapshotAchievementCandidateDto>? directCandidates)
    {
        var merged = new List<ModSnapshotAchievementCandidateDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddCandidates(queuedCandidates);
        AddCandidates(directCandidates);
        return merged;

        void AddCandidates(IEnumerable<ModSnapshotAchievementCandidateDto>? candidates)
        {
            if (candidates is null)
            {
                return;
            }

            foreach (ModSnapshotAchievementCandidateDto candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.AchievementId)
                    || string.IsNullOrWhiteSpace(candidate.EventKey))
                {
                    continue;
                }

                string key = candidate.AchievementId.Trim() + ":" + candidate.EventKey.Trim();
                if (seen.Add(key))
                {
                    merged.Add(candidate);
                }
            }
        }
    }

    private Task<SnapshotSaveCapture> CaptureSaveBytesOnMainThreadAsync(
        bool removeRaidBattleSessions,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SnapshotSaveCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.SetCanceled();
            return completion.Task;
        }

        CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled());
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled();
                    return;
                }

                if (!TryValidateCurrentGameForUpload(out string validationFailureReason))
                {
                    completion.TrySetResult(SnapshotSaveCapture.Failed("UntrackedSaveFile", validationFailureReason));
                    return;
                }

                if (!ClashOfRimGameComponent.TrySaveCurrentGameToBytes(
                        removeRaidBattleSessions,
                        out byte[] saveBytes,
                        out string saveFailureReason))
                {
                    completion.TrySetResult(SnapshotSaveCapture.Failed("SaveToMemoryFailed", saveFailureReason));
                    return;
                }

                completion.TrySetResult(SnapshotSaveCapture.Ok(
                    saveBytes,
                    SnapshotDefenderThreatPointsCapture.TryCapture()));
            }
            catch (Exception ex)
            {
                completion.TrySetResult(SnapshotSaveCapture.Failed("SaveToMemoryException", $"{ex.GetType().Name} {ex.Message}"));
            }
            finally
            {
                registration.Dispose();
            }
        });

        return completion.Task;
    }

    private bool TryValidateCurrentGameForUpload(out string failureReason)
    {
        failureReason = string.Empty;
        if (string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            if (Verse.Current.Game is not null)
            {
                return true;
            }

            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.NoRunningGame");
            return false;
        }

        (string RuntimeSnapshotId, _) = ClashOfRimGameComponent.CopySnapshotLineage();
        if (string.IsNullOrWhiteSpace(RuntimeSnapshotId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.LineageMissing");
            return false;
        }

        if (string.Equals(settings.CurrentSnapshotId, RuntimeSnapshotId, StringComparison.Ordinal))
        {
            return true;
        }

        failureReason = ClashOfRimText.Key(
            "ClashOfRim.SnapshotUpload.LineageMismatch",
            settings.CurrentSnapshotId.Named("SERVER"),
            RuntimeSnapshotId.Named("LOCAL"));
        return false;
    }

    private sealed class SnapshotSaveCapture
    {
        private SnapshotSaveCapture(
            bool success,
            byte[]? saveBytes,
            float? defenderThreatPoints,
            string? errorCode,
            string? message)
        {
            Success = success;
            SaveBytes = saveBytes;
            DefenderThreatPoints = defenderThreatPoints;
            ErrorCode = errorCode;
            Message = message;
        }

        public bool Success { get; }

        public byte[]? SaveBytes { get; }

        public float? DefenderThreatPoints { get; }

        public string? ErrorCode { get; }

        public string? Message { get; }

        public static SnapshotSaveCapture Ok(byte[] saveBytes, float? defenderThreatPoints)
        {
            return new SnapshotSaveCapture(true, saveBytes, defenderThreatPoints, null, null);
        }

        public static SnapshotSaveCapture Failed(string errorCode, string message)
        {
            return new SnapshotSaveCapture(false, null, null, errorCode, message);
        }
    }
}
