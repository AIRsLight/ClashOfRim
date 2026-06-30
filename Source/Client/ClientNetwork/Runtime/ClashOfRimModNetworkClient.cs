using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

public sealed class ClashOfRimModNetworkClient
{
    private static readonly DataContractJsonSerializerSettings JsonSerializerSettings = new()
    {
        UseSimpleDictionaryFormat = true
    };
    private static readonly TimeSpan SessionWebSocketPingInterval = TimeSpan.FromSeconds(10);

    private const string LoginRoute = "/session/login";
    private const string StreamSessionRoute = "/session/stream";
    private const string MaintainPresenceRoute = "/session/presence";
    private const string LogoutRoute = "/session/logout";
    private const string ChangeOfflinePasswordRoute = "/session/password";
    private const string ListPlayersRoute = "/players/list";
    private const string ListAchievementsRoute = "/achievements/list";
    private const string WaitForEventsRoute = "/events/wait";
    private const string PullPendingEventsRoute = "/events/pending";
    private const string PullEventDetailsRoute = "/events/details";
    private const string CreateGiftRoute = "/events/gifts";
    private const string CreateGiftWithSnapshotRoute = "/events/gifts/create-with-snapshot";
    private const string StorePawnPackageRoute = "/pawns/packages/store";
    private const string GetPawnPackageRoute = "/pawns/packages/get";
    private const string StoreThingPackageRoute = "/things/packages/store";
    private const string GetThingPackageRoute = "/things/packages/get";
    private const string QuoteTradeOrderFeeRoute = "/events/trades/quote-fee";
    private const string CreateTradeOrderRoute = "/events/trades";
    private const string CreateTradeOrderWithSnapshotRoute = "/events/trades/create-with-snapshot";
    private const string ListTradeOrdersRoute = "/events/trades/market";
    private const string AcceptTradeOrderRoute = "/events/trades/accept";
    private const string FulfillTradeOrderRoute = "/events/trades/fulfill";
    private const string FulfillTradeOrderWithSnapshotRoute = "/events/trades/fulfill-with-snapshot";
    private const string CancelTradeOrderRoute = "/events/trades/cancel";
    private const string ListServerShopRoute = "/shop/list";
    private const string UpsertServerShopListingRoute = "/shop/admin/upsert";
    private const string RemoveServerShopListingRoute = "/shop/admin/remove";
    private const string PurchaseServerShopListingWithSnapshotRoute = "/shop/purchase-with-snapshot";
    private const string PrepareRaidRoute = "/events/raids/prepare";
    private const string CreateRaidRoute = "/events/raids";
    private const string CreateRaidWithSnapshotRoute = "/events/raids/create-with-snapshot";
    private const string RejectGiftRoute = "/events/gifts/reject";
    private const string CreateDiplomacyEventRoute = "/events/diplomacy";
    private const string RespondDiplomacyEventRoute = "/events/diplomacy/respond";
    private const string CreateSupportPawnRoute = "/events/support-pawns";
    private const string CreateSupportPawnWithSnapshotRoute = "/events/support-pawns/create-with-snapshot";
    private const string RejectSupportPawnRoute = "/events/support-pawns/reject";
    private const string FinishSupportPawnRoute = "/events/support-pawns/finish";
    private const string UploadSnapshotRoute = "/snapshots/upload";
    private const string DownloadLatestSnapshotRoute = "/snapshots/latest";
    private const string DownloadLatestSnapshotPayloadRoute = "/snapshots/latest/payload";
    private const string ConfirmEventApplicationRoute = "/events/confirm-application";
    private const string ConfirmEventApplicationsRoute = "/events/confirm-applications";
    private const string ReportEventApplicationFailureRoute = "/events/application-failure";
    private const string SyncWorldMapMarkersRoute = "/world/markers";
    private const string SyncRuntimeWorldObjectsRoute = "/world/runtime-objects";
    private const string PrepareWorldSessionRoute = "/world/session";
    private const string GetWorldConfigurationRoute = "/world/configuration/current";
    private const string SubmitWorldConfigurationRoute = "/world/configuration";
    private const string SubmitWorldTileGeometryRoute = "/world/configuration/tile-geometry";
    private const string SubmitWorldFeatureNamesRoute = "/world/configuration/feature-names";
    private const string RegisterPlayerColonySitesRoute = "/world/colony-sites";
    private const string PreflightColonyRelocationRoute = "/world/colony-sites/relocation/preflight";
    private const string ConfirmColonyRelocationRoute = "/world/colony-sites/relocation/confirm";
    private const string AbandonPlayerColonyRoute = "/world/colony-sites/abandon";
    private const string GetAdminBaselineRequirementsRoute = "/admin/baseline/requirements";
    private const string SubmitAdminBaselineRoute = "/admin/baseline";
    private const string GetBankStatusRoute = "/bank/status";
    private const string CreateBankLoanWithSnapshotRoute = "/bank/loans/create-with-snapshot";
    private const string CreateBankLoanRoute = "/bank/loans";
    private const string RepayBankLoanWithSnapshotRoute = "/bank/loans/repay-with-snapshot";
    private const string RepayBankLoanRoute = "/bank/loans/repay";
    private const string RepayBankDebtWithSnapshotRoute = "/bank/debts/repay-with-snapshot";
    private const string RepayBankDebtRoute = "/bank/debts/repay";
    private const string QuoteMercenaryRoute = "/mercenaries/quote";
    private const string HireMercenaryWithSnapshotRoute = "/mercenaries/hire-with-snapshot";
    private const string HireMercenaryRoute = "/mercenaries/hire";
    private const string QuoteMercenaryGuardRoute = "/mercenaries/guards/quote";
    private const string HireMercenaryGuardWithSnapshotRoute = "/mercenaries/guards/hire-with-snapshot";
    private const string ReportMercenaryIncidentRoute = "/mercenaries/incidents";
    private const string SendChatMessageRoute = "/chat/send";
    private const string ListChatMessagesRoute = "/chat/list";
    private const string OverrideCompatibilityBaselineRoute = "/compatibility/baseline/override";
    private const string AdminStatusRoute = "/admin/status";
    private const string AdminUpdateConfigurationRoute = "/admin/configuration";
    private const string AdminActionRoute = "/admin/action";
    private const string ServerHelloRoute = "/server/hello";
    private const string LanguageHeader = "X-ClashOfRim-Language";

