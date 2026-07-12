using AIRsLight.ClashOfRim.Visuals;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

[StaticConstructorOnStartup]
internal static class RemoteWorldObjectCommandIcons
{
    public static readonly Texture2D ShowMap = ClashCommandIcons.ScoutObserve;
    public static readonly Texture2D SendSupport = ClashCommandIcons.SendSupport;
    public static readonly Texture2D LaunchRaid = ClashCommandIcons.LaunchRaid;
}
