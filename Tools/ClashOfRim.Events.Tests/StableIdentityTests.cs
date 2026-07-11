using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class StableIdentityTests
{
    public static void RunAll()
    {
        VerifyExactWorldObjectIdentity();
        VerifyGiftPurposeIsStructured();
        VerifyCompatibilityIssueCategoriesAreExplicit();
        VerifyClientAndServerDoNotBranchOnPresentationStrings();
    }

    private static void VerifyExactWorldObjectIdentity()
    {
        var misleadingSettlement = new WorldObjectSummary(
            "1", "WorldObject_1", "ExampleSettlementExtension", "QuestSite", "12,0", "Faction_1", "Settlement Orbit", false);
        var settlement = misleadingSettlement with { Class = "Settlement", Def = "Settlement" };
        var playerColony = misleadingSettlement with { Class = "RemotePlayerColony", Def = "PlayerColony" };

        Require(!WorldObjectTypeIdentity.IsSettlement(misleadingSettlement), "包含 Settlement 的派生类名不能被视为原版据点");
        Require(WorldObjectTypeIdentity.IsSettlement(settlement), "原版 Settlement 精确类型必须被识别");
        Require(!WorldObjectTypeIdentity.IsPlayerColonyMarker(misleadingSettlement), "包含 PlayerColony 的派生类名不能成为玩家殖民地标记");
        Require(WorldObjectTypeIdentity.IsPlayerColonyMarker(playerColony), "明确的 PlayerColony defName 必须被识别");
    }

    private static void VerifyGiftPurposeIsStructured()
    {
        var payload = new GiftEventPayload(
            Array.Empty<EventThingReference>(),
            "TradeCompletedOwnerDelivery-looking user text",
            Purpose: GiftEventPurpose.Gift);

        Require(!payload.IsTradeDelivery, "普通礼物消息即使含交易前缀也不能被误判为交易交付");
        Require(payload.Purpose == GiftEventPurpose.Gift, "礼物流程必须由枚举字段表达");
    }

    private static void VerifyCompatibilityIssueCategoriesAreExplicit()
    {
        foreach (CompatibilityIssueCode code in Enum.GetValues<CompatibilityIssueCode>())
        {
            _ = CompatibilityIssueClassifier.CategoryFor(code);
        }

        Require(
            CompatibilityIssueClassifier.CategoryFor(CompatibilityIssueCode.ConfigFileMismatch) == CompatibilityIssueCategory.Config,
            "配置错误必须进入配置页");
        Require(
            CompatibilityIssueClassifier.CategoryFor(CompatibilityIssueCode.FileHashMismatch) == CompatibilityIssueCategory.Hash,
            "文件错误必须进入文件页");
        Require(
            CompatibilityIssueClassifier.CategoryFor(CompatibilityIssueCode.ModOrderMismatch) == CompatibilityIssueCategory.Manifest,
            "模组顺序错误必须进入清单页");
    }

    private static void VerifyClientAndServerDoNotBranchOnPresentationStrings()
    {
        string root = FindRepositoryRoot();
        string menu = Read(root, "Source", "Client", "MainMenu", "Entry", "ClashOfRimMainMenuPatches.cs");
        string proxy = Read(root, "Source", "Client", "Diplomacy", "Factions", "PlayerFactionProxyUtility.cs");
        string giftProcessor = Read(root, "Source", "Client", "Gifts", "Processing", "GiftClientProcessor.cs");
        string giftLetters = Read(root, "Source", "Client", "EventLetters", "Runtime", "ClashOfRimMod.EventLetters.cs");
        string serverCore = Read(root, "Source", "Server", "ClashOfRim.Network", "Server", "Core", "ClashOfRimNetworkServer.cs");
        string odyssey = Read(root, "Source", "Server", "ClashOfRim.Network", "Plugins", "DlcCompatibility", "Odyssey", "OdysseyServerCompatibility.cs");
        string markerBuilder = Read(root, "Source", "Shared", "ClashOfRim.Events", "WorldMap", "WorldMapMarkerProjectionBuilder.cs");
        string snapshotReceiver = Read(root, "Source", "Shared", "ClashOfRim.Save", "Snapshots", "Upload", "SnapshotUploadReceiver.cs");

        Require(!menu.Contains("LabelEquals(", StringComparison.Ordinal), "主菜单不能按翻译后的按钮文本识别原版操作");
        Require(menu.Contains("MainMenuDrawContext", StringComparison.Ordinal), "主菜单补丁必须由原版绘制上下文限定");
        Require(!proxy.Contains("LastIndexOf('('", StringComparison.Ordinal), "代理阵营所有者不能从显示名括号中解析");
        Require(!proxy.Contains("IsDisplayNameForUser", StringComparison.Ordinal), "代理阵营不能用显示名验证所有者");
        Require(!giftProcessor.Contains("string.Equals(payload.Message", StringComparison.Ordinal)
            && !giftProcessor.Contains("payload.Message.StartsWith", StringComparison.Ordinal), "礼物处理不能用消息文本决定交易流程");
        Require(!giftLetters.Contains("TradeCompletedOwnerDelivery\"", StringComparison.Ordinal), "信件分类不能匹配交易消息前缀");
        Require(!serverCore.Contains("ContainsOrbitalToken", StringComparison.Ordinal), "服务端轨道判定不能搜索名称文本");
        Require(!odyssey.Contains("worldObject.Name", StringComparison.Ordinal)
            && !odyssey.Contains("worldObject.Tile", StringComparison.Ordinal), "Odyssey 分类器不能读取显示名或序列化地块文本");
        Require(!markerBuilder.Contains("Contains(\"PlayerColony\"", StringComparison.Ordinal), "殖民地标记不能按类名子串识别");
        Require(!snapshotReceiver.Contains("Contains(\"Settlement\"", StringComparison.Ordinal), "快照锚点不能按据点类名子串识别");
    }

    private static string Read(string root, params string[] segments)
    {
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                && Directory.Exists(Path.Combine(directory.FullName, "Source")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
