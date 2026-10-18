namespace AIRsLight.ClashOfRim.Protocol;

// Wire keys shared by client capture/editing and authoritative server trade matching.
public static class BiotechThingMetadataKeys
{
    public const string GeneDefNames = "rimworld.biotech.geneDefNames";
    public const string TargetGeneDefNames = "rimworld.biotech.targetGeneDefName";
    public const string XenotypeName = "rimworld.biotech.xenotypeName";
    public const string XenotypeIconDefName = "rimworld.biotech.xenotypeIconDefName";
    public const string ReproductiveSourcePrefix = "rimworld.biotech.reproductiveSource.";
    public const string ReproductiveSourceCount = ReproductiveSourcePrefix + "count";
    public const string SourceRole = "role";
    public const string SourceGlobalId = "globalId";
    public const string SourceOwnerUserId = "ownerUserId";
    public const string SourceName = "name";
    public const string SourceRaceDef = "raceDef";
    public const string SourcePawnKindDef = "pawnKindDef";
    public const string SourceGender = "gender";
    public const string SourceDead = "dead";
    public const string SourceFactionDef = "factionDef";
    public const string SourceXenotypeDef = "xenotypeDef";
    public const string SourceEndogeneDefNames = "endogeneDefNames";
    public const string SourceXenogeneDefNames = "xenogeneDefNames";
    public const int MaxReproductiveSources = 4;
}
