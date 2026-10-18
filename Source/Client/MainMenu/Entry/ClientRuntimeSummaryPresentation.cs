using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim;

internal static class ClientRuntimeSummaryPresentation
{
    internal static string Format(ClashOfRimClientNetworkContext context) => Describe(Find.CurrentMap?.uniqueID, context);
    internal static string Describe(int? mapId, ClashOfRimClientNetworkContext context)
    {
        string map = mapId is { } id ? ClashOfRimText.Key("ClashOfRim.CurrentMapSummary", id.Named("MAPID"))
            : ClashOfRimText.Key("ClashOfRim.CurrentMapMissing");
        string configuration = context.IsConfigured
            ? ClashOfRimText.Key("ClashOfRim.RuntimeConfigurationSummary", context.ServerBaseUrl.Named("SERVER"), context.UserId.Named("USER"), context.ColonyId.Named("COLONY"))
            : ClashOfRimText.Key("ClashOfRim.RuntimeConfigurationMissing");
        string snapshot = string.IsNullOrWhiteSpace(context.CurrentSnapshotId) ? ClashOfRimText.Key("ClashOfRim.CurrentSnapshotMissing")
            : ClashOfRimText.Key("ClashOfRim.CurrentSnapshotSummary", context.CurrentSnapshotId.Named("SNAPSHOT"));
        return $"{map} {configuration} {snapshot}";
    }
}
