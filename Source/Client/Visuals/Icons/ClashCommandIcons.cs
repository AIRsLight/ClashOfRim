using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Visuals;

internal static class ClashCommandIcons
{
    public static readonly Texture2D GiftDelivery = Load("GiftDelivery");
    public static readonly Texture2D ForcedDelivery = Load("ForcedDelivery");
    public static readonly Texture2D SendSupport = Load("SendSupport");
    public static readonly Texture2D TradeFulfill = Load("TradeFulfill");
    public static readonly Texture2D ScoutObserve = Load("ScoutObserve");
    public static readonly Texture2D LaunchRaid = ContentFinder<Texture2D>.Get("UI/Commands/AttackSettlement", reportFailure: false)
        ?? BaseContent.BadTex;

    private static Texture2D Load(string name)
    {
        return ContentFinder<Texture2D>.Get("UI/Commands/ClashOfRim/" + name, reportFailure: false)
            ?? BaseContent.BadTex;
    }
}
