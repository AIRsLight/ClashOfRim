using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;

VerifyPendingLoginSessionCanBeReplaced();
VerifyPersistentRegistries();
VerifyServerShopRegistrySemantics();
VerifyAchievementRegistryThresholdDefinitions();
VerifyPlayerRegistryColonyTombstones();
await VerifyPlayerListUsesLatestSnapshotWealthFallbackAsync();
await VerifyPlayerListHidesPlayersWithoutColonySnapshotAsync();
await VerifyAchievementLeaderboardsHidePlayersWithoutColonySnapshotAsync();
VerifySnapshotUploadAllowsSingleColonyRelocation();
await VerifyColonyRelocationExplicitConfirmationAsync();
await VerifyOnlinePresenceLeaseExpiryAsync();
await VerifyDefaultPersistentServerStateAsync();
await VerifyDiplomacyRelationCooldownAsync();

var state = new ClashOfRimNetworkState(
    serverConfiguration: new ClashOfRimServerConfiguration(
        maxOpenTradeOrdersPerOwner: 2,
        diplomacyRelationChangeCooldown: TimeSpan.Zero));
AuthoritativeEvent attackerLoss = SeedAttackerLossEvent(state.Ledger);

WebApplication app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
app.Urls.Add("http://127.0.0.1:0");
await app.StartAsync();