    private readonly HttpClient httpClient;
    private readonly ClashOfRimClientNetworkContext context;

    public static Action<string>? SessionExpired;
    public static Func<string?>? CompatibilityManifestJsonProvider;
    public static Func<string?>? CompatibilityManifestIdProvider;
    public static Func<string?>? CompatibilityManifestSummaryJsonProvider;
    public static Func<IReadOnlyCollection<string>?, string?>? CompatibilityManifestJsonForPackagesProvider;

    public ClashOfRimModNetworkClient(
        HttpClient httpClient,
        ClashOfRimClientNetworkContext context)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> ServerHelloAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ServerBaseUrl))
        {
            return NotConfigured<ModServerHelloResponseDto>();
        }

        return GetAsync<ModServerHelloResponseDto>(ServerHelloRoute, cancellationToken);
    }

    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(
        string compatibilityDigest,
        string? compatibilityManifestJson = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return await NotConfigured<ModLoginResponseDto>();
        }

        var request = new ModLoginRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId,
            CompatibilityDigest = compatibilityDigest,
            CompatibilityManifestJson = compatibilityManifestJson,
            CompatibilityManifestId = CompatibilityManifestIdProvider?.Invoke(),
            CompatibilityManifestSummaryJson = compatibilityManifestJson is null
                ? CompatibilityManifestSummaryJsonProvider?.Invoke()
                : null,
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword
        };

        ClashOfRimClientNetworkResult<ModLoginResponseDto> result =
            await PostAsync<ModLoginRequestDto, ModLoginResponseDto>(LoginRoute, request, cancellationToken);
        if (!ShouldRetryWithFullCompatibilityManifest(result, compatibilityManifestJson))
        {
            return result;
        }

        request.CompatibilityManifestJson = BuildCompatibilityManifestJsonForRetry(
            result.Response?.RequestedCompatibilityPackageIds);
        if (string.IsNullOrWhiteSpace(request.CompatibilityManifestJson))
        {
            return CompatibilityManifestBuildFailed<ModLoginResponseDto>();
        }

        request.CompatibilityManifestSummaryJson = null;
        return await PostAsync<ModLoginRequestDto, ModLoginResponseDto>(LoginRoute, request, cancellationToken);
    }

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

        return PostAsync<ModOverrideCompatibilityBaselineRequestDto, ModOverrideCompatibilityBaselineResponseDto>(
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

        return PostAsync<ModAdminStatusRequestDto, ModAdminStatusResponseDto>(
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

        return PostAsync<ModChangeOfflinePasswordRequestDto, ModChangeOfflinePasswordResponseDto>(
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

        return PostAsync<ModAdminUpdateConfigurationRequestDto, ModAdminUpdateConfigurationResponseDto>(
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

        return PostAsync<ModAdminActionRequestDto, ModAdminActionResponseDto>(
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

        return PostAsync<ModSyncWorldMapMarkersRequestDto, ModWorldMapMarkerDeliveryDto>(
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

        return PostAsync<ModSyncRuntimeWorldObjectsRequestDto, ModSyncRuntimeWorldObjectsResponseDto>(
            SyncRuntimeWorldObjectsRoute,
            request,
            cancellationToken);
    }

    public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareWorldSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ServerBaseUrl)
            || string.IsNullOrWhiteSpace(context.UserId))
        {
            return await NotConfigured<ModPrepareWorldSessionResponseDto>();
        }

        var request = new ModPrepareWorldSessionRequestDto
        {
            UserId = context.UserId,
            CompatibilityManifestId = CompatibilityManifestIdProvider?.Invoke(),
            CompatibilityManifestSummaryJson = CompatibilityManifestSummaryJsonProvider?.Invoke(),
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword
        };

        ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result =
            await PostAsync<ModPrepareWorldSessionRequestDto, ModPrepareWorldSessionResponseDto>(
            PrepareWorldSessionRoute,
            request,
            cancellationToken);
        if (!ShouldRetryPrepareWorldSessionWithFullCompatibilityManifest(result, request.CompatibilityManifestJson))
        {
            return result;
        }

        request.CompatibilityManifestJson = BuildCompatibilityManifestJsonForRetry(
            result.Response?.RequestedCompatibilityPackageIds);
        if (string.IsNullOrWhiteSpace(request.CompatibilityManifestJson))
        {
            return CompatibilityManifestBuildFailed<ModPrepareWorldSessionResponseDto>();
        }

        request.CompatibilityManifestSummaryJson = null;
        return await PostAsync<ModPrepareWorldSessionRequestDto, ModPrepareWorldSessionResponseDto>(
            PrepareWorldSessionRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitWorldConfigurationAsync(
        ModWorldConfigurationDto configuration,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModSubmitWorldConfigurationResponseDto>();
        }

        var request = new ModSubmitWorldConfigurationRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            Configuration = configuration
        };

        return PostAsync<ModSubmitWorldConfigurationRequestDto, ModSubmitWorldConfigurationResponseDto>(
            SubmitWorldConfigurationRoute,
            request,
            cancellationToken);
    }

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

        return PostAsync<ModSubmitWorldFeatureNamesRequestDto, ModSubmitWorldFeatureNamesResponseDto>(
            SubmitWorldFeatureNamesRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModSubmitWorldTileGeometryResponseDto>> SubmitWorldTileGeometryAsync(
        string worldConfigurationId,
        ModWorldTileGeometryDto geometry,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModSubmitWorldTileGeometryResponseDto>();
        }

        byte[] payload = ModWorldTileGeometryBinaryCodec.Encode(geometry);
        var request = new ModSubmitWorldTileGeometryRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            WorldConfigurationId = worldConfigurationId ?? string.Empty,
            PayloadEncoding = ModWorldTileGeometryBinaryCodec.EncodingName,
            PayloadBase64 = Convert.ToBase64String(payload),
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket)
                ? context.UserId
                : context.SteamAuthTicket,
            Password = context.OfflinePassword
        };

        return PostAsync<ModSubmitWorldTileGeometryRequestDto, ModSubmitWorldTileGeometryResponseDto>(
            SubmitWorldTileGeometryRoute,
            request,
            cancellationToken);
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

        return PostAsync<ModGetWorldConfigurationRequestDto, ModGetWorldConfigurationResponseDto>(
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

        return PostAsync<ModRegisterPlayerColonySitesRequestDto, ModRegisterPlayerColonySitesResponseDto>(
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

        return PostAsync<ModPreflightColonyRelocationRequestDto, ModColonyRelocationResponseDto>(
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

        return PostAsync<ModConfirmColonyRelocationRequestDto, ModColonyRelocationResponseDto>(
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

        return PostAsync<ModAbandonPlayerColonyRequestDto, ModAbandonPlayerColonyResponseDto>(
            AbandonPlayerColonyRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankStatusResponseDto>> GetBankStatusAsync(
        long currentGameTicks,
        int colonyWealth,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankStatusResponseDto>();
        }

        var request = new ModGetBankStatusRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ColonyWealth = colonyWealth
        };

        return PostAsync<ModGetBankStatusRequestDto, ModBankStatusResponseDto>(
            GetBankStatusRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> CreateBankLoanAsync(
        string idempotencyKey,
        long currentGameTicks,
        int colonyWealth,
        int principalSilver,
        int durationDays,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        var request = new ModCreateBankLoanRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ColonyWealth = colonyWealth,
            PrincipalSilver = principalSilver,
            DurationDays = durationDays
        };

        return PostAsync<ModCreateBankLoanRequestDto, ModBankLoanResponseDto>(
            CreateBankLoanRoute,
            request,
            cancellationToken);
    }

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
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        var request = new ModCreateBankLoanWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ColonyWealth = colonyWealth,
            PrincipalSilver = principalSilver,
            DurationDays = durationDays,
            RequestedLoanId = requestedLoanId ?? string.Empty,
            ExpectedInterestSilver = expectedInterestSilver,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.BankLoanCreationConfirmation)
        };

        return PostSnapshotMultipartAsync<ModCreateBankLoanWithSnapshotRequestDto, ModBankLoanResponseDto>(
            CreateBankLoanWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> RepayBankLoanAsync(
        string idempotencyKey,
        long currentGameTicks,
        int silverPaid,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        var request = new ModRepayBankLoanRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SilverPaid = silverPaid
        };

        return PostAsync<ModRepayBankLoanRequestDto, ModBankLoanResponseDto>(
            RepayBankLoanRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> RepayBankLoanWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        string loanId,
        int silverPaid,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        var request = new ModRepayBankLoanWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            LoanId = loanId ?? string.Empty,
            SilverPaid = silverPaid,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.BankLoanRepaymentConfirmation)
        };

        return PostSnapshotMultipartAsync<ModRepayBankLoanWithSnapshotRequestDto, ModBankLoanResponseDto>(
            RepayBankLoanWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankDebtResponseDto>> RepayBankDebtAsync(
        string idempotencyKey,
        long currentGameTicks,
        string debtId,
        int silverPaid,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankDebtResponseDto>();
        }

        var request = new ModRepayBankDebtRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            DebtId = debtId,
            SilverPaid = silverPaid
        };

        return PostAsync<ModRepayBankDebtRequestDto, ModBankDebtResponseDto>(
            RepayBankDebtRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankDebtResponseDto>> RepayBankDebtWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        string debtId,
        int silverPaid,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankDebtResponseDto>();
        }

        var request = new ModRepayBankDebtWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            DebtId = debtId ?? string.Empty,
            SilverPaid = silverPaid,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.BankDebtRepaymentConfirmation)
        };

        return PostSnapshotMultipartAsync<ModRepayBankDebtWithSnapshotRequestDto, ModBankDebtResponseDto>(
            RepayBankDebtWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto>> HireMercenaryAsync(
        string idempotencyKey,
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryHireResponseDto>();
        }

        var request = new ModHireMercenaryRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SkillDefName = skillDefName,
            SkillLevel = skillLevel,
            DurationDays = durationDays
        };

        return PostAsync<ModHireMercenaryRequestDto, ModMercenaryHireResponseDto>(
            HireMercenaryRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto>> HireMercenaryWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryHireResponseDto>();
        }

        var request = new ModHireMercenaryWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SkillDefName = contract.SkillDefName,
            SkillLevel = contract.SkillLevel,
            DurationDays = contract.DurationDays,
            RequestedContractId = contract.ContractId,
            ExpectedPriceSilver = contract.PriceSilver,
            ExpectedHarmfulSurgeryFineSilver = contract.HarmfulSurgeryFineSilver,
            ExpectedDeathFineSilver = contract.DeathFineSilver,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.MercenaryHireConfirmation)
        };

        return PostSnapshotMultipartAsync<ModHireMercenaryWithSnapshotRequestDto, ModMercenaryHireResponseDto>(
            HireMercenaryWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto>> QuoteMercenaryAsync(
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryQuoteResponseDto>();
        }

        var request = new ModQuoteMercenaryRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SkillDefName = skillDefName,
            SkillLevel = skillLevel,
            DurationDays = durationDays
        };

        return PostAsync<ModQuoteMercenaryRequestDto, ModMercenaryQuoteResponseDto>(
            QuoteMercenaryRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto>> QuoteMercenaryGuardAsync(
        long currentGameTicks,
        string tier,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryGuardQuoteResponseDto>();
        }

        var request = new ModQuoteMercenaryGuardRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            Tier = tier
        };

        return PostAsync<ModQuoteMercenaryGuardRequestDto, ModMercenaryGuardQuoteResponseDto>(
            QuoteMercenaryGuardRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModMercenaryGuardHireResponseDto>> HireMercenaryGuardWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryGuardHireResponseDto>();
        }

        var request = new ModHireMercenaryGuardWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            RequestedContractId = contract.ContractId,
            Tier = contract.Tier,
            ExpectedPriceSilver = contract.PriceSilver,
            ExpectedPointRatio = contract.PointRatio,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.MercenaryGuardHireConfirmation)
        };

        return PostSnapshotMultipartAsync<ModHireMercenaryGuardWithSnapshotRequestDto, ModMercenaryGuardHireResponseDto>(
            HireMercenaryGuardWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>> ReportMercenaryIncidentAsync(
        string idempotencyKey,
        long currentGameTicks,
        string contractId,
        string incidentKind,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryIncidentResponseDto>();
        }

        var request = new ModReportMercenaryIncidentRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ContractId = contractId,
            IncidentKind = incidentKind
        };

        return PostAsync<ModReportMercenaryIncidentRequestDto, ModMercenaryIncidentResponseDto>(
            ReportMercenaryIncidentRoute,
            request,
            cancellationToken);
    }

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

        return PostAsync<ModSendChatMessageRequestDto, ModSendChatMessageResponseDto>(
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

        return PostAsync<ModListChatMessagesRequestDto, ModListChatMessagesResponseDto>(
            ListChatMessagesRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>> SubmitAdminBaselineAsync(
        ModSubmitAdminBaselineRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!context.HasServerIdentity)
        {
            return NotConfigured<ModSubmitAdminBaselineResponseDto>();
        }

        request.UserId = context.UserId;
        request.ColonyId = null;

        return PostAsync<ModSubmitAdminBaselineRequestDto, ModSubmitAdminBaselineResponseDto>(
            SubmitAdminBaselineRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto>> GetAdminBaselineRequirementsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.HasServerIdentity)
        {
            return NotConfigured<ModGetAdminBaselineRequirementsResponseDto>();
        }

        var request = new ModGetAdminBaselineRequirementsRequestDto
        {
            UserId = context.UserId,
            ColonyId = null
        };

        return PostAsync<ModGetAdminBaselineRequirementsRequestDto, ModGetAdminBaselineRequirementsResponseDto>(
            GetAdminBaselineRequirementsRoute,
            request,
            cancellationToken);
    }

    public async Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamSessionAsync(
        string? sessionId,
        long knownNotificationVersion,
        long knownWorldConfigurationVersion,
        Action<ModSessionStreamEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return await NotConfigured<ModSessionStreamClosedDto>();
        }

        try
        {
            Uri uri = BuildWebSocketSessionUri(sessionId, knownNotificationVersion, knownWorldConfigurationVersion);
            using var webSocket = new ClientWebSocket();
            string? language = ResolveCurrentRimWorldLanguage();
            if (!string.IsNullOrWhiteSpace(language))
            {
                webSocket.Options.SetRequestHeader(LanguageHeader, language);
            }

            await webSocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            using var pingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task pingTask = SendWebSocketPingLoopAsync(webSocket, pingCancellation.Token);
            try
            {
                while (!cancellationToken.IsCancellationRequested
                    && webSocket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
                {
                    string? message = await ReceiveWebSocketTextAsync(webSocket, cancellationToken).ConfigureAwait(false);
                    if (message is null)
                    {
                        break;
                    }

                    ModSessionWebSocketEnvelopeDto envelope = Deserialize<ModSessionWebSocketEnvelopeDto>(message);
                    if (string.Equals(envelope.EventName, "pong", StringComparison.Ordinal)
                        || string.Equals(envelope.EventName, "keepalive", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    onEvent(new ModSessionStreamEvent(envelope.EventName, envelope.Data));
                }
            }
            finally
            {
                pingCancellation.Cancel();
                try
                {
                    await pingTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
                {
                }

                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Client closed.",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                    }
                }
            }

            return ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(
                new ModSessionStreamClosedDto(cancellationToken.IsCancellationRequested));
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or TaskCanceledException or IOException or ObjectDisposedException or InvalidOperationException or SerializationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new ModSessionStreamClosedDto(true));
            }

            return ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed(ex.GetType().Name, ex.Message);
        }
    }

    private static async Task SendWebSocketPingLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            await SendWebSocketTextAsync(webSocket, "{\"eventName\":\"ping\",\"data\":\"{}\"}", cancellationToken).ConfigureAwait(false);
            await Task.Delay(SessionWebSocketPingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendWebSocketTextAsync(ClientWebSocket webSocket, string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReceiveWebSocketTextAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
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

        return PostAsync<ModMaintainPresenceRequestDto, ModMaintainPresenceResponseDto>(
            MaintainPresenceRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModLogoutResponseDto>();
        }

        var request = new ModLogoutRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            SessionId = sessionId
        };

        return PostAsync<ModLogoutRequestDto, ModLogoutResponseDto>(
            LogoutRoute,
            request,
            cancellationToken);
    }

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

        return PostAsync<ModListPlayersRequestDto, ModListPlayersResponseDto>(
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

        return PostAsync<ModListAchievementsRequestDto, ModListAchievementsResponseDto>(
            ListAchievementsRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>> PullPendingEventsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModPullPendingEventsResponseDto>();
        }

        var request = new ModPullPendingEventsRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty
        };

        return PostAsync<ModPullPendingEventsRequestDto, ModPullPendingEventsResponseDto>(
            PullPendingEventsRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>> WaitForEventsAsync(
        long knownNotificationVersion,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModWaitForEventsResponseDto>();
        }

        var request = new ModWaitForEventsRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            KnownNotificationVersion = knownNotificationVersion,
            TimeoutSeconds = timeoutSeconds
        };

        return PostAsync<ModWaitForEventsRequestDto, ModWaitForEventsResponseDto>(
            WaitForEventsRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>> PullEventDetailsAsync(
        IReadOnlyList<string> eventIds,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModPullEventDetailsResponseDto>();
        }

        var request = new ModPullEventDetailsRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            EventIds = eventIds.ToList()
        };

        return PostAsync<ModPullEventDetailsRequestDto, ModPullEventDetailsResponseDto>(
            PullEventDetailsRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModRejectGiftResponseDto>> RejectGiftAsync(
        string eventId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModRejectGiftResponseDto>();
        }

        var request = new ModRejectGiftRequestDto
        {
            EventId = eventId,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Reason = reason
        };

        return PostAsync<ModRejectGiftRequestDto, ModRejectGiftResponseDto>(
            RejectGiftRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModRejectSupportPawnResponseDto>> RejectSupportPawnAsync(
        string eventId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModRejectSupportPawnResponseDto>();
        }

        var request = new ModRejectSupportPawnRequestDto
        {
            EventId = eventId,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Reason = reason
        };

        return PostAsync<ModRejectSupportPawnRequestDto, ModRejectSupportPawnResponseDto>(
            RejectSupportPawnRoute,
            request,
            cancellationToken);
    }

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
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            FinishReason = finishReason,
            PawnGlobalKey = pawnGlobalKey,
            PawnName = pawnName,
            PawnDead = pawnDead,
            PawnPackage = pawnPackage
        };

        return PostAsync<ModFinishSupportPawnRequestDto, ModFinishSupportPawnResponseDto>(
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
            ExpiresAtUtc = expiresAtUtc?.ToString("O")
        };

        return PostAsync<ModCreateDiplomacyEventRequestDto, ModDiplomacyEventResponseDto>(
            CreateDiplomacyEventRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> RespondDiplomacyEventAsync(
        string eventId,
        bool accepted,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModDiplomacyEventResponseDto>();
        }

        var request = new ModRespondDiplomacyEventRequestDto
        {
            EventId = eventId,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Accepted = accepted,
            Reason = reason
        };

        return PostAsync<ModRespondDiplomacyEventRequestDto, ModDiplomacyEventResponseDto>(
            RespondDiplomacyEventRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateGiftAsync(
        string idempotencyKey,
        string targetUserId,
        string targetColonyId,
        string? targetSnapshotId,
        IReadOnlyList<ModThingReferenceDto> things,
        string? message,
        ModEventTargetContextDto? targetContext = null,
        string? deliveryKind = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var actor = new ModProtocolIdentityDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            SnapshotId = context.CurrentSnapshotId
        };
        var target = new ModProtocolIdentityDto
        {
            UserId = targetUserId,
            ColonyId = targetColonyId,
            SnapshotId = null
        };
        var request = new ModCreateGiftRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Actor = actor,
            Target = target,
            Things = things.ToList(),
            Message = message,
            TargetContext = targetContext,
            DeliveryKind = deliveryKind
        };

        return PostAsync<ModCreateGiftRequestDto, ModEventCreationResponseDto>(
            CreateGiftRoute,
            request,
            cancellationToken);
    }

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
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var gift = new ModCreateGiftRequestDto
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
            Things = things.ToList(),
            Message = message,
            TargetContext = targetContext,
            DeliveryKind = deliveryKind
        };
        var request = new ModCreateGiftWithSnapshotRequestDto
        {
            Gift = gift,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.GiftCreationConfirmation)
        };

        return PostSnapshotMultipartAsync<ModCreateGiftWithSnapshotRequestDto, ModEventCreationResponseDto>(
            CreateGiftWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateTradeOrderAsync(
        string idempotencyKey,
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        int feeSilver,
        bool allowSelfPickup,
        bool allowServerDropPod,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var request = new ModCreateTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Owner = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            OfferedThings = offeredThings.ToList(),
            RequestedThings = requestedThings.ToList(),
            FeeSilver = feeSilver,
            AllowSelfPickup = allowSelfPickup,
            AllowServerDropPod = allowServerDropPod
        };

        return PostAsync<ModCreateTradeOrderRequestDto, ModEventCreationResponseDto>(
            CreateTradeOrderRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteTradeOrderFeeAsync(
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModTradeOrderFeeQuoteResponseDto>();
        }

        var request = new ModQuoteTradeOrderFeeRequestDto
        {
            Owner = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            OfferedThings = offeredThings.ToList(),
            RequestedThings = requestedThings.ToList()
        };

        return PostAsync<ModQuoteTradeOrderFeeRequestDto, ModTradeOrderFeeQuoteResponseDto>(
            QuoteTradeOrderFeeRoute,
            request,
            cancellationToken);
    }

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
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var tradeOrder = new ModCreateTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Owner = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            OfferedThings = offeredThings.ToList(),
            RequestedThings = requestedThings.ToList(),
            FeeSilver = feeSilver,
            AllowSelfPickup = allowSelfPickup,
            AllowServerDropPod = allowServerDropPod
        };
        var request = new ModCreateTradeOrderWithSnapshotRequestDto
        {
            TradeOrder = tradeOrder,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.TradeOrderCreationConfirmation)
        };

        return PostSnapshotMultipartAsync<ModCreateTradeOrderWithSnapshotRequestDto, ModEventCreationResponseDto>(
            CreateTradeOrderWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto>> StorePawnPackageAsync(
        string idempotencyKey,
        ModPawnExchangePackageDto pawnPackage,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModStorePawnPackageResponseDto>();
        }

        var request = new ModStorePawnPackageRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Owner = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            PawnPackage = pawnPackage
        };

        return PostAsync<ModStorePawnPackageRequestDto, ModStorePawnPackageResponseDto>(
            StorePawnPackageRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>> GetPawnPackageAsync(
        string pawnPackageId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModGetPawnPackageResponseDto>();
        }

        var request = new ModGetPawnPackageRequestDto
        {
            Requester = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            PawnPackageId = pawnPackageId
        };

        return PostAsync<ModGetPawnPackageRequestDto, ModGetPawnPackageResponseDto>(
            GetPawnPackageRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto>> StoreThingPackageAsync(
        string idempotencyKey,
        ModThingStatePackageDto thingPackage,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModStoreThingPackageResponseDto>();
        }

        var request = new ModStoreThingPackageRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Owner = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            ThingPackage = thingPackage
        };

        return PostAsync<ModStoreThingPackageRequestDto, ModStoreThingPackageResponseDto>(
            StoreThingPackageRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModGetThingPackageResponseDto>> GetThingPackageAsync(
        string thingPackageId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModGetThingPackageResponseDto>();
        }

        var request = new ModGetThingPackageRequestDto
        {
            Requester = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            ThingPackageId = thingPackageId
        };

        return PostAsync<ModGetThingPackageRequestDto, ModGetThingPackageResponseDto>(
            GetThingPackageRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>> ListTradeOrdersAsync(
        string scope = "Open",
        int offset = 0,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModListTradeOrdersResponseDto>();
        }

        var request = new ModListTradeOrdersRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Scope = scope,
            Offset = Math.Max(0, offset),
            Limit = limit <= 0 ? 10 : limit
        };

        return PostAsync<ModListTradeOrdersRequestDto, ModListTradeOrdersResponseDto>(
            ListTradeOrdersRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto>> AcceptTradeOrderAsync(
        string idempotencyKey,
        string tradeEventId,
        bool postagePaidByAcceptor,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModAcceptTradeOrderResponseDto>();
        }

        var request = new ModAcceptTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            TradeEventId = tradeEventId,
            Acceptor = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            PostagePaidByAcceptor = postagePaidByAcceptor
        };

        return PostAsync<ModAcceptTradeOrderRequestDto, ModAcceptTradeOrderResponseDto>(
            AcceptTradeOrderRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModFulfillTradeOrderResponseDto>> FulfillTradeOrderAsync(
        string idempotencyKey,
        string tradeEventId,
        string acceptedMemoEventId,
        IReadOnlyList<ModThingReferenceDto> deliveredThings,
        string fulfillmentMode = "SelfDelivery",
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModFulfillTradeOrderResponseDto>();
        }

        var request = new ModFulfillTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            TradeEventId = tradeEventId,
            AcceptedMemoEventId = acceptedMemoEventId,
            Acceptor = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            DeliveredThings = deliveredThings.ToList(),
            FulfillmentMode = fulfillmentMode
        };

        return PostAsync<ModFulfillTradeOrderRequestDto, ModFulfillTradeOrderResponseDto>(
            FulfillTradeOrderRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModFulfillTradeOrderResponseDto>> FulfillTradeOrderWithSnapshotAsync(
        string idempotencyKey,
        string tradeEventId,
        string acceptedMemoEventId,
        IReadOnlyList<ModThingReferenceDto> deliveredThings,
        string fulfillmentMode,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModFulfillTradeOrderResponseDto>();
        }

        var fulfillment = new ModFulfillTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            TradeEventId = tradeEventId,
            AcceptedMemoEventId = acceptedMemoEventId,
            Acceptor = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            DeliveredThings = deliveredThings.ToList(),
            FulfillmentMode = fulfillmentMode
        };
        var request = new ModFulfillTradeOrderWithSnapshotRequestDto
        {
            Fulfillment = fulfillment,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.TradeFulfillmentConfirmation)
        };

        return PostSnapshotMultipartAsync<ModFulfillTradeOrderWithSnapshotRequestDto, ModFulfillTradeOrderResponseDto>(
            FulfillTradeOrderWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> CancelTradeOrderAsync(
        string idempotencyKey,
        string tradeEventId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModCloseTradeOrderResponseDto>();
        }

        var request = new ModCloseTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            TradeEventId = tradeEventId,
            Owner = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            Reason = reason
        };

        return PostAsync<ModCloseTradeOrderRequestDto, ModCloseTradeOrderResponseDto>(
            CancelTradeOrderRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModListServerShopResponseDto>> ListServerShopAsync(
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModListServerShopResponseDto>();
        }

        var request = new ModListServerShopRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId
        };
        return PostAsync<ModListServerShopRequestDto, ModListServerShopResponseDto>(
            ListServerShopRoute,
            request,
            cancellationToken);
    }

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
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModUpsertServerShopListingResponseDto>();
        }

        var request = new ModUpsertServerShopListingRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            ListingId = listingId,
            ListingKind = listingKind,
            Item = item,
            PriceSilver = priceSilver,
            StockCount = stockCount,
            PriceIncreaseRatio = priceIncreaseRatio,
            QualityRequirementMode = NormalizeShopRequirementMode(qualityRequirementMode),
            HitPointsRequirementMode = NormalizeShopRequirementMode(hitPointsRequirementMode)
        };
        return PostAsync<ModUpsertServerShopListingRequestDto, ModUpsertServerShopListingResponseDto>(
            UpsertServerShopListingRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto>> RemoveServerShopListingAsync(
        string listingId,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModRemoveServerShopListingResponseDto>();
        }

        var request = new ModRemoveServerShopListingRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            ListingId = listingId
        };
        return PostAsync<ModRemoveServerShopListingRequestDto, ModRemoveServerShopListingResponseDto>(
            RemoveServerShopListingRoute,
            request,
            cancellationToken);
    }

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
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModPurchaseServerShopListingResponseDto>();
        }

        var purchase = new ModPurchaseServerShopListingRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Buyer = new ModProtocolIdentityDto
            {
                UserId = context.UserId,
                ColonyId = context.ColonyId,
                SnapshotId = context.CurrentSnapshotId
            },
            ListingId = listingId,
            UnitPriceSilver = unitPriceSilver,
            TotalPriceSilver = totalPriceSilver,
            PurchaseCount = purchaseCount,
            ListingKind = listingKind,
            DeliveredThings = deliveredThings?.ToList() ?? new List<ModThingReferenceDto>()
        };
        var request = new ModPurchaseServerShopListingWithSnapshotRequestDto
        {
            Purchase = purchase,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.ServerShopPurchaseConfirmation)
        };
        return PostSnapshotMultipartAsync<ModPurchaseServerShopListingWithSnapshotRequestDto, ModPurchaseServerShopListingResponseDto>(
            PurchaseServerShopListingWithSnapshotRoute,
            request,
            confirmedPayload,
            cancellationToken);
    }

    private static string NormalizeShopRequirementMode(string? mode)
    {
        return string.Equals(mode, "AtMost", StringComparison.Ordinal)
            ? "AtMost"
            : "AtLeast";
    }

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

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateRaidAsync(
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
        string opponentKind = "Player",
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var request = new ModCreateRaidRequestDto
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
            DefenderOnline = defenderOnline,
            DefenderWealth = defenderWealth,
            DefenderRaidCooldownUntilUtc = defenderRaidCooldownUntilUtc,
            RaidPreparationId = raidPreparationId,
            TargetWorldObjectId = targetWorldObjectId,
            TargetMapId = targetMapId,
            TargetTile = targetTile,
            DefenderSnapshotId = string.Empty,
            OpponentKind = opponentKind,
            PawnGlobalKeys = pawnGlobalKeys.ToList(),
            CarriedThings = carriedThings.ToList()
        };

        return PostAsync<ModCreateRaidRequestDto, ModEventCreationResponseDto>(
            CreateRaidRoute,
            request,
            cancellationToken);
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

        return PostSnapshotMultipartAsync<ModCreateRaidWithSnapshotRequestDto, ModEventCreationResponseDto>(
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

        return PostAsync<ModPrepareRaidRequestDto, ModPrepareRaidResponseDto>(
            PrepareRaidRoute,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateSupportPawnAsync(
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
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var request = new ModCreateSupportPawnRequestDto
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

        return PostAsync<ModCreateSupportPawnRequestDto, ModEventCreationResponseDto>(
            CreateSupportPawnRoute,
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

        return PostSnapshotMultipartAsync<ModCreateSupportPawnWithSnapshotRequestDto, ModEventCreationResponseDto>(
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

        return PostSnapshotMultipartAsync<ModUploadSnapshotRequestDto, ModUploadSnapshotResponseDto>(
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

        return PostAsync<ModDownloadLatestSnapshotRequestDto, ModDownloadLatestSnapshotResponseDto>(
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

        return PostForBytesAsync(DownloadLatestSnapshotPayloadRoute, request, cancellationToken);
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
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModConfirmEventApplicationResponseDto>();
        }

        var request = new ModConfirmEventApplicationRequestDto
        {
            IdempotencyKey = idempotencyKey,
            EventId = eventId,
            SourceEventId = sourceEventId,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            BaseSnapshotId = baseSnapshotId,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.EventApplicationConfirmation),
            ClientApplicationResult = clientApplicationResult,
            AuthToken = context.AuthToken
        };

        return PostSnapshotMultipartAsync<ModConfirmEventApplicationRequestDto, ModConfirmEventApplicationResponseDto>(
            ConfirmEventApplicationRoute,
            request,
            payload,
            cancellationToken);
    }

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
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModConfirmEventApplicationResponseDto>();
        }

        var request = new ModConfirmEventApplicationRequestDto
        {
            IdempotencyKey = idempotencyKey,
            EventId = eventId,
            SourceEventId = sourceEventId,
            UserId = userId ?? string.Empty,
            ColonyId = colonyId ?? string.Empty,
            BaseSnapshotId = baseSnapshotId,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.EventApplicationConfirmation),
            ClientApplicationResult = clientApplicationResult,
            AuthToken = context.AuthToken
        };

        return PostSnapshotMultipartAsync<ModConfirmEventApplicationRequestDto, ModConfirmEventApplicationResponseDto>(
            ConfirmEventApplicationRoute,
            request,
            payload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmEventApplicationsAsync(
        string idempotencyKey,
        string baseSnapshotId,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] payload,
        IReadOnlyCollection<ModConfirmEventApplicationEntryDto> applications,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModConfirmEventApplicationsResponseDto>();
        }

        var request = new ModConfirmEventApplicationsRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            BaseSnapshotId = baseSnapshotId,
            ConfirmedSnapshot = MarkSnapshotUploadKind(
                confirmedSnapshot,
                ModSnapshotUploadKinds.BatchEventApplicationConfirmation),
            Applications = applications?.ToList() ?? new List<ModConfirmEventApplicationEntryDto>(),
            AuthToken = context.AuthToken
        };

        return PostSnapshotMultipartAsync<ModConfirmEventApplicationsRequestDto, ModConfirmEventApplicationsResponseDto>(
            ConfirmEventApplicationsRoute,
            request,
            payload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModReportEventApplicationFailureResponseDto>> ReportEventApplicationFailureAsync(
        string idempotencyKey,
        string eventId,
        string? sourceEventId,
        string currentSnapshotId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModReportEventApplicationFailureResponseDto>();
        }

        var request = new ModReportEventApplicationFailureRequestDto
        {
            IdempotencyKey = idempotencyKey,
            EventId = eventId ?? string.Empty,
            SourceEventId = sourceEventId,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = currentSnapshotId ?? string.Empty,
            Reason = reason ?? string.Empty,
            AuthToken = context.AuthToken
        };

        return PostAsync<ModReportEventApplicationFailureRequestDto, ModReportEventApplicationFailureResponseDto>(
            ReportEventApplicationFailureRoute,
            request,
            cancellationToken);
    }

    private string BuildStreamSessionRoute(
        string? sessionId,
        long knownNotificationVersion,
        long knownWorldConfigurationVersion)
    {
        var query = new List<string>
        {
            "userId=" + Uri.EscapeDataString(context.UserId),
            "colonyId=" + Uri.EscapeDataString(context.ColonyId),
            "knownNotificationVersion=" + knownNotificationVersion.ToString(),
            "knownWorldConfigurationVersion=" + knownWorldConfigurationVersion.ToString()
        };

        if (!string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
        {
            query.Add("currentSnapshotId=" + Uri.EscapeDataString(context.CurrentSnapshotId));
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            query.Add("sessionId=" + Uri.EscapeDataString(sessionId));
        }

        return StreamSessionRoute.TrimStart('/') + "?" + string.Join("&", query);
    }

    private Uri BuildWebSocketSessionUri(
        string? sessionId,
        long knownNotificationVersion,
        long knownWorldConfigurationVersion)
    {
        Uri baseUri = ClashOfRimServerUrlUtility.BuildHttpBaseUri(context.ServerBaseUrl);
        Uri httpUri = new(baseUri, BuildStreamSessionRoute(sessionId, knownNotificationVersion, knownWorldConfigurationVersion));
        var builder = new UriBuilder(httpUri)
        {
            Scheme = string.Equals(httpUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };
        return builder.Uri;
    }

    private async Task<ClashOfRimClientNetworkResult<TResponse>> PostAsync<TRequest, TResponse>(
        string route,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(route))
            {
                Content = content
            };
            AddClientLanguageHeader(message);
            using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DetectSessionExpiredMessage(body);
                return ClashOfRimClientNetworkResult<TResponse>.Failed(
                    "HttpError",
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            TResponse parsed = Deserialize<TResponse>(body);
            DetectSessionExpired(parsed);
            return ClashOfRimClientNetworkResult<TResponse>.Ok(parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or SerializationException)
        {
            return ClashOfRimClientNetworkResult<TResponse>.Failed(ex.GetType().Name, ex.Message);
        }
    }

    private async Task<ClashOfRimClientNetworkResult<TResponse>> GetAsync<TResponse>(
        string route,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(route));
            AddClientLanguageHeader(message);
            using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DetectSessionExpiredMessage(body);
                return ClashOfRimClientNetworkResult<TResponse>.Failed(
                    "HttpError",
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            TResponse parsed = Deserialize<TResponse>(body);
            DetectSessionExpired(parsed);
            return ClashOfRimClientNetworkResult<TResponse>.Ok(parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or SerializationException)
        {
            return ClashOfRimClientNetworkResult<TResponse>.Failed(ex.GetType().Name, ex.Message);
        }
    }

    private async Task<ClashOfRimClientNetworkResult<byte[]>> PostForBytesAsync<TRequest>(
        string route,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(route))
            {
                Content = content
            };
            AddClientLanguageHeader(message);
            using HttpResponseMessage response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                DetectSessionExpiredMessage(body);
                return ClashOfRimClientNetworkResult<byte[]>.Failed(
                    "HttpError",
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return ClashOfRimClientNetworkResult<byte[]>.Ok(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or SerializationException or IOException)
        {
            return ClashOfRimClientNetworkResult<byte[]>.Failed(ex.GetType().Name, ex.Message);
        }
    }

    private async Task<ClashOfRimClientNetworkResult<TResponse>> PostSnapshotMultipartAsync<TRequest, TResponse>(
        string route,
        TRequest request,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        MultipartFormDataContent? content = null;
        HttpRequestMessage? message = null;
        try
        {
            string json = Serialize(request);
            content = new MultipartFormDataContent();
            content.Add(new StringContent(json, Encoding.UTF8, "application/json"), "request");
            content.Add(new ByteArrayContent(payload ?? Array.Empty<byte>()), "payload", "snapshot.payload");
            message = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(route))
            {
                Content = content
            };
            content = null;
            AddClientLanguageHeader(message);
            using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DetectSessionExpiredMessage(body);
                return ClashOfRimClientNetworkResult<TResponse>.Failed(
                    "HttpError",
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            TResponse parsed = Deserialize<TResponse>(body);
            DetectSessionExpired(parsed);
            return ClashOfRimClientNetworkResult<TResponse>.Ok(parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or SerializationException or IOException)
        {
            return ClashOfRimClientNetworkResult<TResponse>.Failed(ex.GetType().Name, ex.Message);
        }
        finally
        {
            SafeDispose(message);
            SafeDispose(content);
        }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (NullReferenceException ex)
        {
            Log.Warning("[ClashOfRim] Ignored HTTP multipart dispose NullReferenceException: " + ex.Message);
        }
    }

    private Uri BuildRequestUri(string route)
    {
        Uri baseUri = ClashOfRimServerUrlUtility.BuildHttpBaseUri(context.ServerBaseUrl);
        return new Uri(baseUri, route.TrimStart('/'));
    }

    private static Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>()
    {
        return Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed(
            "NotConfigured",
            ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured")));
    }

    private static bool ShouldRetryWithFullCompatibilityManifest(
        ClashOfRimClientNetworkResult<ModLoginResponseDto> result,
        string? explicitManifestJson)
    {
        return string.IsNullOrWhiteSpace(explicitManifestJson)
            && result.Response?.RequiresFullCompatibilityManifest == true;
    }

    private static string? BuildCompatibilityManifestJsonForRetry(IReadOnlyCollection<string>? packageIds)
    {
        string? requested = CompatibilityManifestJsonForPackagesProvider?.Invoke(packageIds);
        string? manifestJson = string.IsNullOrWhiteSpace(requested)
            ? CompatibilityManifestJsonProvider?.Invoke()
            : requested;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            Log.Warning("[ClashOfRim][Compatibility] Full manifest retry was requested, but the local manifest provider returned no payload.");
        }

        return manifestJson;
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

    private static void AddClientLanguageHeader(HttpRequestMessage message)
    {
        string? language = ResolveCurrentRimWorldLanguage();
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        message.Headers.Remove(LanguageHeader);
        message.Headers.Add(LanguageHeader, language);
    }

    private static string? ResolveCurrentRimWorldLanguage()
    {
        try
        {
            Type? languageDatabase = Type.GetType("Verse.LanguageDatabase, Assembly-CSharp");
            object? activeLanguage = languageDatabase
                ?.GetField("activeLanguage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null)
                ?? languageDatabase
                    ?.GetProperty("activeLanguage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null, null);
            if (activeLanguage is null)
            {
                return null;
            }

            Type languageType = activeLanguage.GetType();
            return languageType.GetField("folderName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(activeLanguage) as string
                ?? languageType.GetProperty("folderName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(activeLanguage, null) as string
                ?? languageType.GetProperty("FolderName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(activeLanguage, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void DetectSessionExpired<TResponse>(TResponse response)
    {
        if (response is null)
        {
            return;
        }

        object? result = typeof(TResponse)
            .GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(response, null);
        if (result is not ModProtocolResponseDto protocolResponse
            || protocolResponse.Accepted
            || string.IsNullOrWhiteSpace(protocolResponse.Message)
            || !IsSessionExpiredMessage(protocolResponse.Message))
        {
            return;
        }

        SessionExpired?.Invoke(protocolResponse.Message!);
    }

    private static void DetectSessionExpiredMessage(string? message)
    {
        if (IsSessionExpiredMessage(message))
        {
            SessionExpired?.Invoke(message!);
        }
    }

    private static bool IsSessionExpiredMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message!.IndexOf("\u4f1a\u8bdd\u5df2\u8fc7\u671f", StringComparison.Ordinal) >= 0
                || message.IndexOf("session has expired", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), JsonSerializerSettings);
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static T Deserialize<T>(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), JsonSerializerSettings);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        object? value = serializer.ReadObject(stream);
        if (value is not T typed)
        {
            throw new InvalidOperationException(ClashOfRimText.Key(
                "ClashOfRim.Network.StatusDeserializeFailed",
                typeof(T).Name.Named("TYPE")));
        }

        return typed;
    }
}
