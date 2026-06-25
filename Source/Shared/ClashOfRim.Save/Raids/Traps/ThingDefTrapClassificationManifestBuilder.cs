namespace AIRsLight.ClashOfRim.Save;

public static class ThingDefTrapClassificationManifestBuilder
{
    public static ThingDefTrapClassificationManifest FromUploadPackage(TrapClassificationUploadPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return new ThingDefTrapClassificationManifest(
            package.ApprovedManifestEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.DefName))
                .Select(entry => new ThingDefTrapClassification(
                    entry.DefName,
                    entry.ThingClass,
                    entry.SourceModId,
                    entry.InheritsBuildingTrap,
                    ApprovedCustomTrap: !entry.InheritsBuildingTrap && entry.AdminApproved))
                .ToList());
    }
}
