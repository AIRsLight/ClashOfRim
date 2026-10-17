using AIRsLight.ClashOfRim.Modules.Synchronization;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartMercenarySnapshotConfirmation()
    {
        StartMercenarySnapshotConfirmation(completedContractId: null, completionIdempotencyKey: null);
    }

    internal void StartMercenarySnapshotConfirmation(string? completedContractId, string? completionIdempotencyKey = null)
    {
        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = LocalMutationKind.Mercenary,
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"),
            RetryAction = () => StartMercenarySnapshotConfirmation(completedContractId, completionIdempotencyKey),
            SetStatus = value => mercenaryState.Message = value,
            BuildFailureStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusConfirmationFailed",
                (result.ErrorCode ?? string.Empty).Named("CODE"),
                (result.Message ?? string.Empty).Named("MESSAGE")),
            BuildSuccessStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusConfirmationSucceeded",
                (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            OnSuccessOnMainThread = _ =>
            {
                if (!string.IsNullOrWhiteSpace(completedContractId))
                {
                    StartReportMercenaryIncident(
                        completedContractId!,
                        "Completed",
                        string.IsNullOrWhiteSpace(completionIdempotencyKey)
                            ? "mercenary-completed:" + completedContractId
                            : completionIdempotencyKey);
                    return;
                }

                StartRefreshBankStatus();
            }
        });
    }
}
