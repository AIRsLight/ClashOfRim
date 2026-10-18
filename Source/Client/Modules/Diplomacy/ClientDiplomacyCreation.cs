using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Diplomacy;

internal enum ClientDiplomacyKind { AllianceRequest, AllianceCancellation, WarDeclaration, PeaceRequest, SupportRequest }
internal enum ClientDiplomacyRelation { Neutral, Ally, Hostile }
internal sealed record ClientDiplomacyTarget(string UserId, string ColonyId, string? DisplayName = null);
internal sealed record ClientDiplomacyCommand(string IdempotencyKey, ClientDiplomacyTarget Target, string Kind, string? Message, DateTimeOffset? ExpiresAtUtc);
internal enum ClientDiplomacyCreationStage { AdmissionFailed, Busy, Started, TransportFailed, Rejected, Incomplete, Exception, Accepted }
internal sealed record ClientDiplomacyCreationNotice(ClientDiplomacyCreationStage Stage, string Kind, ClientDiplomacyTarget Target,
    string? Code = null, string? Message = null, Exception? Exception = null,
    LocalMutationAdmission Admission = LocalMutationAdmission.Allowed, LocalMutationState? Pending = null);
internal interface IClientDiplomacyCreationTransport
{
    Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> CreateAsync(
        ClashOfRimClientNetworkContext context, ClientDiplomacyCommand command, CancellationToken token);
}
internal interface IClientDiplomacyCreationPresentation
{
    string DefaultMessage(string kind);
    void Report(ClientDiplomacyCreationNotice notice);
    void ApplyRelation(string userId, string relation, string kind);
}

internal static class ClientDiplomacyNames
{
    // Enum.TryParse alone would also accept numbers; the wire contract accepts names only.
    internal static bool TryKind(string? text, out ClientDiplomacyKind kind) => TryName(text, out kind);
    internal static bool TryRelation(string? text, out ClientDiplomacyRelation relation) => TryName(text, out relation);
    private static bool TryName<T>(string? text, out T value) where T : struct, Enum => Enum.TryParse(text, true, out value)
        && Enum.IsDefined(typeof(T), value) && string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase);
}

internal sealed class ClientDiplomacyCreation
{
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly IClientDiplomacyCreationTransport transport;
    private readonly IClientDiplomacyCreationPresentation presentation;
    internal ClientDiplomacyCreation(ClientSnapshotMutationAdmission admission, ClientSnapshotTransaction transactions,
        ClientLocalMutationCoordinator mutations, IClientDiplomacyCreationTransport transport, IClientDiplomacyCreationPresentation presentation)
    { this.admission = admission; this.transactions = transactions; this.mutations = mutations; this.transport = transport; this.presentation = presentation; }

    internal event Action? RefreshRequired;

    internal bool Start(string kind, ClientDiplomacyTarget target, string? message, out Task work)
    {
        using var guard = ClientSessionWork.EnterPublication();
        work = Task.CompletedTask;
        if (!ClientDiplomacyNames.TryKind(kind, out var action) || string.IsNullOrWhiteSpace(target.UserId) || string.IsNullOrWhiteSpace(target.ColonyId)) return false;
        kind = action.ToString();
        var admitted = admission.Begin();
        if (admitted.Attempt is not { } attempt)
        {
            presentation.Report(new(admitted.Failure == ClientSnapshotAdmissionFailure.Busy ? ClientDiplomacyCreationStage.Busy : ClientDiplomacyCreationStage.AdmissionFailed,
                kind, target, Admission: admitted.Admission, Pending: mutations.Current));
            return false;
        }
        if (target.UserId == attempt.Context.UserId && target.ColonyId == attempt.Context.ColonyId) { attempt.Admission.Dispose(); return false; }
        try
        {
            var command = new ClientDiplomacyCommand(
                $"diplomacy:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{target.UserId}:{target.ColonyId}:{kind}:{Guid.NewGuid():N}",
                target, kind, string.IsNullOrWhiteSpace(message) ? presentation.DefaultMessage(kind) : message,
                action is ClientDiplomacyKind.AllianceRequest or ClientDiplomacyKind.PeaceRequest ? DateTimeOffset.UtcNow.AddDays(3) : null);
            transactions.TryApply(attempt, () => presentation.Report(new(ClientDiplomacyCreationStage.Started, kind, target)));
            work = ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
                () => transport.CreateAsync(attempt.Context, command, attempt.Owner.Token), r => r.Result,
                failure => PublishFailure(attempt, command, failure), response => PublishAccepted(attempt, command, action, response)));
            return true;
        }
        catch { attempt.Admission.Dispose(); throw; }
    }

    private void PublishAccepted(ClientSnapshotTransaction.Attempt attempt, ClientDiplomacyCommand command, ClientDiplomacyKind action, ModDiplomacyEventResponseDto response)
    {
        bool hasRelation = ClientDiplomacyNames.TryRelation(response.RelationKind, out var relation);
        bool immediate = action is ClientDiplomacyKind.WarDeclaration or ClientDiplomacyKind.AllianceCancellation;
        if (response.Result?.Accepted != true || string.IsNullOrWhiteSpace(response.EventId)
            || !hasRelation && (immediate || response.RelationKind is not null))
        { Finish(attempt, new(ClientDiplomacyCreationStage.Incomplete, command.Kind, command.Target)); return; }
        if (hasRelation && !transactions.TryApply(attempt, () => presentation.ApplyRelation(command.Target.UserId, relation.ToString(), command.Kind))) return;
        Finish(attempt, new(ClientDiplomacyCreationStage.Accepted, command.Kind, command.Target));
    }

    private Task PublishFailure(ClientSnapshotTransaction.Attempt attempt, ClientDiplomacyCommand command,
        ClientSnapshotTransactionFailure<ModDiplomacyEventResponseDto> failure) =>
        ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token, effect => transactions.TryApply(attempt, effect), () =>
            Finish(attempt, new(failure.Kind switch
            {
                ClientSnapshotTransactionFailureKind.Transport => ClientDiplomacyCreationStage.TransportFailed,
                ClientSnapshotTransactionFailureKind.Rejected => ClientDiplomacyCreationStage.Rejected,
                _ => ClientDiplomacyCreationStage.Exception
            }, command.Kind, command.Target, failure.Network?.ErrorCode ?? failure.Rejection?.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                failure.Rejection?.Message ?? failure.Network?.Message, failure.Exception)));

    private void Finish(ClientSnapshotTransaction.Attempt attempt, ClientDiplomacyCreationNotice notice)
    {
        if (!transactions.TryApply(attempt, () => presentation.Report(notice))) return;
        transactions.TryApply(attempt, () =>
        {
            attempt.Admission.Dispose();
            RefreshRequired?.Invoke();
        });
    }
}
