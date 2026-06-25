using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;

LoadServerLocalizationForTests();

var tests = new (string Name, Action Run)[]
{
    ("幂等键阻止重复事件", VerifyIdempotentAppend),
    ("在线和离线事件进入同一账本", VerifyOnlineAndOfflineDelivery),
    ("袭击账本保存结算明细", VerifyRaidSettlementLedgerRecord),
    ("袭击结算结果生成防守方账本事件且幂等", VerifyRaidSettlementResultRecordedInLedger),
    ("袭击结算失败进入审计状态", VerifyRaidSettlementFailureRecordedForReview),
    ("NPC 袭击属于本地事件，不生成多人支援标记", VerifyNpcRaidDoesNotParticipateInMultiplayer),
    ("袭击资格检查返回稳定拒绝原因", VerifyRaidEligibility),
    ("袭击资格可见性投影可提前禁用 UI", VerifyRaidAvailabilityProjection),
    ("袭击发起入口通过资格检查后写入账本", VerifyRaidInitiation),
    ("袭击派遣前二次确认不能替代最终保底校验", VerifyRaidDispatchConfirmation),
    ("防守方登录锁定只覆盖正在进行的目标殖民地袭击", VerifyRaidDefenseLoginLock),
    ("未结算袭击限制进攻方重复发动和防守方登录", VerifyRaidUnsettledProjection),
    ("袭击超时后失败并通知双方", VerifyRaidTimeoutProcessing),
    ("袭击宽限期内进攻方离线立即结束", VerifyRaidTimeoutGraceEndsWhenAttackerOffline),
    ("袭击已结算防守损失后仍可超时判定进攻方全灭", VerifyRaidSettlementThenAttackerTimeout),
    ("袭击结果可投影为防守方冷却期", VerifyRaidCooldownProjection),
    ("进攻方损失事件客户端应用优先触发原版远行队失踪", VerifyRaidAttackerLossApplication),
    ("进攻方损失确认快照消费账本事件", VerifyRaidAttackerLossConfirmationConsumption),
    ("礼物落地确认快照消费账本事件", VerifyGiftApplicationConfirmationConsumption),
    ("礼物交易支援载荷覆盖共同事件外壳", VerifyCommonEventShellForPayloads),
    ("pawn 交换包只允许固定安全 JSON 结构", VerifyPawnExchangePackageSafety),
    ("pawn Scribe 载荷保留并只替换一层引用 pawn", VerifyPawnScribePayloadOneLayerReferenceReplacement),
    ("外交和服务器通知事件进入统一账本", VerifyDiplomacyAndServerNotificationEvents),
    ("离线事件绑定快照确认后才消费", VerifySnapshotBoundOfflineConsumption),
    ("事件处理队列摘要按状态分组", VerifyEventQueueSummary),
    ("只读通知事件不要求快照确认", VerifySnapshotlessNotificationEventSemantics),
    ("事件队列查询只读取目标队列状态", VerifyEventQueueLedgerQuery),
    ("玩家袭击源事件不进入防守方信件队列", VerifyPlayerRaidSourceEventsAreHiddenFromDefenderQueue),
    ("交易接单备忘录不进入信件队列", VerifyTradeAcceptanceMemoEventsAreHiddenFromLetterQueue),
    ("事件队列可投影为原版信件模型", VerifyEventLetterProjection),
    ("可拒绝策略和目标地图上下文", VerifyRejectionPolicyAndTargetContext),
    ("拒绝礼物会生成不可拒绝退回事件", VerifyRejectedGiftCreatesReturnEvent),
    ("交易接单只是备忘录，实际交换才确认", VerifyTradeAcceptanceMemoUntilExchange),
    ("交易自提履约校验远行队携带物满足要求", VerifyTradeFulfillmentRequirementMatching),
    ("交易手续费可由服务器标准价格表权威计算", VerifyTradeFeeServerPriceTable),
    ("世界地图标记显示可交易殖民地和被进攻地块", VerifyWorldMapActionMarkers),
    ("登录下发世界地图行动标记列表", VerifyWorldMapMarkerDeliveryForLogin),
    ("登录下发世界地图标记包含袭击可见性", VerifyWorldMapMarkerDeliveryRaidAvailability),
    ("客户端服务器消息契约覆盖核心流程", VerifyProtocolContractCoverage),
    ("应用结果冲突分类可持久化", VerifyApplicationConflictResult),
    ("快照连续性允许同一殖民地迁移地块", VerifySnapshotContinuityAllowsColonyTileRelocation),
    ("快照连续性拒绝无关本地存档", VerifySnapshotContinuityRejectsUnrelatedLocalSave),
    ("快照链式标记允许当前链前进", VerifySnapshotLineageAllowsCurrentToken),
    ("快照历史哈希拒绝旧存档重放", VerifySnapshotHistoryRejectsReplay),
    ("快照游戏 tick 倒退会被拒绝", VerifySnapshotTimeRegressionRejectsRollback),
    ("文件账本重启后恢复事件和幂等索引", VerifyFileLedgerPersistence),
    ("SQLite 账本重启后恢复事件和幂等索引", VerifySqliteLedgerPersistence)
};

foreach ((string name, Action run) in tests)
{
    run();
    Console.WriteLine($"通过：{name}");
}

return 0;

static void VerifyIdempotentAppend()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent first = GiftEvent("gift-001", targetOnline: false);
    AuthoritativeEvent duplicate = GiftEvent("gift-001", targetOnline: false) with
    {
        EventId = "gift:manually-changed-id"
    };

    LedgerAppendResult firstResult = ledger.Append(first);
    LedgerAppendResult duplicateResult = ledger.Append(duplicate);

    Require(firstResult.Created, "第一次追加应创建事件");
    Require(!duplicateResult.Created, "同一幂等键不应重复创建事件");
    Equal(first.EventId, duplicateResult.Event.EventId, "重复追加应返回原事件");
    Equal(1, ledger.ListForUser("user-a").Count, "账本中应只有一条事件");
}

static void LoadServerLocalizationForTests()
{
    string? directory = FindLocalizationDirectory(Directory.GetCurrentDirectory());
    Require(directory is not null, "事件测试需要服务端本地化资源目录");
    ServerLocalization.Reset();
    foreach (string file in Directory.EnumerateFiles(directory!, "*.json", SearchOption.TopDirectoryOnly))
    {
        Dictionary<string, string>? entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
        if (entries is not null)
        {
            ServerLocalization.MergeExternal(Path.GetFileNameWithoutExtension(file), entries);
        }
    }

    ServerLocalization.RequireReady();
    ServerLocalization.SetDefaultLanguage(ServerLocalization.ChineseSimplified);
}

static string? FindLocalizationDirectory(string startDirectory)
{
    DirectoryInfo? directory = new(startDirectory);
    while (directory is not null)
    {
        string candidate = Path.Combine(directory.FullName, "Tools", "ClashOfRim.NetworkServer", "Localization");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    return null;
}

static void VerifyOnlineAndOfflineDelivery()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent online = GiftEvent("gift-online", targetOnline: true);
    AuthoritativeEvent offline = GiftEvent("gift-offline", targetOnline: false);

    ledger.Append(online);
    ledger.Append(offline);

    Equal(ServerEventDeliveryMode.OnlineImmediate, online.DeliveryMode, "在线事件交付模式");
    Equal(ServerEventStatus.ReadyForImmediateDelivery, online.Status, "在线事件状态");
    Equal(ServerEventDeliveryMode.OfflinePending, offline.DeliveryMode, "离线事件交付模式");
    Equal(ServerEventStatus.PendingOfflineDelivery, offline.Status, "离线事件状态");
    Equal(2, ledger.ListForUser("user-b").Count, "同一目标的在线和离线事件都应在同一账本");
}

static void VerifyRaidSettlementLedgerRecord()
{
    ThingSummary missing = Thing("missing-stack", stackCount: "1");
    ThingSummary reduced = Thing("reduced-stack", stackCount: "75");
    ThingSummary reducedReturned = reduced with { StackCount = "70" };
    ThingSummary extra = Thing("extra-stack", stackCount: "99");

    RaidSettlementDiffResult diff = RaidSettlementDiffer.CompareByDisappearance(
        new[] { missing, reduced },
        new[] { reducedReturned, extra },
        new RaidSettlementPolicy(0.5, "raid-event-001"));

    RaidSettlementRecord settlement = RaidSettlementRecord.FromDiff(
        "snapshot-before",
        "snapshot-after",
        diff);

    AuthoritativeEvent raid = AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        Actor(),
        Target(),
        "raid-idempotency-001",
        targetOnline: false,
        new RaidEventPayload("snapshot-before", "snapshot-after", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(30), settlement),
        DateTimeOffset.UnixEpoch);

    Equal(ServerEventType.Raid, raid.Type, "袭击事件类型");
    Equal(ServerEventStatus.PendingOfflineDelivery, raid.Status, "离线袭击状态");
    Equal(1, settlement.MissingThingGlobalKeys.Count, "消失 thing 数量");
    Equal(1, settlement.IgnoredExtraThingCount, "新增 thing 忽略数量");
    Require(settlement.Losses.Any(loss => loss.WholeThingMissing), "应保存整条消失损失");

    RaidSettlementLossRecord reducedLoss = settlement.Losses.Single(loss => loss.ReturnedStackCount.HasValue);
    Equal(75, reducedLoss.OriginalStackCount, "原堆叠数量");
    Equal(70, reducedLoss.ReturnedStackCount, "返回堆叠数量");
    Equal(5, reducedLoss.StolenStackCount, "实际被抢数量");
    Require(reducedLoss.MaxLossCount is 37 or 38, "50% 损失上限应为 37 或 38");
    Equal(5, reducedLoss.LossCount, "实际被抢少于上限时应只损失实际数量");
}

static void VerifyRaidSettlementResultRecordedInLedger()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent sourceRaid = RaidEvent("source-raid-settlement", targetOnline: false);
    ledger.Append(sourceRaid);

    RaidSettlementReturnResult settlementResult = AcceptedSettlementResult(
        sourceRaid.EventId,
        originalSnapshotId: "snapshot-before",
        returnedSnapshotId: "snapshot-after");

    RaidSettlementLedgerRecordResult first = RaidSettlementLedgerRecorder.Record(
        ledger,
        sourceRaid.EventId,
        settlementResult,
        DateTimeOffset.UnixEpoch.AddMinutes(30),
        defenderOnline: false);
    RaidSettlementLedgerRecordResult duplicate = RaidSettlementLedgerRecorder.Record(
        ledger,
        sourceRaid.EventId,
        settlementResult,
        DateTimeOffset.UnixEpoch.AddMinutes(31),
        defenderOnline: false);
    RaidSettlementLedgerRecordResult differentReturnedSnapshotDuplicate = RaidSettlementLedgerRecorder.Record(
        ledger,
        sourceRaid.EventId,
        AcceptedSettlementResult(
            sourceRaid.EventId,
            originalSnapshotId: "snapshot-before",
            returnedSnapshotId: "snapshot-after-later"),
        DateTimeOffset.UnixEpoch.AddMinutes(32),
        defenderOnline: false);

    Equal(RaidSettlementLedgerRecordResultKind.SettlementEventCreated, first.Kind, "首次结算入账结果");
    Require(first.Created, "首次结算应创建事件");
    Require(first.SettlementEvent is not null, "首次结算应返回结算事件");
    Equal(ServerEventType.Raid, first.SettlementEvent!.Type, "结算事件类型");
    Equal($"raid-settlement:{sourceRaid.EventId}", first.SettlementEvent.IdempotencyKey, "结算事件幂等键应只绑定袭击事件");
    Equal(ServerEventStatus.PendingOfflineDelivery, first.SettlementEvent.Status, "离线防守方应收到待处理结算事件");
    Equal(sourceRaid.TargetContext, first.SettlementEvent.TargetContext, "结算事件应保留目标地图上下文");

    RaidEventPayload payload = (RaidEventPayload)first.SettlementEvent.Payload;
    Equal("snapshot-before", payload.DefenderSnapshotId, "结算载荷原始快照");
    Equal("snapshot-after", payload.ReturnedSnapshotId, "结算载荷返回快照");
    Equal(DateTimeOffset.UnixEpoch.AddMinutes(30), payload.FinishedAtUtc, "结算完成时间");
    Require(payload.Settlement is not null, "结算事件应保存损失明细");
    Equal(1, payload.Settlement!.MissingThingGlobalKeys.Count, "结算事件消失对象数量");
    Equal(1, payload.Settlement.Losses.Count(loss => loss.ReturnedStackCount.HasValue), "结算事件堆叠减少数量");

    Equal(RaidSettlementLedgerRecordResultKind.SettlementEventAlreadyExists, duplicate.Kind, "重复结算入账结果");
    Require(!duplicate.Created, "重复结算不应创建新事件");
    Equal(first.SettlementEvent.EventId, duplicate.SettlementEvent!.EventId, "重复结算应返回已有事件");
    Equal(RaidSettlementLedgerRecordResultKind.SettlementEventAlreadyExists, differentReturnedSnapshotDuplicate.Kind, "同一袭击不同返回快照也不应再次入账");
    Require(!differentReturnedSnapshotDuplicate.Created, "同一袭击不同返回快照不应创建第二封结算信");
    Equal(first.SettlementEvent.EventId, differentReturnedSnapshotDuplicate.SettlementEvent!.EventId, "同一袭击不同返回快照应返回已有结算事件");
    Equal(2, ledger.ListForUser("user-b").Count, "源袭击和结算结果各一条");
}

static void VerifyRaidSettlementFailureRecordedForReview()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent sourceRaid = RaidEvent("source-raid-failed-settlement", targetOnline: false);
    ledger.Append(sourceRaid);

    var failedResult = new RaidSettlementReturnResult(
        RaidSettlementReturnResultKind.TargetMapNotFoundInReturnedSnapshot,
        sourceRaid.EventId,
        new SnapshotIdentity("user-b", "colony-b", "snapshot-before"),
        new SnapshotIdentity("user-b", "colony-b", "snapshot-after"),
        "Map_0",
        Settlement: null);

    RaidSettlementLedgerRecordResult recordResult = RaidSettlementLedgerRecorder.Record(
        ledger,
        sourceRaid.EventId,
        failedResult,
        DateTimeOffset.UnixEpoch.AddMinutes(30),
        defenderOnline: false);

    Equal(RaidSettlementLedgerRecordResultKind.SettlementRejectedRecorded, recordResult.Kind, "失败结算入账结果");
    Require(recordResult.FailureReason!.Contains("TargetMapNotFoundInReturnedSnapshot", StringComparison.Ordinal), "失败原因应包含拒绝分类");
    AuthoritativeEvent updatedSource = ledger.Find(sourceRaid.EventId)!;
    Equal(ServerEventStatus.Conflict, updatedSource.Status, "失败结算应让源袭击进入冲突审计");
    Equal(EventApplicationResultKind.NeedsManualReview, updatedSource.LastApplicationResult, "失败结算应用结果");
    Require(updatedSource.LastFailureReason!.Contains("TargetMapNotFoundInReturnedSnapshot", StringComparison.Ordinal), "源袭击应保存失败原因");
    Equal(1, ledger.ListForUser("user-b").Count, "失败结算不应创建防守方结果事件");

    RaidSettlementLedgerRecordResult missingSource = RaidSettlementLedgerRecorder.Record(
        ledger,
        "missing-source-raid",
        failedResult,
        DateTimeOffset.UnixEpoch.AddMinutes(31),
        defenderOnline: false);
    Equal(RaidSettlementLedgerRecordResultKind.SourceRaidNotFound, missingSource.Kind, "源事件不存在结果");
}

static void VerifyNpcRaidDoesNotParticipateInMultiplayer()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent sourceRaid = RaidEvent("source-vanilla-npc-raid", targetOnline: false) with
    {
        Target = new EventParty("npc"),
        Payload = new RaidEventPayload(
            "npc-target:WorldObject_NPC_1",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: new RaidAttackForceRecord(
                "snapshot-before",
                new[] { "owner:user-a/colony:colony-a/snapshot:snapshot-before/map:caravan/thing:pawn-1" },
                Array.Empty<EventThingReference>()),
            AttackerLoss: null,
            OpponentKind: RaidOpponentKind.VanillaNpc)
    };
    ledger.Append(sourceRaid);

    RaidSettlementLedgerRecordResult result = RaidSettlementLedgerRecorder.Record(
        ledger,
        sourceRaid.EventId,
        AcceptedSettlementResult(sourceRaid.EventId, "snapshot-before", "snapshot-after"),
        DateTimeOffset.UnixEpoch.AddMinutes(30),
        defenderOnline: false);

    Equal(RaidSettlementLedgerRecordResultKind.SourceRaidDoesNotRequireSettlement, result.Kind, "主动进攻 NPC 不应生成玩家结算");
    Require(!result.Created, "跳过结算不应创建事件");
    Require(result.SettlementEvent is null, "跳过结算不应返回结算事件");
    ledger.MarkDelivered(sourceRaid.EventId, "snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    IReadOnlyList<WorldMapMarker> deliveredMarkers = WorldMapMarkerProjectionBuilder.BuildActiveRaidTargetMarkers(
        ledger.ListAll(),
        DateTimeOffset.UnixEpoch.AddMinutes(1));
    Require(deliveredMarkers.Count == 0, "主动进攻 NPC 不应广播为可支援目标");
    Equal(0, RaidUnsettledProjector.BuildForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "主动进攻 NPC 不应限制玩家发起多人袭击");
    Equal(0, RaidUnsettledProjector.BuildActiveSourceForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "主动进攻 NPC 不应生成多人袭击恢复状态");

    ledger.MarkApplied(sourceRaid.EventId, "snapshot-after", DateTimeOffset.UnixEpoch.AddMinutes(2));
    IReadOnlyList<WorldMapMarker> appliedMarkers = WorldMapMarkerProjectionBuilder.BuildActiveRaidTargetMarkers(
        ledger.ListAll(),
        DateTimeOffset.UnixEpoch.AddMinutes(3));
    Require(appliedMarkers.Count == 0, "主动进攻 NPC 即使写入本地进度也不应生成多人支援标记");

    RaidTimeoutProcessingResult timeout = RaidTimeoutProcessor.ProcessExpiredRaids(
        ledger,
        ledger.ListAll(),
        DateTimeOffset.UnixEpoch.AddMinutes(30));
    Equal(0, timeout.FailedRaids.Count, "主动进攻 NPC 不应进入服务器多人袭击超时处理");
    Equal(0, timeout.NotificationEvents.Count, "主动进攻 NPC 不应生成多人超时通知");
}

static void VerifyRaidEligibility()
{
    var policy = new RaidEligibilityPolicy(MinimumDefenderWealth: 5000);
    var validRequest = new RaidEligibilityRequest(
        Actor(),
        Target(),
        IsHostile: true,
        DefenderOnline: false,
        CheckedAtUtc: DateTimeOffset.UnixEpoch.AddHours(1),
        DefenderRaidCooldownUntilUtc: DateTimeOffset.UnixEpoch,
        DefenderWealth: 8000,
        DefenderSnapshot: new SnapshotIdentity("user-b", "colony-b", "snapshot-before"),
        DefenderMaps: new[] { RaidMap("Map_0") },
        TargetMapUniqueId: "Map_0");

    RaidEligibilityResult allowed = RaidEligibilityChecker.Check(validRequest, policy);
    Require(allowed.Eligible, "满足条件时应允许袭击");
    Equal(0, allowed.FailureReasons.Count, "允许时不应有拒绝原因");

    RaidEligibilityResult rejected = RaidEligibilityChecker.Check(validRequest with
    {
        IsHostile = false,
        DefenderOnline = true,
        DefenderRaidCooldownUntilUtc = DateTimeOffset.UnixEpoch.AddHours(2),
        DefenderWealth = 1000,
        DefenderSnapshot = null,
        TargetMapUniqueId = "Map_missing"
    }, policy);

    Require(!rejected.Eligible, "不满足条件时应拒绝袭击");
    Require(rejected.FailureReasons.SequenceEqual(new[]
    {
        RaidEligibilityFailureReason.NotHostile,
        RaidEligibilityFailureReason.DefenderOnline,
        RaidEligibilityFailureReason.CooldownActive,
        RaidEligibilityFailureReason.DefenderWealthBelowMinimum,
        RaidEligibilityFailureReason.MissingDefenderSnapshot,
        RaidEligibilityFailureReason.TargetMapUnavailable
    }), "多重拒绝原因应保持稳定顺序");
    Equal(DateTimeOffset.UnixEpoch.AddHours(2), rejected.CooldownUntilUtc, "冷却结束时间");

    RaidEligibilityResult selfRaid = RaidEligibilityChecker.Check(validRequest with
    {
        Defender = Actor()
    }, policy);
    Require(selfRaid.FailureReasons.Contains(RaidEligibilityFailureReason.AttackerIsDefender), "不能袭击自己的殖民地");

    RaidEligibilityResult missingMap = RaidEligibilityChecker.Check(validRequest with
    {
        TargetMapUniqueId = ""
    }, policy);
    Equal(RaidEligibilityFailureReason.MissingTargetMap, missingMap.FailureReasons.Single(), "缺少目标地图应返回明确原因");

    RaidEligibilityResult missingRequest = RaidEligibilityChecker.Check(null, policy);
    Equal(RaidEligibilityFailureReason.MissingRequest, missingRequest.FailureReasons.Single(), "缺少请求应返回明确原因");
}