try
{
    string baseAddress = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!
        .Addresses.Single();

    using var httpClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
    var client = new ClashOfRimNetworkClient(httpClient);

    PrepareWorldSessionResponse firstWorldSession = await client.PrepareWorldSessionAsync(
        new PrepareWorldSessionRequest(
            ProtocolApiVersion.Current,
            "user-a",
            "colony-a",
            compatibilityManifestJson: BuildSmokeCompatibilityManifestJson("smoke-main-manifest"),
            password: string.Empty));
    Require(firstWorldSession.Result.Accepted, $"首个用户应能准备世界会话：{firstWorldSession.Result.ErrorCode} {firstWorldSession.Result.Message}");
    Require(firstWorldSession.IsAdministrator, "首个用户应成为默认管理员");
    Require(firstWorldSession.RequiresInitialWorldConfiguration, "首个管理员应进入初始世界配置分支");
    Require(!firstWorldSession.WorldConfigured, "初始提交前服务器不应已有世界配置");

    SubmitWorldConfigurationResponse submittedWorld = await client.SubmitWorldConfigurationAsync(
        new SubmitWorldConfigurationRequest(
            ProtocolApiVersion.Current,
            "user-a",
            "colony-a",
            new WorldConfigurationDto(
                "world:test",
                "user-a",
                "colony-a",
                DateTimeOffset.UtcNow,
                "seed-a",
                "30",
                "Normal",
                "Normal",
                "Normal",
                "Normal",
                "10000",
                new[] { "OutlanderCivil", "TribeCivil" },
                new[] { new WorldFeatureDto("MountainRange", "测试山脉", 1f, 1f, 2f, 3f) },
                new[] { new WorldFactionDto("OutlanderCivil", "测试外乡人", 0.1f, 0.2f, 0.3f, 1f) },
                new[] { new WorldRoadDto(1, 2, "DirtPath") },
                new[] { new WorldObjectBaselineDto("Settlement", 4, "测试定居点", "OutlanderCivil") },
                new[] { new PlayerColonySiteDto("user-a", "colony-a", "WorldObject_100", "Map_0", 9, "管理员殖民地") },
                "Cassandra",
                "Medium",
                BuildSmokeWorldTileGeometry(),
                extensions: WorldDlcExtensions(
                    new[] { new WorldPollutedTileDto(3, 0.5f) },
                    new WorldIdeoSummaryDto(
                        "owner:user-a/colony:colony-a/ideo:1",
                        "user-a",
                        "colony-a",
                        "world:test",
                        "1",
                        "管理员文化",
                        "Rustic",
                        "粗犷文化",
                        "UI/Ideoligions/Culture/Rustic",
                        null,
                        "#AA7744FF",
                        "IdeoFoundation_Deity",
                        "OutlanderCivil",
                        "IdeoIcon_Classic",
                        "UI/Ideoligions/Icons/Classic",
                        "IdeoColor_Red",
                        "#FF0000FF",
                        "<savedideo><meta><gameVersion>1.6.4633</gameVersion></meta><ideo /></savedideo>",
                        null,
                        null,
                        new[] { "Collectivist" },
                        new[] { "TreeConnection_Desired" },
                        new[] { "Totemic" },
                        hidden: false,
                        initialPlayerIdeo: true,
                        memeCount: 1,
                        preceptCount: 1)))));
    Require(submittedWorld.Result.Accepted, "管理员应能提交初始世界配置");
    Require(submittedWorld.WorldConfigured, "提交后服务器应标记已有世界配置");
    Equal("seed-a", submittedWorld.WorldConfiguration?.SeedString, "服务器应保留世界种子");

    SubmitAdminBaselineResponse adminBaseline = await client.SubmitAdminBaselineAsync(
        new SubmitAdminBaselineRequest(
            ProtocolApiVersion.Current,
            "user-a",
            "colony-a",
            DateTimeOffset.UtcNow,
            new[]
            {
                new StandardMarketValueDto("Silver", 1f),
                new StandardMarketValueDto("Wastepack", 0f),
                new StandardMarketValueDto("MealFine", 18f),
                new StandardMarketValueDto("Steel", 1f),
                new StandardMarketValueDto("WoodLog", 1f),
                new StandardMarketValueDto("Cloth", 1f),
                new StandardMarketValueDto("ComponentIndustrial", 12f)
            },
            new[]
            {
                new TrapClassificationDto(
                    "TrapSpike",
                    "RimWorld.Building_TrapDamager",
                    "ludeon.rimworld",
                    "Core",
                    "ApprovedByInheritance",
                    "inherits:RimWorld.Building_Trap",
                    inheritsBuildingTrap: true,
                    adminApproved: false),
                new TrapClassificationDto(
                    "SomeMod_Mine",
                    "SomeMod.MineBuilding",
                    "some.mod",
                    "Some Mod",
                    "CandidateRequiresApproval",
                    "candidate:name-or-class-marker",
                    inheritsBuildingTrap: false,
                    adminApproved: false)
            }));
    Require(adminBaseline.Result.Accepted, "管理员应能提交物品价格和陷阱清单基线");
    Require(adminBaseline.BaselineConfigured, "提交后服务器应标记已有管理员基线");
    Equal(7, adminBaseline.StandardMarketValueCount, "服务器应登记管理员上传价格表");
    Equal(1, adminBaseline.TrapAutoApprovedCount, "继承原版陷阱的项应自动批准");
    Equal(1, adminBaseline.TrapCandidateCount, "疑似陷阱项应作为候选保留");
    Equal(1, adminBaseline.TrapApprovedCount, "未确认候选项不应进入批准清单");
    Equal(18f, state.AdminBaseline.BuildEffectiveTradeFeePolicy(state.ServerConfiguration).StandardMarketValuePerThing["MealFine"], "交易手续费策略应优先使用管理员上传价格");

    UpsertServerShopListingResponse shopListing = (await (await httpClient.PostAsJsonAsync(
        ProtocolContractManifest.Find(ProtocolMessageKind.UpsertServerShopListing).Route,
        new UpsertServerShopListingRequest(
            "user-a",
            "colony-a",
            listingId: null,
            ServerShopListingKinds.SellToPlayer,
            new ThingReferenceDto("smoke-shop:silver", "Silver", 1, displayLabel: "白银"),
            priceSilver: 12,
            stockCount: 7))).Content.ReadFromJsonAsync<UpsertServerShopListingResponse>())!;
    Require(shopListing.Result.Accepted, "管理员应能上架服务器商店商品");
    Require(shopListing.Listing is not null, "服务器商店上架应返回商品记录");

    ListServerShopResponse shopList = (await (await httpClient.PostAsJsonAsync(
        ProtocolContractManifest.Find(ProtocolMessageKind.ListServerShop).Route,
        new ListServerShopRequest("user-b", "colony-b"))).Content.ReadFromJsonAsync<ListServerShopResponse>())!;
    Require(shopList.Result.Accepted, "玩家应能拉取服务器商店");
    Require(shopList.Listings.Any(listing => listing.ListingId == shopListing.Listing!.ListingId && listing.StockCount == 7), "服务器商店列表应包含管理员上架商品和库存");

    PrepareWorldSessionResponse secondWorldSession = await client.PrepareWorldSessionAsync(
        SmokePrepareWorldSessionRequest("user-b", "colony-b"));
    Require(secondWorldSession.Result.Accepted, $"后续用户应能准备世界会话：{secondWorldSession.Result.ErrorCode} {secondWorldSession.Result.Message}");
    Require(!secondWorldSession.IsAdministrator, "后续用户不应成为默认管理员");
    Require(secondWorldSession.WorldConfigured, "后续用户应进入已有世界配置分支");
    GetWorldConfigurationResponse secondWorldResponse = await client.GetWorldConfigurationAsync(
        SmokeGetWorldConfigurationRequest("user-b", "colony-b"));
    Require(secondWorldResponse.Result.Accepted, $"后续用户应能下载服务器世界配置：{secondWorldResponse.Result.ErrorCode} {secondWorldResponse.Result.Message}");
    WorldConfigurationDto? secondWorld = secondWorldResponse.WorldConfiguration;
    Equal("seed-a", secondWorld?.SeedString, "后续用户应收到服务器世界配置");
    Equal("Cassandra", secondWorld?.StorytellerDefName, "后续用户应收到服务器讲述人基线");
    Equal("Medium", secondWorld?.DifficultyDefName, "后续用户应收到服务器难度基线");
    Require(secondWorld?.FactionDefNames.Contains("OutlanderCivil") == true, "后续用户应收到服务器阵营定义列表");
    Require(secondWorld?.Features.Count == 1, "后续用户应收到服务器世界特征基线");
    Require(secondWorld?.Roads.Count == 1, "后续用户应收到服务器道路基线");
    IReadOnlyList<WorldPollutedTileDto> secondPollutedTiles = ReadPollutionExtension(secondWorld);
    IReadOnlyList<WorldIdeoSummaryDto> secondIdeos = ReadIdeoExtension(secondWorld);
    Require(secondPollutedTiles.Count == 1, "后续用户应收到服务器污染地块基线");
    Require(secondWorld?.WorldObjects.Count == 1, "后续用户应收到服务器世界对象基线");
    Require(secondWorld?.PlayerColonySites.Count == 1, "后续用户应收到玩家殖民地占用地块清单");
    Require(secondIdeos.Count == 1, "后续用户应收到管理员上传的完整文化包目录");
    Equal("IdeoFoundation_Deity", secondIdeos.Single().FoundationDefName, "文化目录应包含基础结构");
    Equal("粗犷文化", secondIdeos.Single().CultureLabel, "文化显示名应随服务器目录下发");
    Equal("UI/Ideoligions/Icons/Classic", secondIdeos.Single().IconPath, "文化图标路径应随服务器目录下发");
    Equal(9, secondWorld?.PlayerColonySites.Single().Tile, "玩家殖民地占用地块应保持服务器权威值");

    RegisterPlayerColonySitesResponse registeredColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-b",
            "colony-b",
            new[]
            {
                new PlayerColonySiteDto("user-b", "colony-b", "WorldObject_111", "0", 42, "玩家B殖民地")
            }));
    Require(registeredColonySites.Result.Accepted, "后续用户应能在快照上传前登记殖民地地块");
    Equal(1, registeredColonySites.AcceptedCount, "服务器应接受一个殖民地地块登记");
    Require(registeredColonySites.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-b" && site.Tile == 42) == true, "登记后世界配置应立即包含后续用户地块");

    RegisterPlayerColonySitesResponse duplicateOwnerColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-b",
            "colony-b",
            new[]
            {
                new PlayerColonySiteDto("user-b", "colony-b", "WorldObject_111", "0", 42, "玩家B殖民地")
            }));
    Require(duplicateOwnerColonySites.Result.Accepted, "同一用户殖民地重复登记同一地块应作为覆盖更新接受");

    RegisterPlayerColonySitesResponse sameUserRefreshedColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-b",
            "colony-b",
            new[]
            {
                new PlayerColonySiteDto("user-b", "colony-b", "WorldObject_201", "Map_21", 42, "玩家B刷新殖民地")
            },
            extensions: IdeoCatalogExtension(
                new WorldIdeoSummaryDto(
                    "owner:user-b/colony:colony-b/ideo:7",
                    "user-b",
                    "colony-b",
                    "world:test",
                    "7",
                    "玩家B文化",
                    "Rustic",
                    null,
                    null,
                    null,
                    null,
                    "IdeoFoundation_Deity",
                    "OutlanderCivil",
                    null,
                    null,
                    null,
                    null,
                    "<savedideo><meta><gameVersion>1.6.4633</gameVersion></meta><ideo /></savedideo>",
                    null,
                    null,
                    new[] { "Individualist" },
                    new[] { "IdeoRole_Leader" },
                    new[] { "Rustic" },
                    hidden: false,
                    initialPlayerIdeo: true,
                    memeCount: 1,
                    preceptCount: 1))));
    Require(sameUserRefreshedColonySites.Result.Accepted, "同一用户同一殖民地应能刷新同地块登记和扩展目录");
    Require(sameUserRefreshedColonySites.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-b" && site.Tile == 42) == true, "同地块刷新后世界配置应保持原地块");

    RegisterPlayerColonySitesResponse sameUserMovedColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-b",
            "colony-b",
            new[]
            {
                new PlayerColonySiteDto("user-b", "colony-b", "WorldObject_202", "Map_22", 43, "玩家B错误普通迁移")
            }));
    Require(!sameUserMovedColonySites.Result.Accepted, "同一用户同一殖民地换地块必须走搬迁确认，不能通过普通登记更新");
    Equal(ProtocolErrorCode.ServerRejected, sameUserMovedColonySites.Result.ErrorCode, "普通登记换地块应由服务器拒绝");

    RegisterPlayerColonySitesResponse sameUserSecondColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-b",
            "colony-b-2",
            new[]
            {
                new PlayerColonySiteDto("user-b", "colony-b-2", "WorldObject_203", "Map_23", 44, "玩家B第二殖民地")
            }));
    Require(!sameUserSecondColonySites.Result.Accepted, "已有殖民地的玩家不能再创建第二个殖民地");
    Equal(ProtocolErrorCode.ServerRejected, sameUserSecondColonySites.Result.ErrorCode, "同一玩家第二殖民地应由服务器拒绝");

    RegisterPlayerColonySitesResponse conflictingColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-c",
            "colony-c",
            new[]
            {
                new PlayerColonySiteDto("user-c", "colony-c", "WorldObject_300", "Map_30", 42, "玩家C重叠殖民地")
            }));
    Require(!conflictingColonySites.Result.Accepted, "不同用户殖民地不能登记到已占用地块");
    Equal(ProtocolErrorCode.ServerRejected, conflictingColonySites.Result.ErrorCode, "重叠殖民地地块应由服务器拒绝");
    Equal(0, conflictingColonySites.AcceptedCount, "重叠殖民地地块不应被接受");
    Require(conflictingColonySites.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-c" && site.Tile == 42) != true, "重叠拒绝后世界配置不应包含冲突地块");

    SaveSnapshotPackage userBInitialPackage = BuildFixturePackageFor(
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        playerSettlementTile: 42);
    SaveSnapshotIndex userBInitialIndex = userBInitialPackage.Index with
    {
        Extensions = IdeologySaveIndexExtensions(new[]
        {
            new IdeoSummary(
                "7",
                "owner:user-b/colony:colony-b/ideo:7",
                "玩家B文化",
                "Rustic",
                null,
                null,
                null,
                null,
                "IdeoFoundation_Deity",
                "OutlanderCivil",
                null,
                null,
                null,
                null,
                new[] { "Individualist" },
                new[] { "IdeoRole_Leader" },
                Array.Empty<IdeoPreceptSummary>(),
                new[] { "Rustic" },
                Hidden: false,
                InitialPlayerIdeo: true,
                MemeCount: 1,
                PreceptCount: 1)
        })
    };
    state.SnapshotStore.StoreLatest(new LatestSnapshotRecord(
        userBInitialPackage.Envelope.Identity,
        userBInitialPackage.Envelope,
        userBInitialIndex,
        DateTimeOffset.UnixEpoch));
    PrepareWorldSessionResponse thirdWorldSession = await client.PrepareWorldSessionAsync(
        SmokePrepareWorldSessionRequest("user-c", "colony-c"));
    Require(thirdWorldSession.Result.Accepted, $"第三个用户应能准备世界会话：{thirdWorldSession.Result.ErrorCode} {thirdWorldSession.Result.Message}");
    GetWorldConfigurationResponse thirdWorldResponse = await client.GetWorldConfigurationAsync(
        SmokeGetWorldConfigurationRequest("user-c", "colony-c"));
    Require(thirdWorldResponse.Result.Accepted, $"第三个用户应能下载世界配置：{thirdWorldResponse.Result.ErrorCode} {thirdWorldResponse.Result.Message}");
    WorldConfigurationDto? thirdWorld = thirdWorldResponse.WorldConfiguration;
    Equal("Cassandra", thirdWorld?.StorytellerDefName, "世界配置应持续保留服务器讲述人基线");
    Equal("Medium", thirdWorld?.DifficultyDefName, "世界配置应持续保留服务器难度基线");
    Require(thirdWorld?.PlayerColonySites.Count == 2, "世界配置应下发所有当前已占用殖民地地块");
    Require(thirdWorld!.PlayerColonySites.Any(site => site.UserId == "user-a" && site.Tile == 9), "占用清单应包含管理员殖民地");
    Require(thirdWorld.PlayerColonySites.Any(site => site.UserId == "user-b" && site.Tile == 42), "占用清单应包含已登记的其他玩家殖民地");
    IReadOnlyList<WorldIdeoSummaryDto> thirdIdeos = ReadIdeoExtension(thirdWorld);
    Require(thirdIdeos.Count == 2, "世界配置应合并管理员文化和玩家注册文化");
    Require(thirdIdeos.Any(ideo => ideo.OwnerUserId == "user-b" && !string.IsNullOrWhiteSpace(ideo.SavedIdeoPackageXml)), "玩家文化应携带完整文化包");

    RegisterPlayerColonySitesResponse summaryOnlyIdeoRefresh = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-b",
            "colony-b",
            new[]
            {
                new PlayerColonySiteDto("user-b", "colony-b", "WorldObject_201", "Map_21", 42, "玩家B刷新殖民地")
            },
            extensions: IdeoCatalogExtension(
                new WorldIdeoSummaryDto(
                    "owner:user-b/colony:colony-b/ideo:7",
                    "user-b",
                    "colony-b",
                    "snapshot-summary-only",
                    "7",
                    "玩家B文化已演进",
                    "Rustic",
                    null,
                    null,
                    null,
                    null,
                    "IdeoFoundation_Deity",
                    "OutlanderCivil",
                    null,
                    null,
                    null,
                    null,
                    savedIdeoPackageXml: null,
                    savedIdeoPackageSha256: null,
                    updatedAtGameTicks: 12_345,
                    new[] { "Individualist", "Collectivist" },
                    new[] { "IdeoRole_Leader", "TreeConnection_Desired" },
                    new[] { "Rustic" },
                    hidden: false,
                    initialPlayerIdeo: true,
                    memeCount: 2,
                    preceptCount: 2))));
    Require(summaryOnlyIdeoRefresh.Result.Accepted, "文化摘要刷新不应被拒绝");
    WorldIdeoSummaryDto refreshedUserBIdeo = ReadIdeoExtension(summaryOnlyIdeoRefresh.WorldConfiguration).Single(ideo => ideo.GlobalKey == "owner:user-b/colony:colony-b/ideo:7");
    Equal("玩家B文化已演进", refreshedUserBIdeo.Name, "文化摘要刷新应更新显示字段");
    Require(!string.IsNullOrWhiteSpace(refreshedUserBIdeo.SavedIdeoPackageXml), "文化摘要刷新不能覆盖已有完整文化包");

    SubmitAdminBaselineResponse rejectedBaseline = await client.SubmitAdminBaselineAsync(
        new SubmitAdminBaselineRequest(
            ProtocolApiVersion.Current,
            "user-b",
            "colony-b",
            DateTimeOffset.UtcNow,
            new[] { new StandardMarketValueDto("Gold", 6f) },
            Array.Empty<TrapClassificationDto>()));
    Require(!rejectedBaseline.Result.Accepted, "非管理员不应能覆盖管理员基线");
    Equal(ProtocolErrorCode.ServerRejected, rejectedBaseline.Result.ErrorCode, "非管理员基线提交应被服务器拒绝");

    LoginResponse login = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-a",
        "colony-a",
        "attacker-snapshot-before",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(login.Result.Accepted, $"登录应成功：{login.Result.ErrorCode} {login.Result.Message}");
    Require(!string.IsNullOrWhiteSpace(login.AuthToken), "登录应返回鉴权令牌");
    string userAAuthToken = login.AuthToken!;
    Require(login.EventQueue != null, "登录应返回事件队列");
    Require(login.WorldMapMarkers != null, "登录应返回世界地图标记");
    WorldMapMarkerDto configuredColonyMarker = login.WorldMapMarkers!.Markers.Single(marker =>
        marker.Kind == "TradeableColony"
        && marker.OwnerUserId == "user-a"
        && marker.WorldObjectId == "WorldObject_100");
    Equal(9, configuredColonyMarker.Tile, "登录大地图标记应包含管理员开局殖民地地块");
    Equal("Map_0", configuredColonyMarker.MapId, "登录大地图标记应包含管理员开局地图上下文");
    Require(
        login.WorldMapMarkers.Markers.Any(marker =>
            marker.Kind == "TradeableColony"
            && marker.OwnerUserId == "user-b"
            && marker.Tile == 42),
        "登录大地图标记应包含最新快照合并出的其他玩家殖民地地块");
    PrepareWorldSessionResponse userBRefreshWithoutSnapshot = await client.PrepareWorldSessionAsync(
        SmokePrepareWorldSessionRequest("user-b", "colony-b"));
    Require(userBRefreshWithoutSnapshot.Result.Accepted, $"已有快照玩家不带当前快照准备会话应成功：{userBRefreshWithoutSnapshot.Result.ErrorCode} {userBRefreshWithoutSnapshot.Result.Message}");
    ListPlayersResponse playersAfterLogin = await client.ListPlayersAsync(new ListPlayersRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-before"));
    Require(playersAfterLogin.Result.Accepted, "入服后应能拉取玩家列表");
    Require(playersAfterLogin.Players.Any(player => player.UserId == "user-a" && player.ColonyId == "colony-a"), "玩家列表应包含已登录玩家");
    Equal(
        "defender-snapshot-before",
        playersAfterLogin.Players.Single(player => player.UserId == "user-b").CurrentSnapshotId,
        "玩家列表应使用服务器最新快照填充对方当前快照");

    SaveSnapshotPackage uploadedPackage = BuildFixturePackage("attacker-snapshot-after");
    UploadSnapshotResponse upload = await client.UploadSnapshotAsync(new UploadSnapshotMetadataRequest(
        "upload:user-a:colony-a:attacker-snapshot-after",
        "user-a",
        "colony-a",
        "attacker-snapshot-after",
        SnapshotMetadata(uploadedPackage),
        authToken: userAAuthToken),
        uploadedPackage.Payload);
    Require(upload.Result.Accepted, "上传快照应通过服务器校验");
    Equal("attacker-snapshot-after", upload.AcceptedSnapshotId, "上传确认快照 ID");
    SaveSnapshotPackage uploadedConfirmationPackage = BuildFixturePackageForWithLineage(
        "user-a",
        "colony-a",
        "attacker-snapshot-after",
        previousSnapshotId: "attacker-snapshot-after",
        upload.NextLineageToken,
        gameTicks: (uploadedPackage.Envelope.GameTicks ?? 0) + 1);
    DiplomacyEventResponse allianceRequest = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
        "diplomacy:user-a:user-b:alliance",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", snapshotId: null),
        "AllianceRequest",
        "请求结盟。",
        DateTimeOffset.UtcNow.AddDays(3)));
    Require(allianceRequest.Result.Accepted, "目标快照为空时创建结盟请求仍应成功");
    AuthoritativeEvent allianceEvent = state.Ledger.Find(allianceRequest.EventId!)!;
    Require(allianceEvent.Payload is AllianceRequestEventPayload, "结盟请求应写入外交载荷");
    Equal(EventRejectionPolicy.RejectableByTarget, allianceEvent.RejectionPolicy, "结盟请求应可由目标拒绝");

    DiplomacyEventResponse acceptedAlliance = await client.RespondDiplomacyEventAsync(new RespondDiplomacyEventRequest(
        allianceRequest.EventId!,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        accepted: true,
        "同意结盟"));
    Require(acceptedAlliance.Result.Accepted, "接受结盟请求应成功");
    Equal("Ally", acceptedAlliance.RelationKind, "接受结盟后关系应为盟友");
    AuthoritativeEvent acceptedAllianceEvent = state.Ledger.Find(allianceRequest.EventId!)!;
    Equal(ServerEventStatus.AppliedToSnapshot, acceptedAllianceEvent.Status, "接受结盟后原事件应完成");
    Equal(TargetEventDecision.Accepted, acceptedAllianceEvent.TargetDecision, "接受结盟应记录目标决策");
    AuthoritativeEvent? allianceNotification = state.Ledger.Find(acceptedAlliance.NotificationEventId!);
    Require(allianceNotification?.Type == ServerEventType.ServerNotification, "接受结盟应通知发起方");
    var allianceNotificationPayload = (ServerNotificationEventPayload)allianceNotification!.Payload;
    Equal(allianceRequest.EventId!, allianceNotificationPayload.RelatedEventId, "接受结盟通知应携带原外交事件 ID");
    Equal(ServerEventType.AllianceRequest, allianceNotificationPayload.RelatedEventType, "接受结盟通知应携带原外交事件类型");
    Equal("user-b", allianceNotificationPayload.RelatedUserId, "接受结盟通知应携带接受方用户");
    Equal(true, allianceNotificationPayload.RelatedAccepted, "接受结盟通知应携带接受结果");
    ListPlayersResponse playersAfterAlliance = await client.ListPlayersAsync(new ListPlayersRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-after"));
    Equal(
        "Ally",
        playersAfterAlliance.Players.Single(player => player.UserId == "user-b").RelationKind,
        "玩家列表应返回当前视角下的盟友关系");
    DiplomacyEventResponse supportRequest = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
        "diplomacy:user-a:user-b:support-request",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        "SupportRequest",
        "请求派遣两名射击较强的殖民者支援边境。",
        null));
    Require(supportRequest.Result.Accepted, "盟友之间应能发送增援请求通知");
    Equal("Ally", supportRequest.RelationKind, "增援请求不应改变盟友关系");
    AuthoritativeEvent supportRequestEvent = state.Ledger.Find(supportRequest.EventId!)!;
    Equal(ServerEventType.ServerNotification, supportRequestEvent.Type, "增援请求应作为通知事件写入账本");
    Require(
        supportRequestEvent.Payload is ServerNotificationEventPayload supportPayload
            && supportPayload.Message.Contains("两名射击", StringComparison.Ordinal),
        "增援请求通知应携带自定义信息");

    DiplomacyEventResponse blockedRepeatSupportRequest = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
        "diplomacy:user-a:user-b:support-request-repeat",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        "SupportRequest",
        "重复请求应被冷却阻止。",
        null));
    Require(!blockedRepeatSupportRequest.Result.Accepted, "同一发送方对同一盟友的增援请求应有 15 分钟冷却");
    Equal(ProtocolErrorCode.ServerRejected, blockedRepeatSupportRequest.Result.ErrorCode, "增援请求冷却应由服务器拒绝");

    DiplomacyEventResponse reverseSupportRequest = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
        "diplomacy:user-b:user-a:support-request",
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        "SupportRequest",
        "反向请求不应受 user-a 到 user-b 的冷却影响。",
        null));
    Require(reverseSupportRequest.Result.Accepted, "接收方反向向发送方请求增援不应受发送方冷却影响");

    DiplomacyEventResponse blockedWarAgainstAlly = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
        "diplomacy:user-a:user-b:blocked-war-from-ally",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        "WarDeclaration",
        "盟友不能直接宣战。",
        null));
    Require(!blockedWarAgainstAlly.Result.Accepted, "盟友不能直接宣战");
    Equal(ProtocolErrorCode.ServerRejected, blockedWarAgainstAlly.Result.ErrorCode, "盟友直接宣战应由服务器拒绝");
    Equal("Ally", blockedWarAgainstAlly.RelationKind, "盟友直接宣战被拒绝时应返回当前盟友关系");

    DiplomacyEventResponse allianceCancellation = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
        "diplomacy:user-a:user-b:cancel-alliance",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        "AllianceCancellation",
        "撕毁盟约。",
        null));
    Require(allianceCancellation.Result.Accepted, "盟友应能先撕毁盟约");
    Equal("Neutral", allianceCancellation.RelationKind, "撕毁盟约后关系应变为中立");

    DiplomacyEventResponse warAfterCancellation = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
        "diplomacy:user-a:user-b:war-after-cancel-alliance",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        "WarDeclaration",
        "盟约已撕毁，现在宣战。",
        null));
    Require(warAfterCancellation.Result.Accepted, "撕毁盟约变为中立后应允许宣战");
    Equal("Hostile", warAfterCancellation.RelationKind, "宣战后关系应变为敌对");

    EventCreationResponse forcedGift = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-b:forced-001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/caravan:caravan-a/thing:wastepack", "Wastepack", 3) },
        "强行投递垃圾包。",
        targetContext: null,
        deliveryKind: "Forced"));
    Require(forcedGift.Result.Accepted, $"敌对且离线目标应允许强行投递：{forcedGift.Result.ErrorCode} {forcedGift.Result.Message}");
    AuthoritativeEvent forcedGiftEvent = state.Ledger.Find(forcedGift.EventId!)!;
    Require(forcedGiftEvent.Payload is ItemDeliveryEventPayload forcedGiftPayload && forcedGiftPayload.IsForcedDelivery, "强投礼物应写入强投载荷");
    Equal(EventRejectionPolicy.NotRejectable, forcedGiftEvent.RejectionPolicy, "强投礼物不可拒绝");

    RejectGiftResponse rejectedForcedGift = await client.RejectGiftAsync(new RejectGiftRequest(
        forcedGift.EventId!,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        "不想收垃圾。"));
    Require(!rejectedForcedGift.Result.Accepted, "强投礼物拒绝接口应被服务器拒绝");
    Equal(ProtocolErrorCode.ServerRejected, rejectedForcedGift.Result.ErrorCode, "强投礼物不可拒绝应返回服务器拒绝");

    EventCreationResponse repeatedForcedGift = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-b:forced-002",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/caravan:caravan-a/thing:wastepack-2", "Wastepack", 1) },
        "重复强投应被冷却阻止。",
        targetContext: null,
        deliveryKind: "Forced"));
    Require(!repeatedForcedGift.Result.Accepted, "同一发送方对同一目标不能连续强行投递");
    Equal(ProtocolErrorCode.ServerRejected, repeatedForcedGift.Result.ErrorCode, "强投冷却应由服务器拒绝");

    EventCreationResponse normalGiftToEnemy = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-b:normal-while-hostile",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/caravan:caravan-a/thing:meal-hostile", "MealFine", 1) },
        "敌对状态下仍可走普通送礼路径。"));
    Require(normalGiftToEnemy.Result.Accepted, "敌对目标仍应允许普通礼物路径");
    Equal(EventRejectionPolicy.RejectableByTarget, state.Ledger.Find(normalGiftToEnemy.EventId!)!.RejectionPolicy, "普通礼物仍可被目标拒绝");
    RaidCooldownStatus forcedGiftCooldownProjection = RaidCooldownProjector.BuildForDefender(
        "user-b",
        "colony-b",
        state.Ledger.ListAll());
    Require(
        !forcedGiftCooldownProjection.Records.Any(record => string.Equals(record.RaidEventId, forcedGift.EventId, StringComparison.Ordinal)),
        "强投成功不应使目标进入入侵后保护投影");

    EventCreationResponse emptyGift = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:self:empty",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        Array.Empty<ThingReferenceDto>(),
        "empty gift should fail",
        new EventTargetContextDto(null, "Map_999", null, "CenterNear")));
    Require(!emptyGift.Result.Accepted, "服务器不应创建空礼物");
    Equal(ProtocolErrorCode.ValidationFailed, emptyGift.Result.ErrorCode, "空礼物应被校验拒绝");

    EventCreationResponse selfGift = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:self:context",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:silver", "Silver", 25) },
        "self gift context smoke",
        new EventTargetContextDto(null, "Map_999", null, "CenterNear")));
    Require(selfGift.Result.Accepted, "创建自测礼物应成功");
    PullEventDetailsResponse selfGiftDetails = await client.PullEventDetailsAsync(new PullEventDetailsRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-after",
        new[] { selfGift.EventId! }));
    Require(selfGiftDetails.Result.Accepted, "拉取自测礼物详情应成功");
    EventDetailDto selfGiftDetail = selfGiftDetails.Events.Single(item => item.EventId == selfGift.EventId);
    Equal("Map_999", selfGiftDetail.TargetContext?.MapUniqueId, "自测礼物应使用客户端提交的当前地图");
    Equal("CenterNear", selfGiftDetail.TargetContext!.LandingMode, "自测礼物默认直接在当前地图附近落地");

    PullPendingEventsResponse pending = await client.PullPendingEventsAsync(new PullPendingEventsRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-after"));
    Require(pending.Result.Accepted, "拉取事件应成功");
    Require(pending.EventQueue.DeliveredUnconfirmed.Any(item => item.EventId == attackerLoss.EventId), "待确认队列应包含进攻方损失事件");

    PullEventDetailsResponse details = await client.PullEventDetailsAsync(new PullEventDetailsRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-after",
        new[] { attackerLoss.EventId }));
    Require(details.Result.Accepted, "拉取事件详情应成功");
    EventDetailDto attackerLossDetail = details.Events.Single(item => item.EventId == attackerLoss.EventId);
    Equal(ServerEventType.Raid, attackerLossDetail.EventType, "事件详情应返回事件类型");
    Equal(EventPayloadType.Raid, attackerLossDetail.PayloadType, "事件详情应返回载荷类型");
    Require(attackerLossDetail.PayloadSummary.Contains("AttackerLoss", StringComparison.Ordinal), "事件详情应返回载荷摘要");

    PullEventDetailsResponse hiddenDetails = await client.PullEventDetailsAsync(new PullEventDetailsRequest(
        "user-c",
        "colony-c",
        "third-party-snapshot",
        new[] { attackerLoss.EventId }));
    Require(!hiddenDetails.Result.Accepted, "其他玩家不能拉取不可见事件详情");
    Equal(ProtocolErrorCode.EventNotFound, hiddenDetails.Result.ErrorCode, "不可见事件不应泄露存在性");

    ConfirmEventApplicationResponse confirm = await client.ConfirmEventApplicationAsync(new ConfirmEventApplicationMetadataRequest(
        "confirm:attacker-loss-001:attacker-snapshot-after",
        attackerLoss.EventId,
        "raid-source-001",
        "user-a",
        "colony-a",
        "attacker-snapshot-before",
        SnapshotMetadata(uploadedConfirmationPackage),
        "AppliedWithSnapshotFallback",
        authToken: userAAuthToken),
        uploadedConfirmationPackage.Payload);
    Require(confirm.Result.Accepted, "确认事件应用应通过服务器校验");
    Equal("attacker-snapshot-after", confirm.AppliedSnapshotId, "事件应用快照 ID");
    Equal(ServerEventStatus.AppliedToSnapshot, state.Ledger.Find(attackerLoss.EventId)!.Status, "账本事件应被标记为已应用");

    LoginResponse userBLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(userBLogin.Result.Accepted, $"第二玩家登录应成功：{userBLogin.Result.ErrorCode} {userBLogin.Result.Message}");
    Require(!string.IsNullOrWhiteSpace(userBLogin.SessionId), "第二玩家登录应返回会话");
    Require(!string.IsNullOrWhiteSpace(userBLogin.AuthToken), "第二玩家登录应返回鉴权令牌");
    string userBAuthToken = userBLogin.AuthToken!;
    LoginResponse duplicatePendingLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(!duplicatePendingLogin.Result.Accepted, "已登录但未建立在线流的玩家不应再次触发登录");
    Equal(ProtocolErrorCode.ServerRejected, duplicatePendingLogin.Result.ErrorCode, "待驻留会话重复登录应由服务器拒绝");
    Require(duplicatePendingLogin.SessionId is null, "待驻留会话重复登录不应获得新会话");

    using var presenceCancellation = new CancellationTokenSource();
    Task<MaintainPresenceResponse> userBPresence = client.MaintainPresenceAsync(new MaintainPresenceRequest(
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        userBLogin.SessionId!),
        presenceCancellation.Token);
    await WaitUntilAsync(
        () => state.OnlinePresence.IsUserOnline("user-b"),
        "在线驻留连接应立即把 user-b 标记为在线");
    LoginResponse duplicateOnlineLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(!duplicateOnlineLogin.Result.Accepted, "在线玩家不应能再次触发登录");
    Equal(ProtocolErrorCode.ServerRejected, duplicateOnlineLogin.Result.ErrorCode, "在线玩家重复登录应由服务器拒绝");
    Require(duplicateOnlineLogin.SessionId is null, "在线玩家重复登录不应获得新会话");
    MaintainPresenceResponse duplicatePresence = await client.MaintainPresenceAsync(new MaintainPresenceRequest(
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        "duplicate-smoke-session"));
    Require(!duplicatePresence.Result.Accepted, "在线玩家不应能建立第二条在线驻留连接");
    Equal(ProtocolErrorCode.ServerRejected, duplicatePresence.Result.ErrorCode, "重复在线驻留应由服务器拒绝");
    ListPlayersResponse playersWithUserB = await client.ListPlayersAsync(new ListPlayersRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-after"));
    Require(playersWithUserB.Players.Any(player => player.UserId == "user-b" && player.Online), "玩家列表应显示在线的第二客户端");

    long knownUserBNotificationVersion = state.EventNotifications.GetVersion("user-b");
    Task<WaitForEventsResponse> waitForGift = client.WaitForEventsAsync(new WaitForEventsRequest(
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        knownNotificationVersion: knownUserBNotificationVersion,
        timeoutSeconds: 10));
    await Task.Delay(100);

    EventCreationResponse gift = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-b:001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:meal", "MealFine", 3) },
        "smoke gift"));
    Require(gift.Result.Accepted, "创建礼物应成功");
    Require(state.Ledger.Find(gift.EventId!)?.Payload is ItemDeliveryEventPayload, "礼物事件应进入账本");
    Equal(ServerEventStatus.ReadyForImmediateDelivery, state.Ledger.Find(gift.EventId!)!.Status, "在线目标的礼物应标记为即时下发");

    WaitForEventsResponse giftNotification = await waitForGift;
    Require(giftNotification.Result.Accepted, "等待在线事件应成功");
    Require(giftNotification.Changed, "在线等待应被新礼物唤醒");
    Require(giftNotification.NotificationVersion > 0, "在线通知版本应递增");
    Require(giftNotification.EventQueue.WaitingForUserChoice.Any(item => item.EventId == gift.EventId), "在线等待返回队列应包含礼物事件");
    Equal(ProtocolDeliverySemantics.OnlineImmediate, giftNotification.EventQueue.WaitingForUserChoice.Single(item => item.EventId == gift.EventId).DeliverySemantics, "在线礼物应标记为即时语义");

    EventCreationResponse giftToConfirm = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-b:confirm",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:packaged-meal", "MealSurvivalPack", 2) },
        "smoke gift confirm"));
    Require(giftToConfirm.Result.Accepted, "创建用于确认的礼物应成功");

    PullEventDetailsResponse giftDetails = await client.PullEventDetailsAsync(new PullEventDetailsRequest(
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        new[] { giftToConfirm.EventId! }));
    Require(giftDetails.Result.Accepted, "拉取待确认礼物详情应成功");
    Equal(ServerEventStatus.DeliveredToClient, state.Ledger.Find(giftToConfirm.EventId!)!.Status, "拉取详情应把礼物绑定到基线快照");
    Equal("defender-snapshot-before", state.Ledger.Find(giftToConfirm.EventId!)!.DeliveredToSnapshotId, "礼物下发快照应绑定当前快照");

    SaveSnapshotPackage giftConfirmedPackage = BuildFixturePackageForWithLineage(
        "user-b",
        "colony-b",
        "defender-snapshot-after-gift",
        previousSnapshotId: "defender-snapshot-before",
        token: null,
        gameTicks: (userBInitialPackage.Envelope.GameTicks ?? 0) + 1,
        playerSettlementTile: 42);
    ConfirmEventApplicationResponse giftConfirm = await client.ConfirmEventApplicationAsync(new ConfirmEventApplicationMetadataRequest(
        "confirm:gift:user-b:defender-snapshot-after-gift",
        giftToConfirm.EventId!,
        sourceEventId: null,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        SnapshotMetadata(giftConfirmedPackage),
        "ItemDeliveryAnchored",
        authToken: userBAuthToken),
        giftConfirmedPackage.Payload);
    Require(
        giftConfirm.Result.Accepted,
        $"礼物投递确认应通过服务器校验：{giftConfirm.Result.ErrorCode} {giftConfirm.Result.Message} {giftConfirm.ServerValidationResult}");
    Equal("defender-snapshot-after-gift", giftConfirm.AppliedSnapshotId, "礼物应用快照 ID");
    Equal(ServerEventStatus.AppliedToSnapshot, state.Ledger.Find(giftToConfirm.EventId!)!.Status, "礼物账本事件应被标记为已应用");

    PullPendingEventsResponse giftQueueAfterConfirm = await client.PullPendingEventsAsync(new PullPendingEventsRequest(
        "user-b",
        "colony-b",
        "defender-snapshot-after-gift"));
    Require(!giftQueueAfterConfirm.EventQueue.WaitingForUserChoice.Any(item => item.EventId == giftToConfirm.EventId), "已确认礼物不应继续等待目标选择");
    Require(!giftQueueAfterConfirm.EventQueue.DeliveredUnconfirmed.Any(item => item.EventId == giftToConfirm.EventId), "已确认礼物不应继续等待快照确认");

    presenceCancellation.Cancel();
    await WaitUntilAsync(
        () => !state.OnlinePresence.IsUserOnline("user-b"),
        "在线驻留连接断开后应立即把 user-b 标记为离线");

    try
    {
        await userBPresence;
    }
    catch (OperationCanceledException)
    {
    }

    Task<WaitForEventsResponse> waitWithoutPresence = client.WaitForEventsAsync(new WaitForEventsRequest(
        "user-c",
        "colony-c",
        "third-party-snapshot",
        knownNotificationVersion: 0,
        timeoutSeconds: 10));
    await Task.Delay(100);
    EventCreationResponse offlineGiftWhileWaiting = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-c:offline-waiting",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-c", "colony-c", "third-party-snapshot"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:beer", "Beer", 1) },
        "smoke offline waiting gift"));
    Require(offlineGiftWhileWaiting.Result.Accepted, "给等待事件但未驻留在线的玩家创建礼物应成功");
    Equal(ServerEventStatus.PendingOfflineDelivery, state.Ledger.Find(offlineGiftWhileWaiting.EventId!)!.Status, "事件等待通道不能单独把玩家判定为在线");
    WaitForEventsResponse offlineWaitNotification = await waitWithoutPresence;
    Require(offlineWaitNotification.Changed, "离线事件仍应唤醒事件等待通道刷新队列");

    LoginResponse userDLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-d",
        "colony-d",
        "ws-snapshot",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(userDLogin.Result.Accepted, $"WS 玩家登录应成功：{userDLogin.Result.ErrorCode} {userDLogin.Result.Message}");
    Require(!string.IsNullOrWhiteSpace(userDLogin.SessionId), "WS 玩家登录应返回会话");
    Require(!string.IsNullOrWhiteSpace(userDLogin.AuthToken), "WS 玩家登录应返回鉴权令牌");
    string userDAuthToken = userDLogin.AuthToken!;
    string userDStreamUrl = "session/stream?userId=user-d&colonyId=colony-d&currentSnapshotId=ws-snapshot&knownNotificationVersion=0&knownWorldConfigurationVersion=0&sessionId="
        + Uri.EscapeDataString(userDLogin.SessionId!);

    using var wsCancellation = new CancellationTokenSource();
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(ToWebSocketUri(httpClient.BaseAddress!, userDStreamUrl), wsCancellation.Token);
    await WaitUntilAsync(
        () => state.OnlinePresence.IsUserOnline("user-d"),
        "WS 会话建立后应立即把 user-d 标记为在线");
    using (var duplicateWs = new ClientWebSocket())
    {
        bool duplicateRejected = false;
        try
        {
            await duplicateWs.ConnectAsync(ToWebSocketUri(httpClient.BaseAddress!, userDStreamUrl), CancellationToken.None);
        }
        catch (WebSocketException)
        {
            duplicateRejected = true;
        }

        Require(duplicateRejected, "在线玩家不应能建立第二条 WS 会话");
    }

    string wsPresence = await ReadWebSocketEventAsync(ws);
    Require(wsPresence.Contains("\"eventName\":\"presence\"", StringComparison.Ordinal), "WS 首条事件应确认在线");

    Task<string> wsLedgerChanged = ReadWebSocketEventAsync(ws);
    EventCreationResponse wsGift = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-d:ws",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-d", "colony-d", "ws-snapshot"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:corn", "Corn", 5) },
        "smoke ws gift"));
    Require(wsGift.Result.Accepted, "给 WS 在线玩家创建礼物应成功");
    Equal(ServerEventStatus.ReadyForImmediateDelivery, state.Ledger.Find(wsGift.EventId!)!.Status, "WS 在线玩家的礼物应即时下发");
    string wsNotification = await wsLedgerChanged;
    Require(wsNotification.Contains("\"eventName\":\"ledgerChanged\"", StringComparison.Ordinal), "WS 应立即收到事件账本更新通知");
    Require(wsNotification.Contains("NotificationVersion", StringComparison.Ordinal), "WS 通知应包含通知版本");

    Task<string> wsWorldConfigurationChanged = ReadWebSocketEventAsync(ws);
    RegisterPlayerColonySitesResponse wsWorldConfigurationRegistration = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-e",
            "colony-e",
            new[]
            {
                new PlayerColonySiteDto("user-e", "colony-e", "WorldObject_301", "Map_301", 77, "玩家E殖民地")
            },
            extensions: IdeoCatalogExtension(
                new WorldIdeoSummaryDto(
                    "owner:user-e/colony:old-client-value/ideo:12",
                    "user-e",
                    "old-client-value",
                    "old-world",
                    "12",
                    "玩家E文化",
                    "Rustic",
                    null,
                    null,
                    null,
                    null,
                    "IdeoFoundation_Deity",
                    "OutlanderCivil",
                    null,
                    null,
                    null,
                    null,
                    "<savedideo><meta><gameVersion>1.6.4633</gameVersion></meta><ideo /></savedideo>",
                    null,
                    null,
                    new[] { "Collectivist" },
                    new[] { "TreeConnection_Desired" },
                    Array.Empty<string>(),
                    hidden: false,
                    initialPlayerIdeo: true,
                    memeCount: 1,
                    preceptCount: 1))));
    Require(wsWorldConfigurationRegistration.Result.Accepted, "其他玩家登记文化应成功");
    string wsWorldConfigurationNotification = await wsWorldConfigurationChanged;
    Require(wsWorldConfigurationNotification.Contains("\"eventName\":\"worldConfigurationChanged\"", StringComparison.Ordinal), "WS 应立即收到世界目录更新通知");
    Require(wsWorldConfigurationNotification.Contains("WorldConfigurationVersion", StringComparison.Ordinal), "世界目录通知应包含目录版本");
    PrepareWorldSessionResponse refreshedWorldSession = await client.PrepareWorldSessionAsync(
        SmokePrepareWorldSessionRequest("user-d", "colony-d"));
    Require(refreshedWorldSession.Result.Accepted, $"在线玩家刷新会话应成功：{refreshedWorldSession.Result.ErrorCode} {refreshedWorldSession.Result.Message}");
    GetWorldConfigurationResponse refreshedWorldResponse = await client.GetWorldConfigurationAsync(
        SmokeGetWorldConfigurationRequest("user-d", "colony-d"));
    Require(refreshedWorldResponse.Result.Accepted, $"在线玩家应能刷新世界配置：{refreshedWorldResponse.Result.ErrorCode} {refreshedWorldResponse.Result.Message}");
    Require(ReadIdeoExtension(refreshedWorldResponse.WorldConfiguration).Any(ideo =>
        ideo.OwnerUserId == "user-e"
        && ideo.OwnerColonyId == "colony-e"
        && ideo.Name == "玩家E文化") == true, "在线玩家刷新世界配置后应看到新玩家文化，且服务器应重写文化所属殖民地");
    wsCancellation.Cancel();
    ws.Dispose();
    await WaitUntilAsync(
        () => !state.OnlinePresence.IsUserOnline("user-d"),
        "WS 会话断开后应立即把 user-d 标记为离线");

    state.Ledger.MarkDelivered(
        wsGift.EventId!,
        "ws-snapshot",
        DateTimeOffset.UtcNow.AddMinutes(-10));
    PullPendingEventsResponse expiredDeliveredQueue = await client.PullPendingEventsAsync(new PullPendingEventsRequest(
        "user-d",
        "colony-d",
        "ws-next-snapshot"));
    Equal(ServerEventStatus.PendingOfflineDelivery, state.Ledger.Find(wsGift.EventId!)!.Status, "过期且未确认的在线礼物应回退为离线待处理");
    Require(expiredDeliveredQueue.EventQueue.WaitingForUserChoice.Any(item => item.EventId == wsGift.EventId), "回退后的礼物应重新出现在离线事件队列");
    PullEventDetailsResponse redeliveredWsGift = await client.PullEventDetailsAsync(new PullEventDetailsRequest(
        "user-d",
        "colony-d",
        "ws-next-snapshot",
        new[] { wsGift.EventId! }));
    Require(redeliveredWsGift.Result.Accepted, "回退后的礼物应可重新下发详情");
    Equal("ws-next-snapshot", state.Ledger.Find(wsGift.EventId!)!.DeliveredToSnapshotId, "重新下发应绑定新的当前快照");

    AuthoritativeEvent onlineOnlyNotice = AuthoritativeEventFactory.Create(
        ServerEventType.ServerNotification,
        new EventParty("server"),
        new EventParty("user-d", "colony-d"),
        "server:shutdown:online-only",
        targetOnline: true,
        new ServerNotificationEventPayload(
            "shutdown",
            "服务器即将关闭",
            "服务器即将关闭，未在线收到则不补发。",
            ServerNotificationSeverity.Warning,
            FromAdministrator: false,
            OnlineOnly: true),
        DateTimeOffset.UtcNow.AddMinutes(-10));
    state.Ledger.Append(onlineOnlyNotice);
    state.Ledger.MarkDelivered(
        onlineOnlyNotice.EventId,
        "ws-snapshot",
        DateTimeOffset.UtcNow.AddMinutes(-10));
    PullPendingEventsResponse onlineOnlyQueue = await client.PullPendingEventsAsync(new PullPendingEventsRequest(
        "user-d",
        "colony-d",
        "ws-next-snapshot"));
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(onlineOnlyNotice.EventId)!.Status, "在线一次性服务器通知过期后应取消而不是转离线");
    Require(!onlineOnlyQueue.EventQueue.DirectlyProcessable
            .Concat(onlineOnlyQueue.EventQueue.WaitingForUserChoice)
            .Concat(onlineOnlyQueue.EventQueue.DeliveredUnconfirmed)
            .Concat(onlineOnlyQueue.EventQueue.Conflicts)
            .Concat(onlineOnlyQueue.EventQueue.Rejected)
            .Any(item => item.EventId == onlineOnlyNotice.EventId),
        "在线一次性服务器通知不应进入离线队列");

    RejectGiftResponse rejectedGift = await client.RejectGiftAsync(new RejectGiftRequest(
        gift.EventId!,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        "smoke reject"));
    Require(rejectedGift.Result.Accepted, "拒绝礼物应成功");
    Require(rejectedGift.ReturnEventCreated, "首次拒绝应创建退回事件");
    Require(state.Ledger.Find(rejectedGift.ReturnEventId!)?.Type == ServerEventType.ItemDelivery, "拒绝礼物应创建退回事件");

    RejectGiftResponse duplicateRejectGift = await client.RejectGiftAsync(new RejectGiftRequest(
        gift.EventId!,
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        "smoke reject duplicate"));
    Equal(ProtocolErrorCode.DuplicateRequest, duplicateRejectGift.Result.ErrorCode, "重复拒绝应返回幂等结果");
    Equal(rejectedGift.ReturnEventId, duplicateRejectGift.ReturnEventId, "重复拒绝应返回既有退回事件");

    RejectGiftResponse rejectedByOtherUser = await client.RejectGiftAsync(new RejectGiftRequest(
        gift.EventId!,
        "user-c",
        "colony-c",
        "other-snapshot",
        "not visible"));
    Require(!rejectedByOtherUser.Result.Accepted, "非目标用户不能拒绝礼物");
    Equal(ProtocolErrorCode.EventNotFound, rejectedByOtherUser.Result.ErrorCode, "非目标用户不应得知事件存在");

    string supportPawnGlobalKey = "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/caravan:Caravan_9/pawn:Pawn_1";
    var supportPawnReference = new CrossMapPawnReferenceDto(
        supportPawnGlobalKey,
        "attacker-snapshot-after",
        "测试支援者",
        dead: false,
        "PlayerColony");
    const string supportScribeXml = "<saveable Class=\"Pawn\"><id>Pawn_1</id><kindDef>Colonist</kindDef></saveable>";
    var supportPawnPackage = new PawnExchangePackageDto(
        1,
        supportPawnReference,
        new PawnExchangeIdentityDto("Human", "Colonist", "PlayerColony", "Female"),
        new PawnExchangeAppearanceDto(
            "测试支援者",
            "Female",
            "Female_Average_Normal",
            "Shaved",
            beardDef: null,
            skinColor: null,
            hairColor: null),
        new PawnExchangeStatusDto(
            dead: false,
            biologicalAgeTicks: 90000000,
            chronologicalAgeTicks: 90000000,
            deathCauseDef: null,
            healthState: "Mobile"),
        Array.Empty<PawnExchangeEquipmentItemDto>(),
        Array.Empty<PawnExchangeEquipmentItemDto>(),
        Array.Empty<PawnExchangeRelationshipStubDto>(),
        new PawnScribePayloadDto(
            supportScribeXml,
            SafePawnExchangeSerializer.ComputeScribeXmlSha256(supportScribeXml),
            Array.Empty<PawnScribePawnReferenceReplacementDto>()));
    LatestSnapshotRecord supportTargetSnapshot = state.SnapshotStore.GetLatest("user-b", "colony-b")!;
    string supportTargetSnapshotId = supportTargetSnapshot.Identity.SnapshotId
        ?? throw new InvalidOperationException("支援目标最新快照缺少快照 ID。");
    MapSummary supportTargetMap = supportTargetSnapshot.Index.Maps.First(map => !string.IsNullOrWhiteSpace(map.UniqueId));
    WorldObjectSummary? supportTargetWorldObject = supportTargetSnapshot.Index.WorldObjects.FirstOrDefault(worldObject =>
        string.Equals(worldObject.UniqueLoadId, supportTargetMap.ParentWorldObjectId, StringComparison.Ordinal));
    int supportTargetTile = int.TryParse(supportTargetWorldObject?.Tile, out int parsedSupportTargetTile)
        ? parsedSupportTargetTile
        : 42;
    EventCreationResponse supportPawn = await client.CreateSupportPawnAsync(new CreateSupportPawnRequest(
        "support:user-a:user-b:001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", supportTargetSnapshotId),
        supportPawnGlobalKey,
        "attacker-snapshot-after",
        "测试支援者",
        temporaryControl: true,
        expectedReturnAtUtc: null,
        supportPawnReference,
        supportPawnPackage,
        new EventTargetContextDto(supportTargetMap.ParentWorldObjectId, supportTargetMap.UniqueId, supportTargetTile, "MapEdge"),
        sourceTile: 9,
        sourceCaravanLoadId: "Caravan_9",
        permanentSupport: false,
        supportDurationDays: 7,
        expiresAtGameTicks: 420000,
        autoReturnOnSettlement: false));
    Require(supportPawn.Result.Accepted, $"创建支援 pawn 应成功：{supportPawn.Result.ErrorCode} {supportPawn.Result.Message}");
    Require(state.Ledger.Find(supportPawn.EventId!)?.Payload is SupportPawnEventPayload, "支援 pawn 应进入账本");
    Equal(EventRejectionPolicy.RejectableByTarget, state.Ledger.Find(supportPawn.EventId!)!.RejectionPolicy, "临时控制支援 pawn 应可被目标拒绝");

    RejectSupportPawnResponse rejectedSupport = await client.RejectSupportPawnAsync(new RejectSupportPawnRequest(
        supportPawn.EventId!,
        "user-b",
        "colony-b",
        supportTargetSnapshotId,
        "smoke support reject"));
    Require(rejectedSupport.Result.Accepted, "拒绝支援 pawn 应成功");
    Require(rejectedSupport.ReturnEventCreated, "首次拒绝支援 pawn 应创建退回事件");
    AuthoritativeEvent rejectedSupportEvent = state.Ledger.Find(supportPawn.EventId!)!;
    Equal(ServerEventStatus.RejectedByTarget, rejectedSupportEvent.Status, "原支援 pawn 应标记为目标拒绝");
    AuthoritativeEvent supportReturn = state.Ledger.Find(rejectedSupport.ReturnEventId!)!;
    Equal(ServerEventType.SupportPawn, supportReturn.Type, "拒绝支援 pawn 应创建支援退回事件");
    Equal("user-a", supportReturn.Target.UserId, "支援退回事件应发送给原发送方");
    Equal(EventRejectionPolicy.NotRejectable, supportReturn.RejectionPolicy, "支援退回事件不可再次拒绝");
    Equal(9, supportReturn.TargetContext?.Tile, "支援退回事件应落在原发送地块");
    var supportReturnPayload = (SupportPawnEventPayload)supportReturn.Payload;
    Require(supportReturnPayload.ReturnToSender, "支援退回载荷应标记为退回发送方");
    Require(!supportReturnPayload.TemporaryControl, "支援退回载荷不应再是临时控制请求");
    Equal(9, supportReturnPayload.SourceTile, "支援退回载荷应保留原出发地块");
    Equal(SafePawnExchangeSerializer.ComputeScribeXmlSha256(supportScribeXml), supportReturnPayload.PawnPackage?.Scribe?.XmlSha256, "支援退回应保留完整 Scribe 哈希");

    RejectSupportPawnResponse duplicateRejectedSupport = await client.RejectSupportPawnAsync(new RejectSupportPawnRequest(
        supportPawn.EventId!,
        "user-b",
        "colony-b",
        supportTargetSnapshotId,
        "smoke support reject duplicate"));
    Equal(ProtocolErrorCode.DuplicateRequest, duplicateRejectedSupport.Result.ErrorCode, "重复拒绝支援 pawn 应返回幂等结果");
    Equal(rejectedSupport.ReturnEventId, duplicateRejectedSupport.ReturnEventId, "重复拒绝支援 pawn 应返回既有退回事件");

    string finishingSupportPawnGlobalKey = "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/caravan:Caravan_9/pawn:Pawn_2";
    var finishingSupportPawnReference = new CrossMapPawnReferenceDto(
        finishingSupportPawnGlobalKey,
        "attacker-snapshot-after",
        "测试支援者二号",
        dead: false,
        "PlayerColony");
    const string finishingSupportScribeXml = "<saveable Class=\"Pawn\"><id>Pawn_2</id><kindDef>Colonist</kindDef></saveable>";
    var finishingSupportPawnPackage = new PawnExchangePackageDto(
        1,
        finishingSupportPawnReference,
        new PawnExchangeIdentityDto("Human", "Colonist", "PlayerColony", "Female"),
        new PawnExchangeAppearanceDto(
            "测试支援者二号",
            "Female",
            "Female_Average_Normal",
            "Shaved",
            beardDef: null,
            skinColor: null,
            hairColor: null),
        new PawnExchangeStatusDto(
            dead: false,
            biologicalAgeTicks: 90000000,
            chronologicalAgeTicks: 90000000,
            deathCauseDef: null,
            healthState: "Mobile"),
        Array.Empty<PawnExchangeEquipmentItemDto>(),
        Array.Empty<PawnExchangeEquipmentItemDto>(),
        Array.Empty<PawnExchangeRelationshipStubDto>(),
        new PawnScribePayloadDto(
            finishingSupportScribeXml,
            SafePawnExchangeSerializer.ComputeScribeXmlSha256(finishingSupportScribeXml),
            Array.Empty<PawnScribePawnReferenceReplacementDto>()));
    EventCreationResponse finishingSupport = await client.CreateSupportPawnAsync(new CreateSupportPawnRequest(
        "support:user-a:user-b:finish-001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", supportTargetSnapshotId),
        finishingSupportPawnGlobalKey,
        "attacker-snapshot-after",
        "测试支援者二号",
        temporaryControl: true,
        expectedReturnAtUtc: null,
        finishingSupportPawnReference,
        finishingSupportPawnPackage,
        new EventTargetContextDto(supportTargetMap.ParentWorldObjectId, supportTargetMap.UniqueId, supportTargetTile, "MapEdge"),
        sourceTile: 9,
        sourceCaravanLoadId: "Caravan_9",
        permanentSupport: false,
        supportDurationDays: 7,
        expiresAtGameTicks: 420000,
        autoReturnOnSettlement: true));
    Require(finishingSupport.Result.Accepted, "创建用于结束的支援 pawn 应成功");
    state.Ledger.ChangeStatus(finishingSupport.EventId!, ServerEventStatus.AppliedToSnapshot);
    FinishSupportPawnResponse finishedSupport = await client.FinishSupportPawnAsync(new FinishSupportPawnRequest(
        "support-finish:user-b:colony-b:finish-001",
        finishingSupport.EventId!,
        "user-b",
        "colony-b",
        supportTargetSnapshotId,
        "Expired",
        finishingSupportPawnGlobalKey,
        "测试支援者二号",
        pawnDead: false,
        finishingSupportPawnPackage));
    Require(finishedSupport.Result.Accepted, "结束支援 pawn 应成功");
    Require(!string.IsNullOrWhiteSpace(finishedSupport.ReturnEventId), "结束支援 pawn 应生成退回事件");
    Equal(ServerEventStatus.AppliedToSnapshot, state.Ledger.Find(finishingSupport.EventId!)!.Status, "结束后的原支援 pawn 应标记已应用");
    AuthoritativeEvent finishedReturn = state.Ledger.Find(finishedSupport.ReturnEventId!)!;
    Equal("user-a", finishedReturn.Target.UserId, "结束支援的退回事件应发送给原所有者");
    var finishedReturnPayload = (SupportPawnEventPayload)finishedReturn.Payload;
    Require(finishedReturnPayload.ReturnToSender, "结束支援退回载荷应标记为退回发送方");
    Equal("Expired", finishedReturnPayload.ReturnReason, "结束支援退回载荷应记录返回原因");
    Equal(SafePawnExchangeSerializer.ComputeScribeXmlSha256(finishingSupportScribeXml), finishedReturnPayload.PawnPackage?.Scribe?.XmlSha256, "结束支援退回应携带最新 pawn Scribe 包");

    EventCreationResponse duplicateGift = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-b:001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:meal", "MealFine", 3) },
        "smoke gift duplicate"));
    Equal(ProtocolErrorCode.DuplicateRequest, duplicateGift.Result.ErrorCode, "重复礼物请求应返回幂等结果");
    Equal(gift.EventId, duplicateGift.EventId, "重复礼物请求应返回既有事件");

    AuthoritativeEvent oldTrade = AuthoritativeEventFactory.Create(
        ServerEventType.Trade,
        new EventParty("user-a", "colony-a"),
        new EventParty("server"),
        "trade:user-a:expired",
        targetOnline: false,
        new TradeEventPayload(
            "trade:user-a:expired",
            TradeStage.MarketOrder,
            new[] { new EventThingReference("thing-expired-steel", "Steel", 50) },
            new[] { new EventThingReference("thing-expired-med", "MedicineIndustrial", 2) },
            FeeSilver: 5),
        DateTimeOffset.UtcNow - TimeSpan.FromDays(8));
    state.Ledger.Append(oldTrade);
    AuthoritativeEvent oldTradeMemo = AuthoritativeEventFactory.Create(
        ServerEventType.Trade,
        new EventParty("user-b", "colony-b"),
        new EventParty("user-b", "colony-b"),
        "trade:user-b:expired-memo",
        targetOnline: false,
        new TradeEventPayload(
            oldTrade.EventId,
            TradeStage.AcceptedMemo,
            ((TradeEventPayload)oldTrade.Payload).OfferedItems,
            ((TradeEventPayload)oldTrade.Payload).RequestedItems,
            FeeSilver: 5,
            AcceptedByUserId: "user-b"),
        DateTimeOffset.UtcNow - TimeSpan.FromDays(7));
    state.Ledger.Append(oldTradeMemo);
    ListTradeOrdersResponse expiredSweep = await client.ListTradeOrdersAsync(new ListTradeOrdersRequest(
        "user-b",
        "colony-b",
        "defender-snapshot-before"));
    Require(expiredSweep.Result.Accepted, "交易市场查询应触发过期交易单扫描");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(oldTrade.EventId)!.Status, "过期交易单应按撤单进入取消状态");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(oldTradeMemo.EventId)!.Status, "过期交易单应关闭接单备忘录");
    AuthoritativeEvent expiredReturn = state.Ledger.ListForUser("user-a")
        .Single(evt => evt.Type == ServerEventType.ItemDelivery
            && evt.IdempotencyKey == $"trade-expired-owner-return:{oldTrade.EventId}");
    ItemDeliveryEventPayload expiredReturnPayload = (ItemDeliveryEventPayload)expiredReturn.Payload;
    Equal(ItemDeliveryPurpose.TradeExpiredOwnerReturn, expiredReturnPayload.Purpose, "过期交易单应生成发布者托管物退回事件");
    Require(expiredReturnPayload.Items.Any(item => item.Def == "Steel" && item.StackCount == 50), "过期退回应包含发布者上架物品");
    string expiredAcceptorNotificationId = $"trade-expired:{oldTrade.EventId}:user-b:colony-b";
    IReadOnlyList<ServerNotificationEventPayload> expiredAcceptorNotifications = state.Ledger.ListForUser("user-b")
        .Where(evt => evt.Type == ServerEventType.ServerNotification)
        .Select(evt => evt.Payload)
        .OfType<ServerNotificationEventPayload>()
        .ToList();
    Require(
        expiredAcceptorNotifications.Any(notification =>
            string.Equals(notification.NotificationId, expiredAcceptorNotificationId, StringComparison.Ordinal)
            && notification.Severity == ServerNotificationSeverity.Warning
            && !notification.FromAdministrator),
        "过期交易单应给接单玩家发送服务器通知事件；当前通知="
        + string.Join(", ", expiredAcceptorNotifications.Select(notification => notification.NotificationId)));

    EventCreationResponse obsoleteTrade = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-a:baseline-invalidated",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:meal-obsolete", "MealFine", 1) },
        new[] { new ThingReferenceDto("market:any/thing:silver", "Silver", 10) },
        feeSilver: 1,
        allowSelfPickup: true,
        allowServerDropPod: false));
    Require(obsoleteTrade.Result.Accepted, "基线失效测试交易单应先允许创建");
    AcceptTradeOrderResponse obsoleteAccepted = await client.AcceptTradeOrderAsync(new AcceptTradeOrderRequest(
        "trade:user-b:accept:baseline-invalidated",
        obsoleteTrade.EventId!,
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        postagePaidByAcceptor: false));
    Require(obsoleteAccepted.Result.Accepted, "基线失效测试交易单应允许接单");
    SubmitAdminBaselineResponse baselineWithoutMeal = await client.SubmitAdminBaselineAsync(
        new SubmitAdminBaselineRequest(
            ProtocolApiVersion.Current,
            "user-a",
            "colony-a",
            DateTimeOffset.UtcNow,
            new[]
            {
                new StandardMarketValueDto("Silver", 1f),
                new StandardMarketValueDto("Wastepack", 0f),
                new StandardMarketValueDto("Steel", 1f),
                new StandardMarketValueDto("WoodLog", 1f),
                new StandardMarketValueDto("Cloth", 1f),
                new StandardMarketValueDto("ComponentIndustrial", 12f)
            },
            Array.Empty<TrapClassificationDto>()));
    Require(baselineWithoutMeal.Result.Accepted, "管理员更新物品基线应成功");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(obsoleteTrade.EventId!)!.Status, "物品基线缺失的交易单应自动取消");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(obsoleteAccepted.MemoEventId!)!.Status, "物品基线缺失应同步取消接单备忘录");
    AuthoritativeEvent baselineInvalidationReturn = state.Ledger.ListForUser("user-a")
        .Single(evt => evt.Type == ServerEventType.ItemDelivery
            && evt.IdempotencyKey == $"trade-baseline-invalidated-owner-return:{obsoleteTrade.EventId}");
    Equal(ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn, ((ItemDeliveryEventPayload)baselineInvalidationReturn.Payload).Purpose, "物品基线失效应按撤单退回发布者托管物");
    Require(
        state.Ledger.ListForUser("user-b").Any(evt =>
            evt.Type == ServerEventType.ServerNotification
            && evt.Payload is ServerNotificationEventPayload notification
            && notification.Message.Contains("MealFine", StringComparison.Ordinal)),
        "物品基线失效应通知已接单玩家失效物品");
    SubmitAdminBaselineResponse baselineRestored = await client.SubmitAdminBaselineAsync(
        new SubmitAdminBaselineRequest(
            ProtocolApiVersion.Current,
            "user-a",
            "colony-a",
            DateTimeOffset.UtcNow,
            new[]
            {
                new StandardMarketValueDto("Silver", 1f),
                new StandardMarketValueDto("Wastepack", 0f),
                new StandardMarketValueDto("MealFine", 18f),
                new StandardMarketValueDto("Steel", 1f),
                new StandardMarketValueDto("WoodLog", 1f),
                new StandardMarketValueDto("Cloth", 1f),
                new StandardMarketValueDto("ComponentIndustrial", 12f)
            },
            Array.Empty<TrapClassificationDto>()));
    Require(baselineRestored.Result.Accepted, "恢复管理员物品基线应成功");

    EventCreationResponse underpaidWasteTrade = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-a:waste-underpaid",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:wastepack", "Wastepack", 2) },
        new[] { new ThingReferenceDto("market:any/thing:silver", "Silver", 10) },
        feeSilver: 199,
        allowSelfPickup: true,
        allowServerDropPod: false));
    Require(!underpaidWasteTrade.Result.Accepted, "有毒垃圾交易单低于固定托管费应被服务器拒绝");
    Equal(ProtocolErrorCode.ServerRejected, underpaidWasteTrade.Result.ErrorCode, "固定托管费不足应返回服务器拒绝");

    EventCreationResponse wasteTrade = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-a:waste-paid",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:wastepack", "Wastepack", 2) },
        new[] { new ThingReferenceDto("market:any/thing:silver", "Silver", 10) },
        feeSilver: 200,
        allowSelfPickup: true,
        allowServerDropPod: false));
    Require(wasteTrade.Result.Accepted, "有毒垃圾交易单支付固定托管费后应允许创建");

    EventCreationResponse trade = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-a:001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:steel", "Steel", 100) },
        new[] { new ThingReferenceDto("market:any/thing:medicine", "MedicineIndustrial", 8) },
        feeSilver: 10,
        allowSelfPickup: true,
        allowServerDropPod: true));
    Require(trade.Result.Accepted, "创建交易单应成功");
    Require(state.Ledger.Find(trade.EventId!)?.Payload is TradeEventPayload, "交易单事件应进入账本");

    EventCreationResponse tooManyTrades = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-a:too-many",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:wood", "WoodLog", 25) },
        new[] { new ThingReferenceDto("market:any/thing:silver", "Silver", 25) },
        feeSilver: 2,
        allowSelfPickup: true,
        allowServerDropPod: false));
    Require(!tooManyTrades.Result.Accepted, "超过开放交易单数量上限时服务器应拒绝创建");
    Equal(ProtocolErrorCode.ServerRejected, tooManyTrades.Result.ErrorCode, "超过开放交易单数量上限应返回服务器拒绝");

    ListTradeOrdersResponse marketForOwner = await client.ListTradeOrdersAsync(new ListTradeOrdersRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-after"));
    Require(marketForOwner.Result.Accepted, "交易市场列表应允许发单人查询");
    Require(!marketForOwner.Orders.Any(order => order.EventId == trade.EventId), "发单人不应在可接单市场中看到自己的交易单");

    ListTradeOrdersResponse marketForUserB = await client.ListTradeOrdersAsync(new ListTradeOrdersRequest(
        "user-b",
        "colony-b",
        "defender-snapshot-before"));
    Require(marketForUserB.Result.Accepted, "其他玩家应能拉取交易市场列表");
    TradeOrderSummaryDto visibleTrade = marketForUserB.Orders.Single(order => order.EventId == trade.EventId);
    Equal("user-a", visibleTrade.Owner.UserId, "交易市场摘要应显示发单人");
    Equal(0, visibleTrade.AcceptedMemoCount, "未接单前备忘录计数应为 0");
    Require(visibleTrade.ServerDropPodPostage is { Reachable: false }, "接收方快照不是最新时空投邮费应显示不可达");

    state.SnapshotStore.StoreLatest(BuildSyntheticSnapshotRecord(
        "user-orbit",
        "colony-orbit",
        "orbit-snapshot",
        "WorldObject_900",
        "Map_900",
        "OrbitalPlatform",
        "OrbitalPlatform",
        "900,1"));
    state.SnapshotStore.StoreLatest(BuildSyntheticSnapshotRecord(
        "user-surface",
        "colony-surface",
        "surface-snapshot",
        "WorldObject_901",
        "Map_901",
        "Settlement",
        "Settlement",
        "123"));
    state.SnapshotStore.StoreLatest(BuildSyntheticSnapshotRecord(
        "user-ground",
        "colony-ground",
        "ground-snapshot",
        "WorldObject_902",
        "Map_902",
        "Settlement",
        "Settlement",
        "150"));
    RegisterPlayerColonySitesResponse registeredSurfaceColony = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-surface",
            "colony-surface",
            new[] { new PlayerColonySiteDto("user-surface", "colony-surface", "WorldObject_901", "Map_901", 123, "地表殖民地") }));
    Require(registeredSurfaceColony.Result.Accepted, "显式登记后地表殖民地应进入多人标记来源");
    WorldMapMarkerDeliveryDto worldMarkers = await client.SyncWorldMapMarkersAsync(new SyncWorldMapMarkersRequest(
        "user-a",
        DateTimeOffset.UnixEpoch));
    WorldMapMarkerDto surfaceMarker = worldMarkers.Markers.Single(marker =>
        marker.OwnerUserId == "user-surface"
        && marker.WorldObjectId == "WorldObject_901");
    Equal("TradeableColony", surfaceMarker.Kind, "同步大地图标记应包含可交易殖民地");
    Equal("colony-surface", surfaceMarker.OwnerColonyId, "同步大地图标记应包含殖民地");
    Equal("Map_901", surfaceMarker.MapId, "同步大地图标记应包含目标地图");
    Equal(123, surfaceMarker.Tile, "同步大地图标记应包含地块");
    Equal("Settlement", surfaceMarker.IconDefName, "同步大地图殖民地标记应携带原始世界对象 def");
    Require(surfaceMarker.CanTrade, "同步大地图标记应启用交易入口");
    Require(!worldMarkers.Markers.Any(marker =>
        marker.OwnerUserId == "user-ground"
        && marker.WorldObjectId == "WorldObject_902"),
        "仅存在于快照中的 NPC 或未登记据点不应成为多人地图标记");

    SyncRuntimeWorldObjectsResponse runtimeSelf = await client.SyncRuntimeWorldObjectsAsync(
        new SyncRuntimeWorldObjectsRequest(
            "user-a",
            "colony-a",
            "attacker-snapshot-after",
            DateTimeOffset.UtcNow,
            new[]
            {
                new RuntimeWorldObjectMarkerDto(
                    "Caravan_9",
                    "Caravan",
                    "Caravan",
                    456,
                    "测试远行队")
            },
            authToken: userAAuthToken));
    Require(runtimeSelf.Result.Accepted, "运行时世界对象同步应通过鉴权");
    Equal(1, runtimeSelf.AcceptedCount, "运行时世界对象同步应接受远行队位置");
    Require(runtimeSelf.WorldMapMarkers is not null, "运行时世界对象同步应返回大地图标记");
    Require(!runtimeSelf.WorldMapMarkers!.Markers.Any(marker =>
        marker.Kind == "RuntimeCaravan" && marker.OwnerUserId == "user-a"),
        "运行时世界对象不应回显给对象所有者");

    WorldMapMarkerDeliveryDto runtimeForOther = await client.SyncWorldMapMarkersAsync(new SyncWorldMapMarkersRequest(
        "user-b",
        DateTimeOffset.UnixEpoch));
    WorldMapMarkerDto runtimeCaravan = runtimeForOther.Markers.Single(marker =>
        marker.Kind == "RuntimeCaravan"
        && marker.OwnerUserId == "user-a"
        && marker.WorldObjectId == "Caravan_9");
    Equal(456, runtimeCaravan.Tile, "其他玩家应看到运行时远行队地块");
    Equal("Caravan", runtimeCaravan.IconDefName, "运行时远行队标记应携带原始图标 def");
    Equal("测试远行队", runtimeCaravan.Label, "运行时远行队标记应保留原版显示名");
    Require(!runtimeCaravan.CanTrade && !runtimeCaravan.CanRaid && !runtimeCaravan.CanReinforce, "运行时远行队仅作为位置标记");

    EventCreationResponse surfaceTrade = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-surface:001",
        new ProtocolIdentity("user-surface", "colony-surface", "surface-snapshot"),
        new[] { new ThingReferenceDto("owner:user-surface/colony:colony-surface/snapshot:surface-snapshot/map:901/thing:component", "ComponentIndustrial", 2) },
        new[] { new ThingReferenceDto("market:any/thing:silver", "Silver", 100) },
        feeSilver: 10,
        allowSelfPickup: true,
        allowServerDropPod: true));
    Require(surfaceTrade.Result.Accepted, "地表交易单应允许创建");
    ListTradeOrdersResponse surfaceMarket = await client.ListTradeOrdersAsync(new ListTradeOrdersRequest(
        "user-ground",
        "colony-ground",
        "ground-snapshot"));
    TradeOrderSummaryDto surfaceVisibleTrade = surfaceMarket.Orders.Single(order => order.EventId == surfaceTrade.EventId);
    Require(surfaceVisibleTrade.ServerDropPodPostage is { Reachable: true, PostageSilver: not null, DistanceTiles: not null }, "默认服务器地块距离估算应允许地表空投邮费计算");

    EventCreationResponse orbitalTrade = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-orbit:001",
        new ProtocolIdentity("user-orbit", "colony-orbit", "orbit-snapshot"),
        new[] { new ThingReferenceDto("owner:user-orbit/colony:colony-orbit/snapshot:orbit-snapshot/map:900/thing:component", "ComponentIndustrial", 4) },
        new[] { new ThingReferenceDto("market:any/thing:silver", "Silver", 100) },
        feeSilver: 10,
        allowSelfPickup: true,
        allowServerDropPod: true));
    Require(orbitalTrade.Result.Accepted, "轨道交易单应允许创建");
    ListTradeOrdersResponse orbitalMarket = await client.ListTradeOrdersAsync(new ListTradeOrdersRequest(
        "user-surface",
        "colony-surface",
        "surface-snapshot"));
    TradeOrderSummaryDto orbitalVisibleTrade = orbitalMarket.Orders.Single(order => order.EventId == orbitalTrade.EventId);
    Require(orbitalVisibleTrade.ServerDropPodPostage is { Reachable: true, PostageSilver: 300, DistanceTiles: 120 }, "跨层轨道空投应按投影距离和配置跨层距离计费");
    AcceptTradeOrderResponse orbitalAcceptedTrade = await client.AcceptTradeOrderAsync(new AcceptTradeOrderRequest(
        "trade:user-surface:accept:orbit",
        orbitalTrade.EventId!,
        new ProtocolIdentity("user-surface", "colony-surface", "surface-snapshot"),
        postagePaidByAcceptor: true));
    Require(orbitalAcceptedTrade.Result.Accepted, "轨道空投可达时应允许使用服务器空投接单");

    EventCreationResponse directDropPodTrade = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-orbit:direct-drop-pod",
        new ProtocolIdentity("user-orbit", "colony-orbit", "orbit-snapshot"),
        new[] { new ThingReferenceDto("owner:user-orbit/colony:colony-orbit/snapshot:orbit-snapshot/map:900/thing:meal", "MealFine", 1) },
        new[] { new ThingReferenceDto("market:any/thing:silver", "Silver", 100) },
        feeSilver: 10,
        allowSelfPickup: true,
        allowServerDropPod: true));
    Require(directDropPodTrade.Result.Accepted, "可达交易单应允许创建直接空投履约测试");
    FulfillTradeOrderResponse directDropPodFulfill = await client.FulfillTradeOrderAsync(new FulfillTradeOrderRequest(
        "trade:user-surface:fulfill:direct-drop-pod",
        directDropPodTrade.EventId!,
        "drop-pod-direct:" + directDropPodTrade.EventId,
        new ProtocolIdentity("user-surface", "colony-surface", "surface-snapshot"),
        new[] { new ThingReferenceDto("owner:user-surface/colony:colony-surface/snapshot:surface-snapshot/map:901/thing:silver", "Silver", 100) },
        "ServerDropPod"));
    Require(directDropPodFulfill.Result.Accepted, "空投履约不应要求已有接单备忘录");
    Require(directDropPodFulfill.ExchangeCreated, "空投履约应创建交换事件");
    Require(!string.IsNullOrWhiteSpace(directDropPodFulfill.AcceptorDeliveryEventId), "空投履约应创建接单方收货事件");
    Require(!string.IsNullOrWhiteSpace(directDropPodFulfill.OwnerDeliveryEventId), "空投履约应创建发布者收货事件");
    TradeEventPayload directDropPodPayload = (TradeEventPayload)state.Ledger.Find(directDropPodFulfill.ExchangeEventId!)!.Payload;
    Equal(TradeStage.ServerDropPodExchange, directDropPodPayload.Stage, "空投履约应写入服务器空投交换阶段");
    Equal(TradeFulfillmentMode.ServerDropPod, directDropPodPayload.FulfillmentMode, "空投履约应记录服务器空投方式");
    ItemDeliveryEventPayload acceptorDeliveryPayload = (ItemDeliveryEventPayload)state.Ledger.Find(directDropPodFulfill.AcceptorDeliveryEventId!)!.Payload;
    ItemDeliveryEventPayload ownerDeliveryPayload = (ItemDeliveryEventPayload)state.Ledger.Find(directDropPodFulfill.OwnerDeliveryEventId!)!.Payload;
    Equal(ItemDeliveryPurpose.TradeCompletedAcceptorDelivery, acceptorDeliveryPayload.Purpose, "空投履约应给接单方生成对方物品交付事件");
    Equal(ItemDeliveryPurpose.TradeCompletedOwnerDelivery, ownerDeliveryPayload.Purpose, "空投履约应给发布者生成己方交付物交付事件");
    LoginResponse surfaceLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-surface",
        "colony-surface",
        "surface-snapshot",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(surfaceLogin.Result.Accepted, $"地表玩家应能登录以回报事件应用失败：{surfaceLogin.Result.ErrorCode} {surfaceLogin.Result.Message}");
    ReportEventApplicationFailureResponse failedDropPodApplication = await client.ReportEventApplicationFailureAsync(
        new ReportEventApplicationFailureRequest(
            "event-application-failed:" + directDropPodFulfill.AcceptorDeliveryEventId,
            directDropPodFulfill.AcceptorDeliveryEventId!,
            "user-surface",
            "colony-surface",
            "surface-snapshot",
            "接收方 pawn 恢复失败。",
            sourceEventId: directDropPodFulfill.ExchangeEventId,
            authToken: surfaceLogin.AuthToken));
    Require(failedDropPodApplication.Result.Accepted, "恢复失败应允许上报为事件应用失败");
    Equal(ServerEventStatus.Failed.ToString(), failedDropPodApplication.TerminalStatus, "恢复失败上报应返回失败终态");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(directDropPodTrade.EventId!)!.Status, "交易落地失败应按撤单取消原交易单");
    Equal(ServerEventStatus.Failed, state.Ledger.Find(directDropPodFulfill.ExchangeEventId!)!.Status, "恢复失败应将交换事件标记失败");
    Equal(ServerEventStatus.Failed, state.Ledger.Find(directDropPodFulfill.AcceptorDeliveryEventId!)!.Status, "恢复失败应将接收方交付事件标记失败");
    Equal(ServerEventStatus.Failed, state.Ledger.Find(directDropPodFulfill.OwnerDeliveryEventId!)!.Status, "恢复失败应将发布者交付事件标记失败");
    AuthoritativeEvent applicationFailedReturn = state.Ledger.ListForUser("user-orbit")
        .Single(evt => evt.Type == ServerEventType.ItemDelivery
            && evt.IdempotencyKey == $"trade-application-failed-owner-return:{directDropPodTrade.EventId}:{directDropPodFulfill.AcceptorDeliveryEventId}");
    Equal(ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn, ((ItemDeliveryEventPayload)applicationFailedReturn.Payload).Purpose, "交易落地失败应生成发布者托管物退回事件");

    AcceptTradeOrderResponse acceptedTrade = await client.AcceptTradeOrderAsync(new AcceptTradeOrderRequest(
        "trade:user-b:accept:001",
        trade.EventId!,
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        postagePaidByAcceptor: false));
    Require(acceptedTrade.Result.Accepted, "接取交易单应成功");
    Require(acceptedTrade.MemoCreated, "首次接取应创建备忘录");
    AuthoritativeEvent acceptedMemo = state.Ledger.Find(acceptedTrade.MemoEventId!)!;
    Require(acceptedMemo.Payload is TradeEventPayload acceptedPayload && acceptedPayload.Stage == TradeStage.AcceptedMemo, "接单应写入备忘录事件");
    Equal("user-b", acceptedMemo.Actor.UserId, "接单备忘录发起方应为接单人");
    Equal("user-b", acceptedMemo.Target.UserId, "接单备忘录只应投递给接单人自己");

    AcceptTradeOrderResponse duplicateAcceptedTrade = await client.AcceptTradeOrderAsync(new AcceptTradeOrderRequest(
        "trade:user-b:accept:duplicate",
        trade.EventId!,
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        postagePaidByAcceptor: false));
    Equal(ProtocolErrorCode.DuplicateRequest, duplicateAcceptedTrade.Result.ErrorCode, "同一玩家重复接取应返回既有备忘录");
    Equal(acceptedTrade.MemoEventId, duplicateAcceptedTrade.MemoEventId, "重复接取应返回同一个备忘录");

    AcceptTradeOrderResponse acceptedByUserC = await client.AcceptTradeOrderAsync(new AcceptTradeOrderRequest(
        "trade:user-c:accept:001",
        trade.EventId!,
        new ProtocolIdentity("user-c", "colony-c", "third-party-snapshot"),
        postagePaidByAcceptor: false));
    Require(acceptedByUserC.Result.Accepted, "交易单应允许多个玩家接取");
    Require(acceptedByUserC.MemoCreated, "第二个玩家接取应创建独立备忘录");

    ListTradeOrdersResponse marketAfterAccept = await client.ListTradeOrdersAsync(new ListTradeOrdersRequest(
        "user-d",
        "colony-d",
        "ws-snapshot"));
    Equal(2, marketAfterAccept.Orders.Single(order => order.EventId == trade.EventId).AcceptedMemoCount, "多人接取后市场应显示备忘录计数");

    long userBVersionBeforeCancel = state.EventNotifications.GetVersion("user-b");
    long userCVersionBeforeCancel = state.EventNotifications.GetVersion("user-c");
    CloseTradeOrderResponse cancelledTrade = await client.CancelTradeOrderAsync(new CloseTradeOrderRequest(
        "trade:user-a:cancel:001",
        trade.EventId!,
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        "smoke cancel"));
    Require(cancelledTrade.Result.Accepted, "发布者撤单应成功");
    Equal(ServerEventStatus.Cancelled.ToString(), cancelledTrade.TerminalStatus, "撤单终态状态");
    Equal(2, cancelledTrade.NotifiedAcceptorCount, "撤单应通知所有已接单玩家");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(trade.EventId!)!.Status, "撤单后市场交易单应进入取消状态");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(acceptedTrade.MemoEventId!)!.Status, "撤单后接单人备忘录应进入取消状态");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(acceptedByUserC.MemoEventId!)!.Status, "撤单应关闭每个接单人的备忘录");
    AuthoritativeEvent cancelledReturn = state.Ledger.ListForUser("user-a")
        .Single(evt => evt.Type == ServerEventType.ItemDelivery
            && evt.IdempotencyKey == $"trade-cancelled-owner-return:{trade.EventId}");
    Equal(ItemDeliveryPurpose.TradeCancelledOwnerReturn, ((ItemDeliveryEventPayload)cancelledReturn.Payload).Purpose, "撤单应生成发布者托管物退回事件");
    Require(state.EventNotifications.GetVersion("user-b") > userBVersionBeforeCancel, "撤单应通知 user-b 刷新接单清单");
    Require(state.EventNotifications.GetVersion("user-c") > userCVersionBeforeCancel, "撤单应通知 user-c 刷新接单清单");

    EventCreationResponse tradeToComplete = await client.CreateTradeOrderAsync(new CreateTradeOrderRequest(
        "trade:user-a:complete-source",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:0/thing:cloth", "Cloth", 50) },
        new[] { new ThingReferenceDto("market:any/thing:component", "ComponentIndustrial", 2) },
        feeSilver: 5,
        allowSelfPickup: true,
        allowServerDropPod: false));
    Require(tradeToComplete.Result.Accepted, "创建待完成交易单应成功");
    AcceptTradeOrderResponse acceptedTradeToComplete = await client.AcceptTradeOrderAsync(new AcceptTradeOrderRequest(
        "trade:user-b:accept:complete-source",
        tradeToComplete.EventId!,
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-after-gift"),
        postagePaidByAcceptor: false));
    Require(acceptedTradeToComplete.Result.Accepted, "待完成交易单接单应成功");
    long userAVersionBeforeFulfill = state.EventNotifications.GetVersion("user-a");
    long userBVersionBeforeFulfill = state.EventNotifications.GetVersion("user-b");
    FulfillTradeOrderResponse insufficientFulfill = await client.FulfillTradeOrderAsync(new FulfillTradeOrderRequest(
        "trade:user-b:fulfill:missing",
        tradeToComplete.EventId!,
        acceptedTradeToComplete.MemoEventId!,
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-after-gift"),
        new[] { new ThingReferenceDto("owner:user-b/colony:colony-b/snapshot:defender-snapshot-after-gift/map:caravan/thing:component-small", "ComponentIndustrial", 1) },
        "SelfDelivery"));
    Require(!insufficientFulfill.Result.Accepted, "远行队携带物不足时不能完成自提履约");
    Require(insufficientFulfill.MissingRequirements.Count > 0, "携带物不足应返回缺失要求");
    Equal(ServerEventStatus.PendingOfflineDelivery, state.Ledger.Find(tradeToComplete.EventId!)!.Status, "履约失败不应关闭市场交易单");

    FulfillTradeOrderResponse fulfilledTrade = await client.FulfillTradeOrderAsync(new FulfillTradeOrderRequest(
        "trade:user-b:fulfill:complete-source",
        tradeToComplete.EventId!,
        acceptedTradeToComplete.MemoEventId!,
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-after-gift"),
        new[] { new ThingReferenceDto("owner:user-b/colony:colony-b/snapshot:defender-snapshot-after-gift/map:caravan/thing:component", "ComponentIndustrial", 2) },
        "SelfDelivery"));
    Require(fulfilledTrade.Result.Accepted, "远行队携带物满足要求时自提交易应成立");
    Require(fulfilledTrade.ExchangeCreated, "自提履约应创建交换事件");
    Require(fulfilledTrade.ReceivedThings.Any(thing => thing.DefName == "Cloth" && thing.StackCount == 50), "自提履约响应应返回接单方获得的发布者物品");
    Require(string.IsNullOrWhiteSpace(fulfilledTrade.AcceptorDeliveryEventId), "自提履约不应再给接单方创建空投收货事件");
    Require(!string.IsNullOrWhiteSpace(fulfilledTrade.OwnerDeliveryEventId), "自提履约应给发布者创建交付物到达事件");
    Equal(ItemDeliveryPurpose.TradeCompletedOwnerDelivery, ((ItemDeliveryEventPayload)state.Ledger.Find(fulfilledTrade.OwnerDeliveryEventId!)!.Payload).Purpose, "自提履约应以交易交付语义通知发布者收货");
    AuthoritativeEvent exchangeEvent = state.Ledger.Find(fulfilledTrade.ExchangeEventId!)!;
    TradeEventPayload exchangePayload = (TradeEventPayload)exchangeEvent.Payload;
    Equal(TradeStage.SelfDeliveryExchange, exchangePayload.Stage, "自提履约应写入自提交换阶段");
    Equal(acceptedTradeToComplete.MemoEventId, exchangePayload.AcceptedMemoEventId, "自提交换事件应绑定实际履约的接单备忘录");
    Equal(TradeFulfillmentMode.SelfDelivery, exchangePayload.FulfillmentMode, "交换事件应记录远行队自提履约方式");
    Equal(ServerEventStatus.AppliedToSnapshot, state.Ledger.Find(tradeToComplete.EventId!)!.Status, "自提交易成立后市场交易单应进入完成状态");
    Equal(ServerEventStatus.AppliedToSnapshot, state.Ledger.Find(acceptedTradeToComplete.MemoEventId!)!.Status, "自提交易成立后接单备忘录应进入完成状态");
    ListTradeOrdersResponse ownerTradeHistory = await client.ListTradeOrdersAsync(new ListTradeOrdersRequest(
        "user-a",
        "colony-a",
        "attacker-snapshot-after",
        scope: "History"));
    TradeOrderSummaryDto completedHistoryOrder = ownerTradeHistory.Orders.Single(order => order.EventId == tradeToComplete.EventId);
    Equal("user-b", completedHistoryOrder.Counterparty?.UserId, "交易历史应显示实际完成交易的接单方");
    Equal("colony-b", completedHistoryOrder.Counterparty?.ColonyId, "交易历史应显示实际完成交易方殖民地");
    Require(state.EventNotifications.GetVersion("user-a") > userAVersionBeforeFulfill, "自提履约应通知发布者刷新交易状态");
    Require(state.EventNotifications.GetVersion("user-b") > userBVersionBeforeFulfill, "自提履约应通知接单方刷新接单清单");

    SaveSnapshotPackage tradeConfirmedPackage = BuildFixturePackageForWithLineage(
        "user-b",
        "colony-b",
        "defender-snapshot-after-trade",
        previousSnapshotId: "defender-snapshot-after-gift",
        giftConfirm.NextLineageToken,
        gameTicks: (userBInitialPackage.Envelope.GameTicks ?? 0) + 2,
        playerSettlementTile: 42);
    LoginResponse userBTradeLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-b",
        "colony-b",
        "defender-snapshot-after-gift",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(userBTradeLogin.Result.Accepted, $"交易确认前 user-b 应能重新建立当前会话：{userBTradeLogin.Result.ErrorCode} {userBTradeLogin.Result.Message}");
    ConfirmEventApplicationResponse tradeConfirm = await client.ConfirmEventApplicationAsync(new ConfirmEventApplicationMetadataRequest(
        "confirm:trade:user-b:defender-snapshot-after-trade",
        fulfilledTrade.ExchangeEventId!,
        tradeToComplete.EventId!,
        "user-b",
        "colony-b",
        "defender-snapshot-after-gift",
        SnapshotMetadata(tradeConfirmedPackage),
        "SelfDeliveryExchangeApplied",
        authToken: userBTradeLogin.AuthToken),
        tradeConfirmedPackage.Payload);
    Require(
        tradeConfirm.Result.Accepted,
        $"自提交易交换确认应通过服务器校验：{tradeConfirm.Result.ErrorCode} {tradeConfirm.Result.Message} {tradeConfirm.ServerValidationResult}");
    Equal("defender-snapshot-after-trade", tradeConfirm.AppliedSnapshotId, "自提交易交换应用快照 ID");
    Equal(ServerEventStatus.AppliedToSnapshot, state.Ledger.Find(fulfilledTrade.ExchangeEventId!)!.Status, "自提交易交换事件应被标记为已应用");

    FulfillTradeOrderResponse duplicateFulfill = await client.FulfillTradeOrderAsync(new FulfillTradeOrderRequest(
        "trade:user-b:fulfill:complete-source",
        tradeToComplete.EventId!,
        acceptedTradeToComplete.MemoEventId!,
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-after-gift"),
        new[] { new ThingReferenceDto("owner:user-b/colony:colony-b/snapshot:defender-snapshot-after-gift/map:caravan/thing:component", "ComponentIndustrial", 2) },
        "SelfDelivery"));
    Require(duplicateFulfill.Result.Accepted, "重复自提履约请求应按幂等键返回既有交换事件");
    Require(!duplicateFulfill.ExchangeCreated, "重复自提履约不应创建第二个交换事件");
    Equal(fulfilledTrade.ExchangeEventId, duplicateFulfill.ExchangeEventId, "重复自提履约应返回同一个交换事件");

    SaveSnapshotPackage defenderPackage = BuildFixturePackageFor(
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        playerSettlementTile: 42);
    state.SnapshotStore.StoreLatest(new LatestSnapshotRecord(
        defenderPackage.Envelope.Identity,
        defenderPackage.Envelope,
        defenderPackage.Index,
        DateTimeOffset.UnixEpoch));
    string defenderMap = defenderPackage.Index.Maps.First(map => !string.IsNullOrWhiteSpace(map.UniqueId)).UniqueId!;
    string defenderWorldObject = defenderPackage.Index.Maps.First(map => map.UniqueId == defenderMap).ParentWorldObjectId ?? "WorldObject_1";
    RaidPreparationRecord preparedRaid = state.RaidPreparations.Create(
        "raid:user-a:user-b:001",
        AuthoritativeEventFactory.BuildEventId(ServerEventType.Raid, "raid:user-a:user-b:001"),
        "user-a",
        "colony-a",
        "user-b",
        "colony-b",
        "defender-snapshot-before",
        defenderWorldObject,
        defenderMap,
        targetTile: null,
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(10));
    Require(!string.IsNullOrWhiteSpace(preparedRaid.PreparationId), "袭击准备应返回准备令牌");

    SaveSnapshotPackage raidBattlefieldPackage = BuildFixturePackageForWithLineage(
        "user-a",
        "colony-a",
        "attacker-snapshot-after-player-raid",
        previousSnapshotId: "attacker-snapshot-after",
        confirm.NextLineageToken,
        gameTicks: (uploadedConfirmationPackage.Envelope.GameTicks ?? 0) + 1);
    EventCreationResponse raid = await client.CreateRaidWithSnapshotAsync(new CreateRaidRequest(
        "raid:user-a:user-b:001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        isHostile: true,
        defenderOnline: false,
        defenderWealth: 8000,
        defenderRaidCooldownUntilUtc: DateTimeOffset.UnixEpoch,
        preparedRaid.PreparationId,
        defenderWorldObject,
        defenderMap,
        "defender-snapshot-before",
        new[] { "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:caravan/thing:pawn-1" },
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:caravan/thing:medicine", "MedicineIndustrial", 8) }),
        SnapshotMetadata(raidBattlefieldPackage),
        raidBattlefieldPackage.Payload);
    Require(raid.Result.Accepted, "创建袭击应成功");
    Require(state.Ledger.Find(raid.EventId!)?.Payload is RaidEventPayload, "袭击事件应进入账本");
    Equal("attacker-snapshot-after-player-raid", raid.AppliedSnapshotId, "玩家袭击创建应确认攻击方战场快照");

    EventCreationResponse forcedGiftDuringRaid = await client.CreateGiftAsync(new CreateGiftRequest(
        "gift:user-a:user-b:forced-during-raid",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after-player-raid"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after-player-raid/caravan:caravan-a/thing:wastepack-raid", "Wastepack", 1) },
        "袭击未结算时强投应被阻止。",
        targetContext: null,
        deliveryKind: "Forced"));
    Require(!forcedGiftDuringRaid.Result.Accepted, "目标存在未结算袭击时不能强行投递");
    Equal(ProtocolErrorCode.ServerRejected, forcedGiftDuringRaid.Result.ErrorCode, "未结算袭击强投保护应由服务器拒绝");
    Require(!string.IsNullOrWhiteSpace(forcedGiftDuringRaid.Result.Message), "强投拒绝应返回可显示原因");

    EventCreationResponse blockedSecondRaid = await client.CreateRaidAsync(new CreateRaidRequest(
        "raid:user-a:user-b:blocked-second",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after-player-raid"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        isHostile: true,
        defenderOnline: false,
        defenderWealth: 8000,
        defenderRaidCooldownUntilUtc: DateTimeOffset.UnixEpoch,
        raidPreparationId: null,
        defenderWorldObject,
        defenderMap,
        "defender-snapshot-before",
        new[] { "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after/map:caravan/thing:pawn-2" },
        Array.Empty<ThingReferenceDto>()));
    Require(!blockedSecondRaid.Result.Accepted, "存在未结算袭击时同一玩家不能再次发动袭击");
    Equal(ProtocolErrorCode.ServerRejected, blockedSecondRaid.Result.ErrorCode, "未结算袭击限制应由服务器拒绝");
    Equal(raid.EventId, blockedSecondRaid.EventId, "重复袭击拒绝响应应指向阻挡的新近袭击");
    state.Ledger.ChangeStatus(raid.EventId!, ServerEventStatus.Cancelled);

    EventCreationResponse npcTargetRaid = await client.CreateRaidAsync(new CreateRaidRequest(
        "raid:user-a:npc:001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-after-player-raid"),
        new ProtocolIdentity("npc", null, null),
        true,
        false,
        0,
        null,
        null,
        "WorldObject_NPC_1",
        "Map_NPC_1",
        string.Empty,
        new[] { "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-after-player-raid/map:caravan/thing:pawn-2" },
        Array.Empty<ThingReferenceDto>(),
        targetTile: 812,
        opponentKind: "VanillaNpc"));
    Require(!npcTargetRaid.Result.Accepted, "主动进攻 NPC 不再创建多人事件");
    Equal(ProtocolErrorCode.ValidationFailed, npcTargetRaid.Result.ErrorCode, "主动进攻 NPC 应被视为本地事件拒绝");
    Require(string.IsNullOrWhiteSpace(npcTargetRaid.EventId), "主动进攻 NPC 不应返回多人账本事件");
    Require(WorldMapMarkerProjectionBuilder.BuildActiveRaidTargetMarkers(state.Ledger.ListAll(), DateTimeOffset.UtcNow)
        .All(marker => marker.RelatedEventId != "raid:user-a:npc:001"), "主动进攻 NPC 不应广播支援标记");

    RegisterPlayerColonySitesResponse transientColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-d",
            "colony-d",
            new[] { new PlayerColonySiteDto("user-d", "colony-d", "WorldObject_900", "Map_90", 900, "临时殖民地") }));
    Require(transientColonySites.Result.Accepted, "无快照玩家应能登记临时殖民地地块");
    Require(transientColonySites.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-d" && site.ColonyId == "colony-d") == true, "临时登记后服务器应包含该殖民地地块");

    RegisterPlayerColonySitesResponse clearedColonySites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-d",
            "colony-d",
            Array.Empty<PlayerColonySiteDto>()));
    Require(!clearedColonySites.Result.Accepted, "空殖民地地块集合不应再作为清理入口");
    Equal(ProtocolErrorCode.ValidationFailed, clearedColonySites.Result.ErrorCode, "清理殖民地必须使用服务器放弃流程");
    Require(clearedColonySites.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-d" && site.ColonyId == "colony-d") == true, "空集合拒绝后服务器应继续保留该殖民地地块");

    state.SnapshotStore.StoreLatest(BuildSyntheticSnapshotRecord(
        "user-d",
        "colony-d",
        "snapshot-user-d-world",
        "WorldObject_900",
        "Map_90",
        "Settlement",
        "Settlement",
        "900",
        Array.Empty<IdeoSummary>()));
    AuthoritativeEvent transientEvent = AuthoritativeEventFactory.Create(
        ServerEventType.ServerNotification,
        new EventParty("user-d", "colony-d", null),
        new EventParty("user-a", "colony-a", null),
        "server-notice:user-d:abandon-cleanup",
        targetOnline: false,
        new ServerNotificationEventPayload(
            "notice:user-d:abandon-cleanup",
            "临时事件",
            "用于验证放弃殖民地时清理事件引用。",
            ServerNotificationSeverity.Info,
            FromAdministrator: false),
        DateTimeOffset.UtcNow);
    state.Ledger.Append(transientEvent);

    LoginResponse userDAbandonLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-d",
        "colony-d",
        "snapshot-user-d-world",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(userDAbandonLogin.Result.Accepted, $"放弃殖民地前过期会话应能重新登录：{userDAbandonLogin.Result.ErrorCode} {userDAbandonLogin.Result.Message}");
    Require(!string.IsNullOrWhiteSpace(userDAbandonLogin.AuthToken), "放弃殖民地前重新登录应返回鉴权令牌");
    userDAuthToken = userDAbandonLogin.AuthToken!;

    AbandonPlayerColonyResponse abandoned = await client.AbandonPlayerColonyAsync(
        new AbandonPlayerColonyRequest(
            ProtocolApiVersion.Current,
            "user-d",
            "colony-d",
            "snapshot-user-d-world",
            "abandon:user-d:001",
            authToken: userDAuthToken));
    Require(abandoned.Result.Accepted, $"服务器放弃殖民地应成功：{abandoned.Result.ErrorCode} {abandoned.Result.Message}");
    Equal(1, abandoned.RemovedSnapshots, "放弃殖民地应删除最新快照");
    Require(abandoned.RemovedSites >= 1, "放弃殖民地应删除殖民地地块");
    Equal(0, abandoned.RemovedEvents, "放弃殖民地不应删除事件账本");
    Equal(null, state.SnapshotStore.GetLatest("user-d", "colony-d"), "放弃后最新快照索引不应保留该殖民地");
    Require(state.Ledger.Find(transientEvent.EventId) is not null, "放弃后事件账本应保留该殖民地引用事件供生命周期收尾");
    Equal(ServerEventStatus.Cancelled, state.Ledger.Find(transientEvent.EventId)!.Status, "放弃后未完成事件应被墓碑生命周期关闭");
    Require(abandoned.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-d" && site.ColonyId == "colony-d") != true, "放弃后世界配置不应保留该殖民地地块");
    Require(!state.Players.List().Any(player => player.UserId == "user-d"), "放弃后玩家不应保留活动殖民地记录");
    Require(state.Players.IsDeleted("user-d", "colony-d"), "放弃后应保留该殖民地实例墓碑");

    PrepareWorldSessionResponse abandonedSession = await client.PrepareWorldSessionAsync(
        SmokePrepareWorldSessionRequest("user-d", "colony-d"));
    Require(abandonedSession.Result.Accepted, $"放弃后的玩家应仍可重新准备世界会话：{abandonedSession.Result.ErrorCode} {abandonedSession.Result.Message}");
    Require(!abandonedSession.HasExistingColony, "放弃后同一玩家不应被识别为已有殖民地");
    Require(!string.Equals(abandonedSession.AssignedColonyId, "colony-d", StringComparison.Ordinal), "放弃后同名重建应分配新的殖民地实例");

    RegisterPlayerColonySitesResponse adminDeleteTargetSites = await client.RegisterPlayerColonySitesAsync(
        new RegisterPlayerColonySitesRequest(
            ProtocolApiVersion.Current,
            "user-f",
            "colony-f",
            new[] { new PlayerColonySiteDto("user-f", "colony-f", "WorldObject_901", "Map_91", 901, "待删除殖民地") }));
    Require(adminDeleteTargetSites.Result.Accepted, "管理员删除测试前应能登记目标殖民地地块");
    state.SnapshotStore.StoreLatest(BuildSyntheticSnapshotRecord(
        "user-f",
        "colony-f",
        "snapshot-user-f-world",
        "WorldObject_901",
        "Map_91",
        "Settlement",
        "Settlement",
        "901",
        Array.Empty<IdeoSummary>()));
    LoginResponse userFLogin = await client.LoginAsync(new LoginRequest(
        ProtocolApiVersion.Current,
        "user-f",
        "colony-f",
        "snapshot-user-f-world",
        "smoke-main-manifest",
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest"));
    Require(userFLogin.Result.Accepted, $"管理员删除测试前目标玩家应能登录：{userFLogin.Result.ErrorCode} {userFLogin.Result.Message}");

    AdminActionResponse adminDeletedPlayerSave = (await (await httpClient.PostAsJsonAsync(
        ProtocolContractManifest.Find(ProtocolMessageKind.AdminAction).Route,
        new AdminActionRequest(
            "user-a",
            "colony-a",
            userAAuthToken,
            "DeletePlayerSave",
            "user-f",
            "colony-f",
            message: null))).Content.ReadFromJsonAsync<AdminActionResponse>())!;
    Require(adminDeletedPlayerSave.Result.Accepted, $"管理员应能删除指定玩家存档：{adminDeletedPlayerSave.Result.ErrorCode} {adminDeletedPlayerSave.Result.Message}");
    Equal(null, state.SnapshotStore.GetLatest("user-f", "colony-f"), "管理员删除后目标最新快照索引不应保留");
    Require(state.WorldConfiguration.Current?.PlayerColonySites.Any(site => site.UserId == "user-f" && site.ColonyId == "colony-f") != true, "管理员删除后世界配置不应保留目标殖民地地块");
    Require(!state.Players.List().Any(player => player.UserId == "user-f"), "管理员删除后目标玩家不应保留活动殖民地记录");
    Require(state.Players.IsDeleted("user-f", "colony-f"), "管理员删除后应保留目标殖民地实例墓碑");
    PrepareWorldSessionResponse adminDeletedPlayerSession = await client.PrepareWorldSessionAsync(
        SmokePrepareWorldSessionRequest("user-f", "colony-f"));
    Require(adminDeletedPlayerSession.Result.Accepted, $"管理员删除后的玩家应仍可重新准备世界会话：{adminDeletedPlayerSession.Result.ErrorCode} {adminDeletedPlayerSession.Result.Message}");
    Require(!adminDeletedPlayerSession.HasExistingColony, "管理员删除后同一玩家不应被识别为已有殖民地");
    Require(!string.Equals(adminDeletedPlayerSession.AssignedColonyId, "colony-f", StringComparison.Ordinal), "管理员删除后同名重建应分配新的殖民地实例");

    Console.WriteLine("通过：网络层最小闭环和事件创建接口");
}
finally
{
    await app.StopAsync();
}

static AuthoritativeEvent SeedAttackerLossEvent(IAuthoritativeEventLedger ledger)
{
    var attackForce = new RaidAttackForceRecord(
        "attacker-snapshot-before",
        new[]
        {
            "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-before/map:caravan/thing:clash-of-rim-smoke-pawn-1"
        },
        Array.Empty<EventThingReference>());
    RaidAttackerLossRecord loss = RaidAttackerLossRecord.FromAttackForce(
        "raid-source-001",
        attackForce,
        "timeout");

    AuthoritativeEvent evt = AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        new EventParty("server"),
        new EventParty("user-a", "colony-a", "Faction_0"),
        "attacker-loss-smoke",
        targetOnline: false,
        new RaidEventPayload(
            "defender-snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: DateTimeOffset.UnixEpoch.AddHours(2),
            Settlement: null,
            AttackForce: attackForce,
            AttackerLoss: loss),
        DateTimeOffset.UnixEpoch);

    ledger.Append(evt);
    return ledger.MarkDelivered(evt.EventId, "attacker-snapshot-before", DateTimeOffset.UtcNow);
}

static SaveSnapshotPackage BuildFixturePackage(string snapshotId)
{
    return BuildFixturePackageFor("user-a", "colony-a", snapshotId);
}

static SnapshotPackageMetadataDto SnapshotMetadata(SaveSnapshotPackage package)
{
    return ProtocolDtoMapper.ToMetadataDto(package);
}

static SaveSnapshotPackage BuildFixturePackageFor(
    string ownerId,
    string colonyId,
    string snapshotId,
    int? playerSettlementTile = null)
{
    string samplePath = ResolveSampleSavePath("SaveHediffFixture.rws");
    Require(File.Exists(samplePath), $"缺少样本存档：{samplePath}");

    if (playerSettlementTile is not null)
    {
        return BuildModifiedFixturePackage(
            samplePath,
            ownerId,
            colonyId,
            snapshotId,
            document =>
            {
                ApplyPlayerSettlementTile(document, playerSettlementTile.Value);
            });
    }

    return SaveSnapshotPackageBuilder.FromFile(
        samplePath,
        new SnapshotIdentity(ownerId, colonyId, snapshotId),
        DateTimeOffset.UnixEpoch,
        SnapshotPayloadEncoding.GzipRws);
}

static string ResolveSampleSavePath(string fileName)
{
    DirectoryInfo? directory = new(Environment.CurrentDirectory);
    while (directory is not null)
    {
        string candidate = Path.Combine(directory.FullName, "SaveSample", fileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "SaveSample", fileName));
}

static SaveSnapshotPackage BuildFixturePackageForWithLineage(
    string ownerId,
    string colonyId,
    string snapshotId,
    string? previousSnapshotId,
    string? token,
    long gameTicks,
    int? playerSettlementTile = null,
    Action<System.Xml.Linq.XDocument>? extraMutate = null)
{
    string samplePath = ResolveSampleSavePath("SaveHediffFixture.rws");
    Require(File.Exists(samplePath), $"缺少样本存档：{samplePath}");

    return BuildModifiedFixturePackage(
        samplePath,
        ownerId,
        colonyId,
        snapshotId,
        document =>
        {
            System.Xml.Linq.XElement game = document.Root!.Element("game")!;
            System.Xml.Linq.XElement tickManager = game.Element("tickManager")!;
            SetElement(tickManager, "ticksGame", gameTicks.ToString());

            System.Xml.Linq.XElement? components = game.Element("components");
            if (components is null)
            {
                components = new System.Xml.Linq.XElement("components");
                game.AddFirst(components);
            }

            System.Xml.Linq.XElement? component = components.Elements("li").FirstOrDefault(item =>
                item.Attribute("Class")?.Value.Contains("AIRsLight.ClashOfRim.ClashOfRimGameComponent", StringComparison.Ordinal) == true);
            if (component is null)
            {
                component = new System.Xml.Linq.XElement(
                    "li",
                    new System.Xml.Linq.XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"));
                components.Add(component);
            }

            SetElement(component, "clashOfRimLineageSnapshotId", previousSnapshotId ?? string.Empty);
            SetElement(component, "clashOfRimLineageToken", token ?? string.Empty);
            if (playerSettlementTile is not null)
            {
                ApplyPlayerSettlementTile(document, playerSettlementTile.Value);
            }

            extraMutate?.Invoke(document);
        });
}

static SaveSnapshotPackage BuildModifiedFixturePackage(
    string samplePath,
    string ownerId,
    string colonyId,
    string snapshotId,
    Action<System.Xml.Linq.XDocument> mutate)
{
    string tempPath = Path.Combine(
        Path.GetTempPath(),
        "clash-of-rim-network-fixture-" + Guid.NewGuid().ToString("N") + ".rws");
    try
    {
        System.Xml.Linq.XDocument document = System.Xml.Linq.XDocument.Load(samplePath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        mutate(document);
        document.Save(tempPath);
        return SaveSnapshotPackageBuilder.FromFile(
            tempPath,
            new SnapshotIdentity(ownerId, colonyId, snapshotId),
            DateTimeOffset.UnixEpoch,
            SnapshotPayloadEncoding.GzipRws);
    }
    finally
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
        }
    }
}

static void ApplyPlayerSettlementTile(System.Xml.Linq.XDocument document, int tile)
{
    System.Xml.Linq.XElement game = document.Root!.Element("game")!;
    System.Xml.Linq.XElement? firstMap = game.Element("maps")?.Elements("li").FirstOrDefault();
    string? parent = firstMap?.Element("mapInfo")?.Element("parent")?.Value;
    string? parentId = parent?.StartsWith("WorldObject_", StringComparison.Ordinal) == true
        ? parent["WorldObject_".Length..]
        : null;
    Require(!string.IsNullOrWhiteSpace(parentId), "测试样本存档缺少当前地图父 world object。");

    System.Xml.Linq.XElement? worldObject = game.Element("world")
        ?.Element("worldObjects")
        ?.Element("worldObjects")
        ?.Elements("li")
        .FirstOrDefault(item => string.Equals(item.Element("ID")?.Value, parentId, StringComparison.Ordinal));
    Require(worldObject is not null, "测试样本存档缺少当前地图父 world object 记录。");
    SetElement(worldObject!, "tile", tile.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",0");
}

static void AddSettlementMap(
    System.Xml.Linq.XDocument document,
    int worldObjectId,
    string mapUniqueId,
    int tile,
    string faction,
    string label)
{
    System.Xml.Linq.XElement game = document.Root!.Element("game")!;
    System.Xml.Linq.XElement? maps = game.Element("maps");
    Require(maps is not null, "测试样本存档缺少地图列表。");
    maps!.Add(new System.Xml.Linq.XElement(
        "li",
        new System.Xml.Linq.XElement("uniqueID", mapUniqueId),
        new System.Xml.Linq.XElement(
            "mapInfo",
            new System.Xml.Linq.XElement("parent", "WorldObject_" + worldObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new System.Xml.Linq.XElement("size", "250,1,250")),
        new System.Xml.Linq.XElement("things")));

    System.Xml.Linq.XElement? worldObjects = game.Element("world")
        ?.Element("worldObjects")
        ?.Element("worldObjects");
    Require(worldObjects is not null, "测试样本存档缺少世界对象列表。");
    worldObjects!.Add(new System.Xml.Linq.XElement(
        "li",
        new System.Xml.Linq.XAttribute("Class", "RimWorld.Settlement"),
        new System.Xml.Linq.XElement("ID", worldObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new System.Xml.Linq.XElement("def", "Settlement"),
        new System.Xml.Linq.XElement("tile", tile.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",0"),
        new System.Xml.Linq.XElement("faction", faction),
        new System.Xml.Linq.XElement("nameInt", label)));
}

static void SetElement(System.Xml.Linq.XElement parent, string name, string value)
{
    System.Xml.Linq.XElement? element = parent.Element(name);
    if (element is null)
    {
        parent.Add(new System.Xml.Linq.XElement(name, value));
        return;
    }

    element.Value = value;
}

static LatestSnapshotRecord BuildSyntheticSnapshotRecord(
    string ownerId,
    string colonyId,
    string snapshotId,
    string worldObjectId,
    string mapUniqueId,
    string worldObjectClass,
    string worldObjectDef,
    string tile,
    IReadOnlyList<IdeoSummary>? ideos = null)
{
    var identity = new SnapshotIdentity(ownerId, colonyId, snapshotId);
    var envelope = new SaveSnapshotEnvelope(
        SaveSnapshotPackageBuilder.CurrentPackageVersion,
        identity,
        DateTimeOffset.UnixEpoch,
        "synthetic.rws",
        "smoke",
        SnapshotPayloadEncoding.GzipRws,
        OriginalSaveBytes: 0,
        PayloadBytes: 0,
        OriginalSha256: string.Empty,
        PayloadSha256: string.Empty);
    IReadOnlyList<SaveIndexExtensionData> extensions = ideos is null
        ? Array.Empty<SaveIndexExtensionData>()
        : IdeologySaveIndexExtensions(ideos);
    var index = new SaveSnapshotIndex(
        "synthetic.rws",
        new SaveMetaSummary("smoke", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        Array.Empty<FactionSummary>(),
        extensions,
        new[]
        {
            new WorldObjectSummary(
                worldObjectId.Replace("WorldObject_", string.Empty, StringComparison.Ordinal),
                worldObjectId,
                worldObjectClass,
                worldObjectDef,
                tile,
                "Faction_0",
                worldObjectDef,
                Destroyed: false)
        },
        new[]
        {
            new MapSummary(
                UniqueId: mapUniqueId,
                GeneratedId: null,
                ParentWorldObjectId: worldObjectId,
                Size: "250,1,250",
                HasCompressedThingMap: false,
                HasTerrainGrid: false,
                HasRoofGrid: false,
                HasFogGrid: false,
                ThingCount: 0,
                PawnCount: 0)
        },
        Array.Empty<ThingSummary>(),
        Array.Empty<PawnSummary>());
    return new LatestSnapshotRecord(identity, envelope, index, DateTimeOffset.UnixEpoch);
}

static IReadOnlyList<SaveIndexExtensionData> IdeologySaveIndexExtensions(IReadOnlyList<IdeoSummary> ideos)
{
    SaveIndexExtensionData? extension = IdeologySaveIndexExtension.BuildExtensionData(ideos);
    return extension is null ? Array.Empty<SaveIndexExtensionData>() : new[] { extension };
}

static void VerifySnapshotUploadAllowsSingleColonyRelocation()
{
    var store = new InMemoryColonySnapshotIndexStore();
    var receiver = new SnapshotUploadReceiver(store, SnapshotUploadPolicy.AllowAnyVersion);

    SaveSnapshotPackage initialPackage = BuildFixturePackageFor(
        "gravship-user",
        "gravship-colony",
        "gravship-before",
        playerSettlementTile: 101);
    SnapshotUploadResult initial = receiver.Receive(
        new SnapshotUploadContext("gravship-user", "gravship-colony", "gravship-before"),
        initialPackage,
        DateTimeOffset.UnixEpoch);
    Require(initial.Accepted, $"重力飞船搬迁测试的初始快照应被接受：{initial.Kind} {initial.Message}");

    string? nextToken = initial.AcceptedSnapshot!.Envelope.NextLineageToken;
    Require(!string.IsNullOrWhiteSpace(nextToken), "初始快照接受后应生成 lineage token");
    long nextTicks = (initial.AcceptedSnapshot.Envelope.GameTicks ?? 0) + 1;
    SaveSnapshotPackage movedPackage = BuildFixturePackageForWithLineage(
        "gravship-user",
        "gravship-colony",
        "gravship-after",
        previousSnapshotId: "gravship-before",
        nextToken,
        nextTicks,
        playerSettlementTile: 707,
        extraMutate: document => AddSettlementMap(document, 9901, "9901", 303, "Faction_1", "额外 NPC 据点"));

    SnapshotUploadResult moved = receiver.Receive(
        new SnapshotUploadContext("gravship-user", "gravship-colony", "gravship-after"),
        movedPackage,
        DateTimeOffset.UnixEpoch.AddSeconds(1));
    Require(moved.Accepted, $"同一殖民地单锚点搬迁快照应被接受：{moved.Kind} {moved.Message}");
    Require(
        store.GetLatest("gravship-user", "gravship-colony")?.Index.WorldObjects.Any(worldObject =>
            worldObject.Tile?.StartsWith("707", StringComparison.Ordinal) == true) == true,
        "搬迁快照接受后最新索引应更新到新地块");
}

static async Task VerifyColonyRelocationExplicitConfirmationAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "clash-of-rim-relocation-smoke-" + Guid.NewGuid().ToString("N"));
    string? previousDataDirectory = Environment.GetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR");
    Environment.SetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR", root);
    var state = new ClashOfRimNetworkState();
    WebApplication app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();

    try
    {
        string baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var client = new ClashOfRimNetworkClient(httpClient);
        string compatibilityManifestJson = BuildSmokeCompatibilityManifestJson("relocation-manifest");

        PrepareWorldSessionResponse prepared = await client.PrepareWorldSessionAsync(
            new PrepareWorldSessionRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                compatibilityManifestJson: compatibilityManifestJson,
                password: string.Empty));
        Require(prepared.Result.Accepted, $"搬迁测试管理员应能准备世界会话：{prepared.Result.ErrorCode} {prepared.Result.Message}");

        SubmitWorldConfigurationResponse submitted = await client.SubmitWorldConfigurationAsync(
            new SubmitWorldConfigurationRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                new WorldConfigurationDto(
                    "world:relocation",
                    "reloc-user",
                    "reloc-colony",
                    DateTimeOffset.UtcNow,
                    "relocation-seed",
                    "30",
                    "Normal",
                    "Normal",
                    "Normal",
                    "Normal",
                    "10000",
                    new[] { "OutlanderCivil" },
                    new[] { new WorldFeatureDto("MountainRange", "搬迁测试山脉", 1f, 1f, 2f, 3f) },
                    new[] { new WorldFactionDto("OutlanderCivil", "搬迁测试外乡人", 0.1f, 0.2f, 0.3f, 1f) },
                    new[] { new WorldRoadDto(1, 2, "DirtPath") },
                    new[] { new WorldObjectBaselineDto("Settlement", 4, "搬迁测试定居点", "OutlanderCivil") },
                    new[]
                    {
                        new PlayerColonySiteDto("reloc-user", "reloc-colony", "WorldObject_100", "Map_0", 101, "旧殖民地"),
                        new PlayerColonySiteDto("neighbor-user", "neighbor-colony", "WorldObject_102", "Map_102", 102, "邻近殖民地")
                    },
                    "Cassandra",
                    "Medium",
                    BuildSmokeWorldTileGeometry())));
        Require(submitted.Result.Accepted, $"搬迁测试应能提交初始世界配置：{submitted.Result.ErrorCode} {submitted.Result.Message}");

        LoginResponse login = await client.LoginAsync(new LoginRequest(
            ProtocolApiVersion.Current,
            "reloc-user",
            "reloc-colony",
            currentSnapshotId: null,
            "relocation-manifest",
            password: string.Empty,
            compatibilityManifestJson: compatibilityManifestJson));
        Require(login.Result.Accepted, $"搬迁测试用户应能登录：{login.Result.ErrorCode} {login.Result.Message}");
        Require(!string.IsNullOrWhiteSpace(login.AuthToken), "搬迁测试登录应返回鉴权令牌");

        SaveSnapshotPackage initialPackage = BuildFixturePackageFor(
            "reloc-user",
            "reloc-colony",
            "reloc-before",
            playerSettlementTile: 101);
        UploadSnapshotResponse initialUpload = await client.UploadSnapshotAsync(new UploadSnapshotMetadataRequest(
            "relocation-upload-before",
            "reloc-user",
            "reloc-colony",
            "reloc-before",
            SnapshotMetadata(initialPackage),
            authToken: login.AuthToken),
            initialPackage.Payload);
        Require(initialUpload.Result.Accepted, "搬迁测试初始快照应能上传");

        AuthoritativeEvent defendingRaid = AuthoritativeEventFactory.Create(
            ServerEventType.Raid,
            new EventParty("raid-attacker", "raid-attacker-colony"),
            new EventParty("reloc-user", "reloc-colony"),
            "relocation-blocked-defender-raid",
            targetOnline: false,
            new RaidEventPayload(
                DefenderSnapshotId: "reloc-before",
                ReturnedSnapshotId: null,
                StartedAtUtc: DateTimeOffset.UtcNow,
                FinishedAtUtc: null,
                Settlement: null,
                AttackForce: new RaidAttackForceRecord(
                    "attacker-snapshot",
                    new[] { "pawn:attacker:1" },
                    Array.Empty<EventThingReference>()),
                OpponentKind: RaidOpponentKind.Player),
            DateTimeOffset.UtcNow,
            new EventTargetContext("WorldObject_100", "Map_0", 101, EventLandingMode.MapEdge));
        state.Ledger.Append(defendingRaid);
        ColonyRelocationResponse blockedByDefendingRaid = await client.PreflightColonyRelocationAsync(
            new PreflightColonyRelocationRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                "reloc-before",
                targetTile: 202,
                idempotencyKey: "relocation-preflight-blocked-defender",
                authToken: login.AuthToken));
        Require(!blockedByDefendingRaid.Result.Accepted, "玩家作为防守方存在未结算袭击时，搬迁预检必须拒绝");
        Equal(ProtocolErrorCode.ServerRejected, blockedByDefendingRaid.Result.ErrorCode, "防守方袭击锁应由服务器拒绝");
        state.Ledger.ChangeStatus(defendingRaid.EventId, ServerEventStatus.AppliedToSnapshot);

        AuthoritativeEvent pendingOldMapGift = AuthoritativeEventFactory.Create(
            ServerEventType.ItemDelivery,
            new EventParty("gift-sender", "gift-sender-colony"),
            new EventParty("reloc-user", "reloc-colony"),
            "relocation-blocked-old-map-gift",
            targetOnline: false,
            new ItemDeliveryEventPayload(
                new[]
                {
                    new EventThingReference(
                        "gift-sender:thing:silver:1",
                        "Silver",
                        StackCount: 25)
                },
                "relocation-smoke-gift"),
            DateTimeOffset.UtcNow,
            new EventTargetContext("WorldObject_100", "Map_0", 101, EventLandingMode.DropPod));
        state.Ledger.Append(pendingOldMapGift);
        ColonyRelocationResponse blockedByPendingOldMapGift = await client.PreflightColonyRelocationAsync(
            new PreflightColonyRelocationRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                "reloc-before",
                targetTile: 202,
                idempotencyKey: "relocation-preflight-blocked-pending-gift",
                authToken: login.AuthToken));
        Require(!blockedByPendingOldMapGift.Result.Accepted, "旧殖民地仍有未处理投递事件时，搬迁预检必须拒绝");
        Equal(ProtocolErrorCode.ServerRejected, blockedByPendingOldMapGift.Result.ErrorCode, "旧地图投递锁应由服务器拒绝");
        state.Ledger.ChangeStatus(pendingOldMapGift.EventId, ServerEventStatus.AppliedToSnapshot);

        ColonyRelocationResponse blockedByNearbyColony = await client.PreflightColonyRelocationAsync(
            new PreflightColonyRelocationRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                "reloc-before",
                targetTile: 103,
                idempotencyKey: "relocation-preflight-blocked-nearby-colony",
                authToken: login.AuthToken));
        Require(!blockedByNearbyColony.Result.Accepted, "搬迁目标紧贴其他玩家殖民地时，服务器预检必须拒绝");
        Equal(ProtocolErrorCode.ServerRejected, blockedByNearbyColony.Result.ErrorCode, "邻近殖民地锁应由服务器拒绝");

        RegisterPlayerColonySitesResponse ordinaryRegistrationRelocation = await client.RegisterPlayerColonySitesAsync(
            new RegisterPlayerColonySitesRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                new[]
                {
                    new PlayerColonySiteDto(
                        "reloc-user",
                        "reloc-colony",
                        "WorldObject_202",
                        "Map_202",
                        202,
                        "错误普通搬迁")
                }));
        Require(!ordinaryRegistrationRelocation.Result.Accepted, "普通地块登记不能把已有殖民地搬迁到新地块");
        Equal(ProtocolErrorCode.ServerRejected, ordinaryRegistrationRelocation.Result.ErrorCode, "普通地块搬迁应由服务器拒绝");
        PlayerColonySiteDto? siteAfterRejectedOrdinaryRegistration = state.WorldConfiguration.Current?.PlayerColonySites
            .SingleOrDefault(site => site.UserId == "reloc-user" && site.ColonyId == "reloc-colony");
        Equal(101, siteAfterRejectedOrdinaryRegistration?.Tile, "普通地块搬迁被拒后服务器权威站点应保持旧地块");

        ColonyRelocationResponse preflight = await client.PreflightColonyRelocationAsync(
            new PreflightColonyRelocationRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                "reloc-before",
                targetTile: 202,
                idempotencyKey: "relocation-preflight",
                authToken: login.AuthToken));
        Require(preflight.Result.Accepted, "搬迁预检应接受合法的新地块");
        Equal(101, preflight.OldSite?.Tile, "搬迁预检应返回旧殖民地地块");

        SaveSnapshotPackage relocatedPackage = BuildFixturePackageForWithLineage(
            "reloc-user",
            "reloc-colony",
            "reloc-after",
            previousSnapshotId: "reloc-before",
            initialUpload.NextLineageToken,
            gameTicks: 1001,
            playerSettlementTile: 202,
            extraMutate: document =>
            {
                AddSettlementMap(document, 9902, "9902", 101, "Faction_0", "旧玩家地点残留");
                AddSettlementMap(document, 9903, "9903", 404, "Faction_1", "搬迁旁路 NPC 据点");
            });
        UploadSnapshotResponse relocatedUpload = await client.UploadSnapshotAsync(new UploadSnapshotMetadataRequest(
            "relocation-upload-after",
            "reloc-user",
            "reloc-colony",
            "reloc-after",
            SnapshotMetadata(relocatedPackage),
            authToken: login.AuthToken,
            confirmationOperation: SnapshotConfirmationOperations.ColonyRelocation),
            relocatedPackage.Payload);
        Require(relocatedUpload.Result.Accepted, "搬迁后的快照应被服务器接受");

        PlayerColonySiteDto? siteBeforeConfirm = state.WorldConfiguration.Current?.PlayerColonySites
            .SingleOrDefault(site => site.UserId == "reloc-user" && site.ColonyId == "reloc-colony");
        Equal(101, siteBeforeConfirm?.Tile, "带搬迁用途上传快照后，服务器站点在确认前仍应保持旧地块");

        ColonyRelocationResponse confirmed = await client.ConfirmColonyRelocationAsync(
            new ConfirmColonyRelocationRequest(
                ProtocolApiVersion.Current,
                "reloc-user",
                "reloc-colony",
                previousSnapshotId: "reloc-before",
                relocatedSnapshotId: "reloc-after",
                targetTile: 202,
                idempotencyKey: "relocation-confirm",
                authToken: login.AuthToken));
        Require(confirmed.Result.Accepted, "搬迁确认应接受最新搬迁快照");
        Equal(202, confirmed.NewSite?.Tile, "搬迁确认响应应返回新地块");

        PlayerColonySiteDto? siteAfterConfirm = state.WorldConfiguration.Current?.PlayerColonySites
            .SingleOrDefault(site => site.UserId == "reloc-user" && site.ColonyId == "reloc-colony");
        Equal(202, siteAfterConfirm?.Tile, "搬迁确认后服务器权威站点应更新到新地块");
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR", previousDataDirectory);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void VerifyServerShopRegistrySemantics()
{
    var registry = new ServerShopRegistry();
    var buyer = new ProtocolIdentity("buyer-user", "buyer-colony", "buyer-snapshot");
    ServerShopListingRecord listing = registry.Upsert(
        listingId: "shop-smoke",
        ServerShopListingKinds.SellToPlayer,
        new ThingReferenceDto("shop-smoke:silver", "Silver", 1, displayLabel: "白银"),
        priceSilver: 25,
        stockCount: 5,
        priceIncreaseRatio: 0,
        ServerShopQualityRequirementModes.AtLeast,
        ServerShopHitPointsRequirementModes.AtLeast,
        actorUserId: "admin-user",
        nowUtc: DateTimeOffset.UnixEpoch);

    Require(listing.StockCount == 5, "服务器商店应保存管理员设置的库存");
    Require(registry.ValidatePurchase("purchase-invalid", listing.ListingId, ServerShopListingKinds.SellToPlayer, 25, 0, 0, buyer).FailureKey == "Shop.InvalidPurchaseCount", "服务器商店应拒绝 0 数量购买");

    bool rejectedCommitCalled = false;
    ServerShopPurchaseResult rejectedCommit = registry.TryPurchaseAfterCommit(
        "purchase-rejected-commit",
        listing.ListingId,
        ServerShopListingKinds.SellToPlayer,
        25,
        75,
        3,
        buyer,
        DateTimeOffset.UnixEpoch,
        _ =>
        {
            rejectedCommitCalled = true;
            return false;
        });
    Require(rejectedCommitCalled, "服务器商店原子购买应在最终校验后调用提交回调");
    Require(!rejectedCommit.Accepted && rejectedCommit.FailureKey == "Shop.SnapshotRejected", "服务器商店应在快照提交失败时拒绝购买");
    Equal(5, registry.Find(listing.ListingId)?.StockCount ?? -1, "服务器商店快照提交失败时不应扣减库存");

    ServerShopPurchaseResult firstPurchase = registry.TryPurchaseAfterCommit(
        "purchase-1",
        listing.ListingId,
        ServerShopListingKinds.SellToPlayer,
        25,
        75,
        3,
        buyer,
        DateTimeOffset.UnixEpoch,
        _ => true);
    Require(firstPurchase.Accepted, "服务器商店应允许购买不超过库存的数量");
    Equal(2, firstPurchase.RemainingStockCount, "服务器商店购买后应扣减库存");

    bool duplicateCommitCalled = false;
    ServerShopPurchaseResult duplicatePurchase = registry.TryPurchaseAfterCommit(
        "purchase-1",
        listing.ListingId,
        ServerShopListingKinds.SellToPlayer,
        25,
        25,
        1,
        buyer,
        DateTimeOffset.UnixEpoch,
        _ =>
        {
            duplicateCommitCalled = true;
            return true;
        });
    Require(duplicatePurchase.Accepted && duplicatePurchase.Duplicate, "服务器商店重复购买键应走幂等成功");
    Require(!duplicateCommitCalled, "服务器商店重复购买不应再次提交快照");
    Equal(2, registry.Find(listing.ListingId)?.StockCount ?? -1, "服务器商店重复购买不应二次扣库存");

    ServerShopPurchaseResult tooMany = registry.ValidatePurchase("purchase-too-many", listing.ListingId, ServerShopListingKinds.SellToPlayer, 25, 75, 3, buyer);
    Require(!tooMany.Accepted && tooMany.FailureKey == "Shop.NotEnoughStock", "服务器商店应拒绝超过剩余库存的购买");

    ServerShopPurchaseResult secondPurchase = registry.TryPurchase("purchase-2", listing.ListingId, ServerShopListingKinds.SellToPlayer, 25, 50, 2, buyer, DateTimeOffset.UnixEpoch);
    Require(secondPurchase.Accepted, "服务器商店应允许买空库存");
    Equal(0, secondPurchase.RemainingStockCount, "服务器商店库存可归零");
    Require(registry.List().Any(item => item.ListingId == listing.ListingId && item.StockCount == 0), "服务器商店库存归零后不应自动下架");

    ServerShopPurchaseResult outOfStock = registry.ValidatePurchase("purchase-empty", listing.ListingId, ServerShopListingKinds.SellToPlayer, 25, 25, 1, buyer);
    Require(!outOfStock.Accepted && outOfStock.FailureKey == "Shop.OutOfStock", "服务器商店库存为 0 时应禁止继续购买");
}

