using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Support;

namespace AIRsLight.ClashOfRim.EventLetters;

// Adapts existing domain parsers/applicators. No identity, admission, iteration,
// package download or confirmation ordering is owned by this engine adapter.
internal sealed class ClientEventApplicationRuntime : IClientEventApplicationRuntime
{
    public ClientEventApplicationKind Classify(ModEventDetailDto detail) =>
        SupportPawnApplicator.IsReturnToSenderDetail(detail) ? ClientEventApplicationKind.SupportReturn :
        SupportPawnApplicator.IsSupportPawnDetail(detail) ? ClientEventApplicationKind.SupportArrival :
        RaidAttackerLossPayloadReader.HasAttackerLoss(detail) ? ClientEventApplicationKind.RaidLoss :
        ItemDeliveryClientProcessor.IsGiftDetail(detail) ? ClientEventApplicationKind.Gift : ClientEventApplicationKind.Other;

    public bool CanRun(ModEventDetailDto detail, ClashOfRimEventLetterActionKind action, string group) =>
        ClientEventLetterPolicy.CanRunEventLetterAction(detail, action, group);
    public bool CanAutoApply(ModEventDetailDto detail) => ClientEventLetterPolicy.CanAutoApplyDirectlyProcessableEvent(detail);

    public ItemDeliveryClientProcessingResult PrepareGift(ModEventDetailDto detail, GiftClientDecision decision,
        ClashOfRimClientNetworkContext context, string? reason) => ItemDeliveryClientProcessor.Process(detail, decision,
            context.UserId, context.ColonyId, context.CurrentSnapshotId ?? string.Empty, rejectionReason: reason);

    public ClientEventApplicationResult Apply(ModEventDetailDto detail, ClientEventApplicationKind kind,
        GiftLandingPlan? gift, ClashOfRimClientNetworkContext context)
    {
        if (kind is ClientEventApplicationKind.SupportArrival or ClientEventApplicationKind.SupportReturn)
        {
            var result = SupportPawnApplicator.ApplyToCurrentMap(detail);
            if (!result.Applied) PawnFlowFailurePolicy.LogApplicationFailure("event-support-apply", detail.EventId, result);
            return new(result.Applied, result.Message, kind == ClientEventApplicationKind.SupportReturn
                ? "SupportPawnReturnCaravanCreated" : "SupportPawnArrived", TerminalFailure: PawnFlowFailurePolicy.ShouldReportTerminalFailure(result));
        }
        if (kind == ClientEventApplicationKind.RaidLoss)
        {
            if (!RaidAttackerLossPayloadReader.TryRead(detail, context.CurrentSnapshotId, out var request, out string message) || request is null)
                return new(false, message, string.Empty);
            ClientRemoteSessionRuntime.CloseRaidBattleForExternalResolution(request.SourceRaidEventId, "raid attacker loss event applied");
            var result = RaidAttackerLossApplicator.Apply(request);
            return new(result.Applied, result.FailureReason ?? string.Empty, result.Kind.ToString(), request.SourceRaidEventId);
        }
        var applied = GiftLandingApplicator.ApplyToCurrentMap(gift!);
        if (!applied.Success) PawnFlowFailurePolicy.LogApplicationFailure("event-gift-apply", detail.EventId, applied);
        return new(applied.Success, applied.Message, "ItemDeliveryAnchored",
            TerminalFailure: PawnFlowFailurePolicy.ShouldReportTerminalFailure(applied), Gift: applied);
    }

    public void MarkHandled(string eventId) => ClashOfRimGameComponent.MarkManualEventHandled(eventId);
    public void RemoveLetters(string eventId) => ClientEventLetterRuntime.RemoveEventLetters(eventId);
}