static void VerifyRaidAvailabilityProjection()
{
    var policy = new RaidEligibilityPolicy(MinimumDefenderWealth: 5000);
    RaidAvailabilitySummary available = RaidAvailabilityProjector.Project(new RaidAvailabilityProjectionRequest(
        RaidStartRequest("raid-availability-ok", isHostile: true, defenderOnline: false, defenderWealth: 8000).Eligibility,
        policy));

    Require(available.CanRaid, "满足条件时 UI 应显示可袭击");
    Equal(RaidAvailabilitySuggestedAction.StartRaid, available.SuggestedAction, "可袭击建议动作");
    Equal("Map_0", available.TargetMapUniqueId, "可袭击目标地图");
    Equal("snapshot-before", available.DefenderSnapshotId, "可袭击目标快照");
    Equal(0, available.DisabledReasons.Count, "可袭击时不应有禁用原因");

    RaidAvailabilitySummary availableWithRawSnapshotMapId = RaidAvailabilityProjector.Project(new RaidAvailabilityProjectionRequest(
        RaidStartRequest("raid-availability-map-id-normalized", isHostile: true, defenderOnline: false, defenderWealth: 8000)
            .Eligibility with
            {
                TargetMapUniqueId = "Map_0",
                DefenderMaps = new[]
                {
                    new MapSummary("0", "0", "WorldObject_1", "(250, 1, 250)", HasCompressedThingMap: true, HasTerrainGrid: true, HasRoofGrid: true, HasFogGrid: true, ThingCount: 10, PawnCount: 3)
                }
            },
        policy));

    Require(availableWithRawSnapshotMapId.CanRaid, "快照地图 0 与世界标记 Map_0 应视为同一目标地图");

    RaidInitiationRequest rejectedRequest = RaidStartRequest(
        "raid-availability-rejected",
        isHostile: false,
        defenderOnline: true,
        defenderWealth: 1000);
    RaidAvailabilitySummary rejected = RaidAvailabilityProjector.Project(new RaidAvailabilityProjectionRequest(
        rejectedRequest.Eligibility with
        {
            DefenderRaidCooldownUntilUtc = DateTimeOffset.UnixEpoch.AddHours(2)
        },
        policy));

    Require(!rejected.CanRaid, "不满足条件时 UI 应禁用袭击");
    Require(rejected.DisabledReasons.Contains(RaidEligibilityFailureReason.NotHostile), "UI 禁用原因应包含非敌对");
    Require(rejected.DisabledReasons.Contains(RaidEligibilityFailureReason.DefenderOnline), "UI 禁用原因应包含目标在线");
    Require(rejected.DisabledReasons.Contains(RaidEligibilityFailureReason.CooldownActive), "UI 禁用原因应包含冷却中");
    Require(rejected.DisabledReasons.Contains(RaidEligibilityFailureReason.DefenderWealthBelowMinimum), "UI 禁用原因应包含财富不足");
    Equal(DateTimeOffset.UnixEpoch.AddHours(2), rejected.CooldownUntilUtc, "UI 应显示冷却截止时间");
    Equal(RaidAvailabilitySuggestedAction.DeclareWar, rejected.SuggestedAction, "非敌对优先建议宣战");

    WorldMapMarker marker = new WorldMapMarker(
        "tradeable-colony:user-b:WorldObject_1",
        WorldMapMarkerKind.TradeableColony,
        "user-b",
        "colony-b",
        "WorldObject_1",
        "Map_0",
        "snapshot-b",
        12345,
        "目标殖民地",
        DateTimeOffset.UnixEpoch,
        RelatedEventId: null,
        TradeEnabled: true,
        ReinforcementEnabled: false,
        RaidAvailability: rejected);

    Require(marker.RaidAvailability is not null, "世界地图标记应携带袭击可用性摘要");
    Require(!marker.RaidAvailability!.CanRaid, "世界地图 UI 可直接读取禁用状态");
}

static void VerifyRaidInitiation()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    var policy = new RaidEligibilityPolicy(MinimumDefenderWealth: 5000);
    RaidInitiationRequest request = RaidStartRequest("raid-start-001", isHostile: true, defenderOnline: false, defenderWealth: 8000);

    RaidInitiationResult first = RaidInitiationService.StartRaid(ledger, request, policy);
    RaidInitiationResult duplicate = RaidInitiationService.StartRaid(ledger, request, policy);

    Equal(RaidInitiationResultKind.RaidEventCreated, first.Kind, "首次发起袭击结果");
    Require(first.Started, "首次发起应开始袭击");
    Require(first.RaidEvent is not null, "首次发起应创建袭击事件");
    Equal(ServerEventType.Raid, first.RaidEvent!.Type, "发起事件类型");
    Equal("snapshot-before", ((RaidEventPayload)first.RaidEvent.Payload).DefenderSnapshotId, "袭击事件目标快照");
    Equal(DateTimeOffset.UnixEpoch.AddHours(1), ((RaidEventPayload)first.RaidEvent.Payload).StartedAtUtc, "袭击开始时间");
    Equal("Map_0", first.RaidEvent.TargetContext!.MapUniqueId, "袭击目标地图");
    Equal(12345, first.RaidEvent.TargetContext.Tile, "袭击目标地块");

    Equal(RaidInitiationResultKind.RaidEventAlreadyExists, duplicate.Kind, "重复发起袭击结果");
    Require(!duplicate.Created, "重复发起不应创建新事件");
    Equal(first.RaidEvent.EventId, duplicate.RaidEvent!.EventId, "重复发起应返回已有袭击事件");

    RaidInitiationRequest rejectedRequest = RaidStartRequest("raid-start-rejected", isHostile: false, defenderOnline: true, defenderWealth: 1000);
    RaidInitiationResult rejected = RaidInitiationService.StartRaid(ledger, rejectedRequest, policy);

    Equal(RaidInitiationResultKind.RejectedNotificationCreated, rejected.Kind, "拒绝发起结果");
    Require(!rejected.Started, "资格拒绝时不应开始袭击");
    Require(rejected.RaidEvent == null, "资格拒绝时不应创建袭击事件");
    Require(rejected.NotificationEvent is not null, "资格拒绝时应创建通知");
    Equal(ServerEventType.ServerNotification, rejected.NotificationEvent!.Type, "拒绝通知类型");
    Equal("user-a", rejected.NotificationEvent.Target.UserId, "拒绝通知发给进攻方");
    ServerNotificationEventPayload payload = (ServerNotificationEventPayload)rejected.NotificationEvent.Payload;
    Require(payload.Message.Contains(nameof(RaidEligibilityFailureReason.NotHostile), StringComparison.Ordinal), "拒绝通知应包含非敌对原因");
    Require(payload.Message.Contains(nameof(RaidEligibilityFailureReason.DefenderOnline), StringComparison.Ordinal), "拒绝通知应包含目标在线原因");
    Equal(2, ledger.ListForUser("user-a").Count, "账本应只包含成功袭击和拒绝通知");
}

static void VerifyRaidDispatchConfirmation()
{
    var policy = new RaidEligibilityPolicy(MinimumDefenderWealth: 5000);
    RaidInitiationRequest startRequest = RaidStartRequest(
        "raid-dispatch-confirm",
        isHostile: true,
        defenderOnline: false,
        defenderWealth: 8000);

    RaidDispatchConfirmationResult confirmed = RaidDispatchConfirmationService.Confirm(
        new RaidDispatchConfirmationRequest(
            startRequest.Eligibility,
            DateTimeOffset.UnixEpoch.AddMinutes(10),
            TimeSpan.FromMinutes(5)),
        policy);

    Require(confirmed.CanDispatch, "资格仍通过时应允许派遣");
    Require(confirmed.Token is not null, "资格仍通过时应生成确认令牌");
    Equal("user-a", confirmed.Token!.AttackerUserId, "令牌进攻方");
    Equal("user-b", confirmed.Token.DefenderUserId, "令牌防守方");
    Equal("colony-b", confirmed.Token.DefenderColonyId, "令牌目标殖民地");
    Equal("Map_0", confirmed.Token.TargetMapUniqueId, "令牌目标地图");
    Equal("snapshot-before", confirmed.Token.DefenderSnapshotId, "令牌目标快照");
    Equal(DateTimeOffset.UnixEpoch.AddMinutes(15), confirmed.Token.ExpiresAtUtc, "令牌过期时间");

    RaidDispatchConfirmationResult changed = RaidDispatchConfirmationService.Confirm(
        new RaidDispatchConfirmationRequest(
            startRequest.Eligibility with { DefenderOnline = true },
            DateTimeOffset.UnixEpoch.AddMinutes(11),
            TimeSpan.FromMinutes(5)),
        policy);

    Require(!changed.CanDispatch, "目标状态变化后应阻止派遣");
    Require(changed.Token == null, "资格拒绝时不应生成令牌");
    Require(changed.Availability.DisabledReasons.Contains(RaidEligibilityFailureReason.DefenderOnline), "二次确认应返回当前拒绝原因");
    Equal("snapshot-before", changed.Availability.DefenderSnapshotId, "拒绝摘要仍应包含目标快照版本");

    var ledger = new InMemoryAuthoritativeEventLedger();
    RaidInitiationResult finalAttempt = RaidInitiationService.StartRaid(
        ledger,
        startRequest with { Eligibility = startRequest.Eligibility with { DefenderOnline = true } },
        policy);

    Equal(RaidInitiationResultKind.RejectedNotificationCreated, finalAttempt.Kind, "旧确认令牌不能替代最终保底校验");
    Require(!finalAttempt.Started, "最终资格变化后不能发起袭击");
    Equal(1, ledger.ListForUser("user-a").Count, "最终拒绝只生成通知");
}

static void VerifyRaidDefenseLoginLock()
{
    var policy = new RaidDefenseLockPolicy(TimeSpan.FromHours(2));
    AuthoritativeEvent activeRaid = RaidEvent("lock-active", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch.AddHours(1),
            FinishedAtUtc: null,
            Settlement: null)
    };
    AuthoritativeEvent otherColonyRaid = activeRaid with
    {
        EventId = "raid:lock-other-colony",
        IdempotencyKey = "lock-other-colony",
        Target = activeRaid.Target with { ColonyId = "other-colony" }
    };
    AuthoritativeEvent cancelledRaid = activeRaid with
    {
        EventId = "raid:lock-cancelled",
        IdempotencyKey = "lock-cancelled",
        Status = ServerEventStatus.Cancelled
    };
    AuthoritativeEvent completedRaid = activeRaid with
    {
        EventId = "raid:lock-completed",
        IdempotencyKey = "lock-completed",
        Payload = new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: "snapshot-after",
            StartedAtUtc: DateTimeOffset.UnixEpoch.AddHours(1),
            FinishedAtUtc: DateTimeOffset.UnixEpoch.AddHours(1.5),
            new RaidSettlementRecord(
                "snapshot-before",
                "snapshot-after",
                0.5,
                Array.Empty<string>(),
                Array.Empty<RaidSettlementLossRecord>(),
                IgnoredExtraThingCount: 0))
    };
    AuthoritativeEvent expiredRaid = activeRaid with
    {
        EventId = "raid:lock-expired",
        IdempotencyKey = "lock-expired",
        Payload = new RaidEventPayload(
            "snapshot-old",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch.AddHours(-5),
            FinishedAtUtc: null,
            Settlement: null)
    };

    RaidDefenseLockStatus locked = RaidDefenseLockProjector.BuildForDefender(
        "user-b",
        "colony-b",
        new[] { activeRaid, otherColonyRaid, cancelledRaid, expiredRaid },
        DateTimeOffset.UnixEpoch.AddHours(1.25),
        policy);

    Require(locked.IsLocked, "正在进行的袭击应锁定防守方登录");
    Equal(1, locked.ActiveLocks.Count, "只有目标殖民地的进行中袭击应锁定");
    Equal(activeRaid.EventId, locked.ActiveLocks.Single().RaidEventId, "锁定来源袭击事件");
    Equal("Map_0", locked.ActiveLocks.Single().TargetMapUniqueId, "锁定目标地图");
    Equal(TimeSpan.FromMinutes(105), locked.LongestRemaining, "锁定剩余时间");

    RaidDefenseLockStatus unlockedBySettlement = RaidDefenseLockProjector.BuildForDefender(
        "user-b",
        "colony-b",
        new[] { activeRaid, completedRaid },
        DateTimeOffset.UnixEpoch.AddHours(1.25),
        policy);
    Require(!unlockedBySettlement.IsLocked, "同目标快照完成结算后应解除锁定");

    RaidDefenseLockStatus unlockedByTimeout = RaidDefenseLockProjector.BuildForDefender(
        "user-b",
        "colony-b",
        new[] { activeRaid },
        DateTimeOffset.UnixEpoch.AddHours(4),
        policy);
    Require(!unlockedByTimeout.IsLocked, "超过最大袭击时长后应解除锁定");

    RaidDefenseLockStatus otherColonyStatus = RaidDefenseLockProjector.BuildForDefender(
        "user-b",
        "other-colony",
        new[] { activeRaid, otherColonyRaid },
        DateTimeOffset.UnixEpoch.AddHours(1.25),
        policy);
    Equal(otherColonyRaid.EventId, otherColonyStatus.ActiveLocks.Single().RaidEventId, "锁定不应影响非目标殖民地");
}

static void VerifyRaidUnsettledProjection()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent activeRaid = RaidEvent("unsettled-active", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: AttackForce())
    };
    ledger.Append(activeRaid);

    Equal(1, RaidUnsettledProjector.BuildForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "进攻方已有未结算袭击时应被限制");
    Equal(1, RaidUnsettledProjector.BuildActiveSourceForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "进攻方源袭击未结算时应阻止重新登录");
    Equal(0, RaidUnsettledProjector.BuildForAttacker("user-a", "colony-a", ledger.ListAll(), activeRaid.IdempotencyKey).Count, "同一幂等请求不应被自己的既有事件阻挡");
    Equal(1, RaidUnsettledProjector.BuildForDefender("user-b", "colony-b", ledger.ListAll()).Count, "防守方存在针对自己的未结算袭击时应被限制登录");

    AuthoritativeEvent defenderAsAttackerRaid = activeRaid with
    {
        EventId = "unsettled-defender-as-attacker",
        IdempotencyKey = "unsettled-defender-as-attacker",
        Actor = new EventParty("user-b", "colony-b"),
        Target = new EventParty("user-c", "colony-c"),
        TargetContext = null
    };
    Equal(
        1,
        RaidUnsettledProjector.BuildForDefender("user-b", "colony-b", new[] { activeRaid, defenderAsAttackerRaid }).Count,
        "防守方登录锁只应统计目标是自己的未结算袭击");

    RaidSettlementLedgerRecordResult settlement = RaidSettlementLedgerRecorder.Record(
        ledger,
        activeRaid.EventId,
        AcceptedSettlementResult(activeRaid.EventId, "snapshot-before", "snapshot-after"),
        DateTimeOffset.UnixEpoch.AddMinutes(30),
        defenderOnline: false);
    Equal(RaidSettlementLedgerRecordResultKind.SettlementEventCreated, settlement.Kind, "防守方结算事件应创建");
    Equal(0, RaidUnsettledProjector.BuildForDefender("user-b", "colony-b", ledger.ListAll()).Count, "防守方结算后不再被源袭击阻挡登录");
    Equal(1, RaidUnsettledProjector.BuildForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "防守方结算不代表进攻方袭击已结束");
    Equal(1, RaidUnsettledProjector.BuildActiveSourceForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "防守方结算后进攻方仍不能重新登录到旧进攻状态");

    RaidTimeoutProcessingResult timeout = RaidTimeoutProcessor.ProcessExpiredRaids(
        ledger,
        ledger.ListAll(),
        DateTimeOffset.UnixEpoch.AddMinutes(17));
    Equal(0, timeout.AttackerLossEventCount, "超时处理器不应脱离快照清理直接生成进攻方损失事件");
    Equal(0, RaidUnsettledProjector.BuildActiveSourceForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "服务器超时兜底后进攻方应可重新登录");
    Equal(0, RaidUnsettledProjector.BuildForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "源袭击失败后不应因旧初始名单继续限制新袭击");
    Equal(0, RaidUnsettledProjector.BuildForDefender("user-b", "colony-b", ledger.ListAll()).Count, "进攻方全损事件不应重新锁定防守方登录");

    var completedLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent completedPlayerRaid = activeRaid with
    {
        EventId = "unsettled-completed-player-raid",
        IdempotencyKey = "unsettled-completed-player-raid"
    };
    completedLedger.Append(completedPlayerRaid);
    completedLedger.MarkDelivered(completedPlayerRaid.EventId, "snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    completedLedger.MarkApplied(completedPlayerRaid.EventId, "snapshot-after", DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(0, RaidUnsettledProjector.BuildActiveSourceForAttacker("user-a", "colony-a", completedLedger.ListAll()).Count, "玩家袭击源事件确认结算后不应再触发袭击恢复");
    Equal(0, RaidUnsettledProjector.BuildForAttacker("user-a", "colony-a", completedLedger.ListAll()).Count, "玩家袭击源事件确认结算后不应再限制进攻方");

    AuthoritativeEvent npcRaid = RaidEvent("unsettled-npc", targetOnline: false) with
    {
        Status = ServerEventStatus.AppliedToSnapshot,
        Target = new EventParty("npc", "site-1"),
        Payload = new RaidEventPayload(
            "npc-target:site-1",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch.AddHours(3),
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: AttackForce(),
            OpponentKind: RaidOpponentKind.VanillaNpc)
    };
    ledger.Append(npcRaid);
    Equal(0, RaidUnsettledProjector.BuildForAttacker("user-a", "colony-a", ledger.ListAll()).Count, "主动进攻 NPC 不应限制新的多人袭击");
}

static void VerifyRaidTimeoutProcessing()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    var policy = new RaidDefenseLockPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2));
    AuthoritativeEvent expiredRaid = RaidEvent("timeout-expired", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: AttackForce())
    };
    AuthoritativeEvent notExpiredRaid = RaidEvent("timeout-active", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-before-2",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(10),
            FinishedAtUtc: null,
            Settlement: null)
    };
    ledger.Append(expiredRaid);
    ledger.Append(notExpiredRaid);

    RaidTimeoutProcessingResult atClientDeadline = RaidTimeoutProcessor.ProcessExpiredRaids(
        ledger,
        ledger.ListForUser("user-b"),
        DateTimeOffset.UnixEpoch.AddMinutes(15),
        policy);
    Equal(0, atClientDeadline.FailedRaidCount, "到达客户端倒计时时服务器应保留上传宽限");
    Equal(ServerEventStatus.PendingOfflineDelivery, ledger.Find(expiredRaid.EventId)!.Status, "宽限期内袭击不应标记失败");

    RaidTimeoutProcessingResult result = RaidTimeoutProcessor.ProcessExpiredRaids(
        ledger,
        ledger.ListForUser("user-b"),
        DateTimeOffset.UnixEpoch.AddMinutes(17),
        policy);

    Equal(1, result.FailedRaidCount, "超时失败袭击数量");
    Equal(0, result.OfflineFailedRaidCount, "未提供在线状态判断时不应归类为离线超时");
    Equal(expiredRaid.EventId, result.FailedRaids.Single().EventId, "超时失败事件");
    Equal(ServerEventStatus.Failed, ledger.Find(expiredRaid.EventId)!.Status, "超时袭击应标记失败");
    Equal(ServerEventStatus.PendingOfflineDelivery, ledger.Find(notExpiredRaid.EventId)!.Status, "未超时袭击不应处理");
    Equal(0, result.AttackerLossEventCount, "超时处理器不应按初始进攻名单生成进攻方损失事件");
    Equal(2, result.NotificationCount, "超时应通知双方");
    Require(result.NotificationEvents.Any(evt => evt.Target.UserId == "user-a"), "进攻方应收到超时通知");
    Require(result.NotificationEvents.Any(evt => evt.Target.UserId == "user-b"), "防守方应收到超时通知");
    Require(
        !ledger.ListForUser("user-b")
            .Where(evt => evt.Type == ServerEventType.Raid)
            .Select(evt => evt.Payload)
            .OfType<RaidEventPayload>()
            .Any(payload => payload.Settlement != null || payload.ReturnedSnapshotId != null),
        "没有最终结算快照时，超时不应凭空生成防守方损失结算");

    RaidDefenseLockStatus lockStatus = RaidDefenseLockProjector.BuildForDefender(
        "user-b",
        "colony-b",
        ledger.ListForUser("user-b"),
        DateTimeOffset.UnixEpoch.AddMinutes(17),
        policy);
    Require(!lockStatus.ActiveLocks.Any(lockState => lockState.RaidEventId == expiredRaid.EventId), "失败袭击应解除防守方登录锁");
    RaidCooldownStatus cooldown = RaidCooldownProjector.BuildForDefender(
        "user-b",
        "colony-b",
        ledger.ListForUser("user-b"));
    Equal(1, cooldown.Records.Count, "无结算快照的超时应只产生一条防守方冷却");
    Equal(RaidCooldownReason.TimeoutFailed, cooldown.Records.Single().Reason, "无结算快照的超时冷却应按超时失败计算");

    RaidTimeoutProcessingResult repeated = RaidTimeoutProcessor.ProcessExpiredRaids(
        ledger,
        ledger.ListForUser("user-b"),
        DateTimeOffset.UnixEpoch.AddMinutes(17),
        policy);
    Equal(0, repeated.FailedRaidCount, "重复处理不应再次失败同一袭击");
    Equal(0, repeated.NotificationCount, "重复处理不应再次通知");
    Equal(0, repeated.AttackerLossEventCount, "重复处理不应重复生成进攻方损失事件");
}

static void VerifyRaidTimeoutGraceEndsWhenAttackerOffline()
{
    var onlineLedger = new InMemoryAuthoritativeEventLedger();
    var offlineLedger = new InMemoryAuthoritativeEventLedger();
    var policy = new RaidDefenseLockPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2));
    AuthoritativeEvent activeRaid = RaidEvent("timeout-grace-presence", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: AttackForce())
    };
    onlineLedger.Append(activeRaid);
    offlineLedger.Append(activeRaid);

    RaidTimeoutProcessingResult stillOnline = RaidTimeoutProcessor.ProcessExpiredRaids(
        onlineLedger,
        onlineLedger.ListAll(),
        DateTimeOffset.UnixEpoch.AddMinutes(16),
        policy,
        _ => true);
    Equal(0, stillOnline.FailedRaidCount, "宽限期内进攻方仍在线时服务器应等待上传");

    RaidTimeoutProcessingResult wentOffline = RaidTimeoutProcessor.ProcessExpiredRaids(
        offlineLedger,
        offlineLedger.ListAll(),
        DateTimeOffset.UnixEpoch.AddMinutes(16),
        policy,
        _ => false);
    Equal(1, wentOffline.FailedRaidCount, "宽限期内进攻方离线应立即结束袭击");
    Equal(1, wentOffline.OfflineFailedRaidCount, "宽限期内离线结束应标记为离线超时");
    Equal(0, wentOffline.AttackerLossEventCount, "离线提前结束不应脱离快照清理生成进攻方损失事件");
    Equal(ServerEventStatus.Failed, offlineLedger.Find(activeRaid.EventId)!.Status, "离线提前结束应标记源袭击失败");
}

static void VerifyRaidSettlementThenAttackerTimeout()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    var policy = new RaidDefenseLockPolicy(TimeSpan.FromHours(2));
    AuthoritativeEvent activeRaid = RaidEvent("timeout-after-settlement", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: AttackForce())
    };
    ledger.Append(activeRaid);

    RaidSettlementLedgerRecordResult settlement = RaidSettlementLedgerRecorder.Record(
        ledger,
        activeRaid.EventId,
        AcceptedSettlementResult(activeRaid.EventId, "snapshot-before", "snapshot-after"),
        DateTimeOffset.UnixEpoch.AddMinutes(30),
        defenderOnline: false);

    Equal(RaidSettlementLedgerRecordResultKind.SettlementEventCreated, settlement.Kind, "防守方结算应先入账");
    Equal(ServerEventStatus.PendingOfflineDelivery, ledger.Find(activeRaid.EventId)!.Status, "防守方结算不应结束源袭击");

    RaidDefenseLockStatus lockAfterSettlement = RaidDefenseLockProjector.BuildForDefender(
        "user-b",
        "colony-b",
        ledger.ListForUser("user-b"),
        DateTimeOffset.UnixEpoch.AddMinutes(30),
        policy);
    Require(!lockAfterSettlement.ActiveLocks.Any(lockState => lockState.RaidEventId == activeRaid.EventId), "防守方结算后应解除登录锁");

    RaidTimeoutProcessingResult timeout = RaidTimeoutProcessor.ProcessExpiredRaids(
        ledger,
        ledger.ListForUser("user-b"),
        DateTimeOffset.UnixEpoch.AddHours(2),
        policy);

    Equal(1, timeout.FailedRaidCount, "源袭击仍应在超时后失败");
    Equal(ServerEventStatus.Failed, ledger.Find(activeRaid.EventId)!.Status, "源袭击应标记失败");
    Equal(0, timeout.AttackerLossEventCount, "超时处理器不应直接生成进攻方损失事件");
    Require(ledger.ListForUser("user-b").Any(evt => evt.EventId == settlement.SettlementEvent!.EventId), "防守方结算事件应仍然存在");

    RaidCooldownStatus cooldown = RaidCooldownProjector.BuildForDefender(
        "user-b",
        "colony-b",
        ledger.ListForUser("user-b"));
    Equal(1, cooldown.Records.Count, "同一场袭击不应同时产生结算和超时两个防守方冷却");
    Equal(RaidCooldownReason.SettlementCompleted, cooldown.Records.Single().Reason, "防守方冷却应按结算完成计算");
}

