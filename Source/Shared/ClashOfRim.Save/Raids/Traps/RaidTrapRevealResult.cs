namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidTrapRevealResult(
    string GlobalKey,
    bool Revealed,
    RaidTrapRevealReason Reason,
    bool RequiresMapMeshRefresh);