static void VerifyPersistentRegistries()
{
    string root = Path.Combine(Path.GetTempPath(), "clash-of-rim-network-persistence-" + Guid.NewGuid().ToString("N"));
    try
    {
        string worldPath = Path.Combine(root, "world-configuration.json");
        string baselinePath = Path.Combine(root, "admin-baseline.json");
        var worldRegistry = new WorldConfigurationRegistry(worldPath);

        WorldSessionState prepared = worldRegistry.Prepare("admin-user");
        Require(prepared.IsAdministrator, "持久化世界注册表应记录首个管理员");

        WorldConfigurationDto configuration = BuildMinimalWorldConfiguration();
        WorldSessionState submitted = worldRegistry.Submit("admin-user", configuration);
        Require(!submitted.WorldConfigured, "世界配置在世界底图持久化前不应开放给玩家");
        WorldSubstrateStoreResult storedSubstrate = worldRegistry.StoreWorldSubstrate(
            "admin-user",
            "colony-a",
            configuration.WorldConfigurationId,
            BuildSmokeWorldSubstrate(configuration.TileGeometry));
        Require(storedSubstrate.Accepted, "持久化世界注册表应接受管理员世界底图包");

        WorldSessionState registered = worldRegistry.RegisterPlayerColonySites(
            "user-b",
            "colony-b",
            new[]
            {
                new PlayerColonySiteDto("ignored-user", "ignored-colony", "WorldObject_20", "Map_20", 20, "玩家B")
            });
        Require(registered.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-b" && site.Tile == 20) == true, "持久化世界注册表应规范化并记录玩家殖民地地块");

        var reopenedWorldRegistry = new WorldConfigurationRegistry(worldPath);
        WorldSessionState reopened = reopenedWorldRegistry.Prepare("user-c");
        Require(!reopened.IsAdministrator, "重启后后续用户不应覆盖已持久化管理员");
        Equal("admin-user", reopened.AdministratorUserId, "重启后的管理员 ID");
        Equal("persistent-seed", reopened.WorldConfiguration?.SeedString, "重启后的世界种子");
        Require(reopened.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-b" && site.Tile == 20) == true, "重启后应保留玩家殖民地地块");
        Require(reopened.WorldConfiguration?.TileGeometry?.Layers.Count == 2, "重启后应从独立二进制槽恢复世界地块几何");
        Require(File.Exists(Path.Combine(root, "world-configuration.binary", "world-substrate.bin")), "世界底图应保存为独立二进制文件");

        var baselineRegistry = new AdminBaselineRegistry(baselinePath);
        baselineRegistry.Submit(new SubmitAdminBaselineRequest(
            ProtocolApiVersion.Current,
            "admin-user",
            "colony-a",
            DateTimeOffset.UnixEpoch,
            new[]
            {
                new StandardMarketValueDto("Silver", 1f),
                new StandardMarketValueDto("Wastepack", 0f)
            },
            new[]
            {
                new TrapClassificationDto(
                    "TrapSpike",
                    "RimWorld.Building_TrapDamager",
                    "ludeon.rimworld",
                    "Core",
                    "ApprovedByInheritance",
                    "inherits:RimWorld.Building_Trap",
                    inheritsBuildingTrap: true,
                    adminApproved: false)
            }));

        var reopenedBaselineRegistry = new AdminBaselineRegistry(baselinePath);
        AdminBaselineSnapshot? reopenedBaseline = reopenedBaselineRegistry.Current;
        Require(reopenedBaseline is not null, "重启后应恢复管理员基线");
        Equal(2, reopenedBaseline!.StandardMarketValuePerThing.Count, "重启后的标准物品价格数量");
        Equal(1, reopenedBaseline.TrapApprovedCount, "重启后的陷阱批准数量");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void VerifyPendingLoginSessionCanBeReplaced()
{
    var sessions = new LoginSessionRegistry();
    DateTimeOffset now = DateTimeOffset.UtcNow;

    Require(sessions.TryCreate("user-a", "colony-a", now, out string firstSession), "初次登录应创建 pending 会话");
    Require(!string.IsNullOrWhiteSpace(firstSession), "初次登录应返回会话 ID");
    Require(sessions.TryCreate("user-a", "colony-a", now.AddSeconds(5), out string replacementSession), "未开始在线连接的 pending 会话应允许被重新登录替换");
    Require(!string.IsNullOrWhiteSpace(replacementSession)
        && !string.Equals(firstSession, replacementSession, StringComparison.Ordinal), "重新登录应生成新的会话 ID");
    Require(!sessions.TryBeginStream("user-a", "colony-a", firstSession, now.AddSeconds(6)), "被替换的旧 pending 会话不应再能上线");
    Require(sessions.TryBeginStream("user-a", "colony-a", replacementSession, now.AddSeconds(6)), "替换后的会话应能开始在线连接");
    Require(!sessions.TryCreate("user-a", "colony-a", now.AddSeconds(7), out _), "已开始在线连接的会话仍应阻止重复登录");

    Console.WriteLine("通过：pending 登录会话可被重新登录替换");
}

static async Task VerifyOnlinePresenceLeaseExpiryAsync()
{
    var presence = new OnlinePresenceRegistry(TimeSpan.FromMilliseconds(40));
    Require(presence.TryConnectExclusive("stale-user", out OnlinePresenceLease? staleLease), "在线租约应可建立");
    Require(staleLease is not null, "在线租约对象不应为空");
    Require(presence.IsUserOnline("stale-user"), "刚建立的租约应显示在线");
    await Task.Delay(90);
    Require(!presence.IsUserOnline("stale-user"), "过期且未续租的在线连接不应继续显示在线");
    Require(presence.TryConnectExclusive("stale-user", out OnlinePresenceLease? replacementLease), "过期在线记录应允许重新建立租约");
    replacementLease?.Dispose();

    var refreshed = new OnlinePresenceRegistry(TimeSpan.FromMilliseconds(80));
    Require(refreshed.TryConnectExclusive("refresh-user", out OnlinePresenceLease? refreshedLease), "续租测试应可建立租约");
    await Task.Delay(40);
    Require(refreshedLease!.Touch(), "有效租约应可续期");
    await Task.Delay(50);
    Require(refreshed.IsUserOnline("refresh-user"), "续期后的租约不应按初始过期时间离线");
    refreshedLease.Dispose();
    Require(!refreshed.IsUserOnline("refresh-user"), "释放租约后应立即离线");
}

static async Task VerifyDefaultPersistentServerStateAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "clash-of-rim-default-server-persistence-" + Guid.NewGuid().ToString("N"));
    string? previousDataDirectory = Environment.GetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR");
    Environment.SetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR", root);
    byte[] expectedPersistentPayload = Array.Empty<byte>();
    try
    {
        WebApplication firstApp = ClashOfRimNetworkServer.Build(Array.Empty<string>());
        firstApp.Urls.Add("http://127.0.0.1:0");
        await firstApp.StartAsync();
        try
        {
            var firstClient = new ClashOfRimNetworkClient(new HttpClient { BaseAddress = new Uri(ServerAddress(firstApp)) });
            string compatibilityManifestJson = BuildSmokeCompatibilityManifestJson("persistent-manifest");
            PrepareWorldSessionResponse prepared = await firstClient.PrepareWorldSessionAsync(
                new PrepareWorldSessionRequest(
                    ProtocolApiVersion.Current,
                    "admin-user",
                    "colony-a",
                    compatibilityManifestJson: compatibilityManifestJson,
                    password: string.Empty));
            Require(prepared.IsAdministrator, "默认持久化服务端应记录首个管理员");

            SubmitWorldConfigurationResponse submitted = await firstClient.SubmitWorldConfigurationAsync(
                new SubmitWorldConfigurationRequest(
                    ProtocolApiVersion.Current,
                    "admin-user",
                    "colony-a",
                    BuildMinimalWorldConfiguration()));
            Require(submitted.Result.Accepted, "默认持久化服务端应接受世界配置");

            RegisterPlayerColonySitesResponse registered = await firstClient.RegisterPlayerColonySitesAsync(
                new RegisterPlayerColonySitesRequest(
                    ProtocolApiVersion.Current,
                    "user-b",
                    "colony-b",
                    new[] { new PlayerColonySiteDto("user-b", "colony-b", "WorldObject_20", "Map_20", 20, "玩家B") }));
            Require(registered.Result.Accepted, "默认持久化服务端应接受玩家地块登记");

            SubmitAdminBaselineResponse baseline = await firstClient.SubmitAdminBaselineAsync(
                new SubmitAdminBaselineRequest(
                    ProtocolApiVersion.Current,
                    "admin-user",
                    "colony-a",
                    DateTimeOffset.UnixEpoch,
                    new[] { new StandardMarketValueDto("Silver", 1f) },
                    Array.Empty<TrapClassificationDto>()));
            Require(baseline.Result.Accepted, "默认持久化服务端应接受管理员基线");

            LoginResponse adminLogin = await firstClient.LoginAsync(new LoginRequest(
                ProtocolApiVersion.Current,
                "admin-user",
                "colony-a",
                currentSnapshotId: null,
                compatibilityDigest: "persistent-manifest",
                password: string.Empty,
                compatibilityManifestJson: compatibilityManifestJson));
            Require(adminLogin.Result.Accepted, $"默认持久化服务端应允许管理员登录：{adminLogin.Result.ErrorCode} {adminLogin.Result.Message}");
            Require(!string.IsNullOrWhiteSpace(adminLogin.AuthToken), "默认持久化服务端登录应返回鉴权令牌");

            SaveSnapshotPackage package = BuildFixturePackageFor("admin-user", "colony-a", "admin-existing-snapshot");
            expectedPersistentPayload = package.Payload;
            UploadSnapshotResponse upload = await firstClient.UploadSnapshotAsync(new UploadSnapshotMetadataRequest(
                "upload:admin-user:colony-a:admin-existing-snapshot",
                "admin-user",
                "colony-a",
                "admin-existing-snapshot",
                SnapshotMetadata(package),
                authToken: adminLogin.AuthToken),
                package.Payload);
            Require(upload.Result.Accepted, "默认持久化服务端应接受已有玩家快照");

            DiplomacyEventResponse persistentWar = await firstClient.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
                "diplomacy:persistent:admin-user:user-b:war",
                new ProtocolIdentity("admin-user", "colony-a", "admin-existing-snapshot"),
                new ProtocolIdentity("user-b", "colony-b", "user-b-existing-snapshot"),
                "WarDeclaration",
                "持久化宣战测试。",
                null));
            Require(persistentWar.Result.Accepted, "默认持久化服务端应接受宣战事件");
            Equal("Hostile", persistentWar.RelationKind, "宣战创建后应立即形成敌对关系");
        }
        finally
        {
            await firstApp.StopAsync();
            await firstApp.DisposeAsync();
        }

        Require(File.Exists(Path.Combine(root, "server.sqlite")), "默认持久化服务端应创建 SQL 数据库");

        WebApplication secondApp = ClashOfRimNetworkServer.Build(Array.Empty<string>());
        secondApp.Urls.Add("http://127.0.0.1:0");
        await secondApp.StartAsync();
        try
        {
            var secondClient = new ClashOfRimNetworkClient(new HttpClient { BaseAddress = new Uri(ServerAddress(secondApp)) });
            PrepareWorldSessionResponse reopened = await secondClient.PrepareWorldSessionAsync(
                new PrepareWorldSessionRequest(
                    ProtocolApiVersion.Current,
                    "user-c",
                    "colony-c",
                    password: string.Empty,
                    compatibilityManifestId: "persistent-manifest"));
            Require(!reopened.IsAdministrator, "默认持久化服务端重启后不应重置管理员");
            Equal("admin-user", reopened.AdministratorUserId, "默认持久化服务端重启后的管理员");
            Require(!reopened.HasExistingColony, "未登记的新用户不应被识别为已有殖民地");
            GetWorldConfigurationResponse reopenedWorld = await secondClient.GetWorldConfigurationAsync(
                new GetWorldConfigurationRequest(
                    ProtocolApiVersion.Current,
                    "user-c",
                    "colony-c",
                    password: string.Empty));
            Require(reopenedWorld.Result.Accepted, $"默认持久化服务端重启后应允许下载世界配置：{reopenedWorld.Result.ErrorCode} {reopenedWorld.Result.Message}");
            Equal("persistent-seed", reopenedWorld.WorldConfiguration?.SeedString, "默认持久化服务端重启后的世界配置");
            Require(reopenedWorld.WorldConfiguration?.PlayerColonySites.Any(site => site.UserId == "user-b" && site.Tile == 20) == true, "默认持久化服务端重启后应保留玩家地块");

            PrepareWorldSessionResponse existingAdmin = await secondClient.PrepareWorldSessionAsync(
                new PrepareWorldSessionRequest(
                    ProtocolApiVersion.Current,
                    "admin-user",
                    "colony-a",
                    password: string.Empty,
                    compatibilityManifestId: "persistent-manifest"));
            Require(existingAdmin.HasExistingColony, "重启后同一管理员殖民地应被识别为已有殖民地");
            Equal("admin-existing-snapshot", existingAdmin.LatestSnapshotId, "重启后应返回已有殖民地最新快照");
            LoginResponse reopenedAdminLogin = await secondClient.LoginAsync(new LoginRequest(
                ProtocolApiVersion.Current,
                "admin-user",
                "colony-a",
                "admin-existing-snapshot",
                "persistent-manifest",
                password: string.Empty,
                compatibilityManifestId: "persistent-manifest"));
            Require(reopenedAdminLogin.Result.Accepted, $"默认持久化服务端重启后应允许管理员重新登录：{reopenedAdminLogin.Result.ErrorCode} {reopenedAdminLogin.Result.Message}");
            Require(!string.IsNullOrWhiteSpace(reopenedAdminLogin.AuthToken), "默认持久化服务端重启后登录应返回鉴权令牌");
            LoginResponse observerLogin = await secondClient.LoginAsync(new LoginRequest(
                ProtocolApiVersion.Current,
                "observer-user",
                "observer-colony",
                currentSnapshotId: null,
                compatibilityDigest: "persistent-manifest",
                password: string.Empty,
                compatibilityManifestId: "persistent-manifest"));
            Require(observerLogin.Result.Accepted, $"默认持久化服务端应允许观察者登录：{observerLogin.Result.ErrorCode} {observerLogin.Result.Message}");
            Require(!string.IsNullOrWhiteSpace(observerLogin.AuthToken), "观察者登录应返回鉴权令牌");
            DownloadLatestSnapshotResponse unauthorizedDownload = await secondClient.DownloadLatestSnapshotAsync(
                new DownloadLatestSnapshotRequest("admin-user", "colony-a", observerLogin.AuthToken));
            Require(!unauthorizedDownload.Result.Accepted, "观察者没有授权 scope 时不应能下载他人快照");
            DownloadLatestSnapshotResponse scoutDownload = await secondClient.DownloadLatestSnapshotAsync(
                new DownloadLatestSnapshotRequest(
                    "admin-user",
                    "colony-a",
                    observerLogin.AuthToken,
                    authorizationEventId: null,
                    authorizationScope: "Scout"));
            Require(scoutDownload.Result.Accepted, "观察者应能以侦察 scope 下载非盟友快照元数据");
            Equal("admin-existing-snapshot", scoutDownload.Package?.SnapshotId, "侦察元数据应匹配目标最新快照");
            DownloadLatestSnapshotResponse downloaded = await secondClient.DownloadLatestSnapshotAsync(
                new DownloadLatestSnapshotRequest("admin-user", "colony-a", reopenedAdminLogin.AuthToken));
            Require(downloaded.Result.Accepted, "重启后应允许下载已有殖民地快照元数据");
            Equal("admin-existing-snapshot", downloaded.Package?.SnapshotId, "下载元数据应匹配最新快照");
            byte[] downloadedPayload = await secondClient.DownloadLatestSnapshotPayloadAsync(
                new DownloadLatestSnapshotPayloadRequest(
                    "admin-user",
                    "colony-a",
                    "admin-existing-snapshot",
                    reopenedAdminLogin.AuthToken));
            Require(downloadedPayload.SequenceEqual(expectedPersistentPayload), "重启后下载的快照载荷应与上传载荷一致");

            PrepareWorldSessionResponse existingRegisteredSite = await secondClient.PrepareWorldSessionAsync(
                new PrepareWorldSessionRequest(
                    ProtocolApiVersion.Current,
                    "user-b",
                    "colony-b",
                    password: string.Empty,
                    compatibilityManifestId: "persistent-manifest"));
            Require(existingRegisteredSite.HasExistingColony, "重启后仅登记地块的玩家也应被识别为已有殖民地");

            ClashOfRimNetworkState reopenedState = secondApp.Services.GetRequiredService<ClashOfRimNetworkState>();
            Require(reopenedState.AdminBaseline.Current is not null, "默认持久化服务端重启后应保留管理员基线");
            Equal(
                "Hostile",
                reopenedState.DiplomacyRelations.GetRelationKind("admin-user", "colony-a", "user-b", "colony-b"),
                "默认持久化服务端重启后应保留外交关系");
        }
        finally
        {
            await secondApp.StopAsync();
            await secondApp.DisposeAsync();
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR", previousDataDirectory);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task VerifyDiplomacyRelationCooldownAsync()
{
    var state = new ClashOfRimNetworkState(
        serverConfiguration: new ClashOfRimServerConfiguration(
            diplomacyRelationChangeCooldown: TimeSpan.FromDays(1)));
    WebApplication app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();
    try
    {
        var client = new ClashOfRimNetworkClient(new HttpClient { BaseAddress = new Uri(ServerAddress(app)) });
        DiplomacyEventResponse war = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
            "cooldown:user-a:user-b:war",
            new ProtocolIdentity("user-a", "colony-a", "snapshot-a"),
            new ProtocolIdentity("user-b", "colony-b", "snapshot-b"),
            "WarDeclaration",
            "冷却测试宣战。",
            null));
        Require(war.Result.Accepted, "冷却测试应允许首次宣战");
        Equal("Hostile", war.RelationKind, "首次宣战后关系应为敌对");

        DiplomacyEventResponse duplicateWar = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
            "cooldown:user-a:user-b:war",
            new ProtocolIdentity("user-a", "colony-a", "snapshot-a"),
            new ProtocolIdentity("user-b", "colony-b", "snapshot-b"),
            "WarDeclaration",
            "冷却测试宣战重试。",
            null));
        Require(duplicateWar.Result.Accepted, "冷却期内幂等重试应返回既有事件而不是被冷却拒绝");
        Equal(ProtocolErrorCode.DuplicateRequest, duplicateWar.Result.ErrorCode, "冷却期内幂等重试应保留重复请求语义");

        DiplomacyEventResponse blockedPeace = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
            "cooldown:user-a:user-b:peace",
            new ProtocolIdentity("user-a", "colony-a", "snapshot-a"),
            new ProtocolIdentity("user-b", "colony-b", "snapshot-b"),
            "PeaceRequest",
            "冷却期内请求求和。",
            DateTimeOffset.UtcNow.AddDays(3)));
        Require(!blockedPeace.Result.Accepted, "同一对玩家冷却期内不应允许再次改变外交关系");
        Equal(ProtocolErrorCode.ServerRejected, blockedPeace.Result.ErrorCode, "同一对玩家冷却期内外交变化应由服务器拒绝");
        Equal("Hostile", blockedPeace.RelationKind, "冷却拒绝应返回当前这对玩家的敌对关系");

        DiplomacyEventResponse otherPairAlliance = await client.CreateDiplomacyEventAsync(new CreateDiplomacyEventRequest(
            "cooldown:user-a:user-c:alliance",
            new ProtocolIdentity("user-a", "colony-a", "snapshot-a"),
            new ProtocolIdentity("user-c", "colony-c", "snapshot-c"),
            "AllianceRequest",
            "另一对玩家不受 A-B 冷却影响。",
            DateTimeOffset.UtcNow.AddDays(3)));
        Require(otherPairAlliance.Result.Accepted, "A-B 外交冷却不应影响 A-C 的外交提交");
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