static void VerifyRaidCooldownProjection()
{
    var cooldownPolicy = new RaidCooldownPolicy(
        SettlementCooldown: TimeSpan.FromDays(3),
        TimeoutCooldown: TimeSpan.FromDays(1),
        CancelledCooldown: TimeSpan.FromHours(12));
    AuthoritativeEvent activeRaid = RaidEvent("cooldown-active", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-active",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch.AddHours(1),
            FinishedAtUtc: null,
            Settlement: null)
    };
    AuthoritativeEvent completedRaid = RaidEvent("cooldown-completed", targetOnline: false) with
    {
        Payload = new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: "snapshot-after",
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: DateTimeOffset.UnixEpoch.AddHours(2),
            new RaidSettlementRecord(
                "snapshot-before",
                "snapshot-after",
                0.5,
                Array.Empty<string>(),
                Array.Empty<RaidSettlementLossRecord>(),
                IgnoredExtraThingCount: 0))
    };
    AuthoritativeEvent failedRaid = RaidEvent("cooldown-failed", targetOnline: false) with
    {
        Status = ServerEventStatus.Failed,
        Payload = new RaidEventPayload(
            "snapshot-failed",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null)
    };
    AuthoritativeEvent timeoutNotice = ServerNotificationEvent("raid-timeout:" + failedRaid.EventId + ":user-b", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddHours(2)
    };
    AuthoritativeEvent cancelledRaid = RaidEvent("cooldown-cancelled", targetOnline: false) with
    {
        Status = ServerEventStatus.Cancelled,
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddHours(3)
    };

    RaidCooldownStatus status = RaidCooldownProjector.BuildForDefender(
        "user-b",
        "colony-b",
        new[] { activeRaid, completedRaid, failedRaid, timeoutNotice, cancelledRaid },
        defenderNextAvailableAtUtc: DateTimeOffset.UnixEpoch.AddHours(4),
        cooldownPolicy);

    Equal(3, status.Records.Count, "完成、失败和取消各产生一条冷却");
    Require(!status.Records.Any(record => record.RaidEventId == activeRaid.EventId), "未完成袭击不应提前产生冷却");

    RaidCooldownRecord completed = status.Records.Single(record => record.RaidEventId == completedRaid.EventId);
    Equal(RaidCooldownReason.SettlementCompleted, completed.Reason, "完成结算冷却原因");
    Equal(DateTimeOffset.UnixEpoch.AddHours(4), completed.StartsAtUtc, "冷却应从防守方下次可登录时间开始");
    Equal(DateTimeOffset.UnixEpoch.AddHours(4).AddDays(3), completed.CooldownUntilUtc, "完成结算冷却截止");

    RaidCooldownRecord failed = status.Records.Single(record => record.RaidEventId == failedRaid.EventId);
    Equal(RaidCooldownReason.TimeoutFailed, failed.Reason, "超时失败冷却原因");
    Equal(DateTimeOffset.UnixEpoch.AddHours(4).AddDays(1), failed.CooldownUntilUtc, "超时失败冷却截止");

    RaidCooldownRecord cancelled = status.Records.Single(record => record.RaidEventId == cancelledRaid.EventId);
    Equal(RaidCooldownReason.Cancelled, cancelled.Reason, "取消冷却原因");
    Equal(DateTimeOffset.UnixEpoch.AddHours(16), cancelled.CooldownUntilUtc, "取消冷却截止");
    Equal(DateTimeOffset.UnixEpoch.AddHours(4).AddDays(3), status.CooldownUntilUtc, "总冷却截止取最晚时间");

    RaidInitiationRequest request = RaidStartRequest("cooldown-eligibility", isHostile: true, defenderOnline: false, defenderWealth: 8000);
    RaidEligibilityResult eligibility = RaidEligibilityChecker.Check(request.Eligibility with
    {
        DefenderRaidCooldownUntilUtc = status.CooldownUntilUtc
    }, new RaidEligibilityPolicy(MinimumDefenderWealth: 5000));
    Require(eligibility.FailureReasons.Contains(RaidEligibilityFailureReason.CooldownActive), "资格检查应读取投影出的冷却截止时间");

    RaidCooldownStatus pendingLoginProtection = RaidCooldownProjector.BuildForDefender(
        "user-b",
        "colony-b",
        new[] { completedRaid },
        policy: cooldownPolicy,
        defenderProtectionStartResolver: _ => null,
        requireDefenderProtectionActivation: true);
    RaidCooldownRecord pendingRecord = pendingLoginProtection.Records.Single();
    Equal(DateTimeOffset.MaxValue, pendingRecord.CooldownUntilUtc, "防守方未登录时袭击保护不应消耗倒计时");

    DateTimeOffset defenderLoginAt = DateTimeOffset.UnixEpoch.AddDays(10);
    RaidCooldownStatus activatedProtection = RaidCooldownProjector.BuildForDefender(
        "user-b",
        "colony-b",
        new[] { completedRaid },
        policy: cooldownPolicy,
        defenderProtectionStartResolver: raid => raid.EventId == completedRaid.EventId ? defenderLoginAt : null,
        requireDefenderProtectionActivation: true);
    RaidCooldownRecord activatedRecord = activatedProtection.Records.Single();
    Equal(defenderLoginAt, activatedRecord.StartsAtUtc, "防守方首次登录后保护倒计时才开始");
    Equal(defenderLoginAt.AddDays(3), activatedRecord.CooldownUntilUtc, "登录激活后的保护截止按配置时长计算");

    RaidCooldownStatus disabledProtection = RaidCooldownProjector.BuildForDefender(
        "user-b",
        "colony-b",
        new[] { completedRaid },
        policy: cooldownPolicy with { SettlementCooldown = TimeSpan.Zero },
        defenderProtectionStartResolver: _ => null,
        requireDefenderProtectionActivation: true);
    Equal(0, disabledProtection.Records.Count, "保护时长为 0 时不应产生离线无限保护");
}

static void VerifyRaidAttackerLossApplication()
{
    RaidAttackerLossRecord loss = RaidAttackerLossRecord.FromAttackForce(
        "raid:loss-application",
        AttackForce(),
        "timeout");

    RaidAttackerLossApplicationResult vanilla = RaidAttackerLossApplicator.Apply(
        loss,
        new RaidAttackerLossClientContext(
            "attacker-snapshot-before",
            MatchingCaravanFound: true,
            CaravanId: "Caravan_1"));

    Equal(RaidAttackerLossApplicationResultKind.AppliedWithVanillaCaravanLostEvent, vanilla.Kind, "找到远行队时应用结果");
    Require(vanilla.Applied, "找到远行队时应应用损失");
    Require(vanilla.TriggeredVanillaCaravanLostEvent, "找到远行队时应触发原版远行队失踪反馈");
    Equal(2, vanilla.RemovedPawnGlobalKeys.Count, "原版反馈后仍应移除全部投入 pawn");
    Equal(1, vanilla.RemovedThings.Count, "原版反馈后仍应移除携带物");
    Require(vanilla.RequiresSnapshotConfirmation, "损失应用后必须上传确认快照");
    Equal("LetterLabelAllCaravanColonistsDied", vanilla.Plan!.VanillaLetterLabelKey, "原版信件标题 key");

    RaidAttackerLossApplicationResult fallback = RaidAttackerLossApplicator.Apply(
        loss,
        new RaidAttackerLossClientContext(
            "attacker-snapshot-before",
            MatchingCaravanFound: false));

    Equal(RaidAttackerLossApplicationResultKind.AppliedWithSnapshotFallback, fallback.Kind, "找不到远行队时应用结果");
    Require(fallback.Applied, "找不到远行队时仍应应用快照损失");
    Require(!fallback.TriggeredVanillaCaravanLostEvent, "找不到远行队时不能伪触发原版 caravan 事件");
    Require(fallback.FailureReason!.Contains("未找到对应远行队", StringComparison.Ordinal), "退回路径应说明原因");
    Require(fallback.RequiresSnapshotConfirmation, "退回快照损失后仍必须上传确认快照");

    RaidAttackerLossApplicationResult mismatch = RaidAttackerLossApplicator.Apply(
        loss,
        new RaidAttackerLossClientContext(
            "wrong-snapshot",
            MatchingCaravanFound: true,
            CaravanId: "Caravan_1"));

    Equal(RaidAttackerLossApplicationResultKind.SnapshotMismatch, mismatch.Kind, "快照不匹配结果");
    Require(!mismatch.Applied, "快照不匹配时不应应用损失");
    Require(!mismatch.RequiresSnapshotConfirmation, "未应用时不需要确认快照");
}

