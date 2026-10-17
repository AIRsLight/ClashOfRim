using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Sessions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Protocol;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

public sealed class ClashOfRimModNetworkClient : IClientPackageReader, IClientPackageWriter
{
    private static readonly string MaintainPresenceRoute = ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).Route;
    private static readonly string ChangeOfflinePasswordRoute = ProtocolContractManifest.Find(ProtocolMessageKind.ChangeOfflinePassword).Route;
    private static readonly string ListPlayersRoute = ProtocolContractManifest.Find(ProtocolMessageKind.ListPlayers).Route;
    private static readonly string ListAchievementsRoute = ProtocolContractManifest.Find(ProtocolMessageKind.ListAchievements).Route;
    private static readonly string GetPawnPackagePreviewRoute = ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackagePreview).Route;
    private static readonly string PrepareRaidRoute = ProtocolContractManifest.Find(ProtocolMessageKind.PrepareRaid).Route;
    private static readonly string CreateRaidWithSnapshotRoute = ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaidWithSnapshot).Route;
    private static readonly string CreateDiplomacyEventRoute = ProtocolContractManifest.Find(ProtocolMessageKind.CreateDiplomacyEvent).Route;
    private static readonly string CreateSupportPawnWithSnapshotRoute = ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawnWithSnapshot).Route;
    private static readonly string FinishSupportPawnRoute = ProtocolContractManifest.Find(ProtocolMessageKind.FinishSupportPawn).Route;
    private static readonly string UploadSnapshotRoute = ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route;
    private static readonly string DownloadLatestSnapshotRoute = ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshot).Route;
    private static readonly string DownloadLatestSnapshotPayloadRoute = ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route;
    private static readonly string SyncWorldMapMarkersRoute = ProtocolContractManifest.Find(ProtocolMessageKind.SyncWorldMapMarkers).Route;
    private static readonly string SyncRuntimeWorldObjectsRoute = ProtocolContractManifest.Find(ProtocolMessageKind.SyncRuntimeWorldObjects).Route;
    private static readonly string PrepareWorldSessionRoute = ProtocolContractManifest.Find(ProtocolMessageKind.PrepareWorldSession).Route;
    private static readonly string GetWorldConfigurationRoute = ProtocolContractManifest.Find(ProtocolMessageKind.GetWorldConfiguration).Route;
    private static readonly string SubmitWorldConfigurationRoute = ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldConfiguration).Route;
    private static readonly string UploadWorldSubstrateRoute = ProtocolContractManifest.Find(ProtocolMessageKind.UploadWorldSubstrate).Route;
    private static readonly string DownloadWorldSubstrateRoute = ProtocolContractManifest.Find(ProtocolMessageKind.DownloadWorldSubstrate).Route;
    private static readonly string SubmitWorldFeatureNamesRoute = ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldFeatureNames).Route;
    private static readonly string RegisterPlayerColonySitesRoute = ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).Route;
    private static readonly string PreflightColonyRelocationRoute = ProtocolContractManifest.Find(ProtocolMessageKind.PreflightColonyRelocation).Route;
    private static readonly string ConfirmColonyRelocationRoute = ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmColonyRelocation).Route;
    private static readonly string AbandonPlayerColonyRoute = ProtocolContractManifest.Find(ProtocolMessageKind.AbandonPlayerColony).Route;
    private static readonly string GetAdminBaselineRequirementsRoute = ProtocolContractManifest.Find(ProtocolMessageKind.GetAdminBaselineRequirements).Route;
    private static readonly string SubmitAdminBaselineRoute = ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route;
    private static readonly string SendChatMessageRoute = ProtocolContractManifest.Find(ProtocolMessageKind.SendChatMessage).Route;
    private static readonly string ListChatMessagesRoute = ProtocolContractManifest.Find(ProtocolMessageKind.ListChatMessages).Route;
    private static readonly string OverrideCompatibilityBaselineRoute = ProtocolContractManifest.Find(ProtocolMessageKind.OverrideCompatibilityBaseline).Route;
    private static readonly string AdminStatusRoute = ProtocolContractManifest.Find(ProtocolMessageKind.AdminStatus).Route;
    private static readonly string AdminUpdateConfigurationRoute = ProtocolContractManifest.Find(ProtocolMessageKind.AdminUpdateConfiguration).Route;
    private static readonly string AdminActionRoute = ProtocolContractManifest.Find(ProtocolMessageKind.AdminAction).Route;
    private static readonly string ServerHelloRoute = ProtocolContractManifest.Find(ProtocolMessageKind.ServerHello).Route;

    private readonly ClientProtocolTransport transport;
    private readonly ClientEventProtocol events;
    private readonly ClientEventConfirmationProtocol eventConfirmations;
    private readonly ClientEventDecisionProtocol eventDecisions;
    private readonly ClientAuthenticationEnvironment authenticationEnvironment = new();
    private readonly ClientSessionProtocol authentication;
    private readonly ClientSetupAuthenticationLease setupAuthentication;
    private readonly ClientSessionWebSocketTransport sessionStream;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly ClientPackageReader packages;
    private readonly ClientPackageWriter packageWriter;
    private readonly ClientShopProtocol shopProtocol;

    public static Action<string, string?>? SessionExpired;
    public static Func<string?>? CompatibilityManifestJsonProvider;
    public static Func<string?>? CompatibilityManifestIdProvider;
    public static Func<string?>? CompatibilityManifestSummaryJsonProvider;
    public static Func<IReadOnlyCollection<string>?, string?>? CompatibilityManifestJsonForPackagesProvider;
    public static Action? ServerEntryDiagnosticsLogger;

    private readonly ClientBankProtocol bankProtocol;
    private readonly ClientMercenaryProtocol mercenaries;
    private readonly ClientGiftProtocol gifts;
    private readonly ClientTradeProtocol trades;

    public ClashOfRimModNetworkClient(
        HttpClient httpClient,
        ClashOfRimClientNetworkContext context)
    {
        _ = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        var protocolEnvironment = new ClientProtocolEnvironment((message, token) => SessionExpired?.Invoke(message, token));
        transport = new ClientProtocolTransport(httpClient, context, protocolEnvironment);
        packageWriter = new ClientPackageWriter(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        shopProtocol = new ClientShopProtocol(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        mercenaries = new ClientMercenaryProtocol(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        bankProtocol = new ClientBankProtocol(context, transport, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        trades = new ClientTradeProtocol(transport, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        gifts = new ClientGiftProtocol(transport, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        events = new ClientEventProtocol(transport, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        eventConfirmations = new ClientEventConfirmationProtocol(transport, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        eventDecisions = new ClientEventDecisionProtocol(transport, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        packages = new ClientPackageReader(transport, context, () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        authentication = new ClientSessionProtocol(transport, authenticationEnvironment, authenticationEnvironment);
        setupAuthentication = new ClientSetupAuthenticationLease(authentication, transport, authenticationEnvironment);
        sessionStream = new ClientSessionWebSocketTransport(new ClientWebSocketFactory(), protocolEnvironment);
    }

    public Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> ServerHelloAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ServerBaseUrl))
        {
            return NotConfigured<ModServerHelloResponseDto>();
        }

        return transport.GetAsync<ModServerHelloResponseDto>(ServerHelloRoute, cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(
        string compatibilityDigest,
        string? compatibilityManifestJson = null,
        CancellationToken cancellationToken = default)
        => authentication.LoginAsync(context, compatibilityDigest, compatibilityManifestJson, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto>> OverrideCompatibilityBaselineAsync(
        string compatibilityManifestJson,
        CancellationToken cancellationToken = default)
    {
        if (!context.HasServerIdentity)
        {
            return NotConfigured<ModOverrideCompatibilityBaselineResponseDto>();
        }

        var request = new ModOverrideCompatibilityBaselineRequestDto
        {
            UserId = context.UserId,
            ColonyId = null,
            CompatibilityManifestJson = compatibilityManifestJson ?? string.Empty,
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword
        };

        return transport.PostAsync<ModOverrideCompatibilityBaselineRequestDto, ModOverrideCompatibilityBaselineResponseDto>(
            OverrideCompatibilityBaselineRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>> AdminStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModAdminStatusResponseDto>();
        }

        var request = new ModAdminStatusRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            AuthToken = context.AuthToken
        };

        return transport.PostAsync<ModAdminStatusRequestDto, ModAdminStatusResponseDto>(
            AdminStatusRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>> ChangeOfflinePasswordAsync(
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModChangeOfflinePasswordResponseDto>();
        }

        var request = new ModChangeOfflinePasswordRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            AuthToken = context.AuthToken,
            CurrentPassword = currentPassword,
            NewPassword = newPassword
        };

        return transport.PostAsync<ModChangeOfflinePasswordRequestDto, ModChangeOfflinePasswordResponseDto>(
            ChangeOfflinePasswordRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto>> AdminUpdateConfigurationAsync(
        ModAdminConfigurationDto configuration,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModAdminUpdateConfigurationResponseDto>();
        }

        var request = new ModAdminUpdateConfigurationRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            AuthToken = context.AuthToken,
            Configuration = configuration
        };

        return transport.PostAsync<ModAdminUpdateConfigurationRequestDto, ModAdminUpdateConfigurationResponseDto>(
            AdminUpdateConfigurationRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModAdminActionResponseDto>> AdminActionAsync(
        string actionKind,
        string? targetUserId,
        string? targetColonyId,
        string? message,
        string? notificationSeverity = null,
        bool persistentNotification = true,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModAdminActionResponseDto>();
        }

        var request = new ModAdminActionRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            AuthToken = context.AuthToken,
            ActionKind = actionKind,
            TargetUserId = targetUserId,
            TargetColonyId = targetColonyId,
            Message = message,
            NotificationSeverity = notificationSeverity,
            PersistentNotification = persistentNotification
        };

        return transport.PostAsync<ModAdminActionRequestDto, ModAdminActionResponseDto>(
            AdminActionRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>> SyncWorldMapMarkersAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModWorldMapMarkerDeliveryDto>();
        }

        var request = new ModSyncWorldMapMarkersRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            KnownAtUtc = DateTimeOffset.UtcNow.ToString("O")
        };

        return transport.PostAsync<ModSyncWorldMapMarkersRequestDto, ModWorldMapMarkerDeliveryDto>(
            SyncWorldMapMarkersRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>> SyncRuntimeWorldObjectsAsync(
        IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> objects,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModSyncRuntimeWorldObjectsResponseDto>();
        }

        var request = new ModSyncRuntimeWorldObjectsRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            SnapshotId = context.CurrentSnapshotId,
            SentAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Objects = objects?.ToList() ?? new List<ModRuntimeWorldObjectMarkerDto>(),
            AuthToken = context.AuthToken
        };

        return transport.PostAsync<ModSyncRuntimeWorldObjectsRequestDto, ModSyncRuntimeWorldObjectsResponseDto>(
            SyncRuntimeWorldObjectsRoute,
            request,
            cancellationToken);
    }

    public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareWorldSessionAsync(
        bool createAccountIfMissing = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ServerBaseUrl)
            || string.IsNullOrWhiteSpace(context.UserId))
        {
            return await NotConfigured<ModPrepareWorldSessionResponseDto>();
        }

        try
        {
            ServerEntryDiagnosticsLogger?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to log the server entry mod list: " + ex);
        }

        var request = new ModPrepareWorldSessionRequestDto
        {
            UserId = context.UserId,
            CompatibilityManifestId = CompatibilityManifestIdProvider?.Invoke(),
            CompatibilityManifestSummaryJson = CompatibilityManifestSummaryJsonProvider?.Invoke(),
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword,
            CreateAccountIfMissing = createAccountIfMissing
        };

        ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result =
            await transport.PostAsync<ModPrepareWorldSessionRequestDto, ModPrepareWorldSessionResponseDto>(
            PrepareWorldSessionRoute,
            request,
            cancellationToken);
        if (!ShouldRetryPrepareWorldSessionWithFullCompatibilityManifest(result, request.CompatibilityManifestJson))
        {
            return result;
        }

        request.CompatibilityManifestJson = authenticationEnvironment.BuildJson(
            result.Response?.RequestedCompatibilityPackageIds);
        if (string.IsNullOrWhiteSpace(request.CompatibilityManifestJson))
        {
            return CompatibilityManifestBuildFailed<ModPrepareWorldSessionResponseDto>();
        }

        request.CompatibilityManifestSummaryJson = null;
        return await transport.PostAsync<ModPrepareWorldSessionRequestDto, ModPrepareWorldSessionResponseDto>(
            PrepareWorldSessionRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitWorldConfigurationAsync(
        ModWorldConfigurationDto configuration,
        CancellationToken cancellationToken = default)
        => setupAuthentication.RunAsync<ModSubmitWorldConfigurationResponseDto>(context, "world-configuration-setup", (authenticated, requestTransport, token) =>
            requestTransport.PostAsync<ModSubmitWorldConfigurationRequestDto, ModSubmitWorldConfigurationResponseDto>(
                SubmitWorldConfigurationRoute,
                new ModSubmitWorldConfigurationRequestDto
                {
                    UserId = authenticated.UserId,
                    ColonyId = authenticated.ColonyId,
                    AuthToken = authenticated.AuthToken,
                    Configuration = configuration
                }, token), cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>> SubmitWorldFeatureNamesAsync(
        string language,
        string worldConfigurationId,
        IReadOnlyList<ModWorldFeatureDto> features,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModSubmitWorldFeatureNamesResponseDto>();
        }

        var request = new ModSubmitWorldFeatureNamesRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            Language = language,
            WorldConfigurationId = worldConfigurationId,
            Features = features?.ToList() ?? new List<ModWorldFeatureDto>(),
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword
        };

        return transport.PostAsync<ModSubmitWorldFeatureNamesRequestDto, ModSubmitWorldFeatureNamesResponseDto>(
            SubmitWorldFeatureNamesRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadWorldSubstrateAsync(
        string worldConfigurationId,
        byte[] payload,
        CancellationToken cancellationToken = default)
        => setupAuthentication.RunAsync<ModUploadWorldSubstrateResponseDto>(context, "world-substrate-setup", (authenticated, requestTransport, token) =>
            requestTransport.PostSnapshotMultipartAsync<ModUploadWorldSubstrateRequestDto, ModUploadWorldSubstrateResponseDto>(
                UploadWorldSubstrateRoute,
                new ModUploadWorldSubstrateRequestDto
                {
                    UserId = authenticated.UserId,
                    ColonyId = authenticated.ColonyId,
                    WorldConfigurationId = worldConfigurationId ?? string.Empty,
                    SteamAuthTicket = string.IsNullOrWhiteSpace(authenticated.SteamAuthTicket) ? authenticated.UserId : authenticated.SteamAuthTicket,
                    Password = authenticated.OfflinePassword
                }, payload, token), cancellationToken);

    public Task<ClashOfRimClientNetworkResult<byte[]>> DownloadWorldSubstrateAsync(
        string worldConfigurationId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<byte[]>();
        }

        var request = new ModDownloadWorldSubstrateRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            WorldConfigurationId = worldConfigurationId ?? string.Empty,
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword
        };
        return transport.PostForBytesAsync(DownloadWorldSubstrateRoute, request, cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>> GetWorldConfigurationAsync(
        bool includeGenerationBaseline = true,
        bool includePlayerColonySites = true,
        bool includeWorldExtensions = true,
        CancellationToken cancellationToken = default)
    {
        if (!context.HasServerIdentity)
        {
            return NotConfigured<ModGetWorldConfigurationResponseDto>();
        }

        var request = new ModGetWorldConfigurationRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword,
            IncludeGenerationBaseline = includeGenerationBaseline,
            IncludePlayerColonySites = includePlayerColonySites,
            IncludeWorldExtensions = includeWorldExtensions
        };

        return transport.PostAsync<ModGetWorldConfigurationRequestDto, ModGetWorldConfigurationResponseDto>(
            GetWorldConfigurationRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterPlayerColonySitesAsync(
        IReadOnlyCollection<ModPlayerColonySiteDto> sites,
        IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions = null,
        bool suppressWorldConfigurationNotification = false,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModRegisterPlayerColonySitesResponseDto>();
        }

        var request = new ModRegisterPlayerColonySitesRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            Sites = sites?.ToList() ?? new List<ModPlayerColonySiteDto>(),
            Extensions = extensions?.ToList() ?? new List<ModWorldConfigurationExtensionDto>(),
            SuppressWorldConfigurationNotification = suppressWorldConfigurationNotification
        };

        return transport.PostAsync<ModRegisterPlayerColonySitesRequestDto, ModRegisterPlayerColonySitesResponseDto>(
            RegisterPlayerColonySitesRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> PreflightColonyRelocationAsync(
        int targetTile,
        string idempotencyKey,
        int targetTileLayerId = 0,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModColonyRelocationResponseDto>();
        }

        var request = new ModPreflightColonyRelocationRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            TargetTile = targetTile,
            TargetTileLayerId = Math.Max(0, targetTileLayerId),
            IdempotencyKey = idempotencyKey,
            AuthToken = context.AuthToken
        };

        return transport.PostAsync<ModPreflightColonyRelocationRequestDto, ModColonyRelocationResponseDto>(
            PreflightColonyRelocationRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> ConfirmColonyRelocationAsync(
        string previousSnapshotId,
        string relocatedSnapshotId,
        int targetTile,
        string idempotencyKey,
        int targetTileLayerId = 0,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModColonyRelocationResponseDto>();
        }

        var request = new ModConfirmColonyRelocationRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            PreviousSnapshotId = previousSnapshotId ?? string.Empty,
            RelocatedSnapshotId = relocatedSnapshotId ?? string.Empty,
            TargetTile = targetTile,
            TargetTileLayerId = Math.Max(0, targetTileLayerId),
            IdempotencyKey = idempotencyKey,
            AuthToken = context.AuthToken
        };

        return transport.PostAsync<ModConfirmColonyRelocationRequestDto, ModColonyRelocationResponseDto>(
            ConfirmColonyRelocationRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>> AbandonPlayerColonyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModAbandonPlayerColonyResponseDto>();
        }

        var request = new ModAbandonPlayerColonyRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId,
            IdempotencyKey = idempotencyKey,
            AuthToken = context.AuthToken
        };

        return transport.PostAsync<ModAbandonPlayerColonyRequestDto, ModAbandonPlayerColonyResponseDto>(
            AbandonPlayerColonyRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankStatusResponseDto>> GetBankStatusAsync(
        long currentGameTicks,
        int colonyWealth,
        CancellationToken cancellationToken = default)
        => bankProtocol.GetBankStatusAsync(currentGameTicks, colonyWealth, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> CreateBankLoanAsync(
        string idempotencyKey,
        long currentGameTicks,
        int colonyWealth,
        int principalSilver,
        int durationDays,
        CancellationToken cancellationToken = default)
        => bankProtocol.CreateBankLoanAsync(idempotencyKey, currentGameTicks, colonyWealth, principalSilver, durationDays, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> CreateBankLoanWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        int colonyWealth,
        int principalSilver,
        int durationDays,
        string requestedLoanId,
        int expectedInterestSilver,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => bankProtocol.CreateBankLoanWithSnapshotAsync(idempotencyKey, currentGameTicks, colonyWealth, principalSilver, durationDays, requestedLoanId, expectedInterestSilver, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> RepayBankLoanAsync(
        string idempotencyKey,
        long currentGameTicks,
        int silverPaid,
        CancellationToken cancellationToken = default)
        => bankProtocol.RepayBankLoanAsync(idempotencyKey, currentGameTicks, silverPaid, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> RepayBankLoanWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        string loanId,
        int silverPaid,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => bankProtocol.RepayBankLoanWithSnapshotAsync(idempotencyKey, currentGameTicks, loanId, silverPaid, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModBankDebtResponseDto>> RepayBankDebtAsync(
        string idempotencyKey,
        long currentGameTicks,
        string debtId,
        int silverPaid,
        CancellationToken cancellationToken = default)
        => bankProtocol.RepayBankDebtAsync(idempotencyKey, currentGameTicks, debtId, silverPaid, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModBankDebtResponseDto>> RepayBankDebtWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        string debtId,
        int silverPaid,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => bankProtocol.RepayBankDebtWithSnapshotAsync(idempotencyKey, currentGameTicks, debtId, silverPaid, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto>> HireMercenaryAsync(
        string idempotencyKey,
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays,
        CancellationToken cancellationToken = default)
        => mercenaries.HireMercenaryAsync(idempotencyKey, currentGameTicks, skillDefName, skillLevel, durationDays, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto>> HireMercenaryWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => mercenaries.HireMercenaryWithSnapshotAsync(idempotencyKey, currentGameTicks, contract, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto>> QuoteMercenaryAsync(
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays,
        CancellationToken cancellationToken = default)
        => mercenaries.QuoteMercenaryAsync(currentGameTicks, skillDefName, skillLevel, durationDays, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto>> QuoteMercenaryGuardAsync(
        long currentGameTicks,
        string tier,
        CancellationToken cancellationToken = default)
        => mercenaries.QuoteMercenaryGuardAsync(currentGameTicks, tier, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModMercenaryGuardHireResponseDto>> HireMercenaryGuardWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => mercenaries.HireMercenaryGuardWithSnapshotAsync(idempotencyKey, currentGameTicks, contract, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>> ReportMercenaryIncidentAsync(
        string idempotencyKey,
        long currentGameTicks,
        string contractId,
        string incidentKind,
        CancellationToken cancellationToken = default)
        => mercenaries.ReportMercenaryIncidentAsync(idempotencyKey, currentGameTicks, contractId, incidentKind, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModSendChatMessageResponseDto>> SendChatMessageAsync(
        string? targetUserId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModSendChatMessageResponseDto>();
        }

        var request = new ModSendChatMessageRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            AuthToken = context.AuthToken,
            TargetUserId = targetUserId,
            Text = text
        };

        return transport.PostAsync<ModSendChatMessageRequestDto, ModSendChatMessageResponseDto>(
            SendChatMessageRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModListChatMessagesResponseDto>> ListChatMessagesAsync(
        long afterSequence,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModListChatMessagesResponseDto>();
        }

        var request = new ModListChatMessagesRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            AuthToken = context.AuthToken,
            AfterSequence = Math.Max(0, afterSequence),
            Limit = limit
        };

        return transport.PostAsync<ModListChatMessagesRequestDto, ModListChatMessagesResponseDto>(
            ListChatMessagesRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>> SubmitAdminBaselineAsync(
        ModSubmitAdminBaselineRequestDto request,
        CancellationToken cancellationToken = default)
    {
        return setupAuthentication.RunAsync<ModSubmitAdminBaselineResponseDto>(context, "admin-baseline-setup", (authenticated, requestTransport, token) =>
        {
            return requestTransport.PostAsync<ModSubmitAdminBaselineRequestDto, ModSubmitAdminBaselineResponseDto>(
                SubmitAdminBaselineRoute, request.WithAuthentication(authenticated.UserId, authenticated.ColonyId, authenticated.AuthToken), token);
        }, cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto>> GetAdminBaselineRequirementsAsync(
        CancellationToken cancellationToken = default)
    {
        return setupAuthentication.RunAsync<ModGetAdminBaselineRequirementsResponseDto>(context, "admin-baseline-requirements", (authenticated, requestTransport, token) =>
            requestTransport.PostAsync<ModGetAdminBaselineRequirementsRequestDto, ModGetAdminBaselineRequirementsResponseDto>(
                GetAdminBaselineRequirementsRoute,
                new ModGetAdminBaselineRequirementsRequestDto
                {
                    UserId = authenticated.UserId,
                    ColonyId = authenticated.ColonyId,
                    AuthToken = authenticated.AuthToken
                }, token), cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamSessionAsync(
        string? sessionId,
        long knownNotificationVersion,
        long knownWorldConfigurationVersion,
        Action<ModSessionStreamEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured) return NotConfigured<ModSessionStreamClosedDto>();
        return sessionStream.StreamAsync(context, sessionId, knownNotificationVersion, knownWorldConfigurationVersion, onEvent, cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModMaintainPresenceResponseDto>> MaintainPresenceAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMaintainPresenceResponseDto>();
        }

        var request = new ModMaintainPresenceRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId,
            SessionId = sessionId
        };

        return transport.PostAsync<ModMaintainPresenceRequestDto, ModMaintainPresenceResponseDto>(
            MaintainPresenceRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
        => authentication.LogoutAsync(context, sessionId, cancellationToken);

    internal Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutDetachedAsync(
        string? sessionId, CancellationToken cancellationToken)
        => authentication.LogoutAsync(context, sessionId, cancellationToken, bindToSession: false);

    public Task<ClashOfRimClientNetworkResult<ModListPlayersResponseDto>> ListPlayersAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModListPlayersResponseDto>();
        }

        var request = new ModListPlayersRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId
        };

        return transport.PostAsync<ModListPlayersRequestDto, ModListPlayersResponseDto>(
            ListPlayersRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>> ListAchievementsAsync(
        string? targetUserId = null,
        string? targetColonyId = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModListAchievementsResponseDto>();
        }

        var request = new ModListAchievementsRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId,
            TargetUserId = string.IsNullOrWhiteSpace(targetUserId) ? context.UserId : targetUserId,
            TargetColonyId = string.IsNullOrWhiteSpace(targetColonyId) ? context.ColonyId : targetColonyId
        };

        return transport.PostAsync<ModListAchievementsRequestDto, ModListAchievementsResponseDto>(
            ListAchievementsRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>> PullPendingEventsAsync(
        CancellationToken cancellationToken = default) => events.PullAsync(context, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>> WaitForEventsAsync(
        long knownNotificationVersion, int timeoutSeconds, CancellationToken cancellationToken = default) =>
        events.WaitAsync(context, knownNotificationVersion, timeoutSeconds, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>> PullEventDetailsAsync(
        IReadOnlyList<string> eventIds, CancellationToken cancellationToken = default) => events.DetailsAsync(context, eventIds, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModRejectGiftResponseDto>> RejectGiftAsync(
        string eventId,
        string? reason,
        CancellationToken cancellationToken = default)
        => eventDecisions.RejectGiftAsync(context, eventId, reason, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModRejectSupportPawnResponseDto>> RejectSupportPawnAsync(
        string eventId,
        string? reason,
        CancellationToken cancellationToken = default)
        => eventDecisions.RejectSupportAsync(context, eventId, reason, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>> FinishSupportPawnAsync(
        string idempotencyKey,
        string eventId,
        string finishReason,
        string pawnGlobalKey,
        string? pawnName,
        bool pawnDead,
        ModPawnExchangePackageDto? pawnPackage,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModFinishSupportPawnResponseDto>();
        }

        var request = new ModFinishSupportPawnRequestDto
        {
            IdempotencyKey = idempotencyKey,
            EventId = eventId,
            AuthToken = context.AuthToken,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            FinishReason = finishReason,
            PawnGlobalKey = pawnGlobalKey,
            PawnName = pawnName,
            PawnDead = pawnDead,
            PawnPackage = pawnPackage
        };

        return transport.PostAsync<ModFinishSupportPawnRequestDto, ModFinishSupportPawnResponseDto>(
            FinishSupportPawnRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> CreateDiplomacyEventAsync(
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        string kind,
        string? message,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModDiplomacyEventResponseDto>();
        }

        var request = new ModCreateDiplomacyEventRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Actor = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Target = new ModProtocolIdentityDto
            {
                UserId = targetUserId,
                ColonyId = targetColonyId,
                SnapshotId = null
            },
            Kind = kind,
            Message = message,
            ExpiresAtUtc = expiresAtUtc?.ToString("O"),
            AuthToken = context.AuthToken
        };

        return transport.PostAsync<ModCreateDiplomacyEventRequestDto, ModDiplomacyEventResponseDto>(
            CreateDiplomacyEventRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> RespondDiplomacyEventAsync(
        string eventId,
        bool accepted,
        string? reason,
        CancellationToken cancellationToken = default)
        => eventDecisions.RespondDiplomacyAsync(context, eventId, accepted, reason, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateGiftWithSnapshotAsync(
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        IReadOnlyList<ModThingReferenceDto> things,
        string? message,
        ModEventTargetContextDto? targetContext,
        string? deliveryKind,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => gifts.CreateGiftWithSnapshotAsync(context, idempotencyKey, targetUserId, targetColonyId,
            targetSnapshotId, things, message, targetContext, deliveryKind, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteTradeOrderFeeAsync(
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        CancellationToken cancellationToken = default)
        => trades.QuoteTradeOrderFeeAsync(context, offeredThings, requestedThings, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateTradeOrderWithSnapshotAsync(
        string idempotencyKey,
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        int feeSilver,
        bool allowSelfPickup,
        bool allowServerDropPod,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => trades.CreateTradeOrderWithSnapshotAsync(context, idempotencyKey, offeredThings, requestedThings, feeSilver, allowSelfPickup, allowServerDropPod, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto>> StorePawnPackageAsync(
        string idempotencyKey,
        ModPawnExchangePackageDto pawnPackage,
        CancellationToken cancellationToken = default)
        => packageWriter.StorePawnPackageAsync(idempotencyKey, pawnPackage, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>> GetPawnPackageAsync(
        string pawnPackageId,
        CancellationToken cancellationToken = default)
        => packages.GetPawnPackageAsync(pawnPackageId, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModStorePawnPackagePreviewResponseDto>> StorePawnPackagePreviewAsync(
        string pawnPackageId,
        byte[] previewPng,
        CancellationToken cancellationToken = default)
        => packageWriter.StorePawnPackagePreviewAsync(pawnPackageId, previewPng, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<byte[]>> GetPawnPackagePreviewAsync(
        string pawnPackageId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<byte[]>();
        }

        var request = new ModGetPawnPackagePreviewRequestDto
        {
            Requester = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            PawnPackageId = pawnPackageId
        };

        return transport.PostForBytesAsync(
            GetPawnPackagePreviewRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto>> StoreThingPackageAsync(
        string idempotencyKey,
        ModThingStatePackageDto thingPackage,
        CancellationToken cancellationToken = default)
        => packageWriter.StoreThingPackageAsync(idempotencyKey, thingPackage, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModGetThingPackageResponseDto>> GetThingPackageAsync(
        string thingPackageId,
        CancellationToken cancellationToken = default)
        => packages.GetThingPackageAsync(thingPackageId, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>> ListTradeOrdersAsync(
        string scope = "Open",
        int offset = 0,
        int limit = 10,
        CancellationToken cancellationToken = default)
        => trades.ListTradeOrdersAsync(context, scope, offset, limit, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto>> AcceptTradeOrderAsync(
        string idempotencyKey,
        string tradeEventId,
        bool postagePaidByAcceptor,
        CancellationToken cancellationToken = default)
        => trades.AcceptTradeOrderAsync(context, idempotencyKey, tradeEventId, postagePaidByAcceptor, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModFulfillTradeOrderResponseDto>> FulfillTradeOrderWithSnapshotAsync(
        string idempotencyKey,
        string tradeEventId,
        string acceptedMemoEventId,
        IReadOnlyList<ModThingReferenceDto> deliveredThings,
        string fulfillmentMode,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => trades.FulfillTradeOrderWithSnapshotAsync(context, idempotencyKey, tradeEventId, acceptedMemoEventId, deliveredThings, fulfillmentMode, confirmedSnapshot, confirmedPayload, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> CancelTradeOrderAsync(
        string idempotencyKey,
        string tradeEventId,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => trades.CancelTradeOrderAsync(context, idempotencyKey, tradeEventId, reason, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModListServerShopResponseDto>> ListServerShopAsync(
        CancellationToken cancellationToken = default)
        => shopProtocol.ListServerShopAsync(cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModUpsertServerShopListingResponseDto>> UpsertServerShopListingAsync(
        string? listingId,
        string listingKind,
        ModThingReferenceDto item,
        int priceSilver,
        int stockCount,
        double priceIncreaseRatio,
        string qualityRequirementMode,
        string hitPointsRequirementMode,
        CancellationToken cancellationToken = default)
        => shopProtocol.UpsertServerShopListingAsync(listingId, listingKind, item, priceSilver, stockCount, priceIncreaseRatio, qualityRequirementMode, hitPointsRequirementMode, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto>> RemoveServerShopListingAsync(
        string listingId,
        CancellationToken cancellationToken = default)
        => shopProtocol.RemoveServerShopListingAsync(listingId, cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto>> PurchaseServerShopListingWithSnapshotAsync(
        string idempotencyKey,
        string listingId,
        string listingKind,
        int unitPriceSilver,
        int totalPriceSilver,
        int purchaseCount,
        IReadOnlyList<ModThingReferenceDto>? deliveredThings,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
        => shopProtocol.PurchaseServerShopListingWithSnapshotAsync(idempotencyKey, listingId, listingKind, unitPriceSilver, totalPriceSilver, purchaseCount, deliveredThings, confirmedSnapshot, confirmedPayload, cancellationToken);

    private static ModSnapshotPackageMetadataDto MarkSnapshotUploadKind(
        ModSnapshotPackageMetadataDto package,
        string snapshotUploadKind)
    {
        package.SnapshotUploadKind = snapshotUploadKind;
        return package;
    }

    private static string NormalizeSnapshotUploadKind(string? snapshotUploadKind, string? confirmationOperation)
    {
        if (!string.IsNullOrWhiteSpace(snapshotUploadKind))
        {
            return snapshotUploadKind!;
        }

        if (string.Equals(confirmationOperation, ModSnapshotUploadKinds.ColonyRelocation, StringComparison.Ordinal))
        {
            return ModSnapshotUploadKinds.ColonyRelocation;
        }

        if (string.Equals(confirmationOperation, ModSnapshotUploadKinds.EndgameAchievement, StringComparison.Ordinal))
        {
            return ModSnapshotUploadKinds.EndgameAchievement;
        }

        return ModSnapshotUploadKinds.ManualUpload;
    }

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateRaidWithSnapshotAsync(
        string idempotencyKey,
        string? raidPreparationId,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        string targetWorldObjectId,
        string targetMapId,
        int? targetTile,
        bool isHostile,
        bool defenderOnline,
        int defenderWealth,
        string? defenderRaidCooldownUntilUtc,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        string? guardDeploymentId = null,
        string opponentKind = "Player",
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var raid = new ModCreateRaidRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Attacker = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Defender = new ModProtocolIdentityDto
            {
                UserId = targetUserId,
                ColonyId = targetColonyId,
                SnapshotId = targetSnapshotId
            },
            IsHostile = isHostile,
            DefenderOnline = defenderOnline,
            DefenderWealth = defenderWealth,
            DefenderRaidCooldownUntilUtc = defenderRaidCooldownUntilUtc,
            RaidPreparationId = raidPreparationId,
            TargetWorldObjectId = targetWorldObjectId,
            TargetMapId = targetMapId,
            TargetTile = targetTile,
            DefenderSnapshotId = targetSnapshotId ?? string.Empty,
            OpponentKind = opponentKind,
            PawnGlobalKeys = pawnGlobalKeys.ToList(),
            CarriedThings = carriedThings.ToList(),
            GuardDeploymentId = guardDeploymentId
        };
        var request = new ModCreateRaidWithSnapshotRequestDto
        {
            Raid = raid,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.RaidCreationConfirmation)
        };

        return transport.PostSnapshotMultipartAsync<ModCreateRaidWithSnapshotRequestDto, ModEventCreationResponseDto>(
            CreateRaidWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>> PrepareRaidAsync(
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string targetWorldObjectId,
        string targetMapId,
        int? targetTile,
        bool isHostile,
        string opponentKind = "Player",
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModPrepareRaidResponseDto>();
        }

        var request = new ModPrepareRaidRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Attacker = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Defender = new ModProtocolIdentityDto
            {
                UserId = targetUserId,
                ColonyId = targetColonyId,
                SnapshotId = null
            },
            IsHostile = isHostile,
            TargetWorldObjectId = targetWorldObjectId,
            TargetMapId = targetMapId,
            TargetTile = targetTile,
            OpponentKind = opponentKind
        };

        return transport.PostAsync<ModPrepareRaidRequestDto, ModPrepareRaidResponseDto>(
            PrepareRaidRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateSupportPawnWithSnapshotAsync(
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        string pawnGlobalKey,
        string pawnName,
        ModCrossMapPawnReferenceDto pawnReference,
        ModPawnExchangePackageDto pawnPackage,
        ModEventTargetContextDto targetContext,
        int sourceTile,
        string? sourceCaravanLoadId,
        bool permanentSupport,
        int? supportDurationDays,
        long? expiresAtGameTicks,
        bool autoReturnOnSettlement,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var supportPawn = new ModCreateSupportPawnRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Actor = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Target = new ModProtocolIdentityDto
            {
                UserId = targetUserId,
                ColonyId = targetColonyId,
                SnapshotId = targetSnapshotId
            },
            PawnGlobalKey = pawnGlobalKey,
            SourceSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            PawnName = pawnName,
            TemporaryControl = true,
            ExpectedReturnAtUtc = null,
            PawnReference = pawnReference,
            PawnPackage = pawnPackage,
            TargetContext = targetContext,
            SourceTile = sourceTile,
            SourceCaravanLoadId = sourceCaravanLoadId,
            PermanentSupport = permanentSupport,
            SupportDurationDays = supportDurationDays,
            ExpiresAtGameTicks = expiresAtGameTicks,
            AutoReturnOnSettlement = autoReturnOnSettlement
        };
        var request = new ModCreateSupportPawnWithSnapshotRequestDto
        {
            SupportPawn = supportPawn,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.SupportPawnCreationConfirmation)
        };

        return transport.PostSnapshotMultipartAsync<ModCreateSupportPawnWithSnapshotRequestDto, ModEventCreationResponseDto>(
            CreateSupportPawnWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadSnapshotAsync(
        string idempotencyKey,
        ModSnapshotPackageMetadataDto package,
        byte[] payload,
        string? confirmationOperation = null,
        IReadOnlyList<ModSnapshotAchievementCandidateDto>? achievementCandidates = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModUploadSnapshotResponseDto>();
        }

        var request = new ModUploadSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            SnapshotId = package.SnapshotId,
            Package = MarkSnapshotUploadKind(
                package,
                NormalizeSnapshotUploadKind(package.SnapshotUploadKind, confirmationOperation)),
            AuthToken = context.AuthToken,
            ConfirmationOperation = confirmationOperation,
            AchievementCandidates = achievementCandidates?.ToList() ?? new List<ModSnapshotAchievementCandidateDto>()
        };

        return transport.PostSnapshotMultipartAsync<ModUploadSnapshotRequestDto, ModUploadSnapshotResponseDto>(
            UploadSnapshotRoute,
            request,
            payload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> DownloadLatestSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModDownloadLatestSnapshotResponseDto>();
        }

        return DownloadLatestSnapshotAsync(context.UserId, context.ColonyId, cancellationToken: cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> DownloadLatestSnapshotAsync(
        string userId,
        string colonyId,
        string? authorizationEventId = null,
        string? authorizationScope = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModDownloadLatestSnapshotResponseDto>();
        }

        var request = new ModDownloadLatestSnapshotRequestDto
        {
            UserId = userId ?? string.Empty,
            ColonyId = colonyId ?? string.Empty,
            AuthToken = context.AuthToken,
            AuthorizationEventId = authorizationEventId,
            AuthorizationScope = authorizationScope
        };

        return transport.PostAsync<ModDownloadLatestSnapshotRequestDto, ModDownloadLatestSnapshotResponseDto>(
            DownloadLatestSnapshotRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<byte[]>> DownloadLatestSnapshotPayloadAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<byte[]>();
        }

        return DownloadLatestSnapshotPayloadAsync(context.UserId, context.ColonyId, snapshotId, cancellationToken: cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<byte[]>> DownloadLatestSnapshotPayloadAsync(
        string userId,
        string colonyId,
        string snapshotId,
        string? authorizationEventId = null,
        string? authorizationScope = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<byte[]>();
        }

        var request = new ModDownloadLatestSnapshotPayloadRequestDto
        {
            UserId = userId ?? string.Empty,
            ColonyId = colonyId ?? string.Empty,
            SnapshotId = snapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            AuthorizationEventId = authorizationEventId,
            AuthorizationScope = authorizationScope
        };

        return transport.PostForBytesAsync(DownloadLatestSnapshotPayloadRoute, request, cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmEventApplicationAsync(
        string idempotencyKey,
        string eventId,
        string? sourceEventId,
        string baseSnapshotId,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] payload,
        string clientApplicationResult,
        CancellationToken cancellationToken = default)
        => eventConfirmations.ConfirmAsync(context, new(idempotencyKey, eventId, sourceEventId, context.UserId, context.ColonyId,
            baseSnapshotId, confirmedSnapshot, payload, clientApplicationResult), cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmEventApplicationForIdentityAsync(
        string idempotencyKey,
        string eventId,
        string? sourceEventId,
        string userId,
        string colonyId,
        string baseSnapshotId,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] payload,
        string clientApplicationResult,
        CancellationToken cancellationToken = default)
        => eventConfirmations.ConfirmAsync(context, new(idempotencyKey, eventId, sourceEventId, userId, colonyId,
            baseSnapshotId, confirmedSnapshot, payload, clientApplicationResult), cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmEventApplicationsAsync(
        string idempotencyKey,
        string baseSnapshotId,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] payload,
        IReadOnlyCollection<ModConfirmEventApplicationEntryDto> applications,
        CancellationToken cancellationToken = default)
        => eventConfirmations.ConfirmBatchAsync(context, new(idempotencyKey, baseSnapshotId, confirmedSnapshot, payload, applications), cancellationToken);

    public Task<ClashOfRimClientNetworkResult<ModReportEventApplicationFailureResponseDto>> ReportEventApplicationFailureAsync(
        string idempotencyKey,
        string eventId,
        string? sourceEventId,
        string currentSnapshotId,
        string reason,
        CancellationToken cancellationToken = default)
        => eventDecisions.ReportFailureAsync(context, new(idempotencyKey, eventId, sourceEventId, currentSnapshotId, reason), cancellationToken);

    private static Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>()
    {
        return Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed(
            "NotConfigured",
            ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured")));
    }

    private static ClashOfRimClientNetworkResult<T> CompatibilityManifestBuildFailed<T>()
    {
        return ClashOfRimClientNetworkResult<T>.Failed(
            "CompatibilityManifestBuildFailed",
            ClashOfRimText.Key("ClashOfRim.Compatibility.ManifestBuildFailed"));
    }

    private static bool ShouldRetryPrepareWorldSessionWithFullCompatibilityManifest(
        ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result,
        string? explicitManifestJson)
    {
        if (!string.IsNullOrWhiteSpace(explicitManifestJson))
        {
            return false;
        }

        if (result.Response?.RequiresFullCompatibilityManifest == true)
        {
            return true;
        }

        string? message = result.Response?.Result?.Message;
        return result.Response?.Result?.Accepted == false
            && !string.IsNullOrWhiteSpace(message)
            && (message!.IndexOf("完整模组列表", StringComparison.Ordinal) >= 0
                || message.IndexOf("complete mod list", StringComparison.OrdinalIgnoreCase) >= 0);
    }

}
