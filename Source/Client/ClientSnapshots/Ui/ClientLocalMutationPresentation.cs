using AIRsLight.ClashOfRim.Modules.Synchronization;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal static class ClientLocalMutationPresentation
{
    internal static string OperationName(LocalMutationKind kind) => ClashOfRimText.Key(kind switch
    {
        LocalMutationKind.Bank => "ClashOfRim.SnapshotConfirmationFailure.OperationBank",
        LocalMutationKind.Mercenary => "ClashOfRim.SnapshotConfirmationFailure.OperationMercenary",
        LocalMutationKind.AutomaticEvents => "ClashOfRim.SnapshotConfirmationFailure.OperationAutomaticEvents",
        LocalMutationKind.Gift => "ClashOfRim.SnapshotConfirmationFailure.OperationGift",
        LocalMutationKind.Support => "ClashOfRim.SnapshotConfirmationFailure.OperationSupport",
        LocalMutationKind.SupportArrival => "ClashOfRim.EventLetter.SupportConfirmOperation",
        LocalMutationKind.Raid => "ClashOfRim.SnapshotConfirmationFailure.OperationRaid",
        LocalMutationKind.RaidRestore => "ClashOfRim.SnapshotConfirmationFailure.OperationRaidRestore",
        LocalMutationKind.RaidAttacker => "ClashOfRim.SnapshotConfirmationFailure.OperationRaidAttacker",
        LocalMutationKind.Shop => "ClashOfRim.SnapshotConfirmationFailure.OperationShop",
        LocalMutationKind.Trade => "ClashOfRim.SnapshotConfirmationFailure.OperationTrade",
        LocalMutationKind.ColonyRelocation => "ClashOfRim.SnapshotConfirmationFailure.OperationColonyRelocation",
        LocalMutationKind.GiftSend => "ClashOfRim.GiftDelivery.OperationSend",
        LocalMutationKind.SupportSend => "ClashOfRim.Support.OperationSend",
        LocalMutationKind.Endgame => "ClashOfRim.Endgame.UploadStarted",
        LocalMutationKind.PreparedEvent => "ClashOfRim.Events.StatusPreparedConfirmOperation",
        _ => "ClashOfRim.LocalAtomicMutation.DefaultOperation"
    });

    internal static string Blocked(LocalMutationState mutation) => string.IsNullOrWhiteSpace(mutation.Status)
        ? ClashOfRimText.Key("ClashOfRim.LocalAtomicMutation.Waiting", OperationName(mutation.Kind).Named("OPERATION"))
        : ClashOfRimText.Key("ClashOfRim.LocalAtomicMutation.WaitingWithStatus",
            OperationName(mutation.Kind).Named("OPERATION"), mutation.Status.Named("STATUS"));

    internal static string AdmissionFailure(LocalMutationAdmission admission, LocalMutationState? mutation) => admission switch
    {
        LocalMutationAdmission.NotConfigured => ClashOfRimText.Key("ClashOfRim.Sync.StatusNotConfigured"),
        LocalMutationAdmission.SnapshotMissing => ClashOfRimText.Key("ClashOfRim.Sync.StatusSnapshotMissing"),
        LocalMutationAdmission.PendingMutation when mutation is not null => Blocked(mutation),
        _ => string.Empty
    };
}