static void VerifyRaidAttackerLossConfirmationConsumption()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent lossEvent = AttackerLossEvent("attacker-loss-001");
    ledger.Append(lossEvent);
    ledger.MarkDelivered(lossEvent.EventId, "attacker-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    var consumer = new RaidAttackerLossConfirmationConsumer(ledger);

    RaidAttackerLossConfirmationResult accepted = consumer.Consume(
        AttackerLossConfirmation(lossEvent.EventId, "attacker-snapshot-after", Array.Empty<ThingSummary>(), Array.Empty<PawnSummary>()),
        DateTimeOffset.UnixEpoch.AddMinutes(2));

    Equal(RaidAttackerLossConfirmationResultKind.Accepted, accepted.Kind, "损失确认结果");
    Equal(ServerEventStatus.AppliedToSnapshot, ledger.Find(lossEvent.EventId)!.Status, "损失确认应消费事件");
    Equal(EventApplicationResultKind.Applied, ledger.Find(lossEvent.EventId)!.LastApplicationResult, "损失确认应用结果");
    Equal("attacker-snapshot-after", ledger.Find(lossEvent.EventId)!.AppliedSnapshotId, "确认快照 ID");

    RaidAttackerLossConfirmationResult repeated = consumer.Consume(
        AttackerLossConfirmation(lossEvent.EventId, "attacker-snapshot-after", Array.Empty<ThingSummary>(), Array.Empty<PawnSummary>()),
        DateTimeOffset.UnixEpoch.AddMinutes(3));
    Equal(RaidAttackerLossConfirmationResultKind.AlreadyApplied, repeated.Kind, "重复确认结果");
    Equal("attacker-snapshot-after", repeated.AppliedSnapshotId, "重复确认保持原应用快照");

    var mismatchLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent mismatchEvent = AttackerLossEvent("attacker-loss-mismatch");
    mismatchLedger.Append(mismatchEvent);
    mismatchLedger.MarkDelivered(mismatchEvent.EventId, "attacker-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    RaidAttackerLossConfirmationResult mismatch = new RaidAttackerLossConfirmationConsumer(mismatchLedger).Consume(
        AttackerLossConfirmation(mismatchEvent.EventId, "attacker-snapshot-after", Array.Empty<ThingSummary>(), Array.Empty<PawnSummary>()) with
        {
            AttackerSnapshotId = "attacker-snapshot-other"
        },
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(RaidAttackerLossConfirmationResultKind.SnapshotBaseMismatch, mismatch.Kind, "基线快照不匹配结果");
    Equal(ServerEventStatus.Conflict, mismatchLedger.Find(mismatchEvent.EventId)!.Status, "基线不匹配应进入冲突");

    var notReflectedLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent notReflectedEvent = AttackerLossEvent("attacker-loss-not-reflected");
    notReflectedLedger.Append(notReflectedEvent);
    notReflectedLedger.MarkDelivered(notReflectedEvent.EventId, "attacker-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    PawnSummary remainingPawn = Pawn("pawn-1", "attacker-snapshot-after");
    RaidAttackerLossConfirmationResult notReflected = new RaidAttackerLossConfirmationConsumer(notReflectedLedger).Consume(
        AttackerLossConfirmation(notReflectedEvent.EventId, "attacker-snapshot-after", Array.Empty<ThingSummary>(), new[] { remainingPawn }),
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(RaidAttackerLossConfirmationResultKind.LossNotReflected, notReflected.Kind, "损失未体现结果");
    Equal(ServerEventStatus.Conflict, notReflectedLedger.Find(notReflectedEvent.EventId)!.Status, "损失未体现应进入冲突");
    Equal(EventApplicationResultKind.LossNotReflected, notReflectedLedger.Find(notReflectedEvent.EventId)!.LastApplicationResult, "损失未体现分类");

    var worldPawnLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent worldPawnEvent = AttackerLossEvent("attacker-loss-world-pawn-reflected");
    worldPawnLedger.Append(worldPawnEvent);
    worldPawnLedger.MarkDelivered(worldPawnEvent.EventId, "attacker-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    PawnSummary worldPawn = Pawn("pawn-1", "attacker-snapshot-after") with
    {
        MapUniqueId = null,
        Source = "worldPawns/pawnsAlive"
    };
    RaidAttackerLossConfirmationResult worldPawnReflected = new RaidAttackerLossConfirmationConsumer(worldPawnLedger).Consume(
        AttackerLossConfirmation(worldPawnEvent.EventId, "attacker-snapshot-after", Array.Empty<ThingSummary>(), new[] { worldPawn }),
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(RaidAttackerLossConfirmationResultKind.Accepted, worldPawnReflected.Kind, "损失 pawn 已移入 worldPawns 时应视为损失已体现");

    var wrongEventLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent gift = GiftEvent("gift-not-loss", targetOnline: false);
    wrongEventLedger.Append(gift);
    RaidAttackerLossConfirmationResult wrongEvent = new RaidAttackerLossConfirmationConsumer(wrongEventLedger).Consume(
        AttackerLossConfirmation(gift.EventId, "attacker-snapshot-after", Array.Empty<ThingSummary>(), Array.Empty<PawnSummary>()),
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(RaidAttackerLossConfirmationResultKind.NotAttackerLossEvent, wrongEvent.Kind, "错误事件确认结果");
    Equal(ServerEventStatus.PendingOfflineDelivery, wrongEventLedger.Find(gift.EventId)!.Status, "错误事件不应被消费");
}

static void VerifyGiftApplicationConfirmationConsumption()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent gift = GiftEvent("gift-confirm-accepted", targetOnline: false);
    ledger.Append(gift);
    ledger.MarkDelivered(gift.EventId, "target-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    var consumer = new GiftApplicationConfirmationConsumer(ledger);

    GiftApplicationConfirmationResult accepted = consumer.Consume(
        GiftConfirmation(gift.EventId, "target-snapshot-before", "target-snapshot-after", "GiftAnchored"),
        DateTimeOffset.UnixEpoch.AddMinutes(2));

    Equal(GiftApplicationConfirmationResultKind.Accepted, accepted.Kind, "礼物确认结果");
    Equal(ServerEventStatus.AppliedToSnapshot, ledger.Find(gift.EventId)!.Status, "礼物确认应消费事件");
    Equal(EventApplicationResultKind.Applied, ledger.Find(gift.EventId)!.LastApplicationResult, "礼物确认应用结果");
    Equal("target-snapshot-after", ledger.Find(gift.EventId)!.AppliedSnapshotId, "礼物确认快照 ID");

    GiftApplicationConfirmationResult repeated = consumer.Consume(
        GiftConfirmation(gift.EventId, "target-snapshot-before", "target-snapshot-after", "GiftAnchored"),
        DateTimeOffset.UnixEpoch.AddMinutes(3));
    Equal(GiftApplicationConfirmationResultKind.AlreadyApplied, repeated.Kind, "礼物重复确认结果");
    Equal("target-snapshot-after", repeated.AppliedSnapshotId, "礼物重复确认保持原应用快照");

    var notDeliveredLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent notDeliveredGift = GiftEvent("gift-confirm-not-delivered", targetOnline: false);
    notDeliveredLedger.Append(notDeliveredGift);
    GiftApplicationConfirmationResult notDelivered = new GiftApplicationConfirmationConsumer(notDeliveredLedger).Consume(
        GiftConfirmation(notDeliveredGift.EventId, "target-snapshot-before", "target-snapshot-after", "GiftAnchored"),
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(GiftApplicationConfirmationResultKind.NotDelivered, notDelivered.Kind, "未下发礼物不能确认");
    Equal(ServerEventStatus.Conflict, notDeliveredLedger.Find(notDeliveredGift.EventId)!.Status, "未下发确认应进入冲突");

    var mismatchLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent mismatchGift = GiftEvent("gift-confirm-mismatch", targetOnline: false);
    mismatchLedger.Append(mismatchGift);
    mismatchLedger.MarkDelivered(mismatchGift.EventId, "target-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    GiftApplicationConfirmationResult mismatch = new GiftApplicationConfirmationConsumer(mismatchLedger).Consume(
        GiftConfirmation(mismatchGift.EventId, "target-snapshot-other", "target-snapshot-after", "GiftAnchored"),
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(GiftApplicationConfirmationResultKind.SnapshotBaseMismatch, mismatch.Kind, "礼物基线快照不匹配结果");
    Equal(ServerEventStatus.Conflict, mismatchLedger.Find(mismatchGift.EventId)!.Status, "礼物基线不匹配应进入冲突");

    var rejectedLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent rejectedGift = GiftEvent("gift-confirm-rejected", targetOnline: false);
    rejectedLedger.Append(rejectedGift);
    rejectedLedger.MarkDelivered(rejectedGift.EventId, "target-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    rejectedLedger.MarkRejected(rejectedGift.EventId, DateTimeOffset.UnixEpoch.AddMinutes(2), "不要礼物");
    GiftApplicationConfirmationResult rejected = new GiftApplicationConfirmationConsumer(rejectedLedger).Consume(
        GiftConfirmation(rejectedGift.EventId, "target-snapshot-before", "target-snapshot-after", "GiftAnchored"),
        DateTimeOffset.UnixEpoch.AddMinutes(3));
    Equal(GiftApplicationConfirmationResultKind.RejectedByTarget, rejected.Kind, "已拒绝礼物不能确认");
    Equal(ServerEventStatus.RejectedByTarget, rejectedLedger.Find(rejectedGift.EventId)!.Status, "已拒绝礼物保持拒绝状态");

    var wrongTargetLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent wrongTargetGift = GiftEvent("gift-confirm-wrong-target", targetOnline: false);
    wrongTargetLedger.Append(wrongTargetGift);
    wrongTargetLedger.MarkDelivered(wrongTargetGift.EventId, "target-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    GiftApplicationConfirmationResult wrongTarget = new GiftApplicationConfirmationConsumer(wrongTargetLedger).Consume(
        GiftConfirmation(wrongTargetGift.EventId, "target-snapshot-before", "target-snapshot-after", "GiftAnchored") with
        {
            OwnerId = "user-c",
            ColonyId = "colony-c",
            ConfirmedSnapshot = SnapshotRecord("user-c", "colony-c", "target-snapshot-after", Array.Empty<ThingSummary>(), Array.Empty<PawnSummary>())
        },
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(GiftApplicationConfirmationResultKind.NotTarget, wrongTarget.Kind, "非目标用户不能确认礼物");
    Equal(ServerEventStatus.DeliveredToClient, wrongTargetLedger.Find(wrongTargetGift.EventId)!.Status, "非目标确认不应消费事件");

    var notAnchoredLedger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent notAnchoredGift = GiftEvent("gift-confirm-not-anchored", targetOnline: false);
    notAnchoredLedger.Append(notAnchoredGift);
    notAnchoredLedger.MarkDelivered(notAnchoredGift.EventId, "target-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(1));
    GiftApplicationConfirmationResult notAnchored = new GiftApplicationConfirmationConsumer(notAnchoredLedger).Consume(
        GiftConfirmation(notAnchoredGift.EventId, "target-snapshot-before", "target-snapshot-after", string.Empty),
        DateTimeOffset.UnixEpoch.AddMinutes(2));
    Equal(GiftApplicationConfirmationResultKind.NotAnchored, notAnchored.Kind, "未声明已生成投递结果不能确认");
    Equal(ServerEventStatus.Conflict, notAnchoredLedger.Find(notAnchoredGift.EventId)!.Status, "未生成投递结果确认应进入冲突");
}

static void VerifyCommonEventShellForPayloads()
{
    AuthoritativeEvent gift = GiftEvent("gift-common", targetOnline: false);
    AuthoritativeEvent trade = AuthoritativeEventFactory.Create(
        ServerEventType.Trade,
        Actor(),
        Target(),
        "trade-common",
        targetOnline: true,
        new TradeEventPayload(
            "trade-001",
            TradeStage.ServerDropPodExchange,
            new[] { new EventThingReference("thing-offer", "Steel", 100) },
            new[] { new EventThingReference("thing-request", "Medicine", 5) },
            FeeSilver: 10,
            AcceptedByUserId: "user-b",
            FulfillmentMode: TradeFulfillmentMode.ServerDropPod,
            PostagePaidByAcceptor: true),
        DateTimeOffset.UnixEpoch);
    var pawnReference = new CrossMapPawnReference(
        "owner:user-a/colony:colony-a/snapshot:snapshot-pawn-source/map:worldPawns/pawn:pawn-global-001",
        "snapshot-pawn-source",
        "Li",
        Dead: false,
        "Faction_0",
        new Dictionary<string, string?>
        {
            ["pawn.metadata.ideoGlobalId"] = "owner:user-a/colony:colony-a/snapshot:snapshot-pawn-source/ideo:ideo-001"
        });
    PawnExchangePackage pawnPackage = PawnExchangePackageFixture(pawnReference);
    AuthoritativeEvent support = AuthoritativeEventFactory.Create(
        ServerEventType.SupportPawn,
        Actor(),
        Target(),
        "support-common",
        targetOnline: true,
        new SupportPawnEventPayload(
            "pawn-global-001",
            "snapshot-pawn-source",
            "Li",
            TemporaryControl: true,
            DateTimeOffset.UnixEpoch.AddDays(1),
            pawnReference,
            pawnPackage),
        DateTimeOffset.UnixEpoch);

    Require(gift.Payload is GiftEventPayload, "礼物载荷类型");
    Require(trade.Payload is TradeEventPayload, "交易载荷类型");
    Require(support.Payload is SupportPawnEventPayload, "支援载荷类型");
    Equal(pawnReference.GlobalId, ((SupportPawnEventPayload)support.Payload).PawnReference!.GlobalId, "支援 pawn 应可携带统一跨地图 pawn 引用");
    Equal("Human", ((SupportPawnEventPayload)support.Payload).PawnPackage!.Identity.ThingDef, "支援 pawn 应可携带固定字段交换包");
    Require(new[] { gift, trade, support }.All(evt => evt.Actor.UserId == "user-a" && evt.Target.UserId == "user-b"), "事件外壳应复用双方字段");
    Require(new[] { gift, trade, support }.All(evt => !string.IsNullOrWhiteSpace(evt.IdempotencyKey)), "事件外壳应复用幂等键字段");
}

static void VerifyPawnExchangePackageSafety()
{
    var reference = new CrossMapPawnReference(
        "owner:user-a/colony:colony-a/snapshot:snapshot-pawn-source/map:worldPawns/pawn:pawn-global-001",
        "snapshot-pawn-source",
        "Li",
        Dead: false,
        "Faction_0",
        new Dictionary<string, string?>
        {
            ["pawn.metadata.ideoGlobalId"] = "owner:user-a/colony:colony-a/snapshot:snapshot-pawn-source/ideo:ideo-001"
        });
    string referenceIdeoGlobalId = reference.Metadata!["pawn.metadata.ideoGlobalId"]!;
    PawnExchangePackage package = PawnExchangePackageFixture(reference) with
    {
        Extensions = new[]
        {
            new PawnExchangeExtensionPackage(
                "ludeon.rimworld.ideology",
                "rimworld.ideology.pawn-reference",
                new Dictionary<string, string?>
                {
                    ["ideoGlobalId"] = referenceIdeoGlobalId
                }),
            new PawnExchangeExtensionPackage(
                "rimworld.biotech",
                "rimworld.biotech.pawn-exchange",
                new Dictionary<string, string?>
                {
                    ["customXenotypeName"] = "测试异种",
                    ["customXenotypeSha256"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                },
                "{\"name\":\"测试异种\",\"xml\":\"<xenotype />\",\"xmlSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}")
        }
    };

    string json = SafePawnExchangeSerializer.Serialize(package);
    PawnExchangeReadResult parsed = SafePawnExchangeSerializer.Deserialize(json);
    Require(parsed.Accepted, $"合法 pawn 交换包应可解析：{parsed.Error}");
    Equal(reference.GlobalId, parsed.Package!.Reference.GlobalId, "pawn 交换包保留全局引用");
    Equal("Colonist", parsed.Package.Identity.PawnKindDef, "pawn 交换包保留 pawnKind");
    Equal("Parka", parsed.Package.Apparel.Single().Def, "pawn 交换包保留服装条目");
    Equal("parent", parsed.Package.Relationships.Single().RelationDef, "pawn 交换包保留关系占位符");
    Require(parsed.Package.Extensions!.Any(extension => extension.Kind == "rimworld.ideology.pawn-reference"), "pawn 交换包保留文化兼容扩展");
    PawnExchangeExtensionPackage biotechExtension = parsed.Package.Extensions!.Single(extension => extension.Kind == "rimworld.biotech.pawn-exchange");
    Require(biotechExtension.PayloadJson?.Contains("<xenotype />", StringComparison.Ordinal) == true, "pawn 交换包应保留兼容包 payload");

    PawnExchangeReadResult unknownField = SafePawnExchangeSerializer.Deserialize(json.Replace("\"PackageVersion\":", "\"Unexpected\":1,\"PackageVersion\":", StringComparison.Ordinal));
    Require(!unknownField.Accepted, "未知字段应被拒绝，避免静默吞掉新语义");

    PawnExchangeReadResult dangerousField = SafePawnExchangeSerializer.Deserialize(json.Replace("\"PackageVersion\":", "\"$type\":\"System.Diagnostics.Process, System.Diagnostics.Process\",\"PackageVersion\":", StringComparison.Ordinal));
    Require(!dangerousField.Accepted, "类型名字段应被拒绝");

    string oversized = "{\"PackageVersion\":1,\"Reference\":{\"GlobalId\":\"" + new string('a', SafePawnExchangeSerializer.MaxJsonBytes) + "\"}}";
    PawnExchangeReadResult oversizedResult = SafePawnExchangeSerializer.Deserialize(oversized);
    Require(!oversizedResult.Accepted, "超限 pawn 交换包应被拒绝");
}

static void VerifyPawnScribePayloadOneLayerReferenceReplacement()
{
    var reference = new CrossMapPawnReference(
        "owner:user-a/colony:colony-a/snapshot:snapshot-pawn-source/map:worldPawns/pawn:pawn-global-001",
        "snapshot-pawn-source",
        "Li",
        Dead: false,
        "Faction_0",
        null);
    var parentReference = new CrossMapPawnReference(
        "owner:user-a/colony:colony-a/snapshot:snapshot-pawn-source/map:worldPawns/pawn:pawn-parent-001",
        "snapshot-pawn-source",
        "Chen",
        Dead: false,
        "Faction_0",
        null);
    const string remoteParentLoadId = "Pawn_RemoteParent_001";
    const string placeholderParentLoadId = "Pawn_CoRPlaceholder_001";
    const string scribeXml =
        "<pawn Class=\"Pawn\">" +
        "<id>Pawn_Main_001</id>" +
        "<kindDef>Colonist</kindDef>" +
        "<relations><directRelations><li><otherPawn>Pawn_RemoteParent_001</otherPawn><def>Parent</def></li></directRelations></relations>" +
        "<equipment><innerList><li Class=\"ThingWithComps\"><def>Gun_BoltActionRifle</def><codedPawn>Pawn_RemoteParent_001</codedPawn></li></innerList></equipment>" +
        "<note>Pawn_RemoteParent_001 should not be replaced inside longer text</note>" +
        "<debugNestedPawn Class=\"Pawn\"><id>Pawn_RemoteParent_001</id><kindDef>Colonist</kindDef><name>DoNotTouch</name></debugNestedPawn>" +
        "</pawn>";
    string scribeHash = SafePawnExchangeSerializer.ComputeScribeXmlSha256(scribeXml);
    var scribe = new PawnScribePayload(
        scribeXml,
        scribeHash,
        new[]
        {
            new PawnScribePawnReferenceReplacement(remoteParentLoadId, placeholderParentLoadId, parentReference)
        });
    PawnExchangePackage package = PawnExchangePackageFixture(reference) with
    {
        Scribe = scribe
    };

    string json = SafePawnExchangeSerializer.Serialize(package);
    PawnExchangeReadResult parsed = SafePawnExchangeSerializer.Deserialize(json);
    Require(parsed.Accepted, $"带 Scribe 的 pawn 交换包应可解析：{parsed.Error}");
    Equal(scribe.XmlSha256, parsed.Package!.Scribe!.XmlSha256, "Scribe XML 哈希应保留");

    PawnScribeImportXmlResult rewritten = SafePawnExchangeSerializer.RewriteScribePawnReferencesForImport(parsed.Package.Scribe);
    Require(rewritten.Accepted, $"Scribe 引用替换应成功：{rewritten.Error}");
    Equal(2, rewritten.ReplacementCount, "只应替换直接引用和生物编码引用");
    Require(rewritten.Xml!.Contains("<otherPawn>Pawn_CoRPlaceholder_001</otherPawn>", StringComparison.Ordinal), "关系引用应替换为占位 pawn load id");
    Require(rewritten.Xml.Contains("<codedPawn>Pawn_CoRPlaceholder_001</codedPawn>", StringComparison.Ordinal), "装备生物编码引用应替换为占位 pawn load id");
    Require(rewritten.Xml.Contains("<note>Pawn_RemoteParent_001 should not be replaced inside longer text</note>", StringComparison.Ordinal), "非精确匹配文本不应替换");
    Require(rewritten.Xml.Contains("<debugNestedPawn Class=\"Pawn\"><id>Pawn_RemoteParent_001</id>", StringComparison.Ordinal), "嵌套 pawn 对象不应递归替换");

    string mismatchJson = json.Replace(scribeHash, new string('0', 64), StringComparison.Ordinal);
    PawnExchangeReadResult mismatch = SafePawnExchangeSerializer.Deserialize(mismatchJson);
    Require(!mismatch.Accepted, "Scribe XML 哈希不匹配应被拒绝");

    PawnScribeImportXmlResult entityXml = SafePawnExchangeSerializer.RewriteScribePawnReferencesForImport(
        scribe with
        {
            Xml = "<!DOCTYPE pawn [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><pawn>&xxe;</pawn>",
            XmlSha256 = null
        });
    Require(!entityXml.Accepted, "带 DTD 的 Scribe XML 应被拒绝");
}

static void VerifyDiplomacyAndServerNotificationEvents()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent alliance = AllianceRequestEvent("alliance-request-001", targetOnline: false);
    AuthoritativeEvent cancellation = AllianceCancellationEvent("alliance-cancellation-001", targetOnline: false);
    AuthoritativeEvent war = WarDeclarationEvent("war-declaration-001", targetOnline: false);
    AuthoritativeEvent peace = PeaceRequestEvent("peace-request-001", targetOnline: false);
    AuthoritativeEvent notice = ServerNotificationEvent("server-notice-001", targetOnline: false);

    ledger.Append(alliance);
    ledger.Append(cancellation);
    ledger.Append(war);
    ledger.Append(peace);
    ledger.Append(notice);

    Require(alliance.Payload is AllianceRequestEventPayload, "结盟请求载荷类型");
    Require(cancellation.Payload is AllianceCancellationEventPayload, "撕毁盟约载荷类型");
    Require(war.Payload is WarDeclarationEventPayload, "宣战通知载荷类型");
    Require(peace.Payload is PeaceRequestEventPayload, "求和请求载荷类型");
    Require(notice.Payload is ServerNotificationEventPayload, "服务器通知载荷类型");
    Equal(EventRejectionPolicy.RejectableByTarget, alliance.RejectionPolicy, "结盟请求应等待目标选择");
    Equal(EventRejectionPolicy.RejectableByTarget, peace.RejectionPolicy, "求和请求应等待目标选择");
    Equal(EventRejectionPolicy.NotRejectable, cancellation.RejectionPolicy, "撕毁盟约通知不可拒绝");
    Equal(EventRejectionPolicy.NotRejectable, war.RejectionPolicy, "宣战通知不可拒绝");
    Equal(EventRejectionPolicy.NotRejectable, notice.RejectionPolicy, "服务器通知不可拒绝");

    EventQueueSummary summary = EventQueueSummaryBuilder.BuildForTarget("user-b", ledger.ListForUser("user-b"));
    Require(summary.WaitingForConfirmation.Any(item => item.EventId == alliance.EventId), "结盟请求进入等待确认");
    Require(summary.WaitingForConfirmation.Any(item => item.EventId == peace.EventId), "求和请求进入等待确认");
    Require(summary.DirectlyProcessable.Any(item => item.EventId == cancellation.EventId), "撕毁盟约通知进入可直接处理");
    Require(summary.DirectlyProcessable.Any(item => item.EventId == war.EventId), "宣战通知进入可直接处理");
    Require(summary.DirectlyProcessable.Any(item => item.EventId == notice.EventId), "服务器通知进入可直接处理");

    AuthoritativeEvent rejectedAlliance = ledger.MarkRejected(alliance.EventId, DateTimeOffset.UnixEpoch.AddMinutes(1), "暂不结盟");
    Equal(ServerEventStatus.RejectedByTarget, rejectedAlliance.Status, "结盟请求可拒绝");
    AuthoritativeEvent acceptedPeace = ledger.MarkAccepted(peace.EventId, DateTimeOffset.UnixEpoch.AddMinutes(2), "接受求和");
    Equal(ServerEventStatus.AppliedToSnapshot, acceptedPeace.Status, "求和请求接受后不再进入待处理队列");
    Equal(TargetEventDecision.Accepted, acceptedPeace.TargetDecision, "求和请求记录目标接受");
    Throws<InvalidOperationException>(() => ledger.MarkRejected(cancellation.EventId, DateTimeOffset.UnixEpoch.AddMinutes(1), "不能拒绝撕毁盟约"));
    Throws<InvalidOperationException>(() => ledger.MarkRejected(war.EventId, DateTimeOffset.UnixEpoch.AddMinutes(1), "不能拒绝宣战"));
    Throws<InvalidOperationException>(() => ledger.MarkRejected(notice.EventId, DateTimeOffset.UnixEpoch.AddMinutes(1), "不能拒绝通知"));
}

static void VerifySnapshotBoundOfflineConsumption()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent pending = GiftEvent("snapshot-bound", targetOnline: false);
    ledger.Append(pending);

    Equal(1, ledger.ListDeliverableForTarget("user-b").Count, "未下发事件应可登录读取");

    AuthoritativeEvent delivered = ledger.MarkDelivered(
        pending.EventId,
        "target-snapshot-before",
        DateTimeOffset.UnixEpoch.AddMinutes(5));
    Equal(ServerEventStatus.DeliveredToClient, delivered.Status, "下发后状态");
    Equal("target-snapshot-before", delivered.DeliveredToSnapshotId, "下发绑定快照");
    Equal(1, ledger.ListDeliverableForTarget("user-b").Count, "已下发但未确认应用时仍应重新下发");

    AuthoritativeEvent applied = ledger.MarkApplied(
        pending.EventId,
        "target-snapshot-after",
        DateTimeOffset.UnixEpoch.AddMinutes(10));
    Equal(ServerEventStatus.AppliedToSnapshot, applied.Status, "确认写入快照后状态");
    Equal("target-snapshot-after", applied.AppliedSnapshotId, "应用快照");
    Equal(0, ledger.ListDeliverableForTarget("user-b").Count, "确认应用后不应再次下发");
}

static void VerifyEventQueueSummary()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent direct = RaidEvent("queue-direct", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1)
    };
    AuthoritativeEvent waiting = GiftEvent("queue-waiting", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(2)
    };
    AuthoritativeEvent delivered = GiftEvent("queue-delivered", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(3)
    };
    AuthoritativeEvent conflict = RaidEvent("queue-conflict", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(4)
    };
    AuthoritativeEvent rejected = GiftEvent("queue-rejected", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(5)
    };
    AuthoritativeEvent applied = RaidEvent("queue-applied", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(6)
    };

    ledger.Append(direct);
    ledger.Append(waiting);
    ledger.Append(delivered);
    ledger.Append(conflict);
    ledger.Append(rejected);
    ledger.Append(applied);
    ledger.MarkDelivered(delivered.EventId, "delivered-snapshot", DateTimeOffset.UnixEpoch.AddMinutes(7));
    ledger.ReportApplicationResult(conflict.EventId, EventApplicationResultKind.SnapshotBaseMismatch, "目标快照不匹配", DateTimeOffset.UnixEpoch.AddMinutes(8));
    ledger.MarkRejected(rejected.EventId, DateTimeOffset.UnixEpoch.AddMinutes(9), "目标拒绝");
    ledger.MarkDelivered(applied.EventId, "applied-before", DateTimeOffset.UnixEpoch.AddMinutes(10));
    ledger.MarkApplied(applied.EventId, "applied-after", DateTimeOffset.UnixEpoch.AddMinutes(11));

    EventQueueSummary summary = EventQueueSummaryBuilder.BuildForTarget("user-b", ledger.ListForUser("user-b"));

    Equal("user-b", summary.UserId, "摘要用户");
    Equal(direct.EventId, summary.DirectlyProcessable.Single().EventId, "可直接处理事件");
    Equal(waiting.EventId, summary.WaitingForConfirmation.Single().EventId, "等待确认事件");
    Equal(delivered.EventId, summary.DeliveredUnconfirmed.Single().EventId, "已下发未确认事件");
    Equal(conflict.EventId, summary.Conflicts.Single().EventId, "冲突事件");
    Equal(rejected.EventId, summary.Rejected.Single().EventId, "已拒绝事件");

    EventQueueItem waitingItem = summary.WaitingForConfirmation.Single();
    Equal(ServerEventType.Gift, waitingItem.Type, "摘要事件类型");
    Equal("Map_0", waitingItem.TargetMapUniqueId, "摘要目标地图");
    Equal(12345, waitingItem.TargetTile, "摘要目标地块");
    Require(waitingItem.NeedsUserChoice, "礼物应需要用户选择");
    Require(!summary.DirectlyProcessable.Single().NeedsUserChoice, "不可拒绝事件不需要用户选择");
    Equal("目标快照不匹配", summary.Conflicts.Single().FailureReason, "摘要失败原因");
    Require(summary.AllItems.All(item => item.EventId != applied.EventId), "已应用事件不应出现在处理队列");
}

static void VerifySnapshotlessNotificationEventSemantics()
{
    AuthoritativeEvent war = WarDeclarationEvent("queue-war-notice", targetOnline: false);
    AuthoritativeEvent cancellation = AllianceCancellationEvent("queue-cancel-notice", targetOnline: false);
    AuthoritativeEvent allianceRequest = AllianceRequestEvent("queue-alliance-request-choice", targetOnline: false);
    AuthoritativeEvent tradeExchange = AuthoritativeEventFactory.Create(
        ServerEventType.Trade,
        Actor(),
        Target(),
        "queue-trade-exchange-application",
        targetOnline: false,
        new TradeEventPayload(
            "trade-001",
            TradeStage.ServerDropPodExchange,
            new[] { new EventThingReference("thing-offer", "Steel", 100) },
            new[] { new EventThingReference("thing-request", "Medicine", 5) },
            FeeSilver: 10,
            AcceptedByUserId: "user-a",
            FulfillmentMode: TradeFulfillmentMode.ServerDropPod,
            PostagePaidByAcceptor: true),
        DateTimeOffset.UnixEpoch,
        TargetContext());

    EventQueueSummary summary = EventQueueSummaryBuilder.BuildForTarget(
        "user-b",
        new[] { war, cancellation, allianceRequest, tradeExchange });

    EventQueueItem warItem = summary.DirectlyProcessable.Single(item => item.EventId == war.EventId);
    EventQueueItem cancellationItem = summary.DirectlyProcessable.Single(item => item.EventId == cancellation.EventId);
    EventQueueItem tradeExchangeItem = summary.DirectlyProcessable.Single(item => item.EventId == tradeExchange.EventId);
    EventQueueItem requestItem = summary.WaitingForConfirmation.Single(item => item.EventId == allianceRequest.EventId);

    Require(!warItem.RequiresClientApplication, "宣战通知已由服务器生效，不应要求目标上传快照确认");
    Require(!cancellationItem.RequiresClientApplication, "撕毁盟约已由服务器生效，不应要求目标上传快照确认");
    Require(tradeExchangeItem.RequiresClientApplication, "交易实际交换会改变本地存档，仍应要求快照确认");
    Require(requestItem.RequiresClientApplication, "结盟请求需要玩家选择并修改外交状态，仍应要求处理");

    IReadOnlyList<EventLetterProjection> letters = EventLetterProjectionBuilder.Build(summary, new[] { war, cancellation, allianceRequest, tradeExchange });
    EventLetterProjection warLetter = letters.Single(letter => letter.EventId == war.EventId);
    EventLetterProjection cancellationLetter = letters.Single(letter => letter.EventId == cancellation.EventId);
    EventLetterProjection tradeLetter = letters.Single(letter => letter.EventId == tradeExchange.EventId);

    Require(!warLetter.Actions.Any(action => action.Kind == EventLetterActionKind.ApplyToSnapshot || action.Kind == EventLetterActionKind.UploadSnapshotConfirmation), "宣战信件不应带快照处理动作");
    Require(!cancellationLetter.Actions.Any(action => action.Kind == EventLetterActionKind.ApplyToSnapshot || action.Kind == EventLetterActionKind.UploadSnapshotConfirmation), "撕毁盟约信件不应带快照处理动作");
    Require(tradeLetter.Actions.Any(action => action.Kind == EventLetterActionKind.ApplyToSnapshot), "交易交换信件仍应提供应用动作");
}

static void VerifyEventQueueLedgerQuery()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent pending = ServerNotificationEvent("queue-query-pending", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1)
    };
    AuthoritativeEvent ready = ServerNotificationEvent("queue-query-ready", targetOnline: true) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(2)
    };
    AuthoritativeEvent delivered = GiftEvent("queue-query-delivered", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(3)
    };
    AuthoritativeEvent conflict = RaidEvent("queue-query-conflict", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(4)
    };
    AuthoritativeEvent rejected = GiftEvent("queue-query-rejected", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(5)
    };
    AuthoritativeEvent applied = RaidEvent("queue-query-applied", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(6)
    };
    AuthoritativeEvent actorOnly = GiftEvent("queue-query-actor-only", targetOnline: false) with
    {
        Actor = Target(),
        Target = Actor(),
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(7)
    };

    ledger.Append(pending);
    ledger.Append(ready);
    ledger.Append(delivered);
    ledger.Append(conflict);
    ledger.Append(rejected);
    ledger.Append(applied);
    ledger.Append(actorOnly);
    ledger.MarkDelivered(delivered.EventId, "delivered-snapshot", DateTimeOffset.UnixEpoch.AddMinutes(8));
    ledger.ReportApplicationResult(conflict.EventId, EventApplicationResultKind.SnapshotBaseMismatch, "目标快照不匹配", DateTimeOffset.UnixEpoch.AddMinutes(9));
    ledger.MarkRejected(rejected.EventId, DateTimeOffset.UnixEpoch.AddMinutes(10), "目标拒绝");
    ledger.MarkDelivered(applied.EventId, "applied-before", DateTimeOffset.UnixEpoch.AddMinutes(11));
    ledger.MarkApplied(applied.EventId, "applied-after", DateTimeOffset.UnixEpoch.AddMinutes(12));

    IReadOnlyList<AuthoritativeEvent> queueEvents = ledger.ListQueueForTarget("user-b");
    List<string> queuedIds = queueEvents.Select(evt => evt.EventId).ToList();
    Equal(5, queuedIds.Count, "队列查询只返回目标用户可见状态");
    Require(queuedIds.Contains(pending.EventId), "队列应包含待离线下发事件");
    Require(queuedIds.Contains(ready.EventId), "队列应包含在线即时事件");
    Require(queuedIds.Contains(delivered.EventId), "队列应包含已下发未确认事件");
    Require(queuedIds.Contains(conflict.EventId), "队列应包含冲突事件");
    Require(queuedIds.Contains(rejected.EventId), "队列应包含已拒绝事件");
    Require(!queuedIds.Contains(applied.EventId), "队列不应包含已应用事件");
    Require(!queuedIds.Contains(actorOnly.EventId), "队列不应包含仅由目标用户发起的事件");

    EventQueueSummary summary = EventQueueSummaryBuilder.BuildForTarget("user-b", queueEvents);
    Equal(2, summary.DirectlyProcessable.Count, "待处理组应包含离线和在线直接事件");
    Equal(delivered.EventId, summary.DeliveredUnconfirmed.Single().EventId, "已下发组");
    Equal(conflict.EventId, summary.Conflicts.Single().EventId, "冲突组");
    Equal(rejected.EventId, summary.Rejected.Single().EventId, "拒绝组");
}

static void VerifyEventLetterProjection()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent gift = GiftEvent("letter-gift", targetOnline: false);
    AuthoritativeEvent raid = RaidEvent("letter-raid", targetOnline: false);
    AuthoritativeEvent notice = ServerNotificationEvent("letter-notice", targetOnline: false);
    AuthoritativeEvent delivered = GiftEvent("letter-delivered", targetOnline: false);
    AuthoritativeEvent conflict = RaidEvent("letter-conflict", targetOnline: false);

    ledger.Append(gift);
    ledger.Append(raid);
    ledger.Append(notice);
    ledger.Append(delivered);
    ledger.Append(conflict);
    ledger.MarkDelivered(delivered.EventId, "delivered-snapshot", DateTimeOffset.UnixEpoch.AddMinutes(1));
    ledger.ReportApplicationResult(conflict.EventId, EventApplicationResultKind.SnapshotBaseMismatch, "目标快照不匹配", DateTimeOffset.UnixEpoch.AddMinutes(2));

    IReadOnlyList<AuthoritativeEvent> events = ledger.ListForUser("user-b");
    EventQueueSummary summary = EventQueueSummaryBuilder.BuildForTarget("user-b", events);
    IReadOnlyList<EventLetterProjection> letters = EventLetterProjectionBuilder.Build(summary, events);

    Equal(summary.AllItems.Count, letters.Count, "每个队列项都应生成一个信件投影");

    EventLetterProjection giftLetter = letters.Single(letter => letter.EventId == gift.EventId);
    Equal(EventLetterKind.Choice, giftLetter.Kind, "可拒绝礼物应生成可选择信件");
    Equal(EventLetterDefName.PositiveEvent, giftLetter.LetterDef, "礼物信件类型");
    Require(giftLetter.Actions.Any(action => action.Kind == EventLetterActionKind.Accept && action.ChangesLedgerState && action.RequiresServerRoundtrip), "礼物应有接受动作");
    Require(giftLetter.Actions.Any(action => action.Kind == EventLetterActionKind.Reject && action.ChangesLedgerState && action.RequiresServerRoundtrip), "礼物应有拒绝动作");
    Require(giftLetter.Actions.Any(action => action.Kind == EventLetterActionKind.Postpone && !action.ChangesLedgerState), "礼物应能稍后处理");
    Require(giftLetter.Actions.Any(action => action.Kind == EventLetterActionKind.JumpToTarget), "有目标地图的事件应可定位");
    Require(!giftLetter.DismissalChangesLedgerState, "关闭礼物信件不应修改账本");

    EventLetterProjection raidLetter = letters.Single(letter => letter.EventId == raid.EventId);
    Equal(EventLetterKind.Standard, raidLetter.Kind, "不可拒绝袭击应生成普通信件");
    Equal(EventLetterDefName.ThreatBig, raidLetter.LetterDef, "袭击应为重大威胁信件");
    Require(raidLetter.Actions.Any(action => action.Kind == EventLetterActionKind.ApplyToSnapshot && action.ChangesLedgerState), "可直接处理事件应有应用动作");
    Require(raidLetter.Actions.Any(action => action.Kind == EventLetterActionKind.Close && !action.ChangesLedgerState), "普通信件关闭不应修改账本");

    EventLetterProjection noticeLetter = letters.Single(letter => letter.EventId == notice.EventId);
    Equal("服务器维护", noticeLetter.Label, "服务器通知应使用载荷标题");
    Equal("服务器将在本轮结束后维护。", noticeLetter.Text, "服务器通知应使用载荷正文");
    Equal(EventLetterDefName.NegativeEvent, noticeLetter.LetterDef, "警告级服务器通知应为负面信件");
    Require(!noticeLetter.Actions.Any(action => action.Kind == EventLetterActionKind.ApplyToSnapshot), "服务器通知不应要求应用快照");
    Require(!noticeLetter.Actions.Any(action => action.Kind == EventLetterActionKind.UploadSnapshotConfirmation), "服务器通知不应要求上传确认快照");

    EventLetterProjection deliveredLetter = letters.Single(letter => letter.EventId == delivered.EventId);
    Require(deliveredLetter.Actions.Any(action => action.Kind == EventLetterActionKind.UploadSnapshotConfirmation && action.ChangesLedgerState), "已下发未确认事件应要求上传确认快照");
    Require(!deliveredLetter.DismissalChangesLedgerState, "关闭已下发未确认信件不应消费事件");

    EventLetterProjection conflictLetter = letters.Single(letter => letter.EventId == conflict.EventId);
    Equal(EventLetterDefName.NegativeEvent, conflictLetter.LetterDef, "冲突事件应为负面信件");
    Require(conflictLetter.Text.Contains("目标快照不匹配", StringComparison.Ordinal), "冲突信件应显示失败原因");
    Require(conflictLetter.Actions.Any(action => action.Kind == EventLetterActionKind.OpenConflict && !action.ChangesLedgerState), "冲突信件应提供查看冲突动作");
}

static void VerifyTradeAcceptanceMemoEventsAreHiddenFromLetterQueue()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent memo = AuthoritativeEventFactory.Create(
        ServerEventType.Trade,
        Target(),
        Target(),
        "queue-trade-accepted-memo",
        targetOnline: true,
        new TradeEventPayload(
            "trade-001",
            TradeStage.AcceptedMemo,
            new[] { new EventThingReference("thing-offer", "Steel", 100) },
            new[] { new EventThingReference("thing-request", "Medicine", 5) },
            FeeSilver: 10,
            AcceptedByUserId: "user-b"),
        DateTimeOffset.UnixEpoch,
        TargetContext());
    ledger.Append(memo);
    ledger.MarkDelivered(memo.EventId, "memo-delivered-snapshot", DateTimeOffset.UnixEpoch.AddMinutes(1));

    IReadOnlyList<AuthoritativeEvent> events = ledger.ListForUser("user-b");
    Require(events.Any(evt => evt.EventId == memo.EventId), "接单备忘录仍应保留在账本查询中");

    EventQueueSummary summary = EventQueueSummaryBuilder.BuildForTarget("user-b", events);
    Require(!summary.AllItems.Any(item => item.EventId == memo.EventId), "接单备忘录不应进入玩家信件队列");
}

static void VerifyPlayerRaidSourceEventsAreHiddenFromDefenderQueue()
{
    AuthoritativeEvent sourceRaid = AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        Actor(),
        Target(),
        "queue-source-player-raid",
        targetOnline: false,
        new RaidEventPayload(
            "defender-snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: new RaidAttackForceRecord(
                "attacker-snapshot-after",
                new[] { "attacker:pawn-1" },
                Array.Empty<EventThingReference>()),
            OpponentKind: RaidOpponentKind.Player),
        DateTimeOffset.UnixEpoch,
        TargetContext());
    AuthoritativeEvent settlementNotice = AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        Actor(),
        Target(),
        "queue-player-raid-settlement",
        targetOnline: false,
        new RaidEventPayload(
            "defender-snapshot-before",
            ReturnedSnapshotId: "defender-snapshot-after",
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(5),
            Settlement: new RaidSettlementRecord(
                "defender-snapshot-before",
                "attacker-returned-snapshot",
                0.5,
                Array.Empty<string>(),
                new[]
                {
                    new RaidSettlementLossRecord(
                        "defender:thing-1",
                        "Steel",
                        Position: null,
                        MapUniqueId: null,
                        WholeThingMissing: true,
                        OriginalStackCount: 20,
                        ReturnedStackCount: null,
                        StolenStackCount: 20,
                        BaseLossCapCount: 10,
                        FractionalCapChance: 0,
                        FractionalRoll: 0,
                        MaxLossCount: 10,
                        LossCount: 10)
                },
                IgnoredExtraThingCount: 0),
            AttackForce: null,
            OpponentKind: RaidOpponentKind.Player),
        DateTimeOffset.UnixEpoch.AddMinutes(6),
        TargetContext());

    EventQueueSummary summary = EventQueueSummaryBuilder.BuildForTarget("user-b", new[] { sourceRaid, settlementNotice });

    Require(summary.AllItems.All(item => item.EventId != sourceRaid.EventId), "玩家袭击源事件不应生成防守方信件");
    EventQueueItem settlementItem = summary.DirectlyProcessable.Single(item => item.EventId == settlementNotice.EventId);
    Require(!settlementItem.RequiresClientApplication, "袭击结算通知不应要求防守方再次应用快照");
}

