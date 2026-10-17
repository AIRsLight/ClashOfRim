using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal sealed class ClientMercenaryConfirmation
{
    private readonly ClientLocalSnapshotConfirmation confirmation;
    private readonly ClientMercenaryState state;
    private readonly IClientMercenaryPresentation presentation;
    internal ClientMercenaryConfirmation(ClientLocalSnapshotConfirmation confirmation, ClientMercenaryState state, IClientMercenaryPresentation presentation)
    { this.confirmation = confirmation; this.state = state; this.presentation = presentation; }
    internal event Action<string, string>? ContractCompleted;
    internal event Action? BankRefreshRequested;
    internal Task ConfirmAsync(string? completedContractId = null, string? completionIdempotencyKey = null) =>
        confirmation.StartAsync(Request(completedContractId, completionIdempotencyKey));
    internal Task ConfirmIncidentAsync(ClientSnapshotTransaction.Attempt attempt, Action confirmed) =>
        confirmation.ContinueAsync(Request(null, null) with { OnSuccessOnMainThread = _ =>
        { confirmed(); BankRefreshRequested?.Invoke(); } }, attempt);
    private ClientLocalSnapshotRequest Request(string? completedContractId, string? completionIdempotencyKey) => new()
    {
        Operation = LocalMutationKind.Mercenary,
        UploadingStatus = presentation.Text("StatusUploadingConfirmation"),
        SetStatus = value => state.Message = value,
        BuildFailureStatus = result => presentation.Text("StatusConfirmationFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message)),
        BuildSuccessStatus = result => presentation.Text("StatusConfirmationSucceeded", ("SNAPSHOT", result.AcceptedSnapshotId)),
        BuildExceptionStatus = ex => presentation.Text("StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)),
        OnSuccessOnMainThread = _ =>
        {
            if (!string.IsNullOrWhiteSpace(completedContractId))
                ContractCompleted?.Invoke(completedContractId!, string.IsNullOrWhiteSpace(completionIdempotencyKey)
                    ? "mercenary-completed:" + completedContractId : completionIdempotencyKey!);
            else BankRefreshRequested?.Invoke();
        }
    };
}
