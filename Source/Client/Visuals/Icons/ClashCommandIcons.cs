using UnityEngine;
using AIRsLight.ClashOfRim.Raids;
using Verse;

namespace AIRsLight.ClashOfRim.Visuals;

[StaticConstructorOnStartup]
internal static class ClashCommandIcons
{
    public static readonly Texture2D GiftDelivery = Load("GiftDelivery");
    public static readonly Texture2D ForcedDelivery = Load("ForcedDelivery");
    public static readonly Texture2D SendSupport = Load("SendSupport");
    public static readonly Texture2D TradeFulfill = Load("TradeFulfill");
    public static readonly Texture2D ScoutObserve = Load("ScoutObserve");
    public static readonly Texture2D DefensePointHold = Load("DefensePointHold");
    public static readonly Texture2D DefensePointDefend = Load("DefensePointDefend");
    public static readonly Texture2D DefensePointAssault = Load("DefensePointAssault");
    public static readonly Texture2D DefensePointOperateEquipment = Load("DefensePointOperateEquipment");
    public static readonly Texture2D LaunchRaid = ContentFinder<Texture2D>.Get("UI/Commands/AttackSettlement", reportFailure: false)
        ?? BaseContent.BadTex;
    public static readonly Texture2D LoadTransporter = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", reportFailure: false)
        ?? BaseContent.BadTex;

    public static Texture2D DefensePointMode(DefensePointAiMode mode)
    {
        return mode switch
        {
            DefensePointAiMode.Hold => DefensePointHold,
            DefensePointAiMode.Defend => DefensePointDefend,
            DefensePointAiMode.Assault => DefensePointAssault,
            DefensePointAiMode.OperateEquipment => DefensePointOperateEquipment,
            _ => DefensePointDefend
        };
    }

    private static Texture2D Load(string name)
    {
        return ContentFinder<Texture2D>.Get("UI/Commands/ClashOfRim/" + name, reportFailure: false)
            ?? BaseContent.BadTex;
    }
}
