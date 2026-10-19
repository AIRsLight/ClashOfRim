using AIRsLight.ClashOfRim.Save;

internal static class RaidEvidenceBindingSnapshot
{
    public static SaveSnapshotPackage Create(string snapshot, string raid)
    {
        string path = Path.Combine(Path.GetTempPath(), "raid-binding-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            File.WriteAllText(path, "<savegame><meta><gameVersion>1.7-test</gameVersion></meta><game><world><worldObjects><worldObjects>"
                + "<li Class=\"AIRsLight.ClashOfRim.RemoteMaps.RemoteSessionMapParent\"><ID>77</ID><def>ClashOfRim_RemoteRaidBattleMapParent</def>"
                + "<clashOfRimRelatedEventId>" + raid + "</clashOfRimRelatedEventId></li></worldObjects></worldObjects></world><maps>"
                + "<li><uniqueID>0</uniqueID><things /></li><li><uniqueID>77</uniqueID><mapInfo><parent>WorldObject_77</parent></mapInfo>"
                + "<things /></li></maps></game></savegame>");
            return SaveSnapshotPackageBuilder.FromFile(path, new("userA", "colonyA", snapshot), DateTimeOffset.UnixEpoch);
        }
        finally { File.Delete(path); }
    }
}