static void VerifyRejectionPolicyAndTargetContext()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent gift = GiftEvent("rejectable-gift", targetOnline: false);
    AuthoritativeEvent trade = TradeExchangeEvent("not-rejectable-trade-exchange", targetOnline: false);

    ledger.Append(gift);
    ledger.Append(trade);

    Equal(EventRejectionPolicy.RejectableByTarget, gift.RejectionPolicy, "礼物应可拒绝");
    Equal(EventRejectionPolicy.NotRejectable, trade.RejectionPolicy, "实际交换事件不可拒绝");
    Equal("Map_0", gift.TargetContext!.MapUniqueId, "礼物目标地图");
    Equal(EventLandingMode.StorageZone, gift.TargetContext.LandingMode, "礼物落点策略");

    AuthoritativeEvent rejectedGift = ledger.MarkRejected(gift.EventId, DateTimeOffset.UnixEpoch.AddMinutes(1), "不要礼物");
    Equal(ServerEventStatus.RejectedByTarget, rejectedGift.Status, "礼物拒绝状态");
    Equal(TargetEventDecision.Rejected, rejectedGift.TargetDecision, "礼物拒绝决策");
    Equal(EventApplicationResultKind.Rejected, rejectedGift.LastApplicationResult, "礼物拒绝应用结果");
    Require(!ledger.ListDeliverableForTarget("user-b").Any(evt => evt.EventId == gift.EventId), "拒绝后的礼物不应再下发");

    Throws<InvalidOperationException>(() => ledger.MarkRejected(trade.EventId, DateTimeOffset.UnixEpoch.AddMinutes(1), "不能拒绝实际交换"));
}

static void VerifyRejectedGiftCreatesReturnEvent()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent gift = GiftEvent("gift-toxic-waste", targetOnline: false);
    ledger.Append(gift);

    GiftReturnResult result = ledger.RejectGiftAndCreateReturn(
        gift.EventId,
        DateTimeOffset.UnixEpoch.AddMinutes(2),
        "不要有毒垃圾",
        originalActorOnline: false);

    Equal(ServerEventStatus.RejectedByTarget, result.RejectedGift.Status, "原礼物拒绝状态");
    Require(result.ReturnEventCreated, "应创建退回事件");
    Equal(ServerEventType.GiftReturn, result.ReturnEvent.Type, "退回事件类型");
    Equal("user-b", result.ReturnEvent.Actor.UserId, "退回事件发起方应为拒绝方");
    Equal("user-a", result.ReturnEvent.Target.UserId, "退回事件目标应为原发起方");
    Equal(EventRejectionPolicy.NotRejectable, result.ReturnEvent.RejectionPolicy, "退回事件不可拒绝");
    Equal(ServerEventStatus.PendingOfflineDelivery, result.ReturnEvent.Status, "离线原发起方应收到待处理退回事件");
    Require(ledger.ListDeliverableForTarget("user-a").Any(evt => evt.EventId == result.ReturnEvent.EventId), "退回事件应下发给原发起方");
    Throws<InvalidOperationException>(() => ledger.MarkRejected(result.ReturnEvent.EventId, DateTimeOffset.UnixEpoch.AddMinutes(3), "不能拒绝退回"));
}

static void VerifyTradeAcceptanceMemoUntilExchange()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent acceptedMemo = TradeAcceptanceMemoEvent("trade-acceptance-memo", targetOnline: false);
    ledger.Append(acceptedMemo);

    var memoPayload = (TradeEventPayload)acceptedMemo.Payload;
    Equal(TradeStage.AcceptedMemo, memoPayload.Stage, "接单阶段");
    Equal("user-b", memoPayload.AcceptedByUserId, "接单玩家");
    Equal(TradeFulfillmentMode.Unspecified, memoPayload.FulfillmentMode, "备忘录未选择履约方式");
    Equal(ServerEventStatus.PendingOfflineDelivery, acceptedMemo.Status, "接单备忘录不是完成状态");

    AuthoritativeEvent cancelledMemo = ledger.ChangeStatus(acceptedMemo.EventId, ServerEventStatus.Cancelled);
    Equal(ServerEventStatus.Cancelled, cancelledMemo.Status, "接单备忘录可在交换前取消");
    Require(!ledger.ListDeliverableForTarget("user-a").Any(evt => evt.EventId == acceptedMemo.EventId), "已取消备忘录不应继续下发");

    AuthoritativeEvent exchange = TradeExchangeEvent("trade-server-drop-pod-exchange", targetOnline: false);
    ledger.Append(exchange);
    var exchangePayload = (TradeEventPayload)exchange.Payload;
    Equal(TradeStage.ServerDropPodExchange, exchangePayload.Stage, "实际交换阶段");
    Equal(TradeFulfillmentMode.ServerDropPod, exchangePayload.FulfillmentMode, "服务器空投履约方式");
    Require(exchangePayload.PostagePaidByAcceptor, "空投交换由接受方支付邮费");
    Throws<InvalidOperationException>(() => ledger.MarkRejected(exchange.EventId, DateTimeOffset.UnixEpoch.AddMinutes(3), "交换已确认"));

    ledger.MarkDelivered(exchange.EventId, "trade-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(4));
    AuthoritativeEvent appliedExchange = ledger.MarkApplied(exchange.EventId, "trade-snapshot-after", DateTimeOffset.UnixEpoch.AddMinutes(5));
    Equal(ServerEventStatus.AppliedToSnapshot, appliedExchange.Status, "实际交换写入快照后才完成");
}

static void VerifyTradeFulfillmentRequirementMatching()
{
    var requirements = new[]
    {
        new ThingReferenceDto("request:medicine", "MedicineIndustrial", 8, "Normal", 50)
    };
    var delivered = new[]
    {
        new ThingReferenceDto("caravan:medicine-1", "MedicineIndustrial", 4, "Good", 60),
        new ThingReferenceDto("caravan:medicine-2", "MedicineIndustrial", 4, "Normal", 50),
        new ThingReferenceDto("caravan:steel", "Steel", 200)
    };

    Require(TradeThingRequirementMatcher.Satisfies(requirements, delivered, out IReadOnlyList<string> missing), "相同或更高品质耐久的携带物应满足求购要求");
    Equal(0, missing.Count, "满足要求时不应返回缺失项");

    var fullDurabilityRequirement = new[]
    {
        new ThingReferenceDto("request:flak-vest", "Apparel_FlakVest", 1, hitPoints: 100, maxHitPoints: 100)
    };
    var nearlyFullDurabilityDelivered = new[]
    {
        new ThingReferenceDto("caravan:flak-vest-nearly-full", "Apparel_FlakVest", 1, hitPoints: 199, maxHitPoints: 200)
    };
    Require(
        TradeThingRequirementMatcher.Satisfies(fullDurabilityRequirement, nearlyFullDurabilityDelivered, out missing),
        "耐久按原版显示百分比匹配，显示为 100% 的轻微受损物品应满足 100% 要求");

    var visiblyDamagedDelivered = new[]
    {
        new ThingReferenceDto("caravan:flak-vest-damaged", "Apparel_FlakVest", 1, hitPoints: 198, maxHitPoints: 200)
    };
    Require(
        !TradeThingRequirementMatcher.Satisfies(fullDurabilityRequirement, visiblyDamagedDelivered, out missing),
        "显示为 99% 的物品不应满足 100% 耐久要求");

    var lowDurabilityRequirement = new[]
    {
        new ThingReferenceDto("request:damaged-flak-vest", "Apparel_FlakVest", 1, hitPoints: 50, maxHitPoints: 100)
    };
    var displayedHalfDurabilityDelivered = new[]
    {
        new ThingReferenceDto("caravan:flak-vest-half", "Apparel_FlakVest", 1, hitPoints: 100, maxHitPoints: 199)
    };
    Require(
        TradeThingRequirementMatcher.Satisfies(
            lowDurabilityRequirement,
            displayedHalfDurabilityDelivered,
            out missing,
            hitPointsRequirementMode: "AtMost"),
        "耐久不高于要求同样按显示百分比匹配");

    var tooHealthyDelivered = new[]
    {
        new ThingReferenceDto("caravan:flak-vest-too-healthy", "Apparel_FlakVest", 1, hitPoints: 102, maxHitPoints: 200)
    };
    Require(
        !TradeThingRequirementMatcher.Satisfies(
            lowDurabilityRequirement,
            tooHealthyDelivered,
            out missing,
            hitPointsRequirementMode: "AtMost"),
        "显示百分比高于上限的物品不应满足低耐久收购要求");

    var lowQualityDelivered = new[]
    {
        new ThingReferenceDto("caravan:poor-medicine", "MedicineIndustrial", 8, "Poor", 60)
    };
    Require(!TradeThingRequirementMatcher.Satisfies(requirements, lowQualityDelivered, out missing), "低于要求品质的携带物不能履约");
    Require(missing.Single().Contains("MedicineIndustrial", StringComparison.Ordinal), "缺失项应指出未满足的 ThingDef");

    var insufficientStack = new[]
    {
        new ThingReferenceDto("caravan:medicine-small", "MedicineIndustrial", 7, "Normal", 50)
    };
    Require(!TradeThingRequirementMatcher.Satisfies(requirements, insufficientStack, out missing), "数量不足不能履约");

    var overlappingRequirements = new[]
    {
        new ThingReferenceDto("request:normal-medicine", "MedicineIndustrial", 5, "Normal", 50),
        new ThingReferenceDto("request:good-medicine", "MedicineIndustrial", 5, "Good", 50)
    };
    var overlappingDelivered = new[]
    {
        new ThingReferenceDto("caravan:good-medicine", "MedicineIndustrial", 5, "Good", 60)
    };
    Require(!TradeThingRequirementMatcher.Satisfies(overlappingRequirements, overlappingDelivered, out missing), "同一批携带物不能被多个要求重复计数");

    var metadataMatchers = new ITradeThingMetadataMatcher[] { new TestTradeThingMetadataMatcher() };
    var geneRequirements = new[]
    {
        new ThingReferenceDto(
            "request:genepack-deathless",
            "Genepack",
            1,
            metadata: BiotechTargetGeneMetadata("Deathless"))
    };
    var matchingGeneDelivered = new[]
    {
        new ThingReferenceDto(
            "caravan:genepack-deathless",
            "Genepack",
            1,
            metadata: BiotechGeneMetadata("Hemogenic", "Deathless"))
    };
    Require(TradeThingRequirementMatcher.Satisfies(geneRequirements, matchingGeneDelivered, out missing, metadataMatchers), "求购目标基因时，任意包含该基因的基因包应可履约");

    var missingGeneDelivered = new[]
    {
        new ThingReferenceDto(
            "caravan:genepack-wrong",
            "Genepack",
            1,
            metadata: BiotechGeneMetadata("Hemogenic"))
    };
    Require(!TradeThingRequirementMatcher.Satisfies(geneRequirements, missingGeneDelivered, out missing, metadataMatchers), "不包含目标基因的基因包不能履约");

    var wrongGeneHolderDelivered = new[]
    {
        new ThingReferenceDto(
            "caravan:xenogerm-deathless",
            "Xenogerm",
            1,
            metadata: BiotechGeneMetadata("Deathless"))
    };
    Require(!TradeThingRequirementMatcher.Satisfies(geneRequirements, wrongGeneHolderDelivered, out missing, metadataMatchers), "基因包求购不能用异种胚芽履约");

    var xenogermRequirements = new[]
    {
        new ThingReferenceDto(
            "request:xenogerm-deathless",
            "Xenogerm",
            1,
            metadata: BiotechTargetGeneMetadata("Deathless"))
    };
    Require(TradeThingRequirementMatcher.Satisfies(xenogermRequirements, wrongGeneHolderDelivered, out missing, metadataMatchers), "异种胚芽求购按同样的目标基因包含规则履约");

    var anySkillBookRequirements = new[]
    {
        new ThingReferenceDto("request:skill-book", "TextBook", 1)
    };
    var shootingSkillBookDelivered = new[]
    {
        new ThingReferenceDto("caravan:shooting-book", "TextBook", 1, metadata: CoreBookSkillMetadata("Shooting"))
    };
    Require(TradeThingRequirementMatcher.Satisfies(anySkillBookRequirements, shootingSkillBookDelivered, out missing, metadataMatchers), "求购技能书时任意技能书都应可履约");

    var shootingBookRequirements = new[]
    {
        new ThingReferenceDto("request:shooting-book", "TextBook", 1, metadata: CoreTargetBookSkillMetadata("Shooting"))
    };
    Require(TradeThingRequirementMatcher.Satisfies(shootingBookRequirements, shootingSkillBookDelivered, out missing, metadataMatchers), "求购射击技能书时包含射击的技能书应可履约");

    var meleeSkillBookDelivered = new[]
    {
        new ThingReferenceDto("caravan:melee-book", "TextBook", 1, metadata: CoreBookSkillMetadata("Melee"))
    };
    Require(!TradeThingRequirementMatcher.Satisfies(shootingBookRequirements, meleeSkillBookDelivered, out missing, metadataMatchers), "不包含目标技能的技能书不能履约");

    var novelRequirements = new[]
    {
        new ThingReferenceDto("request:novel", "Novel", 1)
    };
    var novelDelivered = new[]
    {
        new ThingReferenceDto("caravan:novel", "Novel", 1)
    };
    Require(TradeThingRequirementMatcher.Satisfies(novelRequirements, novelDelivered, out missing), "求购小说时小说应可履约");
    Require(!TradeThingRequirementMatcher.Satisfies(novelRequirements, shootingSkillBookDelivered, out missing, metadataMatchers), "书籍类型由物品定义区分，求购小说时技术图解不能履约");

    var steelSwordRequirements = new[]
    {
        new ThingReferenceDto("request:steel-sword", "LongSword", 1, stuffDefName: "Steel")
    };
    var steelSwordDelivered = new[]
    {
        new ThingReferenceDto("caravan:steel-sword", "LongSword", 1, stuffDefName: "Steel")
    };
    var woodSwordDelivered = new[]
    {
        new ThingReferenceDto("caravan:wood-sword", "LongSword", 1, stuffDefName: "WoodLog")
    };
    Require(TradeThingRequirementMatcher.Satisfies(steelSwordRequirements, steelSwordDelivered, out missing), "指定材质求购应接受相同材质物品");
    Require(!TradeThingRequirementMatcher.Satisfies(steelSwordRequirements, woodSwordDelivered, out missing), "指定材质求购不应接受其他材质物品");

    var anySwordRequirements = new[]
    {
        new ThingReferenceDto("request:any-sword", "LongSword", 1)
    };
    Require(TradeThingRequirementMatcher.Satisfies(anySwordRequirements, woodSwordDelivered, out missing), "未指定材质时任意材质均可履约");
}

