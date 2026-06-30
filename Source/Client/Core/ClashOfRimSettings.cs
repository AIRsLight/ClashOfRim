using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed class ClashOfRimSettings : ModSettings
{
    public string ServerBaseUrl = string.Empty;
    public string UserId = string.Empty;
    public string ColonyId = string.Empty;
    public string CurrentSnapshotId = string.Empty;
    public string CurrentLineageToken = string.Empty;
    public string SteamAuthTicket = string.Empty;
    public string OfflinePassword = string.Empty;
    public string AuthToken = string.Empty;
    public string DisplayName = string.Empty;
    public string TargetUserId = string.Empty;
    public string TargetColonyId = string.Empty;
    public string TargetSnapshotId = string.Empty;
    public string CurrentWorldConfigurationId = string.Empty;
    public Dictionary<string, string> ColonyAppearancesByAccount = new();

    private List<string>? colonyAppearanceAccountKeys;
    private List<string>? colonyAppearanceAccountValues;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServerBaseUrl) &&
        !string.IsNullOrWhiteSpace(UserId) &&
        !string.IsNullOrWhiteSpace(ColonyId);

    public override void ExposeData()
    {
        Scribe_Values.Look(ref ServerBaseUrl, "serverBaseUrl", string.Empty);
        Scribe_Values.Look(ref UserId, "userId", string.Empty);
        Scribe_Values.Look(ref ColonyId, "colonyId", string.Empty);
        Scribe_Values.Look(ref CurrentSnapshotId, "currentSnapshotId", string.Empty);
        Scribe_Values.Look(ref CurrentLineageToken, "currentLineageToken", string.Empty);
        Scribe_Values.Look(ref SteamAuthTicket, "steamAuthTicket", string.Empty);
        Scribe_Values.Look(ref OfflinePassword, "offlinePassword", string.Empty);
        Scribe_Values.Look(ref AuthToken, "authToken", string.Empty);
        Scribe_Values.Look(ref DisplayName, "displayName", string.Empty);
        Scribe_Values.Look(ref TargetUserId, "targetUserId", string.Empty);
        Scribe_Values.Look(ref TargetColonyId, "targetColonyId", string.Empty);
        Scribe_Values.Look(ref TargetSnapshotId, "targetSnapshotId", string.Empty);
        Scribe_Values.Look(ref CurrentWorldConfigurationId, "currentWorldConfigurationId", string.Empty);
        Scribe_Collections.Look(
            ref ColonyAppearancesByAccount,
            "colonyAppearancesByAccount",
            LookMode.Value,
            LookMode.Value,
            ref colonyAppearanceAccountKeys,
            ref colonyAppearanceAccountValues);

        ColonyAppearancesByAccount ??= new Dictionary<string, string>();
    }
}
