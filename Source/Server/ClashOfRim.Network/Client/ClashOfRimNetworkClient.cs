using System.Net.Http.Json;
using AIRsLight.ClashOfRim.Protocol;
using System.Text;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ClashOfRimNetworkClient
{
    private readonly HttpClient httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ClashOfRimNetworkClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<LoginRequest, LoginResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route,
            request,
            cancellationToken);
    }

    public Task<MaintainPresenceResponse> MaintainPresenceAsync(MaintainPresenceRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<MaintainPresenceRequest, MaintainPresenceResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).Route,
            request,
            cancellationToken);
    }

    public Task<ListPlayersResponse> ListPlayersAsync(ListPlayersRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<ListPlayersRequest, ListPlayersResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListPlayers).Route,
            request,
            cancellationToken);
    }

    public Task<ListAchievementsResponse> ListAchievementsAsync(ListAchievementsRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<ListAchievementsRequest, ListAchievementsResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListAchievements).Route,
            request,
            cancellationToken);
    }

    public Task<SendChatMessageResponse> SendChatMessageAsync(SendChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<SendChatMessageRequest, SendChatMessageResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.SendChatMessage).Route,
            request,
            cancellationToken);
    }

    public Task<ListChatMessagesResponse> ListChatMessagesAsync(ListChatMessagesRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<ListChatMessagesRequest, ListChatMessagesResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListChatMessages).Route,
            request,
            cancellationToken);
    }

    public Task<UploadSnapshotResponse> UploadSnapshotAsync(
        UploadSnapshotMetadataRequest request,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        return PostSnapshotMultipartAsync<UploadSnapshotMetadataRequest, UploadSnapshotResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route,
            request,
            payload,
            cancellationToken);
    }

    public Task<DownloadLatestSnapshotResponse> DownloadLatestSnapshotAsync(DownloadLatestSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<DownloadLatestSnapshotRequest, DownloadLatestSnapshotResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshot).Route,
            request,
            cancellationToken);
    }

    public async Task<byte[]> DownloadLatestSnapshotPayloadAsync(DownloadLatestSnapshotPayloadRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route,
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public Task<PullPendingEventsResponse> PullPendingEventsAsync(PullPendingEventsRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<PullPendingEventsRequest, PullPendingEventsResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PullPendingEvents).Route,
            request,
            cancellationToken);
    }

    public Task<WaitForEventsResponse> WaitForEventsAsync(WaitForEventsRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<WaitForEventsRequest, WaitForEventsResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.WaitForEvents).Route,
            request,
            cancellationToken);
    }

    public Task<PullEventDetailsResponse> PullEventDetailsAsync(PullEventDetailsRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<PullEventDetailsRequest, PullEventDetailsResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PullEventDetails).Route,
            request,
            cancellationToken);
    }

    public Task<ConfirmEventApplicationResponse> ConfirmEventApplicationAsync(
        ConfirmEventApplicationMetadataRequest request,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        return PostSnapshotMultipartAsync<ConfirmEventApplicationMetadataRequest, ConfirmEventApplicationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).Route,
            request,
            payload,
            cancellationToken);
    }

    public Task<ConfirmEventApplicationsResponse> ConfirmEventApplicationsAsync(
        ConfirmEventApplicationsMetadataRequest request,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        return PostSnapshotMultipartAsync<ConfirmEventApplicationsMetadataRequest, ConfirmEventApplicationsResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).Route,
            request,
            payload,
            cancellationToken);
    }

    public Task<ReportEventApplicationFailureResponse> ReportEventApplicationFailureAsync(
        ReportEventApplicationFailureRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<ReportEventApplicationFailureRequest, ReportEventApplicationFailureResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ReportEventApplicationFailure).Route,
            request,
            cancellationToken);
    }

    public Task<EventCreationResponse> CreateGiftAsync(CreateGiftRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateGiftRequest, EventCreationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateGift).Route,
            request,
            cancellationToken);
    }

    public Task<RejectGiftResponse> RejectGiftAsync(RejectGiftRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<RejectGiftRequest, RejectGiftResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RejectGift).Route,
            request,
            cancellationToken);
    }

    public Task<DiplomacyEventResponse> CreateDiplomacyEventAsync(
        CreateDiplomacyEventRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateDiplomacyEventRequest, DiplomacyEventResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateDiplomacyEvent).Route,
            request,
            cancellationToken);
    }

    public Task<DiplomacyEventResponse> RespondDiplomacyEventAsync(
        RespondDiplomacyEventRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<RespondDiplomacyEventRequest, DiplomacyEventResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RespondDiplomacyEvent).Route,
            request,
            cancellationToken);
    }

    public Task<EventCreationResponse> CreateSupportPawnAsync(CreateSupportPawnRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateSupportPawnRequest, EventCreationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawn).Route,
            request,
            cancellationToken);
    }

    public Task<RejectSupportPawnResponse> RejectSupportPawnAsync(RejectSupportPawnRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<RejectSupportPawnRequest, RejectSupportPawnResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RejectSupportPawn).Route,
            request,
            cancellationToken);
    }

    public Task<FinishSupportPawnResponse> FinishSupportPawnAsync(FinishSupportPawnRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<FinishSupportPawnRequest, FinishSupportPawnResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.FinishSupportPawn).Route,
            request,
            cancellationToken);
    }

    public Task<EventCreationResponse> CreateTradeOrderAsync(CreateTradeOrderRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateTradeOrderRequest, EventCreationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateTradeOrder).Route,
            request,
            cancellationToken);
    }

    public Task<ListTradeOrdersResponse> ListTradeOrdersAsync(ListTradeOrdersRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<ListTradeOrdersRequest, ListTradeOrdersResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListTradeOrders).Route,
            request,
            cancellationToken);
    }

    public Task<AcceptTradeOrderResponse> AcceptTradeOrderAsync(AcceptTradeOrderRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<AcceptTradeOrderRequest, AcceptTradeOrderResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.AcceptTradeOrder).Route,
            request,
            cancellationToken);
    }

    public Task<FulfillTradeOrderResponse> FulfillTradeOrderAsync(FulfillTradeOrderRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<FulfillTradeOrderRequest, FulfillTradeOrderResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrder).Route,
            request,
            cancellationToken);
    }

    public Task<CloseTradeOrderResponse> CancelTradeOrderAsync(CloseTradeOrderRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CloseTradeOrderRequest, CloseTradeOrderResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CancelTradeOrder).Route,
            request,
            cancellationToken);
    }

    public Task<CloseTradeOrderResponse> CompleteTradeOrderAsync(CloseTradeOrderRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CloseTradeOrderRequest, CloseTradeOrderResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CompleteTradeOrder).Route,
            request,
            cancellationToken);
    }

    public Task<EventCreationResponse> CreateRaidAsync(CreateRaidRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateRaidRequest, EventCreationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaid).Route,
            request,
            cancellationToken);
    }

    public Task<PrepareRaidResponse> PrepareRaidAsync(PrepareRaidRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<PrepareRaidRequest, PrepareRaidResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PrepareRaid).Route,
            request,
            cancellationToken);
    }

    public Task<EventCreationResponse> CreateRaidWithSnapshotAsync(
        CreateRaidRequest raid,
        SnapshotPackageMetadataDto confirmedSnapshot,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        return PostSnapshotMultipartAsync<CreateRaidWithSnapshotRequest, EventCreationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaidWithSnapshot).Route,
            new CreateRaidWithSnapshotRequest(
                raid,
                confirmedSnapshot),
            payload,
            cancellationToken);
    }

    public Task<WorldMapMarkerDeliveryDto> SyncWorldMapMarkersAsync(SyncWorldMapMarkersRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<SyncWorldMapMarkersRequest, WorldMapMarkerDeliveryDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.SyncWorldMapMarkers).Route,
            request,
            cancellationToken);
    }

    public Task<SyncRuntimeWorldObjectsResponse> SyncRuntimeWorldObjectsAsync(
        SyncRuntimeWorldObjectsRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<SyncRuntimeWorldObjectsRequest, SyncRuntimeWorldObjectsResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.SyncRuntimeWorldObjects).Route,
            request,
            cancellationToken);
    }

    public Task<PrepareWorldSessionResponse> PrepareWorldSessionAsync(PrepareWorldSessionRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<PrepareWorldSessionRequest, PrepareWorldSessionResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PrepareWorldSession).Route,
            request,
            cancellationToken);
    }

    public Task<SubmitWorldConfigurationResponse> SubmitWorldConfigurationAsync(SubmitWorldConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<SubmitWorldConfigurationRequest, SubmitWorldConfigurationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldConfiguration).Route,
            request,
            cancellationToken);
    }

    public Task<GetWorldConfigurationResponse> GetWorldConfigurationAsync(GetWorldConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<GetWorldConfigurationRequest, GetWorldConfigurationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.GetWorldConfiguration).Route,
            request,
            cancellationToken);
    }

    public Task<RegisterPlayerColonySitesResponse> RegisterPlayerColonySitesAsync(
        RegisterPlayerColonySitesRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<RegisterPlayerColonySitesRequest, RegisterPlayerColonySitesResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).Route,
            request,
            cancellationToken);
    }

    public Task<ColonyRelocationResponse> PreflightColonyRelocationAsync(
        PreflightColonyRelocationRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<PreflightColonyRelocationRequest, ColonyRelocationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.PreflightColonyRelocation).Route,
            request,
            cancellationToken);
    }

    public Task<ColonyRelocationResponse> ConfirmColonyRelocationAsync(
        ConfirmColonyRelocationRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<ConfirmColonyRelocationRequest, ColonyRelocationResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmColonyRelocation).Route,
            request,
            cancellationToken);
    }

    public Task<AbandonPlayerColonyResponse> AbandonPlayerColonyAsync(
        AbandonPlayerColonyRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<AbandonPlayerColonyRequest, AbandonPlayerColonyResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.AbandonPlayerColony).Route,
            request,
            cancellationToken);
    }

    public Task<SubmitAdminBaselineResponse> SubmitAdminBaselineAsync(SubmitAdminBaselineRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<SubmitAdminBaselineRequest, SubmitAdminBaselineResponse>(
            ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route,
            request,
            cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string route,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(route, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        TResponse? body = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException($"Empty response body for {route}.");
    }

    private async Task<TResponse> PostSnapshotMultipartAsync<TRequest, TResponse>(
        string route,
        TRequest request,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(
            new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json"),
            "request");
        content.Add(new ByteArrayContent(payload), "payload", "snapshot.payload");
        using HttpResponseMessage response = await httpClient.PostAsync(route, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        TResponse? body = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException($"Empty response body for {route}.");
    }

}