static void VerifyTradeFeeServerPriceTable()
{
    var policy = new TradeFeePolicy(
        baseFeeRate: 0.1f,
        fixedFeePerThing: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Wastepack"] = 100
        },
        standardMarketValuePerThing: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Steel"] = 2f,
            ["Wastepack"] = 0f
        });

    TradeFeeCalculationResult result = policy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto("thing-steel", "Steel", 100, marketValue: 999f),
        new ThingReferenceDto("thing-waste", "Wastepack", 2, marketValue: 999f)
    });

    Require(result.Accepted, "价格表覆盖完整时应接受手续费计算");
    Equal(20, result.BaseFeeSilver, "基础手续费应使用服务器标准价而不是客户端提交市价");
    Equal(200, result.FixedFeeSilver, "固定托管费仍按配置叠加");
    Equal(220, result.RequiredFeeSilver, "总手续费应为基础手续费加固定托管费");

    TradeFeeCalculationResult missing = policy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto("thing-med", "MedicineIndustrial", 1, marketValue: 18f)
    });
    Require(!missing.Accepted, "服务器标准价格表缺失物品价格时应拒绝");
    Equal("MedicineIndustrial", missing.MissingMarketValueDefs.Single(), "缺失价格应返回具体 ThingDef");

    var missingPolicy = new TradeFeePolicy(baseFeeRate: 0.1f);
    TradeFeeCalculationResult missingWithoutServerPrice = missingPolicy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto("thing-med", "MedicineIndustrial", 2, marketValue: 18f)
    });
    Require(!missingWithoutServerPrice.Accepted, "缺少服务器标准价格时不应回退使用客户端市价");
    Equal("MedicineIndustrial", missingWithoutServerPrice.MissingMarketValueDefs.Single(), "缺失服务器价格应返回具体 ThingDef");

    TradeFeeCalculationResult missingPawnFee = missingPolicy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto(
            "pawn-hare",
            "Hare",
            1,
            marketValue: 42f,
            pawnPackageId: "pawn-package-hare")
    });
    Require(!missingPawnFee.Accepted, "pawn 交易也应由服务器基线定价，不能回退使用客户端市价");
    Equal("Hare", missingPawnFee.MissingMarketValueDefs.Single(), "pawn 缺失基线价格时应返回具体 ThingDef");

    var pawnPolicy = new TradeFeePolicy(
        baseFeeRate: 0.1f,
        standardMarketValuePerThing: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hare"] = 20f
        });
    TradeFeeCalculationResult pawnFee = pawnPolicy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto(
            "pawn-hare",
            "Hare",
            1,
            quality: "Legendary",
            hitPoints: 1,
            maxHitPoints: 100,
            marketValue: 999f,
            pawnPackageId: "pawn-package-hare")
    });
    Require(pawnFee.Accepted, "动物和奴隶交易应使用服务器 pawn 基线价格");
    Equal(2, pawnFee.BaseFeeSilver, "pawn 交易手续费只按 ThingDef 基础价计算，不使用客户端市价、品质或耐久");
    Equal(0, pawnFee.MissingMarketValueDefs.Count, "pawn 交易不应报告缺失 ThingDef 价格");

    var uniqueWeaponPolicy = new TradeFeePolicy(
        baseFeeRate: 0.1f,
        standardMarketValuePerThing: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["UniqueBlade"] = 100f
        },
        weaponTraitMarketValueOffsets: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["TraitA"] = 50f,
            ["TraitB"] = -10f
        });
    TradeFeeCalculationResult uniqueWeaponFee = uniqueWeaponPolicy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto(
            "thing-unique",
            "UniqueBlade",
            1,
            marketValue: 999f,
            uniqueWeapon: true,
            uniqueWeaponTraits: new[] { "TraitA", "TraitB" })
    });
    Require(uniqueWeaponFee.Accepted, "特化武器应允许使用服务器基线配件价格计算手续费");
    Equal(14, uniqueWeaponFee.BaseFeeSilver, "特化武器手续费应包含配件市场价偏移");

    var modifiedPolicy = new TradeFeePolicy(
        baseFeeRate: 0.1f,
        fixedFeePerThing: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        standardMarketValuePerThing: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Longsword"] = 100f,
            ["Table1x2c"] = 200f
        },
        stuffMarketValuePerThingAndStuff: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Longsword|Plasteel"] = 200f,
            ["Table1x2c|WoodLog"] = 300f
        },
        qualityMarketValueModifiers: new Dictionary<string, TradeQualityValueModifier>(StringComparer.OrdinalIgnoreCase)
        {
            ["Excellent"] = new TradeQualityValueModifier(1.5f, 100f)
        });

    TradeFeeCalculationResult damagedItem = modifiedPolicy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto(
            "thing-sword",
            "Longsword",
            1,
            quality: "Excellent",
            hitPoints: 60,
            marketValue: 999f,
            stuffDefName: "Plasteel",
            maxHitPoints: 100)
    });
    Require(damagedItem.Accepted, "材质、质量和耐久基线覆盖时应接受手续费计算");
    Equal(15, damagedItem.BaseFeeSilver, "普通物品手续费应按材质价、质量倍率和原版耐久曲线估算");

    TradeFeeCalculationResult damagedMinifiedBuilding = modifiedPolicy.CalculateRequiredFeeResult(new[]
    {
        new ThingReferenceDto(
            "thing-minified-table",
            "MinifiedThing",
            1,
            minifiedInnerDefName: "Table1x2c",
            minifiedInnerStuffDefName: "WoodLog",
            minifiedInnerHitPoints: 10,
            minifiedInnerMaxHitPoints: 100)
    });
    Require(damagedMinifiedBuilding.Accepted, "打包建筑材质价格覆盖时应接受手续费计算");
    Equal(17, damagedMinifiedBuilding.BaseFeeSilver, "打包建筑可维修，应使用较轻的建筑耐久曲线折价手续费");
}

static void VerifyWorldMapActionMarkers()
{
    var snapshotIdentity = new SnapshotIdentity("user-a", "colony-a", "snapshot-a");
    var worldObjects = new[]
    {
        new WorldObjectSummary("10", "WorldObject_10", "Settlement", "Settlement", "321", "Faction_0", "北山殖民地", Destroyed: false),
        new WorldObjectSummary("11", "WorldObject_11", "Site", "Site", "654", "Faction_0", "不可交易地点", Destroyed: false),
        new WorldObjectSummary("12", "WorldObject_12", "Settlement", "Settlement", "777", "Faction_0", "已摧毁殖民地", Destroyed: true)
    };
    var maps = new[]
    {
        new MapSummary("Map_10", "10", "WorldObject_10", "(250, 1, 250)", HasCompressedThingMap: true, HasTerrainGrid: true, HasRoofGrid: true, HasFogGrid: true, ThingCount: 10, PawnCount: 3)
    };
    AuthoritativeEvent activeRaid = AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        Target(),
        Actor(),
        "marker-active-raid",
        targetOnline: false,
        new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: new RaidAttackForceRecord(
                "attacker-snapshot",
                new[] { "attacker:pawn-1" },
                Array.Empty<EventThingReference>()),
            OpponentKind: RaidOpponentKind.Player),
        DateTimeOffset.UnixEpoch,
        new EventTargetContext("WorldObject_10", "Map_10", 321, EventLandingMode.MapEdge));
    AuthoritativeEvent completedRaid = activeRaid with
    {
        EventId = "raid:completed",
        IdempotencyKey = "marker-completed-raid",
        Status = ServerEventStatus.AppliedToSnapshot
    };

    IReadOnlyList<WorldMapMarker> markers = WorldMapMarkerProjectionBuilder.Build(
        snapshotIdentity,
        worldObjects,
        maps,
        new[] { activeRaid, completedRaid },
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    Require(!markers.Any(marker => marker.Kind == WorldMapMarkerKind.TradeableColony), "快照中的原版据点不应自动投射为多人可交易殖民地");

    WorldMapMarker raidMarker = markers.Single(marker => marker.Kind == WorldMapMarkerKind.ActiveRaidTarget);
    Equal("active-raid:" + activeRaid.EventId, raidMarker.MarkerId, "进攻标记 ID");
    Equal("user-a", raidMarker.OwnerUserId, "被进攻地块归属防守方");
    Equal("WorldObject_10", raidMarker.WorldObjectId, "进攻标记世界对象");
    Equal("Map_10", raidMarker.MapUniqueId, "进攻标记地图");
    Require(raidMarker.ReinforcementEnabled, "被进攻地块应启用增援入口");
    Require(!raidMarker.TradeEnabled, "被进攻地块标记本身不代表交易入口");
    Require(markers.All(marker => marker.RelatedEventId != completedRaid.EventId), "已完成袭击不应保留进攻标记");
}

static void VerifyWorldMapMarkerDeliveryForLogin()
{
    var sourceA = new WorldMapMarkerSource(
        new SnapshotIdentity("user-a", "colony-a", "snapshot-a"),
        new[]
        {
            new WorldObjectSummary("10", "WorldObject_10", "Settlement", "Settlement", "321", "Faction_0", "北山殖民地", Destroyed: false)
        },
        new[]
        {
            new MapSummary("Map_10", "10", "WorldObject_10", "(250, 1, 250)", HasCompressedThingMap: true, HasTerrainGrid: true, HasRoofGrid: true, HasFogGrid: true, ThingCount: 10, PawnCount: 3)
        });
    var sourceC = new WorldMapMarkerSource(
        new SnapshotIdentity("user-c", "colony-c", "snapshot-c"),
        new[]
        {
            new WorldObjectSummary("20", "WorldObject_20", "Settlement", "Settlement", "654", "Faction_2", "南谷殖民地", Destroyed: false)
        },
        new[]
        {
            new MapSummary("Map_20", "20", "WorldObject_20", "(250, 1, 250)", HasCompressedThingMap: true, HasTerrainGrid: true, HasRoofGrid: true, HasFogGrid: true, ThingCount: 20, PawnCount: 4)
        });
    AuthoritativeEvent activeRaid = AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        Target(),
        Actor(),
        "delivery-active-raid",
        targetOnline: false,
        new RaidEventPayload(
            "snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: null,
            Settlement: null,
            AttackForce: new RaidAttackForceRecord(
                "attacker-snapshot",
                new[] { "attacker:pawn-1" },
                Array.Empty<EventThingReference>()),
            OpponentKind: RaidOpponentKind.Player),
        DateTimeOffset.UnixEpoch,
        new EventTargetContext("WorldObject_20", "Map_20", 654, EventLandingMode.MapEdge));
    AuthoritativeEvent endedRaid = activeRaid with
    {
        EventId = "raid:delivery-ended",
        IdempotencyKey = "delivery-ended-raid",
        Status = ServerEventStatus.Cancelled
    };

    WorldMapMarkerDelivery delivery = WorldMapMarkerDeliveryBuilder.BuildForLogin(
        "viewer-user",
        new[] { sourceA, sourceC },
        new[] { activeRaid, endedRaid },
        DateTimeOffset.UnixEpoch.AddMinutes(2));

    Equal("viewer-user", delivery.UserId, "下发目标用户");
    Equal(DateTimeOffset.UnixEpoch.AddMinutes(2), delivery.GeneratedAtUtc, "下发生成时间");
    Equal(1, delivery.Markers.Count, "下发标记数量");
    Equal(0, delivery.Markers.Count(marker => marker.Kind == WorldMapMarkerKind.TradeableColony), "快照源不应下发可交易殖民地");
    Equal(1, delivery.Markers.Count(marker => marker.Kind == WorldMapMarkerKind.ActiveRaidTarget), "下发进攻地块数量");

    WorldMapMarker raidMarker = delivery.Markers.Single(marker => marker.Kind == WorldMapMarkerKind.ActiveRaidTarget);
    Equal(activeRaid.EventId, raidMarker.RelatedEventId, "进攻标记关联事件");
    Equal("Map_20", raidMarker.MapUniqueId, "进攻标记地图上下文");
    Equal(654, raidMarker.Tile, "进攻标记地块");
    Require(!raidMarker.TradeEnabled, "进攻标记不应启用交易入口");
    Require(raidMarker.ReinforcementEnabled, "进攻标记应启用增援入口");
    Require(delivery.Markers.All(marker => marker.RelatedEventId != endedRaid.EventId), "已结束袭击不应下发进攻标记");
}

static void VerifyWorldMapMarkerDeliveryRaidAvailability()
{
    var source = new WorldMapMarkerSource(
        new SnapshotIdentity("user-b", "colony-b", "snapshot-b"),
        new[]
        {
            new WorldObjectSummary("10", "WorldObject_10", "Settlement", "Settlement", "321", "Faction_1", "北山殖民地", Destroyed: false)
        },
        new[]
        {
            new MapSummary("Map_10", "10", "WorldObject_10", "(250, 1, 250)", HasCompressedThingMap: true, HasTerrainGrid: true, HasRoofGrid: true, HasFogGrid: true, ThingCount: 10, PawnCount: 3)
        });
    var availability = new WorldMapRaidAvailabilitySource(
        source.SnapshotIdentity,
        source.Maps,
        IsHostile: true,
        DefenderOnline: true,
        DefenderWealth: 8000,
        DefenderRaidCooldownUntilUtc: DateTimeOffset.UnixEpoch.AddHours(3),
        new RaidEligibilityPolicy(MinimumDefenderWealth: 5000));

    WorldMapMarker baseMarker = new(
        "tradeable-colony:user-b:WorldObject_10",
        WorldMapMarkerKind.TradeableColony,
        "user-b",
        "colony-b",
        "WorldObject_10",
        "Map_10",
        "snapshot-b",
        321,
        "北山殖民地",
        DateTimeOffset.UnixEpoch,
        RelatedEventId: null,
        TradeEnabled: true,
        ReinforcementEnabled: false);
    WorldMapMarkerDelivery delivery = new(
        "user-a",
        DateTimeOffset.UnixEpoch.AddHours(1),
        WorldMapRaidAvailabilityAttacher.Attach(
            "user-a",
            new[] { baseMarker },
            new[] { availability },
            DateTimeOffset.UnixEpoch.AddHours(1)));

    WorldMapMarker marker = delivery.Markers.Single();
    Require(marker.RaidAvailability is not null, "可交易殖民地标记应包含袭击可见性");
    Require(!marker.RaidAvailability!.CanRaid, "目标在线且冷却中时应禁用袭击");
    Require(marker.RaidAvailability.DisabledReasons.Contains(RaidEligibilityFailureReason.DefenderOnline), "禁用原因应包含目标在线");
    Require(marker.RaidAvailability.DisabledReasons.Contains(RaidEligibilityFailureReason.CooldownActive), "禁用原因应包含冷却中");
    Equal(DateTimeOffset.UnixEpoch.AddHours(3), marker.RaidAvailability.CooldownUntilUtc, "标记应携带冷却截止时间");
    Equal("Map_10", marker.RaidAvailability.TargetMapUniqueId, "标记应携带目标地图");
    Equal("snapshot-b", marker.RaidAvailability.DefenderSnapshotId, "标记应携带目标快照");
}

