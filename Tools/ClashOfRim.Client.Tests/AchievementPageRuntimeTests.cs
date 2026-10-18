using System;
using System.Globalization;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Achievements;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Multiplayer.Achievements;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class AchievementPageRuntimeTests
{
    private static bool succeed, native;
    private static AchievementTrophyData? received;
    private static string? notice;
    private static MessageTypeDef? noticeType;
    private static bool historical;

    internal static void Run()
    {
        var game = Current.Game; var culture = CultureInfo.CurrentCulture;
        var successType = MessageTypeDefOf.TaskCompletion; var failureType = MessageTypeDefOf.RejectInput;
        var harmony = new Harmony("ClashOfRim.Tests.AchievementPageRuntime");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), prefix: Method(nameof(Text)));
            harmony.Patch(AccessTools.Method(typeof(AchievementTrophyUtility), nameof(AchievementTrophyUtility.TryCreatePlanOnCurrentMap)), prefix: Method(nameof(Create)));
            harmony.Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message), new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) }), prefix: Method(nameof(Message)));
            MessageTypeDefOf.TaskCompletion = new MessageTypeDef(); MessageTypeDefOf.RejectInput = new MessageTypeDef();
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Formatting(); Placement();
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; CultureInfo.CurrentCulture = culture;
            MessageTypeDefOf.TaskCompletion = successType; MessageTypeDefOf.RejectInput = failureType;
            received = null; notice = null; noticeType = null; succeed = native = historical = false;
        }
    }

    private static void Formatting()
    {
        var data = new AchievementTrophyData { AchievementId = "award", LabelKey = "label", DescriptionKey = "description", Value = long.MaxValue };
        Check(AchievementText.Label("label", "award") == "称号 İ" && data.ResolvedLabel() == "称号 İ", "Page/trophy label localization diverged.");
        Check(AchievementText.Description("description") == "描述 Français" && data.ResolvedDescription() == "描述 Français", "Page/trophy description localization diverged.");
        data.LabelKey = "missing"; data.DescriptionKey = "missing";
        Check(AchievementText.Label("missing", "award") == "award" && data.ResolvedLabel() == "award"
            && data.ResolvedDescription() == "" && AchievementText.Description(null) == "", "Missing translation fallback changed.");
        data.LabelKey = "empty";
        Check(AchievementText.Label("empty", "award") == "" && data.ResolvedLabel() == "", "Explicit empty translation was treated as missing.");
        data.LabelKey = ""; data.AchievementId = "";
        Check(data.ResolvedLabel() == "unknown" && AchievementText.Label(null, null) == "unknown", "Unknown achievement fallback changed.");
        Check(AchievementText.Value(long.MaxValue) == "9,223,372,036,854,775,807" && data.ResolvedValue() == "9,223,372,036,854,775,807",
            "Achievement page/trophy number formatting depends on caller culture.");
    }

    private static void Placement()
    {
        var placement = new ClientAchievementTrophyPlacement();
        var item = new ModAchievementSummaryDto
        {
            AchievementId = "award", Category = "category", LabelKey = "label", DescriptionKey = "description",
            IconId = "icon", Color = "Purple", Value = long.MaxValue, SourceSnapshotId = "source"
        };
        foreach (bool success in new[] { false, true })
        {
            native = false; succeed = success;
            Check(placement.TryCreate(item, new("owner", "colony"), "玩家") == success, "Placement result did not reach the page.");
            Check(received is { AchievementId: "award", Category: "category", LabelKey: "label", DescriptionKey: "description",
                IconId: "icon", Color: "Purple", Value: long.MaxValue, SourceSnapshotId: "source", OwnerUserId: "owner", OwnerColonyId: "colony", OwnerDisplayName: "玩家" },
                "Trophy plan lost achievement or owner metadata.");
            Check(notice == "placement-result" && !historical && ReferenceEquals(noticeType, success ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput),
                "Placement notice/severity/history semantics changed.");
        }
        native = true; Current.Game = null;
        Check(!placement.TryCreate(item, new("owner", "colony"), "") && notice == "ClashOfRim.Achievement.TrophyNoMap"
            && ReferenceEquals(noticeType, MessageTypeDefOf.RejectInput), "Native utility's missing-map rejection was bypassed.");
    }

    private static bool Text(string __0, ref string __result)
    {
        __result = __0 switch { "label" => "称号 İ", "description" => "描述 Français", "empty" => "", "ClashOfRim.Achievement.Unknown" => "unknown", _ => __0 };
        return false;
    }
    private static bool Create(AchievementTrophyData __0, ref string __1, ref bool __result)
    {
        if (native) return true;
        received = __0; __1 = "placement-result"; __result = succeed; return false;
    }
    private static bool Message(string __0, MessageTypeDef __1, bool __2)
    { notice = __0; noticeType = __1; historical = __2; return false; }
    private static HarmonyMethod Method(string name) => new(typeof(AchievementPageRuntimeTests), name);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