static string ServerAddress(WebApplication app)
{
    return app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!
        .Addresses.Single();
}

static PrepareWorldSessionRequest SmokePrepareWorldSessionRequest(string userId, string colonyId)
{
    return new PrepareWorldSessionRequest(
        ProtocolApiVersion.Current,
        userId,
        colonyId,
        password: string.Empty,
        compatibilityManifestId: "smoke-main-manifest");
}

static GetWorldConfigurationRequest SmokeGetWorldConfigurationRequest(string userId, string colonyId)
{
    return new GetWorldConfigurationRequest(
        ProtocolApiVersion.Current,
        userId,
        colonyId,
        password: string.Empty);
}

static WorldConfigurationDto BuildMinimalWorldConfiguration()
{
    return new WorldConfigurationDto(
        "world:persistent",
        "admin-user",
        "colony-a",
        DateTimeOffset.UnixEpoch,
        "persistent-seed",
        "30",
        "Normal",
        "Normal",
        "Normal",
        "Normal",
        "10000",
        new[] { "OutlanderCivil" },
        Array.Empty<WorldFeatureDto>(),
        Array.Empty<WorldFactionDto>(),
        Array.Empty<WorldRoadDto>(),
        Array.Empty<WorldObjectBaselineDto>(),
        new[] { new PlayerColonySiteDto("admin-user", "colony-a", "WorldObject_10", "Map_10", 10, "管理员") },
        "Cassandra",
        "Medium",
        BuildSmokeWorldTileGeometry());
}