static void VerifyProtocolContractCoverage()
{
    foreach (ProtocolMessageKind kind in Enum.GetValues<ProtocolMessageKind>())
    {
        ProtocolEndpointDescriptor endpoint = ProtocolContractManifest.Find(kind);
        Require(!string.IsNullOrWhiteSpace(endpoint.Route), $"{kind} 应声明路由");
    }

    Require(ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).RequiresIdempotencyKey, "上传快照必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).RequiresIdempotencyKey, "确认事件应用必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).RequiresIdempotencyKey, "批量确认事件应用必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.ReportEventApplicationFailure).RequiresIdempotencyKey, "事件应用失败上报必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.ReportEventApplicationFailure).RequiresSnapshotId, "事件应用失败上报必须绑定当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CreateGift).RequiresIdempotencyKey, "创建礼物必须有幂等键");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.RejectGift).RequiresIdempotencyKey, "拒绝礼物由事件 ID 保持幂等");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CreateTradeOrder).RequiresIdempotencyKey, "创建交易单必须有幂等键");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.ListTradeOrders).RequiresIdempotencyKey, "交易市场列表是只读请求，不应要求幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.ListTradeOrders).RequiresSnapshotId, "交易市场列表必须绑定当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.AcceptTradeOrder).RequiresIdempotencyKey, "交易接单必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.AcceptTradeOrder).RequiresSnapshotId, "交易接单必须绑定当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrder).RequiresIdempotencyKey, "交易自提履约必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrder).RequiresSnapshotId, "交易自提履约必须绑定接单方当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrder).ServerMustValidateSnapshot, "交易自提履约必须由服务器校验快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CancelTradeOrder).RequiresIdempotencyKey, "交易撤单必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CancelTradeOrder).RequiresSnapshotId, "交易撤单必须绑定发布者当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CompleteTradeOrder).RequiresIdempotencyKey, "交易完成必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CompleteTradeOrder).RequiresSnapshotId, "交易完成必须绑定发布者当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaid).RequiresIdempotencyKey, "创建袭击必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawn).RequiresIdempotencyKey, "创建支援 pawn 必须有幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawn).RequiresSnapshotId, "创建支援 pawn 必须绑定当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawn).ServerMustValidateSnapshot, "创建支援 pawn 必须由服务器校验快照");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).RequiresIdempotencyKey, "殖民地地块登记由用户和地块集合覆盖更新，不应要求幂等键");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).RequiresSnapshotId, "殖民地地块登记用于快照前占位，不应要求当前快照");

    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).RequiresSnapshotId, "WS 会话不应依赖快照超时判断");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).RequiresIdempotencyKey, "WS 会话连接不应要求幂等键");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).RequiresSnapshotId, "在线驻留不应依赖快照超时判断");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).RequiresIdempotencyKey, "在线驻留连接不应要求幂等键");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.ListPlayers).RequiresIdempotencyKey, "玩家列表是只读联调请求，不应要求幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.WaitForEvents).RequiresSnapshotId, "等待在线事件必须绑定当前快照");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.WaitForEvents).RequiresIdempotencyKey, "等待在线事件是长轮询请求，不应要求幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.PullPendingEvents).RequiresSnapshotId, "拉取待处理事件必须绑定当前快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.PullEventDetails).RequiresSnapshotId, "拉取事件详情必须绑定当前快照");
    Require(!ProtocolContractManifest.Find(ProtocolMessageKind.PullEventDetails).RequiresIdempotencyKey, "拉取事件详情是只读请求，不应要求幂等键");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).ServerMustValidateSnapshot, "上传快照必须由服务器校验");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).ServerMustValidateSnapshot, "事件确认必须由服务器校验确认快照");
    Require(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).ServerMustValidateSnapshot, "批量事件确认必须由服务器校验确认快照");
    Equal(ProtocolDeliverySemantics.RequiresSnapshotConfirmation, ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).DeliverySemantics, "事件确认语义");
    Equal(ProtocolDeliverySemantics.RequiresSnapshotConfirmation, ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).DeliverySemantics, "批量事件确认语义");
    Equal(ProtocolDeliverySemantics.ServerNotification, ProtocolContractManifest.Find(ProtocolMessageKind.ReportEventApplicationFailure).DeliverySemantics, "事件应用失败上报语义");
    Equal(ProtocolDeliverySemantics.OnlineImmediate, ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).DeliverySemantics, "WS 会话语义");
    Equal(ProtocolDeliverySemantics.OnlineImmediate, ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).DeliverySemantics, "在线驻留语义");
    Equal(ProtocolDeliverySemantics.OnlineImmediate, ProtocolContractManifest.Find(ProtocolMessageKind.WaitForEvents).DeliverySemantics, "在线等待语义");
    Equal(ProtocolDeliverySemantics.RequiresSnapshotConfirmation, ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrder).DeliverySemantics, "交易自提履约语义");
    Equal(ProtocolDeliverySemantics.OnlineImmediate, ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).DeliverySemantics, "殖民地地块登记语义");
    Equal(ProtocolDeliverySemantics.OnlineImmediate, ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaid).DeliverySemantics, "袭击创建语义");
    Equal(ProtocolDeliverySemantics.OnlineImmediate, ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawn).DeliverySemantics, "支援 pawn 创建语义");
    Equal(ProtocolDeliverySemantics.ServerNotification, ProtocolContractManifest.Find(ProtocolMessageKind.ServerNotification).DeliverySemantics, "服务器通知语义");

    var snapshotPackage = new SnapshotPackageMetadataDto(
        "1",
        "user-a",
        "colony-a",
        "snapshot-after",
        "1.6-test",
        "RawRws",
        10,
        10,
        "original-hash",
        "payload-hash",
        previousSnapshotId: null,
        lineageToken: null,
        nextLineageToken: null,
        gameTicks: 1);
    var upload = new UploadSnapshotMetadataRequest(
        "snapshot-upload:user-a:colony-a:snapshot-after",
        "user-a",
        "colony-a",
        "snapshot-after",
        snapshotPackage);
    Equal(upload.UserId, upload.Package.OwnerId, "上传快照账号绑定");
    Equal(upload.ColonyId, upload.Package.ColonyId, "上传快照殖民地绑定");
    Equal(upload.SnapshotId, upload.Package.SnapshotId, "上传快照 ID 绑定");
    Equal("payload-hash", upload.Package.PayloadSha256, "上传快照只在 metadata 中携带载荷哈希");

    var confirmation = new ConfirmEventApplicationMetadataRequest(
        "confirm:attacker-loss-001:snapshot-after",
        "attacker-loss-001",
        "raid-source-001",
        "user-a",
        "colony-a",
        "attacker-snapshot-before",
        snapshotPackage,
        "AppliedWithSnapshotFallback");
    Equal("attacker-snapshot-before", confirmation.BaseSnapshotId, "确认事件必须带原始快照");
    Equal("snapshot-after", confirmation.ConfirmedSnapshot.SnapshotId, "确认事件必须带确认快照");
    Require(!string.Equals(confirmation.ClientApplicationResult, "TrustedComplete", StringComparison.Ordinal), "客户端结果只是报告，不是服务器信任的完成声明");

    var batchConfirmation = new ConfirmEventApplicationsMetadataRequest(
        "confirm-batch:user-a:colony-a:snapshot-after",
        "user-a",
        "colony-a",
        "snapshot-before",
        snapshotPackage,
        new[]
        {
            new ConfirmEventApplicationEntry(
                "gift-return-001",
                sourceEventId: null,
                "GiftReturnAnchored")
        });
    Equal("snapshot-before", batchConfirmation.BaseSnapshotId, "批量确认事件必须共享原始快照");
    Equal("snapshot-after", batchConfirmation.ConfirmedSnapshot.SnapshotId, "批量确认事件必须带确认快照");
    Equal(1, batchConfirmation.Applications.Count, "批量确认事件必须携带确认条目");

    var listPlayers = new ListPlayersRequest("user-a", "colony-a", "snapshot-before");
    Equal("user-a", listPlayers.UserId, "玩家列表请求必须带当前用户");
    var players = new ListPlayersResponse(
        ProtocolResponse.Ok("ok"),
        new[] { new PlayerSummaryDto("user-b", "colony-b", "snapshot-b", online: true, DateTimeOffset.UnixEpoch) });
    Require(players.Players.Single().Online, "玩家列表响应应暴露在线状态用于联调选目标");

    var supportReference = new CrossMapPawnReferenceDto(
        "owner:user-a/colony:colony-a/snapshot:snapshot-before/caravan:Caravan_1/pawn:Pawn_1",
        "snapshot-before",
        "Li",
        dead: false,
        "Faction_0");
    var support = new CreateSupportPawnRequest(
        "support:user-a:Pawn_1:raid-001",
        new ProtocolIdentity("user-a", "colony-a", "snapshot-before"),
        new ProtocolIdentity("user-b", "colony-b", "snapshot-b"),
        supportReference.GlobalId,
        "snapshot-before",
        "Li",
        temporaryControl: true,
        expectedReturnAtUtc: null,
        supportReference,
        pawnPackage: null,
        new EventTargetContextDto("WorldObject_1", "Map_0", 12345, "MapEdge"),
        permanentSupport: false,
        supportDurationDays: 7,
        expiresAtGameTicks: 420000,
        autoReturnOnSettlement: true);
    Equal(support.PawnGlobalKey, support.PawnReference!.GlobalId, "支援 pawn 请求必须绑定同一个跨地图 pawn 引用");
    Equal("snapshot-b", support.Target.SnapshotId, "支援 pawn 必须绑定目标快照");
    Equal(7, support.SupportDurationDays, "临时支援必须携带游戏内期限");
    Require(support.AutoReturnOnSettlement, "战斗地图支援应能标记结算自动返回");

    var gift = new CreateGiftRequest(
        "gift:user-a:001",
        new ProtocolIdentity("user-a", "colony-a", "snapshot-before"),
        new ProtocolIdentity("user-b", "colony-b", "target-snapshot"),
        new[] { new ThingReferenceDto("thing-meal", "MealFine", 3) },
        "gift");
    Equal("snapshot-before", gift.Actor.SnapshotId, "礼物发起方必须绑定快照");
    Equal(1, gift.Things.Count, "礼物必须携带物品引用");

    var rejectGift = new RejectGiftRequest(
        "gift:event-001",
        "user-b",
        "colony-b",
        "target-snapshot",
        "拒绝礼物");
    Equal("target-snapshot", rejectGift.CurrentSnapshotId, "拒绝礼物必须绑定当前快照");
    Equal("user-b", rejectGift.UserId, "拒绝礼物必须绑定目标用户");

    var rejectGiftResponse = new RejectGiftResponse(
        ProtocolResponse.Ok("ok"),
        "gift:event-001",
        "giftreturn:event-001-return",
        returnEventCreated: true);
    Equal("giftreturn:event-001-return", rejectGiftResponse.ReturnEventId, "拒绝礼物响应应返回退回事件");

    var trade = new CreateTradeOrderRequest(
        "trade:user-a:001",
        new ProtocolIdentity("user-a", "colony-a", "snapshot-before"),
        new[] { new ThingReferenceDto("thing-steel", "Steel", 100) },
        new[] { new ThingReferenceDto("thing-med", "MedicineIndustrial", 8) },
        feeSilver: 10,
        allowSelfPickup: true,
        allowServerDropPod: true);
    Require(trade.AllowSelfPickup && trade.AllowServerDropPod, "交易单应表达自提和服务器投递选项");

    var tradeMarket = new ListTradeOrdersRequest(
        "user-b",
        "colony-b",
        "target-snapshot");
    Equal("target-snapshot", tradeMarket.CurrentSnapshotId, "交易市场列表必须带当前快照");

    var tradeSummary = new TradeOrderSummaryDto(
        "trade:event-001",
        new ProtocolIdentity("user-a", "colony-a", null),
        new[] { new ThingReferenceDto("thing-steel", "Steel", 100) },
        new[] { new ThingReferenceDto("thing-med", "MedicineIndustrial", 8, "Normal", 50) },
        feeSilver: 10,
        allowSelfPickup: true,
        allowServerDropPod: true,
        acceptedMemoCount: 2,
        createdAtUtc: DateTimeOffset.UnixEpoch,
        serverDropPodPostage: new TradePostageQuoteDto(
            reachable: false,
            postageSilver: null,
            distanceTiles: null,
            status: "缺少交易双方世界地块。"),
        targetContext: new EventTargetContextDto("WorldObject_10", "Map_10", 321, "StorageZone"));
    Equal(2, tradeSummary.AcceptedMemoCount, "交易单摘要应支持多人接取计数");
    Equal("Normal", tradeSummary.RequestedThings.Single().Quality, "质量要求属于交易列表项，不属于搜索条件");
    Require(!tradeSummary.ServerDropPodPostage!.Reachable, "交易单摘要应能携带空投邮费不可达状态");
    Equal(321, tradeSummary.TargetContext!.Tile, "交易单摘要必须携带发布者地块，供远行队到达后显示履约 gizmo");

    var acceptTrade = new AcceptTradeOrderRequest(
        "trade:user-b:accept:001",
        "trade:event-001",
        new ProtocolIdentity("user-b", "colony-b", "target-snapshot"),
        postagePaidByAcceptor: true);
    Equal("target-snapshot", acceptTrade.Acceptor.SnapshotId, "交易接单必须绑定接单方当前快照");

    var fulfillTrade = new FulfillTradeOrderRequest(
        "trade:user-b:fulfill:001",
        "trade:event-001",
        "trade:memo-001",
        new ProtocolIdentity("user-b", "colony-b", "target-snapshot"),
        new[] { new ThingReferenceDto("caravan:medicine", "MedicineIndustrial", 8, "Good", 60) },
        "SelfDelivery");
    Equal("trade:memo-001", fulfillTrade.AcceptedMemoEventId, "交易履约必须绑定接单备忘录");
    Equal("SelfDelivery", fulfillTrade.FulfillmentMode, "交易履约当前只表达远行队自提");
    Equal(1, fulfillTrade.DeliveredThings.Count, "交易履约必须提交远行队携带物清单");

    var cancelTrade = new CloseTradeOrderRequest(
        "trade:user-a:cancel:001",
        "trade:event-001",
        new ProtocolIdentity("user-a", "colony-a", "snapshot-before"),
        "撤回交易单");
    Equal("snapshot-before", cancelTrade.Owner.SnapshotId, "交易撤单必须绑定发布者当前快照");

    var closeTradeResponse = new CloseTradeOrderResponse(
        ProtocolResponse.Ok("ok"),
        "trade:event-001",
        ServerEventStatus.Cancelled.ToString(),
        notifiedAcceptorCount: 2);
    Equal(2, closeTradeResponse.NotifiedAcceptorCount, "交易终态响应应返回被通知的接单玩家数量");

    var raid = new CreateRaidRequest(
        "raid:user-a:user-b:001",
        new ProtocolIdentity("user-a", "colony-a", "attacker-snapshot-before"),
        new ProtocolIdentity("user-b", "colony-b", "defender-snapshot-before"),
        isHostile: true,
        defenderOnline: false,
        defenderWealth: 8000,
        defenderRaidCooldownUntilUtc: DateTimeOffset.UnixEpoch,
        raidPreparationId: null,
        "WorldObject_1",
        "Map_0",
        "defender-snapshot-before",
        new[] { "pawn-1", "pawn-2" },
        new[] { new ThingReferenceDto("thing-med", "MedicineIndustrial", 8) });
    Equal(2, raid.PawnGlobalKeys.Count, "袭击请求必须携带投入 pawn");
    Equal("defender-snapshot-before", raid.DefenderSnapshotId, "袭击请求必须绑定防守方快照");

    var loginResponse = new LoginResponse(
        ProtocolResponse.Ok("ok"),
        "session-001",
        ProtocolApiVersion.Current,
        new EventQueueSummaryDto(
            Array.Empty<EventReferenceDto>(),
            Array.Empty<EventReferenceDto>(),
            Array.Empty<EventReferenceDto>(),
            Array.Empty<EventReferenceDto>(),
            Array.Empty<EventReferenceDto>()),
        new WorldMapMarkerDeliveryDto("user-a", DateTimeOffset.UnixEpoch, Array.Empty<WorldMapMarkerDto>()),
        new[] { new ServerNotificationDto("notice-001", "服务器通知", "维护", "Warning", fromAdministrator: true) });
    Require(loginResponse.EventQueue != null, "登录响应应能携带事件队列");
    Require(loginResponse.WorldMapMarkers != null, "登录响应应能携带世界地图标记");
    Equal(1, loginResponse.Notifications.Count, "登录响应应能携带服务器通知");

    var eventDetails = new PullEventDetailsRequest(
        "user-a",
        "colony-a",
        "snapshot-before",
        new[] { "event-001" });
    Equal("snapshot-before", eventDetails.CurrentSnapshotId, "事件详情请求必须绑定当前快照");
    Equal(1, eventDetails.EventIds.Count, "事件详情请求应支持批量事件 ID");

    var detailResponse = new PullEventDetailsResponse(
        ProtocolResponse.Ok("ok"),
        new[]
        {
            new EventDetailDto(
                "event-001",
                "Gift",
                "PendingOfflineDelivery",
                new ProtocolIdentity("user-a", "colony-a", null),
                new ProtocolIdentity("user-b", "colony-b", null),
                new EventTargetContextDto("WorldObject_1", "Map_0", 12345, "StorageZone"),
                "GiftEventPayload",
                "{\"items\":1}")
        });
    Equal("GiftEventPayload", detailResponse.Events.Single().PayloadType, "事件详情应携带载荷类型");
    Equal("Map_0", detailResponse.Events.Single().TargetContext!.MapUniqueId, "事件详情应携带目标地图上下文");
}

static void VerifyApplicationConflictResult()
{
    var ledger = new InMemoryAuthoritativeEventLedger();
    AuthoritativeEvent raid = AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        Actor(),
        Target(),
        "conflict-raid",
        targetOnline: false,
        new RaidEventPayload("snapshot-before", null, null, null, null),
        DateTimeOffset.UnixEpoch,
        TargetContext());
    ledger.Append(raid);

    AuthoritativeEvent conflict = ledger.ReportApplicationResult(
        raid.EventId,
        EventApplicationResultKind.SnapshotBaseMismatch,
        "目标快照不是基于下发快照",
        DateTimeOffset.UnixEpoch.AddHours(1));

    Equal(ServerEventStatus.Conflict, conflict.Status, "冲突状态");
    Equal(EventApplicationResultKind.SnapshotBaseMismatch, conflict.LastApplicationResult, "冲突分类");
    Equal("目标快照不是基于下发快照", conflict.LastFailureReason, "冲突原因");
    Equal(DateTimeOffset.UnixEpoch.AddHours(1), conflict.NextRetryAtUtc, "下次重试时间");
}

static void VerifySnapshotContinuityAllowsColonyTileRelocation()
{
    const string ownerId = "continuity-user";
    const string colonyId = "continuity-colony";
    SaveSnapshotPackage package = BuildFixturePackageFor(ownerId, colonyId, "snapshot-after-relocation");
    (string? worldObjectId, string? mapUniqueId, int tile) = FirstColonyAnchor(package.Index);

    Require(
        !string.IsNullOrWhiteSpace(worldObjectId) || !string.IsNullOrWhiteSpace(mapUniqueId),
        "样本存档应包含可用于连续性校验的稳定殖民地锚点");

    var store = new InMemoryColonySnapshotIndexStore();
    store.StoreLatest(SnapshotRecordWithColonyAnchor(
        ownerId,
        colonyId,
        "snapshot-before-relocation",
        worldObjectId,
        mapUniqueId,
        tile + 101));

    var receiver = new SnapshotUploadReceiver(store);
    SnapshotUploadResult result = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-after-relocation", "ColonyRelocation"),
        package,
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    Equal(SnapshotUploadResultKind.Accepted, result.Kind, "稳定殖民地锚点一致时不应因地块变化拒绝上传");
    Equal("snapshot-after-relocation", store.GetLatest(ownerId, colonyId)!.Identity.SnapshotId, "接受后应更新服务器最新快照");
}

static void VerifySnapshotContinuityRejectsUnrelatedLocalSave()
{
    const string ownerId = "continuity-user";
    const string colonyId = "continuity-colony";
    SaveSnapshotPackage package = BuildFixturePackageFor(ownerId, colonyId, "snapshot-unrelated-local-save");

    var store = new InMemoryColonySnapshotIndexStore();
    store.StoreLatest(SnapshotRecordWithColonyAnchor(
        ownerId,
        colonyId,
        "snapshot-server-current",
        "WorldObject_ServerColonyOnly",
        "Map_ServerColonyOnly",
        456789));

    var receiver = new SnapshotUploadReceiver(store);
    SnapshotUploadResult result = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-unrelated-local-save"),
        package,
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    Equal(SnapshotUploadResultKind.SnapshotContinuityMismatch, result.Kind, "稳定殖民地锚点不匹配时应拒绝无关本地存档覆盖服务器快照");
    Equal("snapshot-server-current", store.GetLatest(ownerId, colonyId)!.Identity.SnapshotId, "拒绝后不应更新服务器最新快照");
}

static void VerifySnapshotLineageAllowsCurrentToken()
{
    const string ownerId = "lineage-user";
    const string colonyId = "lineage-colony";
    var store = new InMemoryColonySnapshotIndexStore();
    var receiver = new SnapshotUploadReceiver(store);

    SaveSnapshotPackage firstPackage = BuildFixturePackageFor(ownerId, colonyId, "snapshot-lineage-1");
    SnapshotUploadResult first = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-lineage-1"),
        firstPackage,
        DateTimeOffset.UnixEpoch);
    Require(first.Accepted, "首个快照应通过");
    string token = first.AcceptedSnapshot!.Envelope.NextLineageToken!;

    SaveSnapshotPackage secondPackage = BuildFixturePackageForWithLineage(
        ownerId,
        colonyId,
        "snapshot-lineage-2",
        previousSnapshotId: "snapshot-lineage-1",
        token,
        gameTicks: first.AcceptedSnapshot.Envelope.GameTicks!.Value + 1);
    SnapshotUploadResult second = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-lineage-2"),
        secondPackage,
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    Require(second.Accepted, $"payload 内链式标记匹配服务器当前 token 时应允许前进，实际 {second.Kind}: {second.Message}");
    Equal("snapshot-lineage-2", store.GetLatest(ownerId, colonyId)!.Identity.SnapshotId, "接受后应更新到第二个快照");
}

static void VerifySnapshotHistoryRejectsReplay()
{
    const string ownerId = "replay-user";
    const string colonyId = "replay-colony";
    var store = new InMemoryColonySnapshotIndexStore();
    var receiver = new SnapshotUploadReceiver(store);

    SaveSnapshotPackage firstPackage = BuildFixturePackageFor(ownerId, colonyId, "snapshot-replay-1");
    SnapshotUploadResult first = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-replay-1"),
        firstPackage,
        DateTimeOffset.UnixEpoch);
    Require(first.Accepted, "首个快照应通过");

    SaveSnapshotPackage replayPackage = BuildFixturePackageFor(ownerId, colonyId, "snapshot-replay-2");
    SnapshotUploadResult replay = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-replay-2"),
        replayPackage,
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    Equal(SnapshotUploadResultKind.SnapshotReplayDetected, replay.Kind, "相同原始存档内容不能换新 snapshotId 重放");
    Equal("snapshot-replay-1", store.GetLatest(ownerId, colonyId)!.Identity.SnapshotId, "重放拒绝后服务器快照不应前进");
}

static void VerifySnapshotTimeRegressionRejectsRollback()
{
    const string ownerId = "time-user";
    const string colonyId = "time-colony";
    var store = new InMemoryColonySnapshotIndexStore();
    var receiver = new SnapshotUploadReceiver(store);

    SaveSnapshotPackage firstPackage = BuildFixturePackageForWithLineage(
        ownerId,
        colonyId,
        "snapshot-time-1",
        previousSnapshotId: null,
        token: null,
        gameTicks: 1000);
    SnapshotUploadResult first = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-time-1"),
        firstPackage,
        DateTimeOffset.UnixEpoch);
    Require(first.Accepted, "首个 tick 快照应通过");

    SaveSnapshotPackage rollbackPackage = BuildFixturePackageForWithLineage(
        ownerId,
        colonyId,
        "snapshot-time-2",
        previousSnapshotId: "snapshot-time-1",
        first.AcceptedSnapshot!.Envelope.NextLineageToken,
        gameTicks: 999);
    SnapshotUploadResult rollback = receiver.Receive(
        new SnapshotUploadContext(ownerId, colonyId, "snapshot-time-2"),
        rollbackPackage,
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    Equal(SnapshotUploadResultKind.SnapshotTimeRegression, rollback.Kind, "游戏 tick 明显倒退时应拒绝");
    Equal("snapshot-time-1", store.GetLatest(ownerId, colonyId)!.Identity.SnapshotId, "tick 倒退拒绝后服务器快照不应前进");
}

static void VerifyFileLedgerPersistence()
{
    string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim.Events.Tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(directory, "ledger.json");

    var firstLedger = new FileAuthoritativeEventLedger(path);
    AuthoritativeEvent older = GiftEvent("persist-offline", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1)
    };
    AuthoritativeEvent newer = GiftEvent("persist-online", targetOnline: true) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(2)
    };

    firstLedger.Append(newer);
    firstLedger.Append(older);
    firstLedger.MarkDelivered(newer.EventId, "file-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(3));
    firstLedger.MarkApplied(newer.EventId, "file-snapshot-after", DateTimeOffset.UnixEpoch.AddMinutes(4));

    var reopened = new FileAuthoritativeEventLedger(path);
    IReadOnlyList<AuthoritativeEvent> eventsForTarget = reopened.ListForUser("user-b");
    IReadOnlyList<AuthoritativeEvent> deliverableForTarget = reopened.ListDeliverableForTarget("user-b");

    Equal(2, eventsForTarget.Count, "重启后事件数量");
    Equal(older.EventId, eventsForTarget[0].EventId, "重启后应按创建时间排序");
    Equal(ServerEventStatus.PendingOfflineDelivery, eventsForTarget[0].Status, "离线待处理状态应恢复");
    Equal(ServerEventStatus.AppliedToSnapshot, reopened.Find(newer.EventId)!.Status, "快照应用状态应持久化");
    Equal("file-snapshot-before", reopened.Find(newer.EventId)!.DeliveredToSnapshotId, "下发快照应持久化");
    Equal("file-snapshot-after", reopened.Find(newer.EventId)!.AppliedSnapshotId, "应用快照应持久化");
    Equal(older.EventId, deliverableForTarget.Single().EventId, "已应用事件不应再次下发");
    Require(reopened.Find(older.EventId)!.Payload is GiftEventPayload, "载荷类型应恢复");

    AuthoritativeEvent notice = ServerNotificationEvent("persist-server-notice", targetOnline: false);
    reopened.Append(notice);
    var reopenedWithNotice = new FileAuthoritativeEventLedger(path);
    Require(reopenedWithNotice.Find(notice.EventId)!.Payload is ServerNotificationEventPayload, "服务器通知载荷类型应恢复");

    LedgerAppendResult duplicate = reopened.Append(GiftEvent("persist-offline", targetOnline: false) with
    {
        EventId = "gift:changed-after-reopen"
    });
    Require(!duplicate.Created, "重启后幂等键仍应阻止重复事件");
    Equal(older.EventId, duplicate.Event.EventId, "重启后重复提交应返回原事件");
}

static void VerifySqliteLedgerPersistence()
{
    string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim.Events.Tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(directory, "ledger.db");

    var firstLedger = new SqliteAuthoritativeEventLedger(path);
    AuthoritativeEvent oldest = GiftEvent("sqlite-pending-oldest", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1)
    };
    AuthoritativeEvent applied = GiftEvent("sqlite-applied", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(2)
    };
    AuthoritativeEvent newest = GiftEvent("sqlite-pending-newest", targetOnline: false) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(3)
    };
    AuthoritativeEvent online = GiftEvent("sqlite-online", targetOnline: true) with
    {
        CreatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(4)
    };

    firstLedger.Append(newest);
    firstLedger.Append(applied);
    firstLedger.Append(oldest);
    firstLedger.Append(online);
    firstLedger.MarkDelivered(applied.EventId, "sqlite-snapshot-before", DateTimeOffset.UnixEpoch.AddMinutes(5));
    firstLedger.MarkApplied(applied.EventId, "sqlite-snapshot-after", DateTimeOffset.UnixEpoch.AddMinutes(6));
    firstLedger.MarkDelivered(newest.EventId, "sqlite-unsaved-snapshot", DateTimeOffset.UnixEpoch.AddMinutes(7));

    var reopened = new SqliteAuthoritativeEventLedger(path);
    IReadOnlyList<AuthoritativeEvent> eventsForTarget = reopened.ListForUser("user-b");
    IReadOnlyList<AuthoritativeEvent> pendingForTarget = reopened.ListPendingForTarget("user-b");
    IReadOnlyList<AuthoritativeEvent> deliverableForTarget = reopened.ListDeliverableForTarget("user-b");

    Equal(4, eventsForTarget.Count, "SQLite 重启后事件数量");
    Equal(oldest.EventId, eventsForTarget[0].EventId, "SQLite 重启后应按创建时间排序");
    Equal(1, pendingForTarget.Count, "SQLite Pending 查询应只读取未下发事件");
    Equal(oldest.EventId, pendingForTarget[0].EventId, "SQLite 待处理事件应保持创建顺序");
    Equal(2, deliverableForTarget.Count, "SQLite 登录读取应包含未下发和已下发未应用事件");
    Equal(oldest.EventId, deliverableForTarget[0].EventId, "SQLite 登录读取第一条");
    Equal(newest.EventId, deliverableForTarget[1].EventId, "SQLite 登录读取应重新发送已下发未确认事件");
    Equal(ServerEventStatus.AppliedToSnapshot, reopened.Find(applied.EventId)!.Status, "SQLite 快照应用状态应持久化");
    Equal("sqlite-snapshot-before", reopened.Find(applied.EventId)!.DeliveredToSnapshotId, "SQLite 下发快照应持久化");
    Equal("sqlite-snapshot-after", reopened.Find(applied.EventId)!.AppliedSnapshotId, "SQLite 应用快照应持久化");
    Equal(ServerEventStatus.DeliveredToClient, reopened.Find(newest.EventId)!.Status, "SQLite 已下发未应用状态应持久化");
    Equal("WorldObject_1", reopened.Find(oldest.EventId)!.TargetContext!.WorldObjectId, "SQLite 目标世界对象应恢复");
    Equal("Map_0", reopened.Find(oldest.EventId)!.TargetContext!.MapUniqueId, "SQLite 目标地图应恢复");
    Equal(12345, reopened.Find(oldest.EventId)!.TargetContext!.Tile, "SQLite 目标地块应恢复");
    Equal(EventLandingMode.StorageZone, reopened.Find(oldest.EventId)!.TargetContext!.LandingMode, "SQLite 落点策略应恢复");
    Require(reopened.Find(oldest.EventId)!.Payload is GiftEventPayload, "SQLite 载荷类型应恢复");

    LedgerAppendResult duplicate = reopened.Append(GiftEvent("sqlite-pending-oldest", targetOnline: false) with
    {
        EventId = "gift:sqlite-changed-after-reopen"
    });
    Require(!duplicate.Created, "SQLite 重启后幂等键仍应阻止重复事件");
    Equal(oldest.EventId, duplicate.Event.EventId, "SQLite 重启后重复提交应返回原事件");

    AuthoritativeEvent rejected = GiftEvent("sqlite-rejected-gift", targetOnline: false);
    reopened.Append(rejected);
    GiftReturnResult returnResult = reopened.RejectGiftAndCreateReturn(
        rejected.EventId,
        DateTimeOffset.UnixEpoch.AddMinutes(8),
        "拒绝礼物",
        originalActorOnline: false);

    var reopenedAgain = new SqliteAuthoritativeEventLedger(path);
    AuthoritativeEvent persistedRejected = reopenedAgain.Find(rejected.EventId)!;
    AuthoritativeEvent persistedReturn = reopenedAgain.Find(returnResult.ReturnEvent.EventId)!;
    Equal(ServerEventStatus.RejectedByTarget, persistedRejected.Status, "SQLite 拒绝状态应恢复");
    Equal(TargetEventDecision.Rejected, persistedRejected.TargetDecision, "SQLite 拒绝决策应恢复");
    Equal("拒绝礼物", persistedRejected.DecisionReason, "SQLite 拒绝理由应恢复");
    Equal(ServerEventType.GiftReturn, persistedReturn.Type, "SQLite 退回事件类型应恢复");
    Equal(EventRejectionPolicy.NotRejectable, persistedReturn.RejectionPolicy, "SQLite 退回事件不可拒绝应恢复");
    Equal("user-a", persistedReturn.Target.UserId, "SQLite 退回事件目标应恢复");
    Require(!reopenedAgain.ListDeliverableForTarget("user-b").Any(evt => evt.EventId == rejected.EventId), "SQLite 拒绝后不应再下发");
    Require(reopenedAgain.ListDeliverableForTarget("user-a").Any(evt => evt.EventId == persistedReturn.EventId), "SQLite 退回事件应下发给原发起方");

    AuthoritativeEvent alliance = AllianceRequestEvent("sqlite-alliance-request", targetOnline: false);
    reopenedAgain.Append(alliance);
    var reopenedAfterAlliance = new SqliteAuthoritativeEventLedger(path);
    Require(reopenedAfterAlliance.Find(alliance.EventId)!.Payload is AllianceRequestEventPayload, "SQLite 结盟请求载荷类型应恢复");
    AuthoritativeEvent cancellation = AllianceCancellationEvent("sqlite-alliance-cancellation", targetOnline: false);
    reopenedAfterAlliance.Append(cancellation);
    var reopenedAfterCancellation = new SqliteAuthoritativeEventLedger(path);
    Require(reopenedAfterCancellation.Find(cancellation.EventId)!.Payload is AllianceCancellationEventPayload, "SQLite 撕毁盟约载荷类型应恢复");
}

