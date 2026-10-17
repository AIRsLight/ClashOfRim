using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal sealed record ClientEventFailureReport(string IdempotencyKey, string EventId, string? SourceEventId, string SnapshotId, string Reason);

internal interface IClientEventDecisionProtocol
{
    Task<ClashOfRimClientNetworkResult<ModRejectGiftResponseDto>> RejectGiftAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModRejectSupportPawnResponseDto>> RejectSupportAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> RespondDiplomacyAsync(ClashOfRimClientNetworkContext context, string eventId, bool accepted, string? reason, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModReportEventApplicationFailureResponseDto>> ReportFailureAsync(ClashOfRimClientNetworkContext context, ClientEventFailureReport report, CancellationToken token);
}

internal sealed class ClientEventDecisionProtocol : IClientEventDecisionProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfiguredMessage;
    internal ClientEventDecisionProtocol(ClientProtocolTransport transport, Func<string> notConfiguredMessage)
    { this.transport = transport; this.notConfiguredMessage = notConfiguredMessage; }

    public Task<ClashOfRimClientNetworkResult<ModRejectGiftResponseDto>> RejectGiftAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token) =>
        Send<ModRejectGiftRequestDto, ModRejectGiftResponseDto>(context, ProtocolMessageKind.RejectGift, new()
        { EventId = eventId, UserId = context.UserId, ColonyId = context.ColonyId, CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Reason = reason, AuthToken = context.AuthToken }, token);

    public Task<ClashOfRimClientNetworkResult<ModRejectSupportPawnResponseDto>> RejectSupportAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token) =>
        Send<ModRejectSupportPawnRequestDto, ModRejectSupportPawnResponseDto>(context, ProtocolMessageKind.RejectSupportPawn, new()
        { EventId = eventId, UserId = context.UserId, ColonyId = context.ColonyId, CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Reason = reason, AuthToken = context.AuthToken }, token);

    public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> RespondDiplomacyAsync(ClashOfRimClientNetworkContext context, string eventId, bool accepted, string? reason, CancellationToken token) =>
        Send<ModRespondDiplomacyEventRequestDto, ModDiplomacyEventResponseDto>(context, ProtocolMessageKind.RespondDiplomacyEvent, new()
        { EventId = eventId, UserId = context.UserId, ColonyId = context.ColonyId, CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Accepted = accepted, Reason = reason }, token);

    public Task<ClashOfRimClientNetworkResult<ModReportEventApplicationFailureResponseDto>> ReportFailureAsync(ClashOfRimClientNetworkContext context, ClientEventFailureReport report, CancellationToken token) =>
        Send<ModReportEventApplicationFailureRequestDto, ModReportEventApplicationFailureResponseDto>(context, ProtocolMessageKind.ReportEventApplicationFailure, new()
        { IdempotencyKey = report.IdempotencyKey, EventId = report.EventId ?? string.Empty, SourceEventId = report.SourceEventId,
            UserId = context.UserId, ColonyId = context.ColonyId, CurrentSnapshotId = report.SnapshotId ?? string.Empty,
            Reason = report.Reason ?? string.Empty, AuthToken = context.AuthToken }, token);

    private Task<ClashOfRimClientNetworkResult<TResponse>> Send<TRequest, TResponse>(ClashOfRimClientNetworkContext context,
        ProtocolMessageKind kind, TRequest request, CancellationToken token) => context.IsConfigured
            ? transport.ForContext(context).PostAsync<TRequest, TResponse>(ProtocolContractManifest.Find(kind).Route, request, token)
            : Task.FromResult(ClashOfRimClientNetworkResult<TResponse>.Failed("NotConfigured", notConfiguredMessage()));
}