static string BuildSmokeCompatibilityManifestJson(string manifestId)
{
    return CompatibilityManifestJsonWriter.Write(new CompatibilityManifest
    {
        SchemaVersion = 1,
        ManifestId = manifestId,
        ProtocolVersion = ProtocolApiVersion.Current,
        RimWorldVersion = "1.6.4633",
        ConfigVersion = "smoke",
        ConfigSha256 = "smoke",
        Mods = new[]
        {
            new ModManifestEntry
            {
                LoadOrder = 0,
                PackageId = "ludeon.rimworld",
                Name = "Core",
                Source = "Smoke",
                Role = ModCompatibilityRole.Required
            }
        }
    });
}

static IReadOnlyList<WorldConfigurationExtensionDto> IdeoCatalogExtension(params WorldIdeoSummaryDto[] ideos)
{
    if (ideos.Length == 0)
    {
        return Array.Empty<WorldConfigurationExtensionDto>();
    }

    WorldConfigurationExtensionKey key = BuiltInWorldExtensionKey("ideo");
    return new[]
    {
        new WorldConfigurationExtensionDto(
            key.ProviderId,
            key.Kind,
            "1",
            JsonSerializer.Serialize(ideos, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new Dictionary<string, string?>
            {
                ["count"] = ideos.Length.ToString()
            })
    };
}

static IReadOnlyList<WorldConfigurationExtensionDto> PollutionExtension(params WorldPollutedTileDto[] pollutedTiles)
{
    if (pollutedTiles.Length == 0)
    {
        return Array.Empty<WorldConfigurationExtensionDto>();
    }

    IWorldTileFloatLayerExtensionProvider provider = BuiltInTileFloatProvider("pollution");
    string layerId = BuiltInTileFloatLayerId(provider, "pollution");
    WorldConfigurationExtensionDto? extension = provider.BuildTileFloatLayerExtension(
        layerId,
        pollutedTiles
            .Select(tile => new WorldTileFloatLayerValue(tile.Tile, tile.Pollution))
            .ToList());
    return extension is null ? Array.Empty<WorldConfigurationExtensionDto>() : new[] { extension };
}

static IReadOnlyList<WorldConfigurationExtensionDto> WorldDlcExtensions(
    IReadOnlyList<WorldPollutedTileDto> pollutedTiles,
    params WorldIdeoSummaryDto[] ideos)
{
    return PollutionExtension(pollutedTiles.ToArray())
        .Concat(IdeoCatalogExtension(ideos))
        .ToList();
}

static IReadOnlyList<WorldIdeoSummaryDto> ReadIdeoExtension(WorldConfigurationDto? configuration)
{
    return ReadWorldConfigurationExtension<WorldIdeoSummaryDto>(configuration, BuiltInWorldExtensionKey("ideo"));
}

static IReadOnlyList<WorldPollutedTileDto> ReadPollutionExtension(WorldConfigurationDto? configuration)
{
    if (configuration is null)
    {
        return Array.Empty<WorldPollutedTileDto>();
    }

    IWorldTileFloatLayerExtensionProvider provider = BuiltInTileFloatProvider("pollution");
    string layerId = BuiltInTileFloatLayerId(provider, "pollution");
    return provider.ReadTileFloatLayer(layerId, configuration.Extensions)
        .Select(tile => new WorldPollutedTileDto(tile.Tile, tile.Value))
        .ToList();
}

static WorldConfigurationExtensionKey BuiltInWorldExtensionKey(string kindContains)
{
    return BuiltInDlcServerPlugins.Descriptors
        .SelectMany(descriptor => descriptor.WorldConfigurationExtensionProviders ?? Array.Empty<IWorldConfigurationExtensionProvider>())
        .SelectMany(provider => provider.HandledKeys)
        .Single(key => key.Kind.Contains(kindContains, StringComparison.OrdinalIgnoreCase));
}

static IWorldTileFloatLayerExtensionProvider BuiltInTileFloatProvider(string layerContains)
{
    return BuiltInDlcServerPlugins.Descriptors
        .SelectMany(descriptor => descriptor.WorldConfigurationExtensionProviders ?? Array.Empty<IWorldConfigurationExtensionProvider>())
        .OfType<IWorldTileFloatLayerExtensionProvider>()
        .Single(provider => provider.HandledTileFloatLayers.Any(layer => layer.Contains(layerContains, StringComparison.OrdinalIgnoreCase)));
}

static string BuiltInTileFloatLayerId(IWorldTileFloatLayerExtensionProvider provider, string layerContains)
{
    return provider.HandledTileFloatLayers.Single(layer => layer.Contains(layerContains, StringComparison.OrdinalIgnoreCase));
}

static IReadOnlyList<T> ReadWorldConfigurationExtension<T>(
    WorldConfigurationDto? configuration,
    WorldConfigurationExtensionKey key)
{
    WorldConfigurationExtensionDto? extension = configuration?.Extensions.FirstOrDefault(extension =>
        string.Equals(extension.ProviderId, key.ProviderId, StringComparison.Ordinal)
        && string.Equals(extension.Kind, key.Kind, StringComparison.Ordinal));
    if (string.IsNullOrWhiteSpace(extension?.PayloadJson))
    {
        return Array.Empty<T>();
    }

    List<T>? parsed = JsonSerializer.Deserialize<List<T>>(
        extension.PayloadJson!,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (parsed is null)
    {
        return Array.Empty<T>();
    }

    return parsed;
}

static WorldTileGeometryDto BuildSmokeWorldTileGeometry()
{
    return new WorldTileGeometryDto(new[]
    {
        new WorldTileLayerGeometryDto(
            0,
            "Surface",
            1f,
            new[]
            {
                new WorldTileCenterDto(10, 1f, 0f, 0f),
                new WorldTileCenterDto(123, 1f, 0f, 0f),
                new WorldTileCenterDto(150, 0f, 1f, 0f),
                new WorldTileCenterDto(101, 0f, 0f, 1f),
                new WorldTileCenterDto(102, 0f, 1f, 0f),
                new WorldTileCenterDto(103, 0.54f, 0.84f, 0f),
                new WorldTileCenterDto(202, -1f, 0f, 0f)
            }),
        new WorldTileLayerGeometryDto(
            1,
            "Orbit",
            1f,
            new[]
            {
                new WorldTileCenterDto(900, 2f, 0f, 0f)
            })
    });
}

static byte[] BuildSmokeWorldSubstrate(WorldTileGeometryDto? geometry)
{
    byte[] geometryBytes = WorldTileGeometryBinaryCodec.Encode(geometry)
        ?? throw new InvalidOperationException("Smoke geometry cannot be empty.");
    return WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(
        persistentRandomValue: 123,
        gridXml: "<grid><layers><keys><li>0</li></keys><values><li><def>Surface</def></li></values></layers></grid>",
        featuresXml: "<features />",
        landmarksXml: "<landmarks />",
        tileGeometryPayload: geometryBytes));
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}：期望 {expected}，实际 {actual}");
    }
}

