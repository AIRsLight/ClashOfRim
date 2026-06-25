using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal enum RemoteNpcLordJobKind
{
    AssaultColony,
    StageThenAttack,
    SleepThenAssaultColony,
    MechanoidsDefend,
    SleepThenMechanoidsDefend
}

internal sealed class RemoteNpcLordSnapshot
{
    public RemoteNpcLordJobKind Kind;
    public string FactionLoadId = string.Empty;
    public List<string> PawnLoadIds = new();
    public List<string> ThingLoadIds = new();
    public List<string> OwnedBuildingLoadIds = new();
    public List<string> ThingsToNotifyOnDefeatLoadIds = new();

    public bool CanKidnap = true;
    public bool CanTimeoutOrFlee = true;
    public bool Sappers;
    public bool UseAvoidGridSmart;
    public bool CanSteal = true;
    public bool Breachers;
    public bool CanPickUpOpportunisticWeapons;

    public string StageLoc = string.Empty;
    public int RaidSeed;
    public bool CanTimeoutFlee = true;
    public int DelayMin = 5000;
    public int DelayMax = 15000;

    public bool SendWokenUpMessage = true;
    public bool AwakeOnClamor;

    public float DefendRadius;
    public string DefSpot = string.Empty;
    public bool CanAssaultColony;
    public bool IsMechCluster;

    public RemoteNpcLordSnapshot CloneWithProjectedThingIds(IReadOnlyDictionary<string, string> projectedByOriginalLoadId)
    {
        return new RemoteNpcLordSnapshot
        {
            Kind = Kind,
            FactionLoadId = FactionLoadId,
            PawnLoadIds = Project(PawnLoadIds, projectedByOriginalLoadId),
            ThingLoadIds = Project(ThingLoadIds, projectedByOriginalLoadId),
            OwnedBuildingLoadIds = Project(OwnedBuildingLoadIds, projectedByOriginalLoadId),
            ThingsToNotifyOnDefeatLoadIds = Project(ThingsToNotifyOnDefeatLoadIds, projectedByOriginalLoadId),
            CanKidnap = CanKidnap,
            CanTimeoutOrFlee = CanTimeoutOrFlee,
            Sappers = Sappers,
            UseAvoidGridSmart = UseAvoidGridSmart,
            CanSteal = CanSteal,
            Breachers = Breachers,
            CanPickUpOpportunisticWeapons = CanPickUpOpportunisticWeapons,
            StageLoc = StageLoc,
            RaidSeed = RaidSeed,
            CanTimeoutFlee = CanTimeoutFlee,
            DelayMin = DelayMin,
            DelayMax = DelayMax,
            SendWokenUpMessage = SendWokenUpMessage,
            AwakeOnClamor = AwakeOnClamor,
            DefendRadius = DefendRadius,
            DefSpot = DefSpot,
            CanAssaultColony = CanAssaultColony,
            IsMechCluster = IsMechCluster
        };
    }

    private static List<string> Project(
        IEnumerable<string> loadIds,
        IReadOnlyDictionary<string, string> projectedByOriginalLoadId)
    {
        return loadIds
            .Select(loadId => projectedByOriginalLoadId.TryGetValue(loadId, out string projected) ? projected : loadId)
            .Distinct()
            .ToList();
    }
}