static AuthoritativeEvent GiftEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.Gift,
        Actor(),
        Target(),
        idempotencyKey,
        targetOnline,
        new GiftEventPayload(new[] { new EventThingReference("thing-gift", "MealFine", 3) }, "gift"),
        DateTimeOffset.UnixEpoch,
        TargetContext());
}

static AuthoritativeEvent RaidEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        Actor(),
        Target(),
        idempotencyKey,
        targetOnline,
        new RaidEventPayload("snapshot-before", null, null, null, null),
        DateTimeOffset.UnixEpoch,
        TargetContext());
}

static AuthoritativeEvent AllianceRequestEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.AllianceRequest,
        Actor(),
        Target(),
        idempotencyKey,
        targetOnline,
        new AllianceRequestEventPayload("alliance-001", "我们可以共同防守边境。", DateTimeOffset.UnixEpoch.AddDays(3)),
        DateTimeOffset.UnixEpoch);
}

static AuthoritativeEvent AllianceCancellationEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.AllianceCancellation,
        Actor(),
        Target(),
        idempotencyKey,
        targetOnline,
        new AllianceCancellationEventPayload("alliance-cancellation-001", "盟约到此为止。"),
        DateTimeOffset.UnixEpoch);
}

static AuthoritativeEvent WarDeclarationEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.WarDeclaration,
        Actor(),
        Target(),
        idempotencyKey,
        targetOnline,
        new WarDeclarationEventPayload("war-001", "边境冲突升级。"),
        DateTimeOffset.UnixEpoch);
}

static AuthoritativeEvent PeaceRequestEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.PeaceRequest,
        Actor(),
        Target(),
        idempotencyKey,
        targetOnline,
        new PeaceRequestEventPayload("peace-001", "边境冲突可以到此为止。", DateTimeOffset.UnixEpoch.AddDays(3)),
        DateTimeOffset.UnixEpoch);
}

static AuthoritativeEvent ServerNotificationEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.ServerNotification,
        new EventParty("server", null, null),
        Target(),
        idempotencyKey,
        targetOnline,
        new ServerNotificationEventPayload(
            "notice-001",
            "服务器维护",
            "服务器将在本轮结束后维护。",
            ServerNotificationSeverity.Warning,
            FromAdministrator: true,
            AdministratorUserId: "admin-001"),
        DateTimeOffset.UnixEpoch);
}

static AuthoritativeEvent TradeAcceptanceMemoEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.Trade,
        Target(),
        Actor(),
        idempotencyKey,
        targetOnline,
        new TradeEventPayload(
            "trade-001",
            TradeStage.AcceptedMemo,
            new[] { new EventThingReference("thing-offer", "Steel", 100) },
            new[] { new EventThingReference("thing-request", "Medicine", 5) },
            FeeSilver: 10,
            AcceptedByUserId: "user-b"),
        DateTimeOffset.UnixEpoch,
        TargetContext());
}

static AuthoritativeEvent TradeExchangeEvent(string idempotencyKey, bool targetOnline)
{
    return AuthoritativeEventFactory.Create(
        ServerEventType.Trade,
        Target(),
        Actor(),
        idempotencyKey,
        targetOnline,
        new TradeEventPayload(
            "trade-001",
            TradeStage.ServerDropPodExchange,
            new[] { new EventThingReference("thing-offer", "Steel", 100) },
            new[] { new EventThingReference("thing-request", "Medicine", 5) },
            FeeSilver: 10,
            AcceptedByUserId: "user-b",
            FulfillmentMode: TradeFulfillmentMode.ServerDropPod,
            PostagePaidByAcceptor: true),
        DateTimeOffset.UnixEpoch,
        TargetContext());
}

static EventParty Actor()
{
    return new EventParty("user-a", "colony-a", "Faction_0");
}

static EventParty Target()
{
    return new EventParty("user-b", "colony-b", "Faction_1");
}

static EventTargetContext TargetContext()
{
    return new EventTargetContext("WorldObject_1", "Map_0", 12345, EventLandingMode.StorageZone);
}

static MapSummary RaidMap(string uniqueId)
{
    return new MapSummary(
        uniqueId,
        "Generated_" + uniqueId,
        "WorldObject_1",
        "(250, 1, 250)",
        HasCompressedThingMap: true,
        HasTerrainGrid: true,
        HasRoofGrid: true,
        HasFogGrid: true,
        ThingCount: 100,
        PawnCount: 10);
}

static RaidInitiationRequest RaidStartRequest(
    string idempotencyKey,
    bool isHostile,
    bool defenderOnline,
    int defenderWealth)
{
    return new RaidInitiationRequest(
        idempotencyKey,
        new RaidEligibilityRequest(
            Actor(),
            Target(),
            isHostile,
            defenderOnline,
            CheckedAtUtc: DateTimeOffset.UnixEpoch.AddHours(1),
            DefenderRaidCooldownUntilUtc: DateTimeOffset.UnixEpoch,
            defenderWealth,
            new SnapshotIdentity("user-b", "colony-b", "snapshot-before"),
            new[] { RaidMap("Map_0") },
            "Map_0"),
        TargetContext(),
        AttackerOnline: true,
        CreatedAtUtc: DateTimeOffset.UnixEpoch.AddHours(1),
        AttackForce());
}

static RaidAttackForceRecord AttackForce()
{
    return new RaidAttackForceRecord(
        "attacker-snapshot-before",
        new[]
        {
            "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-before/map:caravan/thing:pawn-1",
            "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-before/map:caravan/thing:pawn-2"
        },
        new[]
        {
            new EventThingReference(
                "owner:user-a/colony:colony-a/snapshot:attacker-snapshot-before/map:caravan/thing:medicine",
                "MedicineIndustrial",
                8,
                "attacker-snapshot-before")
        });
}

static AuthoritativeEvent AttackerLossEvent(string idempotencyKey)
{
    RaidAttackForceRecord attackForce = AttackForce();
    RaidAttackerLossRecord loss = RaidAttackerLossRecord.FromAttackForce(
        "raid-source-001",
        attackForce,
        "timeout");

    return AuthoritativeEventFactory.Create(
        ServerEventType.Raid,
        new EventParty("server"),
        Actor(),
        idempotencyKey,
        targetOnline: false,
        new RaidEventPayload(
            "defender-snapshot-before",
            ReturnedSnapshotId: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            FinishedAtUtc: DateTimeOffset.UnixEpoch.AddHours(2),
            Settlement: null,
            AttackForce: attackForce,
            AttackerLoss: loss),
        DateTimeOffset.UnixEpoch,
        TargetContext());
}

static RaidAttackerLossConfirmationRequest AttackerLossConfirmation(
    string attackerLossEventId,
    string confirmedSnapshotId,
    IReadOnlyList<ThingSummary> things,
    IReadOnlyList<PawnSummary> pawns)
{
    return new RaidAttackerLossConfirmationRequest(
        attackerLossEventId,
        "raid-source-001",
        "user-a",
        "colony-a",
        "attacker-snapshot-before",
        SnapshotRecord("user-a", "colony-a", confirmedSnapshotId, things, pawns));
}

static GiftApplicationConfirmationRequest GiftConfirmation(
    string giftEventId,
    string baseSnapshotId,
    string confirmedSnapshotId,
    string clientApplicationResult)
{
    return new GiftApplicationConfirmationRequest(
        giftEventId,
        "user-b",
        "colony-b",
        baseSnapshotId,
        SnapshotRecord("user-b", "colony-b", confirmedSnapshotId, Array.Empty<ThingSummary>(), Array.Empty<PawnSummary>()),
        clientApplicationResult);
}

static LatestSnapshotRecord SnapshotRecord(
    string ownerId,
    string colonyId,
    string snapshotId,
    IReadOnlyList<ThingSummary> things,
    IReadOnlyList<PawnSummary> pawns)
{
    var identity = new SnapshotIdentity(ownerId, colonyId, snapshotId);
    var envelope = new SaveSnapshotEnvelope(
        "1",
        identity,
        DateTimeOffset.UnixEpoch,
        "test.rws",
        "1.6-test",
        SnapshotPayloadEncoding.RawRws,
        OriginalSaveBytes: 0,
        PayloadBytes: 0,
        OriginalSha256: "empty",
        PayloadSha256: "empty");
    var index = new SaveSnapshotIndex(
        "test.rws",
        new SaveMetaSummary("1.6-test", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        Array.Empty<FactionSummary>(),
        Array.Empty<SaveIndexExtensionData>(),
        Array.Empty<WorldObjectSummary>(),
        Array.Empty<MapSummary>(),
        things,
        pawns);

    return new LatestSnapshotRecord(identity, envelope, index, DateTimeOffset.UnixEpoch);
}

static SaveSnapshotPackage BuildFixturePackageFor(string ownerId, string colonyId, string snapshotId)
{
    string samplePath = Path.GetFullPath(Path.Combine(
        Environment.CurrentDirectory,
        "SaveSample",
        "SaveHediffFixture.rws"));
    Require(File.Exists(samplePath), $"缺少样本存档：{samplePath}");

    return SaveSnapshotPackageBuilder.FromFile(
        samplePath,
        new SnapshotIdentity(ownerId, colonyId, snapshotId),
        DateTimeOffset.UnixEpoch,
        SnapshotPayloadEncoding.GzipRws);
}

static SaveSnapshotPackage BuildFixturePackageForWithLineage(
    string ownerId,
    string colonyId,
    string snapshotId,
    string? previousSnapshotId,
    string? token,
    long gameTicks)
{
    string samplePath = Path.GetFullPath(Path.Combine(
        Environment.CurrentDirectory,
        "SaveSample",
        "SaveHediffFixture.rws"));
    Require(File.Exists(samplePath), $"缺少样本存档：{samplePath}");

    string tempPath = Path.Combine(
        Path.GetTempPath(),
        "clash-of-rim-lineage-" + Guid.NewGuid().ToString("N") + ".rws");
    try
    {
        System.Xml.Linq.XDocument document = System.Xml.Linq.XDocument.Load(samplePath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
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

static LatestSnapshotRecord SnapshotRecordWithColonyAnchor(
    string ownerId,
    string colonyId,
    string snapshotId,
    string? worldObjectId,
    string? mapUniqueId,
    int tile)
{
    string parentWorldObjectId = string.IsNullOrWhiteSpace(worldObjectId)
        ? "WorldObject_SyntheticColony"
        : worldObjectId!;

    var identity = new SnapshotIdentity(ownerId, colonyId, snapshotId);
    var envelope = new SaveSnapshotEnvelope(
        SaveSnapshotPackageBuilder.CurrentPackageVersion,
        identity,
        DateTimeOffset.UnixEpoch,
        "synthetic.rws",
        "1.6-test",
        SnapshotPayloadEncoding.GzipRws,
        OriginalSaveBytes: 0,
        PayloadBytes: 0,
        OriginalSha256: "synthetic",
        PayloadSha256: "synthetic");
    var index = new SaveSnapshotIndex(
        "synthetic.rws",
        new SaveMetaSummary("1.6-test", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        Array.Empty<FactionSummary>(),
        Array.Empty<SaveIndexExtensionData>(),
        new[]
        {
            new WorldObjectSummary(
                parentWorldObjectId.Replace("WorldObject_", string.Empty, StringComparison.Ordinal),
                parentWorldObjectId,
                "RimWorld.Planet.Settlement",
                "Settlement",
                tile.ToString(),
                "Faction_0",
                "测试殖民地",
                Destroyed: false)
        },
        new[]
        {
            new MapSummary(
                mapUniqueId,
                GeneratedId: null,
                parentWorldObjectId,
                "250,1,250",
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

static (string? WorldObjectId, string? MapUniqueId, int Tile) FirstColonyAnchor(SaveSnapshotIndex index)
{
    foreach (MapSummary map in index.Maps)
    {
        if (string.IsNullOrWhiteSpace(map.ParentWorldObjectId))
        {
            continue;
        }

        WorldObjectSummary? worldObject = index.WorldObjects.FirstOrDefault(candidate =>
            string.Equals(candidate.UniqueLoadId, map.ParentWorldObjectId, StringComparison.Ordinal)
            || string.Equals(candidate.Id, map.ParentWorldObjectId, StringComparison.Ordinal));
        if (worldObject is null
            || worldObject.Destroyed
            || !IsSettlement(worldObject)
            || !TryParseTile(worldObject.Tile, out int tile))
        {
            continue;
        }

        return (worldObject.UniqueLoadId ?? worldObject.Id, map.UniqueId, tile);
    }

    throw new InvalidOperationException("样本存档没有可识别的殖民地锚点。");
}

static bool IsSettlement(WorldObjectSummary worldObject)
{
    return string.Equals(worldObject.Def, "Settlement", StringComparison.Ordinal)
        || (!string.IsNullOrWhiteSpace(worldObject.Class)
            && worldObject.Class.Contains("Settlement", StringComparison.Ordinal));
}

static bool TryParseTile(string? value, out int tile)
{
    tile = default;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    string candidate = value;
    int comma = value.IndexOf(',');
    if (comma >= 0)
    {
        candidate = value[..comma];
    }

    return int.TryParse(candidate.Trim(), out tile);
}

static PawnSummary Pawn(string localId, string snapshotId)
{
    return new PawnSummary(
        localId,
        $"owner:user-a/colony:colony-a/snapshot:{snapshotId}/map:caravan/thing:{localId}",
        "caravan",
        "thing",
        "Human",
        "Colonist",
        localId,
        false,
        "Faction_0",
        null);
}

static PawnExchangePackage PawnExchangePackageFixture(CrossMapPawnReference reference)
{
    return new PawnExchangePackage(
        SafePawnExchangeSerializer.CurrentPackageVersion,
        reference,
        new PawnExchangeIdentity(
            "Human",
            "Colonist",
            "Faction_0",
            "Female"),
        new PawnExchangeAppearance(
            "Li",
            "Female",
            "Narrow",
            "Bob",
            null,
            "0.72,0.55,0.43",
            "0.08,0.06,0.04"),
        new PawnExchangeStatus(
            Dead: false,
            BiologicalAgeTicks: 12000000,
            ChronologicalAgeTicks: 18000000,
            DeathCauseDef: null,
            HealthState: "Healthy"),
        new[]
        {
            new PawnExchangeEquipmentItem(
                "owner:user-a/thing:apparel-001",
                "Parka",
                "厚皮大衣",
                StackCount: 1,
                "Normal",
                HitPoints: 75,
                WornByCorpse: false,
                Biocoded: false,
                BiocodedPawnGlobalId: null,
                UniqueWeapon: false,
                UniqueWeaponName: null,
                UniqueWeaponTraits: null)
        },
        new[]
        {
            new PawnExchangeEquipmentItem(
                "owner:user-a/thing:weapon-001",
                "Gun_BoltActionRifle",
                "步枪",
                StackCount: 1,
                "Good",
                HitPoints: 98,
                WornByCorpse: null,
                Biocoded: true,
                BiocodedPawnGlobalId: reference.GlobalId,
                UniqueWeapon: false,
                UniqueWeaponName: null,
                UniqueWeaponTraits: Array.Empty<string>())
        },
        new[]
        {
            new PawnExchangeRelationshipStub(
                "owner:user-b/colony:colony-b/snapshot:snapshot-b/map:worldPawns/pawn:pawn-parent-001",
                "Chen",
                OtherPawnDead: false,
                "parent")
        });
}

static RaidSettlementReturnResult AcceptedSettlementResult(
    string eventId,
    string originalSnapshotId,
    string returnedSnapshotId)
{
    ThingSummary missing = Thing("missing-stack", stackCount: "1") with
    {
        GlobalKey = $"owner:user-b/colony:colony-b/snapshot:{originalSnapshotId}/map:0/thing:missing-stack"
    };
    ThingSummary reduced = Thing("reduced-stack", stackCount: "75") with
    {
        GlobalKey = $"owner:user-b/colony:colony-b/snapshot:{originalSnapshotId}/map:0/thing:reduced-stack"
    };
    ThingSummary reducedReturned = reduced with
    {
        GlobalKey = $"owner:user-b/colony:colony-b/snapshot:{returnedSnapshotId}/map:0/thing:reduced-stack",
        StackCount = "70"
    };

    RaidSettlementDiffResult diff = RaidSettlementDiffer.CompareByDisappearance(
        new[] { missing, reduced },
        new[] { reducedReturned },
        new RaidSettlementPolicy(0.5, eventId),
        thing => $"{thing.MapUniqueId}/thing:{thing.LocalId}");

    return new RaidSettlementReturnResult(
        RaidSettlementReturnResultKind.Accepted,
        eventId,
        new SnapshotIdentity("user-b", "colony-b", originalSnapshotId),
        new SnapshotIdentity("user-b", "colony-b", returnedSnapshotId),
        "Map_0",
        diff);
}

static ThingSummary Thing(string localId, string stackCount)
{
    string globalKey = $"owner:user-a/colony:colony-a/snapshot:snapshot-before/map:0/thing:{localId}";
    return new ThingSummary(
        localId,
        globalKey,
        "0",
        "ThingWithComps",
        "Steel",
        "(1, 0, 1)",
        "Faction_0",
        stackCount,
        "100",
        null,
        null,
        IsPawn: false);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}：期望 {expected}，实际 {actual}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"期望抛出 {typeof(TException).Name}。");
}

static Dictionary<string, string?> BiotechGeneMetadata(params string[] geneDefNames)
{
    return new Dictionary<string, string?>
    {
        ["rimworld.biotech.geneDefNames"] = string.Join(",", geneDefNames)
    };
}

static Dictionary<string, string?> BiotechTargetGeneMetadata(string geneDefName)
{
    return new Dictionary<string, string?>
    {
        ["rimworld.biotech.targetGeneDefName"] = geneDefName
    };
}

static Dictionary<string, string?> CoreBookSkillMetadata(params string[] skillDefNames)
{
    return new Dictionary<string, string?>
    {
        ["clashofrim.core.bookSkillDefNames"] = string.Join(",", skillDefNames)
    };
}

static Dictionary<string, string?> CoreTargetBookSkillMetadata(string skillDefName)
{
    return new Dictionary<string, string?>
    {
        ["clashofrim.core.targetBookSkillDefName"] = skillDefName
    };
}

internal sealed class TestTradeThingMetadataMatcher : ITradeThingMetadataMatcher
{
    private const string BiotechGeneDefNamesKey = "rimworld.biotech.geneDefNames";
    private const string BiotechTargetGeneDefNameKey = "rimworld.biotech.targetGeneDefName";
    private const string CoreBookSkillDefNamesKey = "clashofrim.core.bookSkillDefNames";
    private const string CoreTargetBookSkillDefNameKey = "clashofrim.core.targetBookSkillDefName";
    private const string CoreResearchProjectDefNameKey = "clashofrim.core.researchProjectDefName";
    private const string CoreTargetResearchProjectDefNameKey = "clashofrim.core.targetResearchProjectDefName";

    public int RequirementStrictness(ThingReferenceDto requirement)
    {
        int strictness = 0;
        if (!string.IsNullOrWhiteSpace(MetadataValue(requirement, BiotechTargetGeneDefNameKey)))
        {
            strictness += 1_000_000;
        }

        if (!string.IsNullOrWhiteSpace(MetadataValue(requirement, CoreTargetBookSkillDefNameKey)))
        {
            strictness += 500_000;
        }

        if (!string.IsNullOrWhiteSpace(MetadataValue(requirement, CoreTargetResearchProjectDefNameKey)))
        {
            strictness += 500_000;
        }

        return strictness;
    }

    public bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate)
    {
        string? targetGene = MetadataValue(requirement, BiotechTargetGeneDefNameKey);
        if (!string.IsNullOrWhiteSpace(targetGene)
            && !MetadataList(candidate, BiotechGeneDefNamesKey).Any(gene =>
                string.Equals(gene, targetGene, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string? targetBookSkill = MetadataValue(requirement, CoreTargetBookSkillDefNameKey);
        if (!string.IsNullOrWhiteSpace(targetBookSkill)
            && !MetadataList(candidate, CoreBookSkillDefNamesKey).Any(skill =>
                string.Equals(skill, targetBookSkill, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string? targetResearchProject = MetadataValue(requirement, CoreTargetResearchProjectDefNameKey);
        if (!string.IsNullOrWhiteSpace(targetResearchProject)
            && !string.Equals(MetadataValue(candidate, CoreResearchProjectDefNameKey), targetResearchProject, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public IReadOnlyList<string> DescribeConstraints(ThingReferenceDto requirement)
    {
        return Array.Empty<string>();
    }

    private static string? MetadataValue(ThingReferenceDto reference, string key)
    {
        return reference.Metadata.TryGetValue(key, out string? value) ? value : null;
    }

    private static IEnumerable<string> MetadataList(ThingReferenceDto reference, string key)
    {
        string? value = MetadataValue(reference, key);
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item));
    }
}