static void VerifyAchievementRegistryThresholdDefinitions()
{
    var registry = new AchievementRegistry();
    DateTimeOffset now = DateTimeOffset.UtcNow;

    registry.RecordSnapshotMetricAchievements(
        "user-a",
        "colony-a",
        "snapshot-low",
        new Dictionary<string, long>
        {
            [AchievementRegistry.MetricColonyWealth] = 499_999
        },
        additionalDefinitions: null,
        now);
    Require(!registry.ListAggregates().Any(), "财富未达到阈值时不应授予财富成就");

    registry.RecordSnapshotMetricAchievements(
        "user-a",
        "colony-a",
        "snapshot-hit",
        new Dictionary<string, long>
        {
            [AchievementRegistry.MetricColonyWealth] = 500_000
        },
        additionalDefinitions: null,
        now.AddMinutes(1));

    AchievementAggregateRecord wealthAchievement = registry.ListAggregates().Single();
    Equal("wealth_500000", wealthAchievement.AchievementId, "财富阈值成就 ID 应稳定");
    Equal(500_000L, wealthAchievement.Value, "财富阈值成就应记录触发时的财富");
    Equal(AchievementColors.Blue, wealthAchievement.Color, "财富阈值成就应使用蓝色颜色");

    registry.RecordSnapshotMetricAchievements(
        "user-a",
        "colony-a",
        "snapshot-later",
        new Dictionary<string, long>
        {
            [AchievementRegistry.MetricColonyWealth] = 600_000
        },
        additionalDefinitions: null,
        now.AddMinutes(2));
    Equal(1, registry.ListAggregates().Count, "同一阈值成就不应重复授予");
    Equal(600_000L, registry.ListAggregates().Single().Value, "Max 聚合阈值成就应随后续更高快照刷新记录值");

    AchievementDefinition pluginDefinition = AchievementDefinition.NumericThreshold(
        achievementId: "custom_metric_10",
        metricId: "custom_metric",
        threshold: 10,
        category: AchievementRegistry.CategorySnapshot,
        labelKey: "ClashOfRim.Achievement.CustomMetric",
        iconId: null,
        color: AchievementColors.Purple);
    registry.RecordSnapshotMetricAchievements(
        "user-a",
        "colony-a",
        "snapshot-plugin",
        new Dictionary<string, long>
        {
            ["custom_metric"] = 10
        },
        new[] { pluginDefinition },
        now.AddMinutes(3));
    Require(
        registry.ListAggregates().Any(record => record.AchievementId == "custom_metric_10"),
        "外部注册的阈值成就应能通过上传后指标流程授予");
    Equal(
        AchievementColors.Purple,
        registry.ListAggregates().Single(record => record.AchievementId == "custom_metric_10").Color,
        "外部注册的阈值成就应保留自定义颜色");

    var endgameRegistry = new AchievementRegistry();
    endgameRegistry.RecordSnapshotMetricAchievements(
        "user-a",
        "colony-a",
        "snapshot-no-launch",
        new Dictionary<string, long>
        {
            [AchievementRegistry.MetricColonistsLaunched] = 0
        },
        additionalDefinitions: null,
        now);
    Require(!endgameRegistry.ListAggregates().Any(), "未送走殖民者时不应授予飞船人数成就");

    endgameRegistry.RecordSnapshotMetricAchievements(
        "user-a",
        "colony-a",
        "snapshot-launched",
        new Dictionary<string, long>
        {
            [AchievementRegistry.MetricColonistsLaunched] = 3
        },
        additionalDefinitions: null,
        now.AddMinutes(1));
    AchievementAggregateRecord launchedAchievement = endgameRegistry.ListAggregates().Single();
    Equal("waiting_for_the_sun", launchedAchievement.AchievementId, "飞船人数成就应由快照累计人数指标触发");
    Equal(3L, launchedAchievement.Value, "飞船人数成就应记录快照中的原版累计送走人数");

    endgameRegistry.RecordSnapshotMetricAchievements(
        "user-a",
        "colony-a",
        "snapshot-launched-more",
        new Dictionary<string, long>
        {
            [AchievementRegistry.MetricColonistsLaunched] = 5
        },
        additionalDefinitions: null,
        now.AddMinutes(2));
    Equal(1, endgameRegistry.ListAggregates().Count, "飞船人数成就不应因后续快照重复解锁");
    Equal(5L, endgameRegistry.ListAggregates().Single().Value, "飞船人数成就应随快照累计人数刷新最大值");
}

