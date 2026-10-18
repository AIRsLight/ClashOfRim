using AIRsLight.ClashOfRim.Modules.Maps;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public readonly struct RemoteMapSessionPolicy
{
    public RemoteMapSessionPolicy(
        bool canControlLocalUnits,
        bool canInjectUnits,
        bool canUseTransportPods,
        bool hideEnemyTraps,
        bool enableDefensePointAi,
        bool requiresSettlement,
        bool serverMayForceClose)
    {
        CanControlLocalUnits = canControlLocalUnits;
        CanInjectUnits = canInjectUnits;
        CanUseTransportPods = canUseTransportPods;
        HideEnemyTraps = hideEnemyTraps;
        EnableDefensePointAi = enableDefensePointAi;
        RequiresSettlement = requiresSettlement;
        ServerMayForceClose = serverMayForceClose;
    }

    public bool CanControlLocalUnits { get; }

    public bool CanInjectUnits { get; }

    public bool CanUseTransportPods { get; }

    public bool HideEnemyTraps { get; }

    public bool EnableDefensePointAi { get; }

    public bool RequiresSettlement { get; }

    public bool ServerMayForceClose { get; }

    public static RemoteMapSessionPolicy For(RemoteMapSessionKind kind)
    {
        return kind switch
        {
            RemoteMapSessionKind.ObserveAlly => new RemoteMapSessionPolicy(
                canControlLocalUnits: false,
                canInjectUnits: false,
                canUseTransportPods: false,
                hideEnemyTraps: false,
                enableDefensePointAi: false,
                requiresSettlement: false,
                serverMayForceClose: false),
            RemoteMapSessionKind.ScoutEnemy => new RemoteMapSessionPolicy(
                canControlLocalUnits: false,
                canInjectUnits: false,
                canUseTransportPods: false,
                hideEnemyTraps: true,
                enableDefensePointAi: false,
                requiresSettlement: false,
                serverMayForceClose: false),
            RemoteMapSessionKind.ObserveRaid => new RemoteMapSessionPolicy(
                canControlLocalUnits: false,
                canInjectUnits: false,
                canUseTransportPods: false,
                hideEnemyTraps: true,
                enableDefensePointAi: false,
                requiresSettlement: false,
                serverMayForceClose: true),
            RemoteMapSessionKind.RaidBattle => new RemoteMapSessionPolicy(
                canControlLocalUnits: true,
                canInjectUnits: true,
                canUseTransportPods: true,
                hideEnemyTraps: true,
                enableDefensePointAi: true,
                requiresSettlement: true,
                serverMayForceClose: true),
            _ => default
        };
    }

    public static RemoteMapSessionPolicy ForMode(string? mode) => For(ClientRemoteMapModes.Kind(mode));
    public static string NormalizeMode(string? mode) => ClientRemoteMapModes.Normalize(mode);
    public static string ObservationDownloadingStatusKey(string mode) => ClientRemoteMapModes.DownloadingKey(mode);
    public static string ObservationLoadedMessageKey(string mode) => ClientRemoteMapModes.LoadedKey(mode);
}
