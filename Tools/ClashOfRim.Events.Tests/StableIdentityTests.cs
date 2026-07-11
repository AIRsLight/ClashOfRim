using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class StableIdentityTests
{
    public static void RunAll()
    {
        VerifyExactWorldObjectIdentity();
        VerifyItemDeliveryPurposeIsStructured();
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

    private static void VerifyItemDeliveryPurposeIsStructured()
    {
        var payload = new ItemDeliveryEventPayload(
            Array.Empty<EventThingReference>(),
            "TradeCompletedOwnerDelivery-looking user text",
            Purpose: ItemDeliveryPurpose.Gift);

        Require(!payload.IsTradeDelivery, "普通礼物消息即使含交易前缀也不能被误判为交易交付");
        Require(payload.Purpose == ItemDeliveryPurpose.Gift, "物品到达子类型必须由枚举字段表达");
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
        string serverEntry = Read(root, "Source", "Client", "MainMenu", "Entry", "ClashOfRimServerEntryDialog.cs");
        string mainButtonDefs = Read(root, "Defs", "MainButtonDefs", "ClashOfRim_MainButtons.xml");
        string proxy = Read(root, "Source", "Client", "Diplomacy", "Factions", "PlayerFactionProxyUtility.cs");
        string giftProcessor = Read(root, "Source", "Client", "Gifts", "Processing", "ItemDeliveryClientProcessor.cs");
        string giftLetters = Read(root, "Source", "Client", "EventLetters", "Runtime", "ClashOfRimMod.EventLetters.cs");
        string eventDtos = Read(root, "Source", "Client", "ClientNetwork", "Dtos", "ModEventDtos.cs");
        string protocolEventReference = Read(root, "Source", "Shared", "ClashOfRim.Protocol", "Events", "EventReferenceDto.cs");
        string serverCore = Read(root, "Source", "Server", "ClashOfRim.Network", "Server", "Core", "ClashOfRimNetworkServer.cs");
        string odyssey = Read(root, "Source", "Server", "ClashOfRim.Network", "Plugins", "DlcCompatibility", "Odyssey", "OdysseyServerCompatibility.cs");
        string markerBuilder = Read(root, "Source", "Shared", "ClashOfRim.Events", "WorldMap", "WorldMapMarkerProjectionBuilder.cs");
        string snapshotReceiver = Read(root, "Source", "Shared", "ClashOfRim.Save", "Snapshots", "Upload", "SnapshotUploadReceiver.cs");

        Require(!menu.Contains("LabelEquals(", StringComparison.Ordinal), "主菜单不能按翻译后的按钮文本识别原版操作");
        Require(menu.Contains("MainMenuDrawContext", StringComparison.Ordinal), "主菜单补丁必须由原版绘制上下文限定");
        Require(serverEntry.Contains("ServerAddressInputControl", StringComparison.Ordinal), "登录页必须注册服务器地址焦点");
        Require(serverEntry.Contains("UserIdInputControl", StringComparison.Ordinal), "登录页必须注册用户 ID 焦点");
        Require(serverEntry.Contains("PasswordInputControl", StringComparison.Ordinal), "登录页必须注册密码焦点");
        Require(serverEntry.Contains("KeyCode.Tab", StringComparison.Ordinal), "登录页必须处理 Tab 键");
        Require(serverEntry.Contains("Event.current.shift", StringComparison.Ordinal), "登录页必须支持 Shift+Tab");
        Require(serverEntry.Contains("Event.current.Use()", StringComparison.Ordinal), "登录页必须消费 Tab 事件");
        const string multiplayerWorkerType = "AIRsLight.ClashOfRim.Multiplayer.MainButtonWorker_ClashOfRimMultiplayer";
        Require(mainButtonDefs.Contains($"<workerClass>{multiplayerWorkerType}</workerClass>", StringComparison.Ordinal),
            "多人底栏按钮必须绑定会话可见性 worker");
        string multiplayerWorkerPath = Path.Combine(root, "Source", "Client", "Multiplayer", "MainButton", "MainButtonWorker_ClashOfRimMultiplayer.cs");
        Require(File.Exists(multiplayerWorkerPath), "多人底栏按钮会话可见性 worker 必须存在");
        string multiplayerWorker = File.ReadAllText(multiplayerWorkerPath);
        Require(multiplayerWorker.Contains("override bool Visible", StringComparison.Ordinal)
            && multiplayerWorker.Contains("base.Visible", StringComparison.Ordinal)
            && multiplayerWorker.Contains("ShouldShowMultiplayerMainButton", StringComparison.Ordinal),
            "多人底栏按钮必须保留原版可见性并要求有效服务器会话");
        Require(!proxy.Contains("LastIndexOf('('", StringComparison.Ordinal), "代理阵营所有者不能从显示名括号中解析");
        Require(!proxy.Contains("IsDisplayNameForUser", StringComparison.Ordinal), "代理阵营不能用显示名验证所有者");
        Require(!giftProcessor.Contains("string.Equals(payload.Message", StringComparison.Ordinal)
            && !giftProcessor.Contains("payload.Message.StartsWith", StringComparison.Ordinal), "礼物处理不能用消息文本决定交易流程");
        Require(!giftLetters.Contains("TradeCompletedOwnerDelivery\"", StringComparison.Ordinal), "信件分类不能匹配交易消息前缀");
        Require(!giftLetters.Contains("string.Equals(detail.EventType", StringComparison.Ordinal), "信件流程不能比较事件类型字符串");
        Require(!eventDtos.Contains("public string EventType", StringComparison.Ordinal), "客户端事件 DTO 必须使用协议枚举");
        Require(!protocolEventReference.Contains("string eventType", StringComparison.Ordinal)
            && !protocolEventReference.Contains("public string EventType", StringComparison.Ordinal), "协议事件引用必须使用协议枚举");
        Require(!serverCore.Contains("ContainsOrbitalToken", StringComparison.Ordinal), "服务端轨道判定不能搜索名称文本");
        Require(!odyssey.Contains("worldObject.Name", StringComparison.Ordinal)
            && !odyssey.Contains("worldObject.Tile", StringComparison.Ordinal), "Odyssey 分类器不能读取显示名或序列化地块文本");
        Require(!markerBuilder.Contains("Contains(\"PlayerColony\"", StringComparison.Ordinal), "殖民地标记不能按类名子串识别");
        Require(!snapshotReceiver.Contains("Contains(\"Settlement\"", StringComparison.Ordinal), "快照锚点不能按据点类名子串识别");

        string allSource = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "Source"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        Require(!allSource.Contains("ServerEventType.Gift", StringComparison.Ordinal), "Gift 不能继续作为顶层事件类型");
        Require(!allSource.Contains("ServerEventType.GiftReturn", StringComparison.Ordinal), "GiftReturn 不能继续作为顶层事件类型");
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