static void VerifyPlayerRegistryColonyTombstones()
{
    string root = Path.Combine(Path.GetTempPath(), "clash-of-rim-player-registry-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string persistencePath = Path.Combine(root, "players.json");
    var registry = new PlayerRegistry(persistencePath);
    DateTimeOffset now = DateTimeOffset.Parse("2026-06-06T00:00:00Z");

    registry.Record("user-a", "debug-colony-a", "snapshot-a", now);
    Equal("debug-colony-a", registry.ResolveActiveColonyId("user-a", "ignored-colony", now), "活动用户应继续使用原活动殖民地实例");

    registry.MarkDeleted("user-a", "debug-colony-a", "snapshot-a", now.AddMinutes(1));
    Require(registry.List().Count == 0, "已废弃殖民地实例不应出现在活动玩家列表");
    Require(registry.IsDeleted("user-a", "debug-colony-a"), "废弃实例应保留墓碑");
    Require(registry.ListDeleted().Count == 1, "应记录一个废弃实例墓碑");

    registry.Record("user-a", "debug-colony-a", "snapshot-old", now.AddMinutes(2));
    Require(registry.List().Count == 0, "废弃实例不应被旧客户端重新登记为活动殖民地");

    string replacement = registry.ResolveActiveColonyId("user-a", "debug-colony-a", now.AddMinutes(3));
    Require(!string.Equals(replacement, "debug-colony-a", StringComparison.Ordinal), "同名重建应分配新的殖民地实例");
    registry.Record("user-a", replacement, "snapshot-new", now.AddMinutes(4));
    PlayerSessionRecord active = registry.List().Single();
    Equal(replacement, active.ColonyId, "新殖民地实例应成为当前活动实例");

    var reopened = new PlayerRegistry(persistencePath);
    Require(reopened.IsDeleted("user-a", "debug-colony-a"), "废弃实例墓碑应持久化");
    Equal(replacement, reopened.List().Single().ColonyId, "重启后新殖民地实例应保持活动");

    Directory.Delete(root, recursive: true);
}

static async Task VerifyPlayerListUsesLatestSnapshotWealthFallbackAsync()
{
    DateTimeOffset now = DateTimeOffset.Parse("2026-06-14T00:00:00Z");
    var state = new ClashOfRimNetworkState();
    state.Players.Record("score-user", "old-colony", "old-snapshot", now);
    var identity = new SnapshotIdentity("score-user", "new-colony", "new-snapshot");
    state.SnapshotStore.StoreLatest(new LatestSnapshotRecord(
        identity,
        new SaveSnapshotEnvelope(
            "clash-of-rim-snapshot-v1",
            identity,
            now.AddMinutes(1),
            "new-snapshot.rws",
            "1.6.4850",
            SnapshotPayloadEncoding.RawRws,
            OriginalSaveBytes: 100,
            PayloadBytes: 100,
            OriginalSha256: "original",
            PayloadSha256: "payload"),
        new SaveSnapshotIndex(
            string.Empty,
            new SaveMetaSummary("1.6.4850", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            Array.Empty<FactionSummary>(),
            Array.Empty<SaveIndexExtensionData>(),
            Array.Empty<WorldObjectSummary>(),
            new[] { new MapSummary("0", "0", "WorldObject_1", "(250, 1, 250)", false, false, false, false, 0, 0, WealthTotal: 12345f) },
            Array.Empty<ThingSummary>(),
            Array.Empty<PawnSummary>()),
        now.AddMinutes(1)));

    WebApplication app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();

    try
    {
        string baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var client = new ClashOfRimNetworkClient(httpClient);

        ListPlayersResponse response = await client.ListPlayersAsync(new ListPlayersRequest(
            "viewer",
            "viewer-colony",
            currentSnapshotId: null));
        Require(response.Result.Accepted, $"玩家列表应返回成功：{response.Result.ErrorCode} {response.Result.Message}");
        PlayerSummaryDto scoreUser = response.Players.Single(player => player.UserId == "score-user");
        Equal("new-colony", scoreUser.ColonyId, "玩家列表应使用同用户最新快照修正殖民地实例");
        Equal("new-snapshot", scoreUser.CurrentSnapshotId, "玩家列表应使用同用户最新快照 ID");
        Equal(12345, scoreUser.LatestSnapshotWealth, "玩家列表应从同用户最新快照返回财富");

        PlayerSessionRecord cached = state.Players.List().Single(record => record.UserId == "score-user");
        Equal("new-snapshot", cached.LatestSnapshotWealthSnapshotId, "财富查询应记录对应快照 ID");
        Equal(12345, cached.LatestSnapshotWealth, "财富查询应缓存快照财富");

        Require(state.SnapshotStore.RemoveLatest("score-user", "new-colony"), "测试应能移除原始快照");
        ListPlayersResponse cachedResponse = await client.ListPlayersAsync(new ListPlayersRequest(
            "viewer",
            "viewer-colony",
            currentSnapshotId: null));
        Require(cachedResponse.Result.Accepted, $"缓存玩家列表应返回成功：{cachedResponse.Result.ErrorCode} {cachedResponse.Result.Message}");
        PlayerSummaryDto cachedScoreUser = cachedResponse.Players.Single(player => player.UserId == "score-user");
        Equal("new-snapshot", cachedScoreUser.CurrentSnapshotId, "缓存玩家列表应保留快照 ID");
        Equal(12345, cachedScoreUser.LatestSnapshotWealth, "同一快照应直接使用已缓存财富");
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

static async Task VerifyPlayerListHidesPlayersWithoutColonySnapshotAsync()
{
    DateTimeOffset now = DateTimeOffset.Parse("2026-06-14T00:00:00Z");
    var state = new ClashOfRimNetworkState();
    state.Players.Record("no-colony-user", "pending-colony", currentSnapshotId: null, now);
    state.Players.Record("colony-user", "active-colony", currentSnapshotId: "active-snapshot", now);

    WebApplication app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();

    try
    {
        string baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var client = new ClashOfRimNetworkClient(httpClient);

        ListPlayersResponse response = await client.ListPlayersAsync(new ListPlayersRequest(
            "viewer",
            "viewer-colony",
            currentSnapshotId: null));
        Require(response.Result.Accepted, $"玩家列表应返回成功：{response.Result.ErrorCode} {response.Result.Message}");
        Require(response.Players.Any(player => player.UserId == "colony-user"), "已有快照的玩家应显示在外交玩家列表");
        Require(!response.Players.Any(player => player.UserId == "no-colony-user"), "没有殖民地快照的玩家不应显示在外交玩家列表");
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

static async Task VerifyAchievementLeaderboardsHidePlayersWithoutColonySnapshotAsync()
{
    DateTimeOffset now = DateTimeOffset.Parse("2026-06-14T00:00:00Z");
    var state = new ClashOfRimNetworkState();
    state.Players.Record("no-colony-user", "pending-colony", currentSnapshotId: null, now);
    state.Players.Record("colony-user", "active-colony", currentSnapshotId: "active-snapshot", now);
    var candidate = new SnapshotAchievementCandidateDto(
        "wealth_500000",
        "event",
        500_000,
        category: AchievementRegistry.CategorySnapshot,
        labelKey: "ClashOfRim.Achievement.Wealth500k",
        iconId: "wealth",
        color: AchievementColors.Blue);
    state.Achievements.Record(
        "no-colony-user",
        "pending-colony",
        "hidden-snapshot",
        candidate,
        now);
    state.Achievements.Record(
        "colony-user",
        "active-colony",
        "active-snapshot",
        candidate,
        now);

    WebApplication app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();

    try
    {
        string baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var client = new ClashOfRimNetworkClient(httpClient);

        ListAchievementsResponse response = await client.ListAchievementsAsync(new ListAchievementsRequest(
            "viewer",
            "viewer-colony",
            currentSnapshotId: null));
        Require(response.Result.Accepted, $"成就列表应返回成功：{response.Result.ErrorCode} {response.Result.Message}");
        AchievementLeaderboardDto leaderboard = response.Leaderboards.Single(board => board.AchievementId == "wealth_500000");
        Require(
            leaderboard.Entries.Any(entry => entry.UserId == "colony-user"),
            "已有快照的玩家应显示在成就排行榜");
        Require(
            !leaderboard.Entries.Any(entry => entry.UserId == "no-colony-user"),
            "没有殖民地快照的玩家不应显示在成就排行榜");
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (predicate())
        {
            return;
        }

        await Task.Delay(20);
    }

    throw new InvalidOperationException(failureMessage);
}

static Uri ToWebSocketUri(Uri baseAddress, string relativePath)
{
    Uri httpUri = new(baseAddress, relativePath);
    var builder = new UriBuilder(httpUri)
    {
        Scheme = string.Equals(httpUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws"
    };
    return builder.Uri;
}

static async Task<string> ReadWebSocketEventAsync(ClientWebSocket webSocket)
{
    var buffer = new byte[4096];
    using var stream = new MemoryStream();
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (DateTimeOffset.UtcNow < deadline)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        WebSocketReceiveResult result;
        try
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            continue;
        }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new InvalidOperationException("WS 流提前关闭。");
        }

        if (result.MessageType != WebSocketMessageType.Text)
        {
            continue;
        }

        stream.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    throw new InvalidOperationException("等待 WS 事件超时。");
}
