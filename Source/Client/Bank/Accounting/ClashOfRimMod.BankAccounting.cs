using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Mercenaries;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private void RetryMercenaryArrivalAndConfirmation(Pawn? pawn)
    {
        if (pawn is null || pawn.Destroyed)
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusArrivalPawnMissing");
            ShowUnconfirmedSnapshotFailure(
                LocalMutationKind.Mercenary,
                mercenaryStatus,
                () => RetryMercenaryArrivalAndConfirmation(pawn));
            return;
        }

        if (!ClashMercenaryQuestUtility.TrySendArrivalShuttle(pawn, out string shuttleMessage))
        {
            mercenaryStatus = shuttleMessage;
            ShowUnconfirmedSnapshotFailure(
                LocalMutationKind.Mercenary,
                shuttleMessage,
                () => RetryMercenaryArrivalAndConfirmation(pawn));
            return;
        }

        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusArrivalRetried");
        Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
        StartMercenarySnapshotConfirmation();
    }

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
            SetStatus = value => mercenaryStatus = value,
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
