using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal enum ClientEventApplicationKind { Gift, SupportArrival, SupportReturn, RaidLoss, Other }
internal enum ClientEventApplicationStage
{
    MissingDetail, NoGift, NoPending, AdmissionFailed, Busy, Applying, Prepared, HydrationFailed,
    Applied, Failed, Exception, Confirming
}
internal enum ClientEventApplicationReason { MissingDetail, RejectGift, AcceptLetter, RejectLetter, RejectSupportLetter }
internal sealed record ClientEventApplicationNotice(ClientEventApplicationStage Stage, bool Automatic = false,
    string EventId = "", ClientEventApplicationKind Kind = ClientEventApplicationKind.Gift,
    string? Message = null, Exception? Exception = null, int Count = 0, int Pending = 0,
    LocalMutationAdmission Admission = LocalMutationAdmission.Allowed, LocalMutationState? Mutation = null,
    ItemDeliveryClientProcessingResult? Prepared = null, GiftLandingApplicationResult? Gift = null);
internal sealed record ClientEventApplicationResult(bool Applied, string Message, string ApplicationResult,
    string? SourceEventId = null, bool TerminalFailure = false, GiftLandingApplicationResult? Gift = null);

internal interface IClientEventApplicationRuntime
{
    ClientEventApplicationKind Classify(ModEventDetailDto detail);
    bool CanRun(ModEventDetailDto detail, ClashOfRimEventLetterActionKind action, string group);
    bool CanAutoApply(ModEventDetailDto detail);
    ItemDeliveryClientProcessingResult PrepareGift(ModEventDetailDto detail, GiftClientDecision decision,
        ClashOfRimClientNetworkContext context, string? reason);
    ClientEventApplicationResult Apply(ModEventDetailDto detail, ClientEventApplicationKind kind,
        GiftLandingPlan? gift, ClashOfRimClientNetworkContext context);
    void MarkHandled(string eventId);
    void RemoveLetters(string eventId);
}
internal interface IClientGiftPackageHydration
{
    Task<PawnPackageTransferResult> HydrateAsync(GiftLandingPlan plan, ClashOfRimClientNetworkContext context,
        CancellationToken cancellationToken = default);
}
internal interface IClientEventApplicationPresentation
{
    string Format(ClientEventApplicationNotice notice);
    void Report(ClientEventApplicationNotice notice);
    string Reason(ClientEventApplicationReason reason);
}
internal interface IClientEventApplicationConfirmations
{
    Task GiftAsync(string eventId, string applicationResult);
    Task SupportArrivalAsync(string eventId);
    Task AutomaticAsync(IReadOnlyList<ModConfirmEventApplicationEntryDto> applications);
}
internal interface IClientEventApplicationDecisions
{
    bool RejectGift(string eventId, string? reason, out Task work);
    bool RejectSupport(string eventId, string? reason, out Task work);
    bool RespondDiplomacy(string eventId, string actorUserId, ServerEventType eventType, bool accepted, string? reason, out Task work);
    Task ReportFailureAsync(string eventId, string? sourceEventId, string reason);
}
